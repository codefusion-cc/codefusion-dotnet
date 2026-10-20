using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CodeFusion.Data;

// What a query method returns. Each is a reply (IReply) that runs its command when it is written: the rows go from
// the provider's receive buffer straight into the response's JSON. One and Write can be awaited instead, for the
// first row copied into the request's arena. A result runs once, in the request that made it.

/// <summary>The query a result runs: its context, its held command, the compiled query, and the replies it was given.</summary>
readonly struct QueryCall
{
    readonly DataContext? _context;
    readonly PendingCommand _command;
    readonly QueryOps? _ops;
    public readonly ReplyOptions Options;

    public QueryCall(DataContext context, in PendingCommand command, QueryOps ops)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ops);
        _context = context;
        _command = command;
        _ops = ops;
        context.Hold(command, ops);
    }

    QueryCall(in QueryCall from, in ReplyOptions options)
    {
        _context = from._context;
        _command = from._command;
        _ops = from._ops;
        Options = options;
    }

    /// <summary>The same call with other replies.</summary>
    public QueryCall With(in ReplyOptions options) => new(this, options);

    DataContext Context => _context ?? throw new InvalidOperationException("a default result has no query");

    public ValueTask Reply(IResponse response) => Context.Reply(response, _command, _ops!, Options);

    public RowAwaiter<T> Await<T>() where T : struct => Context.Await<T>(_command, _ops!, Options);

    public ValueTask<DataResult> Run() => Context.Run(_command, _ops!);

    public static int CheckStatus(int status) => status is >= 100 and <= 999 ? status : throw new ArgumentOutOfRangeException(nameof(status));
}

/// <summary>
/// The first row of a query, or a reply for none (<see cref="OrNotFound"/>; 404 by default). As a reply it writes
/// the row as JSON, wrapped in the view's <see cref="JsonEnvelopeAttribute"/> if it has one. Awaited, it gives the
/// row as a <see cref="Row{T}"/> whose text lives in the request's arena.
/// </summary>
public readonly struct One<T> : IReply where T : struct
{
    readonly QueryCall _call;

    /// <summary>For generated code: the result of a held command.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public One(DataContext context, in PendingCommand command, QueryOps ops) => _call = new QueryCall(context, command, ops);

    One(in QueryCall call) => _call = call;

    /// <summary>Replies <paramref name="reply"/> when there is no row.</summary>
    public One<T> OrNotFound(ErrorReply reply) => new(_call.With(_call.Options with { Empty = reply }));

    /// <summary>Replies <paramref name="reply"/> when a conditional write found its row but its condition didn't hold.</summary>
    public One<T> OrForbidden(ErrorReply reply) => new(_call.With(_call.Options with { Refused = reply }));

    /// <summary>Replies <paramref name="reply"/> when a unique key already held a value.</summary>
    public One<T> Conflict(ErrorReply reply) => new(_call.With(_call.Options with { Conflict = reply }));

    /// <summary>Sends <paramref name="status"/> with the row instead of 200.</summary>
    public One<T> WithStatus(int status) => new(_call.With(_call.Options with { Status = QueryCall.CheckStatus(status) }));

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse => _call.Reply(response);

    /// <summary>Runs the query for its row, copied into the bound request's arena.</summary>
    public RowAwaiter<T> GetAwaiter() => _call.Await<T>();
}

