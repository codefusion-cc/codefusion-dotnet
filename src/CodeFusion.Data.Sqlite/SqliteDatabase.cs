using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace CodeFusion.Data.Sqlite;

/// <summary><c>PRAGMA synchronous</c> for the writer.</summary>
// The pragma's own numbers; OFF (0) isn't offered: a write must survive the process.
#pragma warning disable CA1008
public enum SqliteSync
{
    /// <summary>NORMAL: no sync per commit in WAL mode, only at checkpoints. A commit survives the process crashing; the
    /// last ones may roll back if the machine loses power.</summary>
    Normal = 1,
    /// <summary>FULL: the WAL is synced at every commit. Group commit shares the sync among the group.</summary>
    Full = 2,
}
#pragma warning restore CA1008

/// <summary>
/// A SQLite database file and how its connections are opened (<see cref="Sqlite.Open(string)"/>). The defaults suit a
/// server: WAL, synchronous=FULL, the file mapped into memory, a reader connection per core.
/// </summary>
public sealed class SqliteDatabase
{
    /// <summary>The database at <paramref name="path"/>, made if missing.</summary>
    public SqliteDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary><c>PRAGMA synchronous</c> for the writer: <see cref="SqliteSync.Full"/> unless set.</summary>
    public SqliteSync Synchronous { get; init; } = SqliteSync.Full;

