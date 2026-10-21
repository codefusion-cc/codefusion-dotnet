using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CodeFusion.Http;

/// <summary>
/// Arrays of one size, kept in a few locked stacks. ArrayPool.Shared keeps only 32 arrays per size per core and
/// allocates past that, which thousands of requests in flight at once would do on every request. Arrays of another
/// size (a request or response grown past it) come from and go back to ArrayPool.Shared through it, cleared, since
/// other code in the process rents from there. Its own arrays go back as they are: only this server rents them.
/// On a server's event loops each loop also keeps a few arrays of its own, which only its thread touches, so a request
/// handled there rents and returns without a lock (<see cref="AttachLoops"/>).
/// </summary>
internal sealed class BufferPool
{
    readonly Shard[] _shards;
    readonly int _capacity;
    public readonly int Size;
    int _created, _dropped;
    int _sharedOut; // Debug builds count the larger arrays out here; Release ones don't
    // The event loops': one stack per loop of the server they belong to, at most capacity / (2 x loops) arrays each, so
    // together half the capacity again however many loops there are. Only the loop's own thread touches its stack; a
    // rent or return on any other thread goes to the shards, as does what a full stack can't take.
    HttpServer? _server;
    LoopStack[] _loops = [];
    int _loopRents, _loopReturns, _sharedReturns; // Debug builds count here; Release ones don't

    public BufferPool(int size, int capacity)
    {
        Size = size;
        _capacity = capacity;
        int n = Math.Max(1, Math.Min(Environment.ProcessorCount, 64));
        _shards = new Shard[n];
        for (int i = 0; i < n; i++) _shards[i] = new Shard((capacity + n - 1) / n);
    }

    /// <summary>Gives each of <paramref name="server"/>'s <paramref name="loops"/> event loops a stack of its own. Before
    /// the loops start.</summary>
    public void AttachLoops(HttpServer server, int loops)
    {
        int each = _capacity / (2 * loops);
        if (each == 0) return;
        var stacks = new LoopStack[loops];
        for (int i = 0; i < loops; i++) stacks[i] = new LoopStack(each);
        _loops = stacks;
        _server = server;
    }

    /// <summary>The stack of the loop whose thread this is, if it is one of this pool's server.</summary>
    LoopStack? Local() => _server is { } s && EventLoop.Current is { } loop && loop.Server == s ? _loops[loop.Index] : null;

    public byte[] Rent()
    {
        if (Local()?.TryPop() is { } own)
        {
            Count(ref _loopRents);
            PoolCheck.Rented(own, filled: true);
            return own;
        }
        int home = Thread.GetCurrentProcessorId() % _shards.Length;
        for (int i = 0; i < _shards.Length; i++)
        {
            byte[]? a = _shards[(home + i) % _shards.Length].TryPop();
            if (a is not null)
            {
                PoolCheck.Rented(a, filled: true);
                return a;
            }
        }
        Interlocked.Increment(ref _created);
        return GC.AllocateUninitializedArray<byte>(Size);
    }

    /// <summary>An array of at least <paramref name="length"/> bytes from ArrayPool.Shared, for one larger than <see cref="Size"/>.</summary>
    public byte[] RentLarger(int length)
    {
        byte[] a = ArrayPool<byte>.Shared.Rent(length);
        PoolCheck.Rented(a, filled: false);
        SharedRented(1);
        return a;
    }

    /// <summary>Arrays made so far; with <see cref="Available"/>, how many are out. For tests.</summary>
    internal int Created => Volatile.Read(ref _created);

    internal int Available
    {
        get
        {
            int n = 0;
            foreach (var s in _shards) n += s.Count;
            foreach (var l in _loops) n += l.Count;
            return n;
        }
    }

    /// <summary>Arrays rented and not given back (exact once nothing is renting or returning). In Debug builds the
    /// larger arrays from ArrayPool.Shared count too. For tests.</summary>
    internal int Out
    {
        get => Created - Available - Volatile.Read(ref _dropped) + Volatile.Read(ref _sharedOut);
    }

