using System;
using System.ComponentModel;
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
}
