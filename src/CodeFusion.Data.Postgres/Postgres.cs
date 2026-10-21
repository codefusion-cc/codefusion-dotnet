using System;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// The Postgres provider for CodeFusion.Data: a context derives from <c>DataContext&lt;Postgres&gt;</c>, and the
/// generator writes its queries in Postgres's SQL and runs them on a <see cref="PgReader"/> (reads, in groups) and a
/// <see cref="PgWriter"/> (writes, in group commits). Register with
/// <see cref="PostgresServices.AddDatabase{TContext}(IServiceRegistry, PostgresDatabase, int, int)"/>,
/// or with a source of the app's own.
/// </summary>
[SqlDialect("postgres")]
// A context is declared as DataContext<Postgres>: the type keeps the short name, and the namespace the package's.
#pragma warning disable CA1724
public readonly struct Postgres : IDataProvider<Postgres, PgStatement, PgCommand>
#pragma warning restore CA1724
{
    /// <summary>A database from a <c>postgres://user:password@host:port/db</c> URL (or an Npgsql-style string), with
    /// <see cref="PostgresDatabase"/>'s session settings.</summary>
    // A string, as DATABASE_URL arrives: an Npgsql-style one is no Uri at all.
#pragma warning disable CA1054
    public static PostgresDatabase FromUrl(string url) => new(PgSettings.Parse(url));
#pragma warning restore CA1054

    /// <summary>A database from <paramref name="settings"/>, with <see cref="PostgresDatabase"/>'s session settings added.</summary>
    public static PostgresDatabase FromSettings(PgSettings settings) => new(settings);

    /// <inheritdoc/>
    public static PgStatement Statement(string sql, params ReadOnlySpan<DataType> parameters)
    {
        Span<PgType> types = parameters.Length <= 32 ? stackalloc PgType[parameters.Length] : new PgType[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) types[i] = TypeOf(parameters[i]);
        return new PgStatement(sql, types);
    }

    /// <summary>
    /// The update's statement, and the one that runs a group commit's run of them as one: its parameters as arrays,
    /// unnested, one row each. A run never holds two writes to one key (the writer starts another run there), so each
    /// write updates its row once and is checked as running the updates one by one would. Only an integer key is
    /// merged, whose values are equal exactly when their bytes are. That holds only while each row's update sees
    /// nothing of the others', so a check of the table runs first, as a statement of its own in the same transaction:
    /// a trigger or rule (which would see one statement, or the rows in another order), partitions or inheritance
    /// children (whose own triggers and indexes it doesn't see), or a unique or exclusion index on a column the update
    /// sets (which would judge the rows against each other at once, not one write after another) make it fail with
    /// "CodeFusion: no merged updates on" and the table, and the update never runs. An update that sets its own key
    /// isn't merged: the rows it finds would depend on the writes before it.
    /// </summary>
    public static PgStatement KeyedUpdate(string sql, string table, string key, int keyParameter, string[] columns, int[] columnParameters,
        params ReadOnlySpan<DataType> parameters)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(columnParameters);
        var statement = Statement(sql, parameters);
        int n = parameters.Length;
        if (columns.Length == 0 || columns.Length != columnParameters.Length || (uint)keyParameter >= (uint)n || Array.IndexOf(columns, key) >= 0
            || parameters[keyParameter] is not (DataType.Int32 or DataType.Int64))
            return statement;
        var arrays = new PgType[n];
        var elements = new uint[n];
        for (int i = 0; i < n; i++)
        {
            if (parameters[i] == DataType.TextArray) return statement;
            elements[i] = (uint)TypeOf(parameters[i]);
            arrays[i] = ArrayOf(parameters[i]);
        }
        var b = new System.Text.StringBuilder("UPDATE ").Append(table).Append(" AS t0 SET ");
        for (int c = 0; c < columns.Length; c++)
        {
            if ((uint)columnParameters[c] >= (uint)n) return statement;
            b.Append(c == 0 ? "" : ", ").Append(columns[c]).Append(" = v.p").Append(columnParameters[c] + 1);
        }
        b.Append(" FROM unnest(");
        for (int i = 0; i < n; i++) b.Append(i == 0 ? "$" : ", $").Append(i + 1);
        b.Append(") AS v(");
        for (int i = 0; i < n; i++) b.Append(i == 0 ? "p" : ", p").Append(i + 1);
        b.Append(") WHERE t0.").Append(key).Append(" = v.p").Append(keyParameter + 1);
        // The table's check: a statement of its own, run first in the same transaction, so a refusal skips the update
        var g = new System.Text.StringBuilder("SELECT 1 FROM pg_class c WHERE c.oid = '").Append(table.Replace("'", "''", StringComparison.Ordinal))
            .Append("'::regclass AND CASE WHEN c.relhasrules OR c.relkind = 'p' OR c.relhassubclass")
            .Append(" OR EXISTS (SELECT 1 FROM pg_trigger tr WHERE tr.tgrelid = c.oid AND NOT tr.tgisinternal)")
            .Append(" OR EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND (i.indisunique OR i.indisexclusion)")
            .Append(" AND (i.indexprs IS NOT NULL OR i.indpred IS NOT NULL OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid")
            .Append(" AND a.attnum = ANY (i.indkey) AND a.attname IN (");
        for (int c = 0; c < columns.Length; c++) g.Append(c == 0 ? "'" : ", '").Append(Unquoted(columns[c]).Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        g.Append(")))) THEN ('").Append(NotMergedMarker).Append(" ' || c.relname)::int ELSE 0 END = 1");
        statement.Batch = new PgStatement(b.ToString(), arrays) { ElementTypes = elements, KeyParameter = keyParameter, Check = new PgStatement(g.ToString()) };
        return statement;
    }

    /// <summary>The start of the error a merged update fails with when its table can't take merged updates.</summary>
    internal const string NotMergedMarker = "CodeFusion: no merged updates on";

    /// <summary>A column's name as the catalog has it: a quoted identifier without its quotes, a plain one as it is
    /// (the generator writes it unquoted only when it is lower case already).</summary>
    static string Unquoted(string column) =>
        column.Length >= 2 && column[0] == '"' && column[^1] == '"' ? column[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : column;

    static PgType ArrayOf(DataType type) => type switch
    {
        DataType.Int64 => (PgType)1016,
        DataType.Int32 => (PgType)1007,
        DataType.Boolean => (PgType)1000,
        DataType.Double => (PgType)1022,
        DataType.Text => (PgType)1009,
        DataType.Timestamp => (PgType)1185,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <inheritdoc/>
    public static PgStatement Alone(PgStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        statement.MarkAlone();
        return statement;
    }

    static PgType TypeOf(DataType type) => type switch
    {
        DataType.Int64 => PgType.Int8,
        DataType.Int32 => PgType.Int4,
        DataType.Boolean => PgType.Bool,
        DataType.Double => PgType.Float8,
        DataType.Text => PgType.Text,
        DataType.Timestamp => PgType.Timestamptz,
        DataType.TextArray => PgType.TextArray,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <inheritdoc/>
    public static PgCommand Read(DataContext context, PgStatement statement) => SourceOf(context).Reader.Command(statement);

    /// <inheritdoc/>
    public static PgCommand Write(DataContext context, PgStatement statement) => SourceOf(context).Writer.Command(statement);

    /// <inheritdoc/>
    public static ValueTask<DataResult> Run<TReader>(in PendingCommand command, object? state, CancellationToken aborted) where TReader : IRowReader =>
        PgHeld.From(command).RunData(Rows<TReader>.Handler, state, aborted);

    /// <inheritdoc/>
    public static void Discard(in PendingCommand command) => PgHeld.From(command).Discard();

    static PgDataSource SourceOf(DataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Source as PgDataSource ?? throw new InvalidOperationException("the context's source is not a Postgres one");
    }

    /// <summary>The row handler that hands Postgres's rows to a generated reader: one per reader type, made once.</summary>
    static class Rows<TReader> where TReader : IRowReader
    {
        internal static readonly PgRowHandler Handler = static (in PgRow row, object? state) => TReader.Read(in row, state);
    }
}

/// <summary>
/// Where a context's database is and how its sessions start. The defaults are the contest server's: generic plans
/// (the generated statements are written so the generic plan is the right one, so each is planned once per
/// connection), no JIT and no parallel workers (they never pay off for statements this short), a 30 s statement
/// timeout, and a 2 s lock timeout for the writer.
/// </summary>
public sealed class PostgresDatabase
{
    /// <summary>A database at <paramref name="settings"/>, with the session settings above added (where it sets none of its own).</summary>
    public PostgresDatabase(PgSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Clone();
        Default("plan_cache_mode", "force_generic_plan");
        Default("jit", "off");
        Default("max_parallel_workers_per_gather", "0");
        Default("statement_timeout", "30s");
    }

    void Default(string name, string value)
    {
        foreach (var p in Settings.Parameters)
            if (p.Key == name) return;
        Settings.Set(name, value);
    }

    /// <summary>The readers' connection settings; the writers' add <see cref="WriterLockTimeout"/>.</summary>
    public PgSettings Settings { get; }

    /// <summary>
    /// How long a write waits for a row lock another transaction holds. Past it only that write fails (503) and the
    /// rest of its group commits without it, instead of every write in the group waiting for the statement timeout.
    /// </summary>
    public TimeSpan WriterLockTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The replies for failures and empty results.</summary>
    public DataErrors Errors { get; init; } = DataErrors.Default;

    /// <summary>Told of each command that failed for a reason a client can't cause, or timed out, on the loop's thread
    /// (<see cref="DataSource.OnFailure"/>).</summary>
    public Action<string, DataResult>? OnFailure { get; init; }

    /// <summary>Starts <paramref name="readers"/> read loops and <paramref name="writers"/> write loops, each on a
    /// connection of its own (opened on first use).</summary>
    public PgDataSource Open(int readers, int writers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(writers, 1);
        var writer = Settings.Clone().Set("lock_timeout", $"{(long)WriterLockTimeout.TotalMilliseconds}ms");
        var source = new PgDataSource(new PgReader(Settings), new PgWriter(writer)) { Errors = Errors, OnFailure = OnFailure };
        source.Reader.Start(readers);
        source.Writer.Start(writers);
        return source;
    }
}

/// <summary>A Postgres database's read and write loops, shared by every context on it.</summary>
public sealed class PgDataSource : DataSource
{
    /// <summary>A source over loops the caller has started (or will).</summary>
    public PgDataSource(PgReader reader, PgWriter writer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        Reader = reader;
        Writer = writer;
    }

    /// <summary>Where reads run, in groups.</summary>
    public PgReader Reader { get; }

    /// <summary>Where writes run, in group commits.</summary>
    public PgWriter Writer { get; }

    /// <inheritdoc/>
    public override void Stop()
    {
        Reader.Complete();
        Writer.Complete();
    }
}

/// <summary>Registers a Postgres database with an app.</summary>
public static class PostgresServices
{
    /// <summary>
    /// Opens <paramref name="database"/> with <paramref name="readers"/> read loops and <paramref name="writers"/>
    /// write loops, and registers <typeparamref name="TContext"/> per connection: each connection gets its own
    /// context, bound to each of its requests. The loops stop when the app stops.
    /// </summary>
    /// <returns>The source, for its counters.</returns>
    public static PgDataSource AddDatabase<TContext>(this IServiceRegistry services, PostgresDatabase database, int readers, int writers)
        where TContext : DataContext<Postgres>, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);
        return services.AddDatabase<TContext>(database.Open(readers, writers));
    }

    /// <summary>
    /// Registers <paramref name="source"/>, which the app built itself (its own loops, error replies or failure log),
    /// and <typeparamref name="TContext"/> per connection on it, as the other overload does. Its loops are the
    /// caller's to start; they stop when the app stops.
    /// </summary>
    /// <returns><paramref name="source"/>.</returns>
    public static PgDataSource AddDatabase<TContext>(this IServiceRegistry services, PgDataSource source)
        where TContext : DataContext<Postgres>, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);
        services.AddSingleton(source);
        services.AddPerConnection(source.CreateContext<TContext>);
        services.OnStopping(source.Stop);
        return source;
    }
}
