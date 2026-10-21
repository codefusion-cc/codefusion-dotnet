using System;
using System.Threading;

namespace CodeFusion.Data;

/// <summary>
/// What an Each needs per read, pooled across every context of the process rather than kept per connection: its
/// slots here, its run (with its array of awaiters) in <see cref="RunPool{TRun}"/>, and its list in
/// <see cref="EachLists{T}"/>. A request of hundreds of reads takes them and gives them
/// back, so once warm it allocates nothing, and an idle connection holds none of them. Each pool is capped, so a peak
/// leaves at most that many behind.
/// </summary>
static class EachPool<T> where T : struct
{
    static readonly Lock Gate = new();
    static RowSlot<T>?[] _slots = new RowSlot<T>?[64];
    static int _slotCount;
    // a peak past this leaves no more than this many behind
    const int MaxSlots = 8192;

    internal static RowSlot<T> RentSlot()
    {
        lock (Gate)
            if (_slotCount > 0)
            {
                var slot = _slots[--_slotCount]!;
                _slots[_slotCount] = null;
                return slot;
            }
        return new RowSlot<T> { Pooled = true };
    }

    /// <summary>The slots of an Each's <paramref name="count"/> reads, under one lock:
    /// <paramref name="into"/>[0..count).</summary>
    internal static void RentSlots(RowSlot<T>?[] into, int count)
    {
        int taken;
        lock (Gate)
        {
            taken = Math.Min(count, _slotCount);
            _slotCount -= taken;
            Array.Copy(_slots, _slotCount, into, 0, taken);
            Array.Clear(_slots, _slotCount, taken);
        }
        for (int i = taken; i < count; i++) into[i] = new RowSlot<T> { Pooled = true };
    }

    /// <summary>An Each's slots back, under one lock; <paramref name="from"/> lets go of them.</summary>
    internal static void ReturnSlots(RowSlot<T>?[] from, int count)
    {
        lock (Gate)
        {
            int n = Math.Min(count, MaxSlots - _slotCount);
            if (_slotCount + n > _slots.Length) Array.Resize(ref _slots, Math.Min(Math.Max(_slots.Length * 2, _slotCount + n), MaxSlots));
            Array.Copy(from, 0, _slots, _slotCount, n);
            _slotCount += n;
        }
        Array.Clear(from, 0, count);
    }

    /// <summary>The slots pooled now. For tests.</summary>
    internal static int Slots
    {
        get { lock (Gate) return _slotCount; }
    }

}

/// <summary>What a pooled run keeps between Eaches: arrays sized for its largest Each so far.</summary>
interface IPooledRun
{
    /// <summary>The entries of its arrays past <see cref="RunPool{TRun}.SmallArray"/>, which the pool's budget
    /// counts.</summary>
    int LargeEntries { get; }

    /// <summary>Lets go of those arrays.</summary>
    void LetGoOfLargeArrays();
}

/// <summary>
/// An Each's runs (reads' or writes'), pooled across every context of the process, so a connection keeps none between
/// requests. A pooled run keeps arrays of up to <see cref="SmallArray"/> entries; larger ones only while all the
/// pool's together stay within <see cref="EntryBudget"/>, the least recently used letting go first. So a peak of
/// large Eaches leaves at most that much behind (some 600 KB for a small row), and a few large ones (about 8 of 500
/// reads) still allocate nothing once warm; more than that at once allocate their arrays anew.
/// </summary>
static class RunPool<TRun> where TRun : class, IPooledRun, new()
{
    internal const int SmallArray = 128, EntryBudget = 8192;
    const int MaxRuns = 256;
    static readonly Lock Gate = new();
    static TRun?[] _runs = new TRun?[8];
    static int _count;
    static int _keptEntries;

    internal static TRun Rent()
    {
        lock (Gate)
            if (_count > 0)
            {
                var run = _runs[--_count]!;
                _runs[_count] = null;
                _keptEntries -= run.LargeEntries;
                return run;
            }
        return new TRun();
    }

    internal static void Return(TRun run)
    {
        lock (Gate)
        {
            if (_count == MaxRuns) return;
            // over budget: the least recently used runs (the bottom) let go of theirs first, so the run in use keeps
            // its arrays
            int over = _keptEntries + run.LargeEntries - EntryBudget;
            for (int i = 0; i < _count && over > 0; i++)
            {
                var cold = _runs[i]!;
                int entries = cold.LargeEntries;
                if (entries == 0) continue;
                cold.LetGoOfLargeArrays();
                _keptEntries -= entries;
                over -= entries;
            }
            if (over > 0) run.LetGoOfLargeArrays();
            _keptEntries += run.LargeEntries;
            if (_count == _runs.Length) Array.Resize(ref _runs, Math.Min(_runs.Length * 2, MaxRuns));
            _runs[_count++] = run;
        }
    }