/// <summary>
/// Every row of a query as a JSON array, or, with <see cref="JsonListAttribute"/>, an object holding it. A list of
/// a row's children (<c>SelectMany</c>) tells a missing row from an empty list: <see cref="OrNotFound"/> answers the
/// first.
/// </summary>
public readonly struct Many<T> : IReply
{
    readonly QueryCall _call;

    /// <summary>For generated code: the result of a held command.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Many(DataContext context, in PendingCommand command, QueryOps ops) => _call = new QueryCall(context, command, ops);

    Many(in QueryCall call) => _call = call;

    /// <summary>Replies <paramref name="reply"/> when the row whose children these are is missing.</summary>
    public Many<T> OrNotFound(ErrorReply reply) => new(_call.With(_call.Options with { Empty = reply }));

    /// <summary>Sends <paramref name="status"/> with the list instead of 200.</summary>
    public Many<T> WithStatus(int status) => new(_call.With(_call.Options with { Status = QueryCall.CheckStatus(status) }));

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse => _call.Reply(response);

    // ---------- inside query definitions, as an entity's navigation ----------

    /// <summary>Whether there is a row (<c>EXISTS</c>).</summary>
    [QueryDefinition] public bool Any() => Definition.Never<bool>();

    /// <summary>Whether <paramref name="predicate"/> holds for a row (<c>EXISTS</c>).</summary>
    [QueryDefinition] public bool Any(Func<T, bool> predicate) => Definition.Never<bool>();

    /// <summary>How many rows there are (a correlated <c>count(*)</c>).</summary>
    [QueryDefinition] public long Count() => Definition.Never<long>();

    /// <summary>How many rows <paramref name="predicate"/> holds for.</summary>
    [QueryDefinition] public long Count(Func<T, bool> predicate) => Definition.Never<long>();

    /// <summary>The rows <paramref name="predicate"/> holds for.</summary>
    [QueryDefinition] public Many<T> Where(Func<T, bool> predicate) => Definition.Never<Many<T>>();

    /// <summary>Each row as <typeparamref name="TResult"/>.</summary>
    [QueryDefinition] public Many<TResult> Select<TResult>(Func<T, TResult> selector) => Definition.Never<Many<TResult>>();

    /// <summary>Ordered by <paramref name="key"/>, ascending.</summary>
    [QueryDefinition] public Many<T> OrderBy<TKey>(Func<T, TKey> key) => Definition.Never<Many<T>>();

    /// <summary>Ordered by <paramref name="key"/>, descending.</summary>
    [QueryDefinition] public Many<T> OrderByDescending<TKey>(Func<T, TKey> key) => Definition.Never<Many<T>>();

    /// <summary>Then by <paramref name="key"/>, ascending.</summary>
    [QueryDefinition] public Many<T> ThenBy<TKey>(Func<T, TKey> key) => Definition.Never<Many<T>>();

    /// <summary>Then by <paramref name="key"/>, descending.</summary>
    [QueryDefinition] public Many<T> ThenByDescending<TKey>(Func<T, TKey> key) => Definition.Never<Many<T>>();

    /// <summary>At most <paramref name="count"/> rows (none for a negative count). Where, OrderBy, and Any or Count
    /// with a predicate can't follow it, as SQL would apply them first.</summary>
    [QueryDefinition] public Many<T> Take(long count) => Definition.Never<Many<T>>();

    /// <summary>The one selected value of every row as one JSON array, <c>[]</c> without rows.</summary>
    [QueryDefinition] public RawJson AsJson() => Definition.Never<RawJson>();
}

/// <summary>
/// One page of a list and the list's length up to its cap: as a reply, <c>{"items": [...], "count": n}</c>, or the
/// names of the view's <see cref="JsonListAttribute"/>.
/// </summary>
public readonly struct Page<T> : IReply where T : struct
{
    readonly QueryCall _call;

    /// <summary>For generated code: the result of a held command.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Page(DataContext context, in PendingCommand command, QueryOps ops) => _call = new QueryCall(context, command, ops);

    Page(in QueryCall call) => _call = call;

    /// <summary>Sends <paramref name="status"/> with the page instead of 200.</summary>
    public Page<T> WithStatus(int status) => new(_call.With(_call.Options with { Status = QueryCall.CheckStatus(status) }));

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse => _call.Reply(response);
}

/// <summary>
/// A write and the row it gives back, run on the writer in a group commit: the reply goes out once it committed.
/// A unique violation is <see cref="Conflict"/>'s reply (409 by default); a conditional update that found its row
/// but whose condition didn't hold is <see cref="OrForbidden"/>'s.
/// </summary>
public readonly struct Write<T> : IReply where T : struct
{
    readonly QueryCall _call;

    /// <summary>For generated code: the result of a held command.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Write(DataContext context, in PendingCommand command, QueryOps ops) => _call = new QueryCall(context, command, ops);

    Write(in QueryCall call) => _call = call;

    /// <summary>Replies <paramref name="reply"/> when the write found no row.</summary>
    public Write<T> OrNotFound(ErrorReply reply) => new(_call.With(_call.Options with { Empty = reply }));

    /// <summary>Replies <paramref name="reply"/> when the write found its row but its condition didn't hold.</summary>
    public Write<T> OrForbidden(ErrorReply reply) => new(_call.With(_call.Options with { Refused = reply }));

    /// <summary>Replies <paramref name="reply"/> when a unique key already held a value.</summary>
    public Write<T> Conflict(ErrorReply reply) => new(_call.With(_call.Options with { Conflict = reply }));

    /// <summary>Sends <paramref name="status"/> with the row instead of 200: <c>WithStatus(201)</c> for a create.</summary>
    public Write<T> WithStatus(int status) => new(_call.With(_call.Options with { Status = QueryCall.CheckStatus(status) }));

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse => _call.Reply(response);

    /// <summary>Runs the write for its row, copied into the bound request's arena once the write committed.</summary>
    public RowAwaiter<T> GetAwaiter() => _call.Await<T>();
}

