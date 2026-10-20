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
/// </summary>
internal sealed class BufferPool
{
    readonly Shard[] _shards;
    public readonly int Size;
    int _created, _dropped;
    int _sharedOut; // Debug builds count the larger arrays out here; Release ones don't

    public BufferPool(int size, int capacity)
    {
        Size = size;
        int n = Math.Max(1, Math.Min(Environment.ProcessorCount, 64));
        _shards = new Shard[n];
        for (int i = 0; i < n; i++) _shards[i] = new Shard((capacity + n - 1) / n);
    }

    public byte[] Rent()
    {
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
        int home = Thread.GetCurrentProcessorId() % _shards.Length;
        for (int i = 0; i < _shards.Length; i++)
            if (_shards[(home + i) % _shards.Length].TryPush(a)) return;
        Interlocked.Increment(ref _dropped); // every shard is full: the GC takes it
    }

    [Conditional("DEBUG")]
    void SharedRented(int n) => Interlocked.Add(ref _sharedOut, n);

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