    /// <summary>Reader connections. Reads run on the caller's thread, each on a connection it holds for one statement,
    /// so this many reads run at once.</summary>
    public int Readers { get; init; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>Bytes of the file mapped into memory per connection (<c>PRAGMA mmap_size</c>): reads copy no pages.</summary>
    public long MmapSize { get; init; } = 256L << 20;

    /// <summary>Page cache per connection in KiB (<c>PRAGMA cache_size</c>).</summary>
    public int CacheKiB { get; init; } = 8 << 10;

    /// <summary>How long a connection waits for a lock another process holds; this process's writer never waits on its
    /// readers.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a statement may run before it is interrupted (<see cref="DataStatus.StatementTimeout"/>); a write
    /// interrupted is undone. <see cref="Timeout.InfiniteTimeSpan"/> turns the watchdog off.</summary>
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The most writes in one transaction.</summary>
    public int MaxGroup { get; init; } = 256;

    /// <summary>The most writes waiting for the writer; more are refused at once (<see cref="DataStatus.Busy"/>), so a
    /// stalled disk can't pile up requests.</summary>
    public int MaxQueued { get; init; } = 8192;

    /// <summary>WAL frames after which the writer runs a passive checkpoint, once a group's writes have completed.</summary>
    public int CheckpointFrames { get; init; } = 1000;

    /// <summary>Foreign keys checked (<c>PRAGMA foreign_keys</c>), as Postgres checks them.</summary>
    public bool ForeignKeys { get; init; } = true;

    /// <summary>
    /// A write's caller continues on the writer's thread, right after the commit, instead of on the thread pool. Saves a
    /// hop per write on a box with few cores; but the continuation must never block on another write (that write needs
    /// the same thread: a deadlock), and the next group waits for it. Off by default.
    /// </summary>
    public bool CompleteOnWriterThread { get; init; }

    /// <summary>
    /// A write that finds the writer idle and nothing queued commits on its caller's thread instead of handing over to
    /// the writer's. Off by default: it saves a lone write two thread switches, but a thread that submits writes one
    /// after another (an event loop on one core) then commits each alone, where the writer's thread would take them as
    /// one group.
    /// </summary>
    public bool LeaderCommit { get; init; }

    /// <summary>
    /// One connection does every read and write, and the file is this process's alone (<c>PRAGMA locking_mode=EXCLUSIVE</c>):
    /// SQLite keeps the WAL index in the process's memory instead of a shared-memory file, takes no file lock per
    /// transaction, and keeps one page cache instead of one per reader. Reads still run on the caller's thread, one at a
    /// time, and wait while a group commits. For a server of one or two cores; <see cref="Readers"/> is ignored. Off by
    /// default.
    /// </summary>
    public bool Exclusive { get; init; }

    /// <summary>
    /// With <see cref="Exclusive"/>, behind a server that runs event loops (CodeFusion.Http's EventLoop transport): a
    /// write made on a loop's thread runs there at once, inside one transaction per loop wake-up, and every write of the
    /// wake-up commits together when the server ends the loop's batch (<see cref="SqliteDataSource.EndEventLoopBatch"/>,
    /// which <see cref="SqliteServices.AddDatabase{TContext}"/> registers). A write completes after that COMMIT, on the
    /// loop, before the batch's answers are sent. Each write has a savepoint, so one that fails fails alone. A read on
    /// the loop of a table the batch has written and not committed yet runs inside its transaction, sees the writes, and
    /// completes with them: after the one COMMIT, or failed with it. A read of tables the batch hasn't written answers at
    /// once (the tables are what SQLite reports as it prepares each statement, triggers and foreign-key actions
    /// included). Other threads wait for the batch. Writes off a loop go to the writer thread as before. Off by default.
    /// </summary>
    public bool EventLoopBatches { get; init; }

    /// <summary>
    /// The schema, as numbered steps: step n (from 1) is <c>Migrations[n - 1]</c>. At <see cref="OpenSource"/> each step
    /// past the file's <c>PRAGMA user_version</c> runs in its own transaction with the version moved inside it, so a
    /// crash leaves the file at one version or the next, and the next open finishes. A file at a version the app
    /// doesn't have is refused.
    /// </summary>
    public IReadOnlyList<string> Migrations { get; init; } = [];

    /// <summary>The replies for failures and empty results.</summary>
    public DataErrors Errors { get; init; } = DataErrors.Default;

    /// <summary>Told of each command that failed for a reason a client can't cause, on the thread that ran it.</summary>
    public Action<string, DataResult>? OnFailure { get; init; }

    /// <summary>Opens the writer (which sets WAL and runs the migrations), then the readers, and starts the writer's
    /// thread and the statement watchdog.</summary>
    public SqliteDataSource OpenSource()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Readers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGroup, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueued, 1);
        if (EventLoopBatches && !Exclusive) throw new ArgumentException("EventLoopBatches needs Exclusive: the batch's transaction holds the one connection", nameof(EventLoopBatches));
        var writerConn = SqliteConn.Open(Path, this, writer: true);
        SqliteReaders? readers = null;
        try
        {
            Migrate(writerConn);
            var writer = new SqliteWriter(writerConn, this, System.IO.Path.GetFileName(Path));
            readers = Exclusive ? new SqliteReaders(writerConn, writer.Clock) : new SqliteReaders(Path, this, writer.Clock);
            writer.Readers = readers;
            readers.Writer = writer;
            var source = new SqliteDataSource(this, readers, writer) { Errors = Errors, OnFailure = OnFailure };
            writer.Start();
            source.StartWatchdog();
            return source;
        }
        catch
        {
            readers?.Dispose();
            writerConn.Dispose();
            throw;
        }
    }

    void Migrate(SqliteConn conn)
    {
        long version = conn.Scalar("PRAGMA user_version");
        if (version > Migrations.Count)
            throw new InvalidOperationException($"{Path} is at schema version {version}, newer than this app's {Migrations.Count}");
        for (long v = version; v < Migrations.Count; v++)
            conn.Exec(string.Create(CultureInfo.InvariantCulture, $"BEGIN IMMEDIATE; {Migrations[(int)v]}; PRAGMA user_version = {v + 1}; COMMIT"));
    }
}

/// <summary>A SQLite database's reader connections and writer, shared by every context on it.</summary>
public sealed class SqliteDataSource : DataSource, IDisposable
{
    readonly SqliteDatabase _database;
    readonly Thread _watchdog;
    int _stopped;

    internal readonly SqliteReaders Readers;

    /// <summary>Debug builds: jobs rented from this source and not yet back in the pool (0 at rest). Release builds: 0.</summary>
    internal readonly JobCounter Jobs = new();

