using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.Data;

/// <summary>What a result is, for the shared reply code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum ResultKind : byte
{
    /// <summary>The first row, or a reply for none.</summary>
    One,
    /// <summary>A list.</summary>
    Many,
    /// <summary>A page with its count.</summary>
    Page,
    /// <summary>A write that gives back nothing: 204.</summary>
    Write,
}

/// <summary>
/// One compiled query's run-time half, generated: how its command runs with each kind of reader, and the JSON
/// around its rows. A result holds the query's one instance, so a reply makes one virtual call per step and no
/// delegate or allocation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class QueryOps
{
    /// <summary>The query method's name, for <see cref="DataSource.OnFailure"/>.</summary>
    public abstract string Name { get; }

    /// <summary>What the result is.</summary>
    public abstract ResultKind Kind { get; }

    /// <summary>Whether the command runs on the writer.</summary>
    public abstract bool IsWrite { get; }

    /// <summary>For a list: whether no rows at all means its source row is missing (one row with a NULL key is an
    /// empty list).</summary>
    public virtual bool HasSource => false;

    /// <summary>The status a successful reply sends.</summary>
    public virtual int Status => Kind == ResultKind.Write ? 204 : 200;

    /// <summary>Runs the command with the reader that writes each row into <see cref="RowState.Json"/>.</summary>
    public abstract ValueTask<DataResult> RunJson(in PendingCommand command, RowState state, CancellationToken aborted);

    /// <summary>Runs the command with the reader that copies its first row into <paramref name="state"/>'s arena.</summary>
    public abstract ValueTask<DataResult> RunCopy(in PendingCommand command, RowState state, CancellationToken aborted);

    /// <summary>Gives the command back unrun.</summary>
    public abstract void Discard(in PendingCommand command);

    /// <summary>Writes what comes before the rows: an envelope, a list's start.</summary>
    public virtual void Begin(Utf8JsonWriter json) { }

    /// <summary>Writes what comes after the rows of a successful run: a list's end, a page's count.</summary>
    public virtual void End(Utf8JsonWriter json, in DataResult result) { }
}

/// <summary>
/// What a generated reader works with: the JSON writer of the reply in progress, or the arena an awaited row is
/// copied into and the place its value goes. Each context keeps its own, so a run costs nothing.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class RowState
{
    private protected RowState() { }

    /// <summary>The reply's JSON writer (a reply's run).</summary>
    public Utf8JsonWriter Json { get; private protected set; } = null!;

    /// <summary>The request's arena (an awaited run).</summary>
    public IRequestArena Arena { get; private protected set; } = null!;

    /// <summary>Rows a reader has seen in this run: a result of one row reads only the first.</summary>
    public int Seen { get; set; }

    /// <summary>What the reader returned for the first row, returned again for any later one.</summary>
    public long First { get; set; }

    /// <summary>A reader's return for the last row: it was found, but the write's condition didn't hold.</summary>
    public const long Refused = long.MinValue;

    /// <summary>Gives a lent list back to <paramref name="context"/> after its request.</summary>
    internal virtual void ReturnTo(DataContext context) { }

    private protected void StartRun()
    {
        Seen = 0;
        First = 0;
    }
}

/// <summary>Where an awaited row of <typeparamref name="T"/> goes: set by the generated reader, on the provider's thread.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RowSlot<T> : RowState where T : struct
{
    internal RowSlot<T>? Next;
    T _value;
    bool _has;

    internal RowSlot() { }

    /// <summary>Whether a row was copied already: only the first counts.</summary>
    public bool HasValue => _has;

    /// <summary>The row kept, without letting go of the slot (a lent one's).</summary>
    internal T Value => _value;

    /// <summary>Keeps the row's value; its text is in <see cref="RowState.Arena"/>.</summary>
    public void Set(T value)
    {
        _value = value;
        _has = true;
    }

    internal void Start(IRequestArena arena)
    {
        StartRun();
        Arena = arena;
        _has = false;
        _value = default;
    }

    internal T Take()
    {
        T value = _value;
        _value = default;
        Arena = null!;
        return value;
    }

    /// <summary>The slot belongs to the Each pool (see <c>EachPool</c>), not to a context.</summary>
    internal bool Pooled;
}


/// <summary>
/// Where the rows of an awaited list of <typeparamref name="T"/> go: added by the generated reader, on the provider's
/// thread. The context keeps it, and its array, for the next list of the same type once the request has ended.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RowList<T> : RowState
{
    // a list this long is let go after its request rather than kept for the context's next list
    const int KeptLength = 1024;

    internal RowList<T>? Next;
    T[] _items = [];
    int _count;

    internal RowList() { }

    internal T[] Items => _items;

    internal int Count => _count;

    /// <summary>Whether the awaiting handler took the list: only then can the context use it again.</summary>
    internal bool Taken;

    /// <summary>Adds a row; its text is in <see cref="RowState.Arena"/>.</summary>
    public void Add(T value)
    {
        if (_count == _items.Length) Grow();
        _items[_count++] = value;
    }

    void Grow() => Array.Resize(ref _items, Math.Max(16, _items.Length * 2));

    internal void Start(IRequestArena arena)
    {
        StartRun();
        Arena = arena;
        _count = 0;
        Taken = false;
    }

    /// <summary>The list belongs to the Each pool (see <c>EachPool</c>), not to a context.</summary>
    internal bool Pooled;

    /// <summary>Lets go of the array, which the pool can't keep.</summary>
    internal void LetGoOfItems() => _items = [];

    internal override void ReturnTo(DataContext context) => context.ReturnLentList(this);

    /// <summary>Bumped each time the list is let go, so a Debug build's <see cref="Rows{T}"/> sees it was used after.</summary>
    internal int Lease;

    /// <summary>Lets go of the rows: their text pointed into an arena that has been reset.</summary>
    internal void Clear()
    {
        Lease++;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_items, 0, _count);
        if (_items.Length > KeptLength) _items = [];
        _count = 0;
        Arena = null!;
    }
}