/// <summary>A write that gives back nothing: as a reply, 204 once it committed; awaited, its <see cref="DataResult"/>.</summary>
public readonly struct Write : IReply
{
    readonly QueryCall _call;

    /// <summary>For generated code: the result of a held command.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Write(DataContext context, in PendingCommand command, QueryOps ops) => _call = new QueryCall(context, command, ops);

    Write(in QueryCall call) => _call = call;

    /// <summary>Replies <paramref name="reply"/> when a unique key already held a value.</summary>
    public Write Conflict(ErrorReply reply) => new(_call.With(_call.Options with { Conflict = reply }));

    /// <summary>Sends <paramref name="status"/> instead of 204.</summary>
    public Write WithStatus(int status) => new(_call.With(_call.Options with { Status = QueryCall.CheckStatus(status) }));

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse => _call.Reply(response);

    /// <summary>Runs the write for its outcome.</summary>
#pragma warning disable CA2012 // the awaiter is the one consumer of the run
    public ValueTaskAwaiter<DataResult> GetAwaiter() => _call.Run().GetAwaiter();
#pragma warning restore CA2012

    // Chained on the instance in definitions; they never run.
#pragma warning disable CA1822
    /// <summary>Another write in the same unit (query definitions only).</summary>
    [QueryDefinition] public Write Then(Write next) => Definition.Never<Write>();

    /// <summary>Another write in the same unit, only when <paramref name="condition"/> holds (query definitions only).</summary>
    [QueryDefinition] public Write ThenIf(bool condition, Write next) => Definition.Never<Write>();
#pragma warning restore CA1822
}

/// <summary>
/// An awaited result: its status, and its first row when there was one. The row's text lives in the request's
/// arena, valid until the response has been sent. Converts to <c>T?</c>: null without a row.
/// </summary>
public readonly struct Row<T> where T : struct
{
    readonly T _value;

    internal Row(DataStatus status, bool found, bool refused, T value, ErrorReply? error)
    {
        Status = status;
        Found = found;
        Refused = refused;
        _value = value;
        Error = error;
    }

    /// <summary>How the command ended.</summary>
    public DataStatus Status { get; }

    /// <summary>The command didn't run to its end: <see cref="Error"/> is the reply for it.</summary>
    public bool Failed => Status != DataStatus.Ok;

    /// <summary>There is a row (and a conditional write's condition held).</summary>
    public bool Found { get; }

    /// <summary>A conditional write found its row, but its condition didn't hold.</summary>
    public bool Refused { get; }

    /// <summary>The row. Throws without one.</summary>
    public T Value => Found ? _value : throw new InvalidOperationException($"no row: {Status}{(Refused ? ", refused" : "")}");

    /// <summary>The row, or null.</summary>
    public T? OrNull => Found ? _value : null;

    /// <summary>The reply for a failure (503 when busy or timed out, the context's conflict reply for a unique
    /// violation, 500 otherwise); null when the command ran.</summary>
    public ErrorReply? Error { get; }

    /// <summary>The row, if there is one.</summary>
    public bool TryGet(out T value)
    {
        value = _value;
        return Found;
    }

    /// <summary>The row, or null.</summary>
    public static implicit operator T?(Row<T> row) => row.OrNull;

    /// <summary>The row, or null.</summary>
    public T? ToNullable() => OrNull;
}

/// <summary>Awaits a <see cref="One{T}"/> or <see cref="Write{T}"/>.</summary>
public readonly struct RowAwaiter<T> : ICriticalNotifyCompletion where T : struct
{
    readonly DataContext _context;
    readonly RowSlot<T> _slot;
    readonly QueryOps _ops;
    readonly ReplyOptions _options;
    readonly ValueTask<DataResult> _run;

    internal RowAwaiter(DataContext context, RowSlot<T> slot, QueryOps ops, in ReplyOptions options, ValueTask<DataResult> run)
    {
        _context = context;
        _slot = slot;
        _ops = ops;
        _options = options;
        _run = run;
    }

    /// <summary>Whether the row is there already.</summary>
    public bool IsCompleted => _run.IsCompleted;

    /// <inheritdoc/>
    public void OnCompleted(Action continuation) => _run.GetAwaiter().OnCompleted(continuation);

    /// <inheritdoc/>
    public void UnsafeOnCompleted(Action continuation) => _run.GetAwaiter().UnsafeOnCompleted(continuation);

    /// <summary>The outcome and the row.</summary>
    public Row<T> GetResult()
    {
        DataResult r;
        try
        {
            r = _run.GetAwaiter().GetResult();
        }
        catch
        {
            _slot.Take();
            _context.ReturnSlot(_slot);
            throw;
        }
        bool has = _slot.HasValue;
        T value = _slot.Take();
        _context.ReturnSlot(_slot);
        if (r.Status != DataStatus.Ok)
        {
            // as a reply reports it: not for a client that left
            if (r.Status is DataStatus.Broken or DataStatus.Failed or DataStatus.ReaderFailed && _context.Response?.Aborted.IsCancellationRequested != true)
                _context.Source.OnFailure?.Invoke(_ops.Name, r);
            return new Row<T>(r.Status, false, false, default, _context.Source.Errors.For(r.Status, _ops.IsWrite, _options.Conflict));
        }
        bool refused = r.Rows > 0 && r.Value == RowState.Refused;
        return new Row<T>(DataStatus.Ok, has && !refused, refused, has && !refused ? value : default, null);
    }
}