    /// <summary><see cref="Jobs"/>' count, read now.</summary>
    internal int JobsOut => Volatile.Read(ref Jobs.Out);

    internal SqliteDataSource(SqliteDatabase database, SqliteReaders readers, SqliteWriter writer)
    {
        _database = database;
        Readers = readers;
        Writer = writer;
        _watchdog = new Thread(Watch) { IsBackground = true, Name = "sqlite watchdog " + System.IO.Path.GetFileName(database.Path) };
    }

    /// <summary>Where writes run, in groups.</summary>
    public SqliteWriter Writer { get; }

    /// <summary>Statements the watchdog interrupted for running past <see cref="SqliteDatabase.StatementTimeout"/>.</summary>
    public long Interrupted => Interlocked.Read(ref _interrupted);
    long _interrupted;

    /// <summary>A command that only reads.</summary>
    public SqliteCommand Read(SqliteStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new(Readers, SqliteJob.Rent(statement, this));
    }

    /// <summary>A command that writes: its statements commit together.</summary>
    public SqliteCommand Write(SqliteStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new(Writer, SqliteJob.Rent(statement, this));
    }

    internal void StartWatchdog()
    {
        if (_database.StatementTimeout != Timeout.InfiniteTimeSpan) _watchdog.Start();
    }

    /// <summary>Interrupts statements that have run past the timeout: a reader's read, or the writer's group.</summary>
    void Watch()
    {
        long limit = (long)(_database.StatementTimeout.TotalSeconds * Stopwatch.Frequency);
        int sleep = (int)Math.Clamp(_database.StatementTimeout.TotalMilliseconds / 4, 1, 100);
        while (Volatile.Read(ref _stopped) == 0)
        {
            Thread.Sleep(sleep);
            long now = Stopwatch.GetTimestamp();
            if (!_database.Exclusive) // else the one connection is the writer's
                foreach (var c in Readers.Connections)
                    if (c.InterruptIfOver(now, limit)) Interlocked.Increment(ref _interrupted);
            if (Writer.Connection.InterruptIfOver(now, limit)) Interlocked.Increment(ref _interrupted);
        }
    }

    /// <summary>
    /// Ends this loop thread's batch (<see cref="SqliteDatabase.EventLoopBatches"/>): commits the writes its wake-up made,
    /// gives the connection back, and completes them here, on the loop. Their continuations may write again; those are
    /// committed too before this returns. Nothing to do on a thread with no batch open. The server calls it after each
    /// wake-up; <paramref name="loop"/> is the loop's index.
    /// </summary>
    public void EndEventLoopBatch(int loop)
    {
        Debug.Assert(loop >= 0, "a loop's index");
        Writer.EndBatch(); // the batch is the calling thread's
    }

    /// <summary>Takes no more commands: the writer commits what has queued, reads running finish, then every
    /// connection closes.</summary>
    public override void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (_watchdog.IsAlive) _watchdog.Join();
        if (_database.Exclusive)
        {
            // the readers' connection is the writer's: no read may run while the writer closes it
            Readers.Dispose();
            Writer.Stop();
            return;
        }
        Writer.Stop();
        Readers.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}

/// <summary>Registers a SQLite database with an app.</summary>
public static class SqliteServices
{
    /// <summary>
    /// Opens <paramref name="database"/> and registers <typeparamref name="TContext"/> per connection: each connection
    /// gets its own context, bound to each of its requests. The source stops when the app stops.
    /// </summary>
    /// <returns>The source, for its counters.</returns>
    public static SqliteDataSource AddDatabase<TContext>(this IServiceRegistry services, SqliteDatabase database)
        where TContext : DataContext<Sqlite>, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);
        var source = database.OpenSource();
        services.AddSingleton(source);
        services.AddPerConnection(source.CreateContext<TContext>);
        services.OnStopping(source.Stop);
        if (database.EventLoopBatches) services.OnEventLoopBatch(source.EndEventLoopBatch);
        return source;
    }
}
