using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.Data;

// The seams between the generated queries and a database provider. Generated code is written against these as
// generic constraints and is instantiated with the provider's own struct types, so the JIT compiles it for that
// provider alone: a column read is a direct call into the provider's row, not an interface call.

/// <summary>The type of a statement's parameter, declared when the statement is made.</summary>
public enum DataType : byte
{
    /// <summary>A 64-bit integer (Postgres bigint).</summary>
    Int64,
    /// <summary>A 32-bit integer (Postgres integer).</summary>
    Int32,
    /// <summary>A bool.</summary>
    Boolean,
    /// <summary>A double (Postgres double precision).</summary>
    Double,
    /// <summary>Text, sent as UTF-8.</summary>
    Text,
    /// <summary>An instant to the microsecond (Postgres timestamptz).</summary>
    Timestamp,
    /// <summary>A one-dimensional array of text.</summary>
    TextArray,
}

/// <summary>
/// One row of a result, read in place: a provider's <c>ref struct</c> over its receive buffer. It is valid only
/// during the call it is passed to. The generated readers take it as a generic <c>TRow</c>, so each call below is a
/// direct call into the provider's struct.
/// </summary>
public interface IDataRow
{
    /// <summary>How many columns the row has.</summary>
    int FieldCount { get; }

    /// <summary>Whether column <paramref name="c"/> (from 0) is NULL.</summary>
    bool IsNull(int c);

    /// <summary>A text, JSON or binary column's bytes (text as UTF-8). Throws for NULL.</summary>
    ReadOnlySpan<byte> GetBytes(int c);

    /// <summary>A JSON column's text, as UTF-8: what the provider's format puts around it left out (Postgres's binary
    /// jsonb starts with a version byte). Throws for NULL.</summary>
    ReadOnlySpan<byte> GetJson(int c);

    /// <summary>An integer column of any width.</summary>
    long GetInt64(int c);

    /// <summary>A bool column.</summary>
    bool GetBoolean(int c);

    /// <summary>A floating-point column.</summary>
    double GetDouble(int c);

    /// <summary>A timestamp column, to the microsecond.</summary>
    Timestamp GetTimestamp(int c);
}

/// <summary>
/// Builds one command: a statement's parameters in order, then any further statements with theirs. A provider's
/// <c>ref struct</c>; generated code binds through it as a generic <c>TCommand</c>. Values are copied as they are
/// added, so spans into a request's buffer will do.
/// </summary>
/// <typeparam name="TStatement">The provider's prepared statement.</typeparam>
public interface IDataCommand<TStatement>
{
    /// <summary>A 64-bit integer.</summary>
    void Int64(long value);

    /// <summary>A 32-bit integer.</summary>
    void Int32(int value);

    /// <summary>A bool.</summary>
    void Boolean(bool value);

    /// <summary>A double.</summary>
    void Double(double value);

    /// <summary>Text from its UTF-8 bytes.</summary>
    void Text(ReadOnlySpan<byte> utf8);

    /// <summary>Text from UTF-16, encoded as it is copied.</summary>
    void Text(ReadOnlySpan<char> text);

    /// <summary>An instant.</summary>
    void Timestamp(Timestamp value);

    /// <summary>NULL, for a parameter of any type.</summary>
    void Null();

    /// <summary>Starts a text array; its elements follow.</summary>
    void BeginTextArray();

    /// <summary>One element of the text array.</summary>
    void Element(ReadOnlySpan<byte> utf8);

    /// <summary>Ends the text array.</summary>
    void EndTextArray();

    /// <summary>Another statement in the same command, run in the same unit; its parameters follow.</summary>
    void Then(TStatement statement);

    /// <summary>Ends the command without running it: it runs later, once the caller knows how its rows are read.</summary>
    PendingCommand Hold();

    /// <summary>Gives the command back unrun, when computing a value threw while it was bound (a list read as its
    /// elements are bound); nothing when a call above threw, which gave it back already.</summary>
    void Discard();
}

/// <summary>
/// What a generated reader does with each row: writes it into a response's JSON, or copies it into a request's
/// arena. Static, so the provider calls it through a generic type parameter with no delegate or interface call of
/// its own. What it returns for the last row is <see cref="DataResult.Value"/>.
/// </summary>
public interface IRowReader
{
    /// <summary>Reads one row; <paramref name="state"/> is what the run was given.</summary>
    static abstract long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct;
}

/// <summary>Marks a provider type, such as <c>CodeFusion.Data.Postgres.Postgres</c>, for <see cref="DataContext{TProvider}"/>.</summary>
// Empty on purpose: DataContext<TProvider> names it in its constraint, which an attribute can't be.
#pragma warning disable CA1040
public interface IDataProvider;
#pragma warning restore CA1040