    /// <summary>Takes back an array from <see cref="Rent"/>, or one of another size to ArrayPool.Shared.</summary>
    public void Return(byte[] a)
    {
        PoolCheck.Returning(a);
        if (a.Length != Size)
        {
            SharedRented(-1);
            // ArrayPool.Shared lends it to any code in the process next: what a request held (a token, a password)
            // mustn't go with it. Debug builds filled it with PoolCheck.Fill already, which a clear would undo.
            ArrayPool<byte>.Shared.Return(a, clearArray: !PoolCheck.Fills);
            return;
        }
        if (Local() is { } own && own.TryPush(a))
        {
            Count(ref _loopReturns);
            return;
        }
        Count(ref _sharedReturns);
        int home = Thread.GetCurrentProcessorId() % _shards.Length;
        for (int i = 0; i < _shards.Length; i++)
            if (_shards[(home + i) % _shards.Length].TryPush(a)) return;
        Interlocked.Increment(ref _dropped); // every shard is full: the GC takes it
    }

    [Conditional("DEBUG")]
    void SharedRented(int n) => Interlocked.Add(ref _sharedOut, n);

    [Conditional("DEBUG")]
    static void Count(ref int counter) => Interlocked.Increment(ref counter);

    /// <summary>Debug builds: rents served by a loop's own stack, returns it took, and returns of this size that went
    /// to the shards. For tests.</summary>
    internal int LoopRents => Volatile.Read(ref _loopRents);
    internal int LoopReturns => Volatile.Read(ref _loopReturns);
    internal int SharedReturns => Volatile.Read(ref _sharedReturns);

    /// <summary>What loop <paramref name="loop"/>'s own stack holds now. For tests.</summary>
    internal int LoopAvailable(int loop) => loop < _loops.Length ? _loops[loop].Count : 0;

    /// <summary>One loop's arrays: no lock, since only that loop's thread pushes and pops. The count is published for
    /// tests that read it from elsewhere.</summary>
    sealed class LoopStack(int capacity)
    {
        readonly byte[]?[] _items = new byte[capacity][];
        int _count;

        public int Count => Volatile.Read(ref _count);

        public byte[]? TryPop()
        {
            if (_count == 0) return null;
            byte[]? a = _items[--_count];
            _items[_count] = null;
            Volatile.Write(ref _count, _count);
            return a;
        }

        public bool TryPush(byte[] a)
        {
            if (_count == _items.Length) return false;
            _items[_count] = a;
            Volatile.Write(ref _count, _count + 1);
            return true;
        }
    }

    sealed class Shard(int capacity)
    {
        readonly byte[]?[] _items = new byte[capacity][];
        readonly Lock _lock = new();
        int _count;

        public int Count => Volatile.Read(ref _count);

        public byte[]? TryPop()
        {
            if (Volatile.Read(ref _count) == 0) return null;
            lock (_lock)
            {
                if (_count == 0) return null;
                byte[]? a = _items[--_count];
                _items[_count] = null;
                return a;
            }
        }

        public bool TryPush(byte[] a)
        {
            if (Volatile.Read(ref _count) == _items.Length) return false;
            lock (_lock)
            {
                if (_count == _items.Length) return false;
                _items[_count++] = a;
                return true;
            }
        }
    }
}

/// <summary>
/// Debug builds only (the calls compile away in Release): an array given back to a pool is filled with
/// <see cref="Fill"/>, so code still reading it sees garbage instead of the next request's bytes; giving it back a
/// second time throws; and one written to while it sat in a <see cref="BufferPool"/> throws when it is rented again.
/// </summary>
internal static class PoolCheck
{
    public const byte Fill = 0xDD;
#if DEBUG
    /// <summary>Whether <see cref="Returning"/> fills what it is given.</summary>
    public const bool Fills = true;
#else
    /// <summary>Whether <see cref="Returning"/> fills what it is given.</summary>
    public const bool Fills = false;
#endif
#if DEBUG
    /// <summary>Arrays given back and not rented since. Weak: one the pool drops goes with the GC.</summary>
    static readonly ConditionalWeakTable<byte[], object> Returned = new();
    static readonly object Marker = new();
#endif

    [Conditional("DEBUG")]
    public static void Returning(byte[] a)
    {
#if DEBUG
        if (!Returned.TryAdd(a, Marker)) throw new InvalidOperationException("a pooled buffer was returned twice");
        a.AsSpan().Fill(Fill);
#endif
    }

    /// <param name="a">The array rented.</param>
    /// <param name="filled">It comes from a pool of ours, where nothing may write to it: it must hold only
    /// <see cref="Fill"/>. (ArrayPool.Shared lends its arrays to others too.)</param>
    [Conditional("DEBUG")]
    public static void Rented(byte[] a, bool filled)
    {
#if DEBUG
        Returned.Remove(a);
        if (filled && a.AsSpan().IndexOfAnyExcept(Fill) >= 0) throw new InvalidOperationException("a pooled buffer was written to after its return");
#endif
    }
}
