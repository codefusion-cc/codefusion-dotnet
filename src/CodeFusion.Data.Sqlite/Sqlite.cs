using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.Data.Sqlite;

/// <summary>
/// The SQLite provider for CodeFusion.Data: a context derives from <c>DataContext&lt;Sqlite&gt;</c>, the generator writes
/// its queries in SQLite's SQL, reads run on the caller's thread on <see cref="SqliteDataSource"/>'s reader connections,
/// and writes are group-committed by its <see cref="SqliteWriter"/>. Open a database with <see cref="Open(string)"/>
/// and register it with <see cref="SqliteServices.AddDatabase{TContext}"/>.
/// </summary>
[SqlDialect("sqlite",
    Parameter = "?{0}",
    Offset = "LIMIT -1 OFFSET {0}",
    LockRows = "",
    BigIntLiteral = "{0}",
    NullSafeEqual = "{0} IS {1}",
    InArray = "{0} IN (SELECT value FROM json_each({1}))",
    Now = "cf_now()",
    JsonArray = "(SELECT json_group_array({1}) FROM ({0}))",
    JsonAggregate = "CASE WHEN count(*) = 0 THEN NULL ELSE json_group_array({0}{1}) END",
    NextKey = "coalesce((SELECT seq FROM sqlite_sequence WHERE name = '{0}'), 0) + 1",
    LastInsertedKey = "(SELECT seq FROM sqlite_sequence WHERE name = '{0}')",
    ToInteger = "CAST({0} AS {1})",
    Least = "min({0}, {1})",
    Greatest = "max({0}, {1})",
    Lateral = false,
    WritableCtes = false,
    InsertSelectNeedsWhere = true)]
// A context is declared as DataContext<Sqlite>: the type keeps the short name, and the namespace the package's.
#pragma warning disable CA1724
public readonly struct Sqlite : IDataProvider<Sqlite, SqliteStatement, SqliteCommand>
#pragma warning restore CA1724
{
    /// <summary>The database file at <paramref name="path"/> (made if missing), with <see cref="SqliteDatabase"/>'s defaults.</summary>
    public static SqliteDatabase Open(string path) => new(path);

    /// <inheritdoc/>
    public static SqliteStatement Statement(string sql, params ReadOnlySpan<DataType> parameters) => new(sql, parameters);

    /// <inheritdoc/>
    public static SqliteCommand Read(DataContext context, SqliteStatement statement) => SourceOf(context).Read(statement);

    /// <inheritdoc/>
    public static SqliteCommand Write(DataContext context, SqliteStatement statement) => SourceOf(context).Write(statement);

    /// <summary>
    /// Runs a held command, handing each row its statements return to <typeparamref name="TReader"/>. A read runs here,
    /// on the caller's thread: the task is complete when this returns. A write completes on the writer's thread once its
    /// group committed. Await the task exactly once.
    /// </summary>
    public static ValueTask<DataResult> Run<TReader>(in PendingCommand command, object? state, CancellationToken aborted) where TReader : IRowReader
    {
        var job = Claim(command);
        if (command.Owner is SqliteReaders readers)
        {
            var result = readers.Run<TReader>(job, state, aborted);
            job.Return();
            return new ValueTask<DataResult>(result);
        }
        return ((SqliteWriter)command.Owner!).Submit<TReader>(job, state, aborted);
    }

    /// <inheritdoc/>
    public static void Discard(in PendingCommand command)
    {
        if (command.Job is SqliteJob job && job.Claim(command.Token)) job.Return();
    }

    static SqliteJob Claim(in PendingCommand command) =>
        command.Job is SqliteJob job && command.Owner is SqliteReaders or SqliteWriter
            ? job.Claim(command.Token) ? job : throw new InvalidOperationException("the command was already run or given back")
            : throw new ArgumentException("not a SQLite command", nameof(command));

    static SqliteDataSource SourceOf(DataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Source as SqliteDataSource ?? throw new InvalidOperationException("the context's source is not a SQLite one");
    }

    internal static long ClockNow() => Clock.UnixMicroseconds();
}

/// <summary>Builds one command on a source's readers or writer; generated code binds through it as its <c>TCommand</c>.
/// Values are copied as they are added, so spans into a request's buffer will do.</summary>
public readonly ref struct SqliteCommand : IDataCommand<SqliteStatement>
{
    readonly object _owner;
    readonly SqliteJob _job;
    readonly int _generation;

    internal SqliteCommand(object owner, SqliteJob job)
    {
        _owner = owner;
        _job = job;
        _generation = job.Generation;
    }

    SqliteJob Job => _job is not null && _job.Generation == _generation ? _job : throw new InvalidOperationException("the command was already held or given back");

    /// <inheritdoc/>
    public void Int64(long value) => Job.Int64(value);

    /// <inheritdoc/>
    public void Int32(int value) => Job.Int64(value);

    /// <inheritdoc/>
    public void Boolean(bool value) => Job.Int64(value ? 1 : 0);

    /// <inheritdoc/>
    public void Double(double value) => Job.Double(value);

    /// <inheritdoc/>
    public void Text(ReadOnlySpan<byte> utf8) => Job.Text(utf8);

    /// <inheritdoc/>
    public void Text(ReadOnlySpan<char> text) => Job.Text(text);

    /// <summary>An instant, stored as an integer of microseconds since 1970.</summary>
    public void Timestamp(Timestamp value) => Job.Int64(value.UnixMicroseconds);

    /// <inheritdoc/>
    public void Null() => Job.Null();

    /// <summary>Starts a text array, bound as one JSON text (read in SQL with <c>json_each</c>); its elements follow.</summary>
    public void BeginTextArray() => Job.BeginTextArray();

    /// <inheritdoc/>
    public void Element(ReadOnlySpan<byte> utf8) => Job.Element(utf8);

    /// <inheritdoc/>
    public void EndTextArray() => Job.EndTextArray();

    /// <inheritdoc/>
    public void Then(SqliteStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        Job.Statement(statement);
    }

    /// <inheritdoc/>
    public void Discard()
    {
        if (_job is not null && _job.Generation == _generation) _job.Return();
    }

    /// <inheritdoc/>
    public PendingCommand Hold()
    {
        var job = Job;
        job.EndStatement();
        job.Hold();
        return new PendingCommand(_owner, job, _generation);
    }
}