/// <summary>
/// A database provider: a struct whose static members the generated queries call. The generator reads its
/// <see cref="SqlDialectAttribute"/> to write each query's SQL at build time.
/// </summary>
/// <typeparam name="TSelf">The provider struct.</typeparam>
/// <typeparam name="TStatement">Its prepared statement, made once per query.</typeparam>
/// <typeparam name="TCommand">Its command builder.</typeparam>
public interface IDataProvider<TSelf, TStatement, TCommand> : IDataProvider
    where TSelf : struct, IDataProvider<TSelf, TStatement, TCommand>
    where TStatement : class
    where TCommand : IDataCommand<TStatement>, allows ref struct
{
    /// <summary>A statement of <paramref name="sql"/> taking <paramref name="parameters"/>, made once (a static field).</summary>
    static abstract TStatement Statement(string sql, params ReadOnlySpan<DataType> parameters);

    /// <summary>A command that only reads, on <paramref name="context"/>'s source.</summary>
    static abstract TCommand Read(DataContext context, TStatement statement);

    /// <summary>A command that writes, on <paramref name="context"/>'s source: its statements commit together.</summary>
    static abstract TCommand Write(DataContext context, TStatement statement);

    /// <summary>
    /// Runs a held command, handing each row of its last statement to <typeparamref name="TReader"/> with
    /// <paramref name="state"/>. Completes once the rows have been read (a write: once it committed), on whatever
    /// thread the provider completes on. If <paramref name="aborted"/> fires before the command is sent, it ends
    /// <see cref="DataStatus.Canceled"/> without running. Await the task exactly once.
    /// </summary>
    static abstract ValueTask<DataResult> Run<TReader>(in PendingCommand command, object? state, CancellationToken aborted) where TReader : IRowReader;

    /// <summary>Gives back a held command that will not run.</summary>
    static abstract void Discard(in PendingCommand command);
}

/// <summary>A command built and held by a provider (<see cref="IDataCommand{TStatement}.Hold"/>) until it runs.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct PendingCommand
{
    /// <summary>What the provider holds for it.</summary>
    public PendingCommand(object owner, object job, int token)
    {
        Owner = owner;
        Job = job;
        Token = token;
    }

    /// <summary>The provider object that runs it.</summary>
    public object? Owner { get; }

    /// <summary>The command itself.</summary>
    public object? Job { get; }

    /// <summary>Which use of <see cref="Job"/> this is (jobs are pooled).</summary>
    public int Token { get; }

    /// <summary>No command.</summary>
    public bool IsEmpty => Job is null;
}

/// <summary>How a command ended. Each failure a client can cause is a status, not an exception.</summary>
public enum DataStatus : byte
{
    /// <summary>It ran (and a write committed).</summary>
    Ok,
    /// <summary>Too many commands were waiting: refused at once, nothing ran.</summary>
    Busy,
    /// <summary>The request was aborted before the command was sent: nothing ran.</summary>
    Canceled,
    /// <summary>A row lock wasn't granted in time (Postgres 55P03). Nothing was written.</summary>
    LockTimeout,
    /// <summary>The statement ran too long and was stopped (Postgres 57014). Nothing was written.</summary>
    StatementTimeout,
    /// <summary>A unique key already held the value (Postgres 23505). Nothing was written.</summary>
    UniqueViolation,
    /// <summary>The connection broke or couldn't be opened. A write's outcome is unknown.</summary>
    Broken,
    /// <summary>The source is stopped and takes no more commands.</summary>
    Stopped,
    /// <summary>The database refused the statement for another reason: <see cref="DataResult.Error"/> says why.</summary>
    Failed,
    /// <summary>Reading a row failed (a column of the wrong type): <see cref="DataResult.Error"/>. A write committed all the same.</summary>
    ReaderFailed,
}

/// <summary>How a command ended, with what its reader returned for the last row.</summary>
public readonly struct DataResult
{
    /// <summary>A result as a provider reports it.</summary>
    public DataResult(DataStatus status, int rows = 0, long value = 0, Exception? error = null, string? nativeCode = null)
    {
        Status = status;
        Rows = rows;
        Value = value;
        Error = error;
        NativeCode = nativeCode;
    }

    /// <summary>How it ended.</summary>
    public DataStatus Status { get; }

    /// <summary>Rows of the last statement.</summary>
    public int Rows { get; }

    /// <summary>What the reader returned for the last row; 0 without rows.</summary>
    public long Value { get; }

    /// <summary>The database's error, the reader's exception, or why the connection broke; null when it ran.</summary>
    public Exception? Error { get; }

    /// <summary>The database's own code for the error, such as a SQLSTATE; null without one.</summary>
    public string? NativeCode { get; }

    /// <summary>It ran.</summary>
    public bool IsOk => Status == DataStatus.Ok;

    /// <summary>A result that ended at once with <paramref name="status"/>, nothing ran.</summary>
    public static DataResult Of(DataStatus status) => new(status);

    /// <summary>The status, rows and value, for logs and test failures.</summary>
    public override string ToString() => $"{Status} rows={Rows} value={Value}{(Error is null ? "" : " " + Error.Message)}";
}

/// <summary>
/// A provider's connections and loops: what a context's commands run on. Made once per database and shared by every
/// context; the provider package makes it (<c>AddDatabase</c>).
/// </summary>
public abstract class DataSource
{
    /// <summary>The replies for failures and empty results; the defaults use the RealWorld spec's error shape.</summary>
    public DataErrors Errors { get; init; } = DataErrors.Default;

    /// <summary>Called on the provider's thread for each command that failed for a reason a client can't cause
    /// (<see cref="DataStatus.Broken"/>, <see cref="DataStatus.Failed"/>, <see cref="DataStatus.ReaderFailed"/>),
    /// with the query's name. Keep it quick: it runs on the provider's loop.</summary>
    public Action<string, DataResult>? OnFailure { get; init; }

    /// <summary>A new context of type <typeparamref name="TContext"/> on this source.</summary>
    public TContext CreateContext<TContext>() where TContext : DataContext, new()
    {
        var context = new TContext();
        context.Attach(this);
        return context;
    }

    /// <summary>Takes no more commands; the loops finish what has queued and close their connections.</summary>
    public abstract void Stop();
}