    /// <summary>The runs pooled now. For tests.</summary>
    internal static int Count
    {
        get { lock (Gate) return _count; }
    }

    /// <summary>The entries of the pooled runs' large arrays. For tests.</summary>
    internal static int KeptEntries
    {
        get { lock (Gate) return _keptEntries; }
    }
}

/// <summary>The lists of Eaches, pooled as <see cref="EachPool{T}"/>'s slots and <see cref="RunPool{TRun}"/>'s runs
/// are.</summary>
static class EachLists<T>
{
    static readonly Lock Gate = new();
    static RowList<T>?[] _lists = new RowList<T>?[8];
    static int _listCount;
    static int _keptItems;
    const int MaxLists = 256;

    /// <summary>As the runs' budget (<see cref="RunPool{TRun}"/>): a pooled list keeps a large array only while all
    /// the pool's together stay within this.</summary>
    internal const int ItemBudget = 8192;

    internal static RowList<T> Rent()
    {
        lock (Gate)
            if (_listCount > 0)
            {
                var list = _lists[--_listCount]!;
                _lists[_listCount] = null;
                _keptItems -= Large(list);
                return list;
            }
        return new RowList<T> { Pooled = true };
    }

    internal static void Return(RowList<T> list)
    {
        lock (Gate)
        {
            if (_listCount == MaxLists) return;
            // over budget: the least recently used lists let go of theirs first, as the runs do
            int over = _keptItems + Large(list) - ItemBudget;
            for (int i = 0; i < _listCount && over > 0; i++)
            {
                var cold = _lists[i]!;
                int items = Large(cold);
                if (items == 0) continue;
                cold.LetGoOfItems();
                _keptItems -= items;
                over -= items;
            }
            if (over > 0) list.LetGoOfItems();
            _keptItems += Large(list);
            if (_listCount == _lists.Length) Array.Resize(ref _lists, Math.Min(_lists.Length * 2, MaxLists));
            _lists[_listCount++] = list;
        }
    }

    static int Large(RowList<T> list) => list.Items.Length > RunPool<WriteEachRun>.SmallArray ? list.Items.Length : 0;

    /// <summary>The entries of the pooled lists' large arrays. For tests.</summary>
    internal static int KeptItems
    {
        get { lock (Gate) return _keptItems; }
    }
}

/// <summary>
/// The arena an Each's reads copy their rows' text into: its reads finish on whichever provider threads ran them, so
/// it takes a lock (only this Each's reads contend for it, and only for rows with text). Lent to the request until it
/// ends, then pooled across contexts; it takes no memory until a row has text, and keeps at most a few kilobytes
/// between requests.
/// </summary>
sealed class EachArena : RowState, IRequestArena
{
    static readonly Lock PoolGate = new();
    static EachArena?[] _pool = new EachArena?[8];
    static int _pooled;
    const int MaxPooled = 256, Kept = 4 << 10;

    readonly Lock _gate = new();
    byte[] _buffer = [];
    int _used;
    /// <summary>The Each gathered its rows: its reads write nothing more here.</summary>
    internal bool Gathered;

    internal static EachArena Rent()
    {
        EachArena? arena = null;
        lock (PoolGate)
            if (_pooled > 0)
            {
                arena = _pool[--_pooled];
                _pool[_pooled] = null;
            }
        arena ??= new EachArena();
        arena.Gathered = false;
        return arena;
    }

    public ArenaBytes Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_gate)
        {
            if (_buffer.Length - _used < length)
            {
                // a new buffer: what was handed out before stays valid in the old one
                _buffer = new byte[Math.Max(Math.Max(256, _buffer.Length * 2), length)];
                _used = 0;
            }
            var bytes = new ArenaBytes(_buffer, _used, length);
            _used += length;
            return bytes;
        }
    }

    /// <summary>The request ended: back to the pool, unless its Each was never awaited (its reads may still write).</summary>
    internal override void ReturnTo(DataContext context)
    {
        if (!Gathered) return;
        if (_buffer.Length > Kept) _buffer = [];
        _used = 0;
        lock (PoolGate)
        {
            if (_pooled == MaxPooled) return;
            if (_pooled == _pool.Length) Array.Resize(ref _pool, Math.Min(_pool.Length * 2, MaxPooled));
            _pool[_pooled++] = this;
        }
    }
}
