using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodeFusion.Data.Sqlite;

/// <summary>
/// A statement's SQL and parameter types, made once (a static field of the generated code). Each connection prepares
/// it the first time it runs it (SQLITE_PREPARE_PERSISTENT) and keeps it by <see cref="Id"/>: an array index, no lookup.
/// </summary>
public sealed class SqliteStatement
{
    static int _next;

    internal readonly int Id;
    /// <summary>The SQL as UTF-8, ending in a NUL.</summary>
    internal readonly byte[] Utf8;
    internal readonly DataType[] Types;

    /// <summary>A statement of <paramref name="sql"/> taking parameters of <paramref name="types"/> (?1, ?2, ...).</summary>
    public SqliteStatement(string sql, params ReadOnlySpan<DataType> types)
    {
        ArgumentNullException.ThrowIfNull(sql);
        Sql = sql;
        Types = types.ToArray();
        Utf8 = new byte[Encoding.UTF8.GetByteCount(sql) + 1];
        Encoding.UTF8.GetBytes(sql, Utf8);
        Id = Interlocked.Increment(ref _next) - 1;
    }

    /// <summary>The SQL.</summary>
    public string Sql { get; }

    /// <summary>How many parameters it takes.</summary>
    public int ParamCount => Types.Length;

    /// <inheritdoc/>
    public override string ToString() => Sql;
}

/// <summary>One connection, used by one thread at a time (SQLITE_OPEN_NOMUTEX), with its prepared statements.</summary>
internal sealed unsafe class SqliteConn : IDisposable
{
    nint _db;
    nint[] _statements = new nint[64];
    nint[]?[] _more = new nint[]?[64];
    /// <summary>cf_now()'s clock: the writer sets it per group; 0 reads the time at each call.</summary>
    internal readonly long* Clock;
    /// <summary>The writer's WAL size in frames after its last commit (its WAL hook writes it).</summary>
    internal readonly int* WalFrames;
    /// <summary>The running job's number, 0 between jobs, <see cref="Claimed"/> while the watchdog interrupts it: the slot
    /// the watchdog claims a job by, so its interrupt can only land on the job it timed.</summary>
    long _running;
    /// <summary>When job <see cref="_running"/> started (Stopwatch ticks).</summary>
    long _startedAt;
    long _jobs;
    /// <summary>The job the watchdog interrupted last.</summary>
    long _interrupted;
    const long Claimed = -1;

    /// <summary>The cells <see cref="Clock"/> and <see cref="WalFrames"/> point into: GC memory, pinned, never freed while
    /// anything still holds this connection, so a read of them after a close is stale, not of freed memory.</summary>
    readonly long[] _cells = GC.AllocateArray<long>(2, pinned: true);

    SqliteConn(nint db)
    {
        _db = db;
        Clock = (long*)Unsafe.AsPointer(ref _cells[0]);
        WalFrames = (int*)Unsafe.AsPointer(ref _cells[1]);
    }

    internal nint Db => _db;

    static int _initialized;
    static readonly Lock InitLock = new();

    /// <summary>Whether SQLite's memory statistics are off: false when something in the process initialized SQLite first.</summary>
    internal static bool MemoryStatusOff { get; private set; }

    /// <summary>What the provider needs of the SQLite build it loads, by the compile options that would take it away:
    /// JSON (lists, arrays as parameters), CTEs (pages), upserts (OrIgnore, OrUpdate), WAL (readers beside the writer),
    /// subqueries (Any, In, navigations).</summary>
    internal static readonly string[] Omissions = ["OMIT_JSON", "OMIT_CTE", "OMIT_UPSERT", "OMIT_WAL", "OMIT_SUBQUERY"];

    /// <summary>Once per process, before the first connection: where the library comes from, SQLite's memory statistics
    /// off (a process-wide mutex on every allocation otherwise), initialized, and its build checked.</summary>
    internal static void InitializeOnce()
    {
        if (Volatile.Read(ref _initialized) != 0) return;
        lock (InitLock)
        {
            if (_initialized != 0) return;
            Sqlite3.UseAppDirectory();
            MemoryStatusOff = Sqlite3.DisableMemoryStatus();
            int rc = Sqlite3.Initialize();
            if (rc != Sqlite3.Ok) throw new SqliteException("sqlite3_initialize failed", rc);
            if (Unfit(Sqlite3.LibVersionNumber(), Sqlite3.SourceId(), !Sqlite3.FromAppDirectory, Sqlite3.ThreadSafe(), Sqlite3.CompileOptions()) is { } why)
                throw new InvalidOperationException(why);
            Volatile.Write(ref _initialized, 1);
        }
    }

    /// <summary>The SQLite build this provider was tested with, which the package's native library must be
    /// (SourceGear.sqlite3, pinned exactly in Directory.Packages.props, its file's hash checked at build): its
    /// sqlite3_libversion_number and sqlite3_sourceid.</summary>
    internal const int TestedVersion = 3_050_004;
    internal const string TestedSourceId = "2025-07-30 19:33:53 4d8adfb30e03f9cf27f800a2c1ba3c48fb4ca1b08b0f5ed59a4d5ecbf45e20a3";

    /// <summary>Why a SQLite of <paramref name="version"/> and <paramref name="sourceId"/>, from the package's library
    /// (<paramref name="fromPackage"/>) or an app's own, <paramref name="threadSafe"/> (sqlite3_threadsafe) and the
    /// compile options <paramref name="used"/> says it has (null: it can't say), can't serve the provider; null when it
    /// can. The package's must be the build tested; an app's own build need only be new enough and have what's used.</summary>
    internal static string? Unfit(int version, string sourceId, bool fromPackage, int threadSafe, Func<string, bool>? used)
    {
        if (version < Sqlite3.MinVersion) return $"SQLite {version} is older than {Sqlite3.MinVersion}, which the generated SQL needs";
        if (fromPackage && (version != TestedVersion || sourceId != TestedSourceId))
            return $"the package's SQLite is {version} from {sourceId}, not the build CodeFusion.Data.Sqlite was tested with ({TestedVersion} from {TestedSourceId})";
        // connections move between threads (readers, the writer, an event loop's batch) and the watchdog interrupts
        // from its own: THREADSAFE=1 or 2
        if (threadSafe == 0) return "SQLite was built with SQLITE_THREADSAFE=0; the provider uses connections from several threads";
        if (used is null) return "SQLite was built with SQLITE_OMIT_COMPILEOPTION_DIAGS, so it can't say whether it has what the generated SQL needs";
        foreach (var o in Omissions)
            if (used(o)) return $"SQLite was built with SQLITE_{o}; the generated SQL needs what it leaves out";
        return null;
    }

    public static SqliteConn Open(string path, SqliteDatabase options, bool writer)
    {
        InitializeOnce();
        int len = Encoding.UTF8.GetByteCount(path);
        var name = new byte[len + 1];
        Encoding.UTF8.GetBytes(path, name);
        nint db;
        int rc;
        fixed (byte* p = name)
            rc = Sqlite3.Open(p, out db, Sqlite3.OpenReadWrite | Sqlite3.OpenCreate | Sqlite3.OpenNoMutex | Sqlite3.OpenPrivateCache | Sqlite3.OpenExResCode, null);
        if (rc != Sqlite3.Ok)
        {
            var e = SqliteException.From(db, rc);
            _ = Sqlite3.Close(db);
            throw e;
        }
        var conn = new SqliteConn(db);
        try
        {
            _ = Sqlite3.BusyTimeout(db, (int)options.BusyTimeout.TotalMilliseconds);
            // journal_mode=WAL is the file's, kept in it: the writer sets it, before any reader opens the file
            if (writer)
            {
                // exclusive before the WAL is first touched: SQLite then keeps the WAL index in this process's memory
                if (options.Exclusive) conn.Exec("PRAGMA locking_mode=EXCLUSIVE");
                conn.Exec("PRAGMA journal_mode=WAL");
                // no checkpoint inside COMMIT: the writer runs one after the group's writes have completed
                _ = Sqlite3.WalHook(db, &SqlFunctions.WalFrames, (nint)conn.WalFrames);
            }
            conn.Exec(string.Create(CultureInfo.InvariantCulture,
                $"PRAGMA synchronous={(writer ? (int)options.Synchronous : 1)}; PRAGMA mmap_size={options.MmapSize}; PRAGMA cache_size={-options.CacheKiB}; " +
                $"PRAGMA temp_store=MEMORY; PRAGMA foreign_keys={(options.ForeignKeys ? 1 : 0)}"));
            if (!writer) conn.Exec("PRAGMA query_only=1");
            conn.AddFunctions();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    void AddFunctions()
    {
        ReadOnlySpan<byte> now = "cf_now\0"u8;
        fixed (byte* n = now)
            Check(Sqlite3.CreateFunction(_db, n, 0, Sqlite3.Utf8 | Sqlite3.Innocuous, (nint)Clock, &SqlFunctions.Now, 0, 0, 0));
    }

    /// <summary>Runs SQL with no parameters and no rows wanted (cold: pragmas, schema, migrations).</summary>
    public void Exec(string sql)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(sql) + 1];
        Encoding.UTF8.GetBytes(sql, bytes);
        fixed (byte* p = bytes)
            Check(Sqlite3.Exec(_db, p, 0, 0, 0));
    }

    /// <summary>The first column of the first row of <paramref name="sql"/> as an integer (cold: user_version).</summary>
    public long Scalar(string sql)
    {
        var s = new SqliteStatement(sql);
        nint stmt = Statement(s);
        try
        {
            int rc = Sqlite3.Step(stmt);
            if (rc == Sqlite3.Row) return Sqlite3.ColumnInt64(stmt, 0);
            if (rc != Sqlite3.Done) throw SqliteException.From(_db, Sqlite3.ExtendedErrCode(_db));
            return 0;
        }
        finally
        {
            _ = Sqlite3.Reset(stmt);
        }
    }

    /// <summary>The connection's prepared copy of <paramref name="s"/> (its first statement), prepared on first use.</summary>
    public nint Statement(SqliteStatement s)
    {
        var all = _statements;
        if ((uint)s.Id < (uint)all.Length && all[s.Id] != 0) return all[s.Id];
        return Prepare(s);
    }

    /// <summary>The statements after the first, when <paramref name="s"/>'s SQL holds several (run in order, in the
    /// same unit); null for one. Valid once <see cref="Statement"/> prepared it.</summary>
    public nint[]? More(SqliteStatement s) => (uint)s.Id < (uint)_more.Length ? _more[s.Id] : null;

    // ---------- the tables each statement reads and writes ----------
    //
    // What SQLite reports as it prepares a statement (sqlite3_set_authorizer): every table it reads, a count(*) included,
    // and every table it writes, those its triggers and foreign-key actions write included, and views as their tables.
    // Kept per statement as a mask over this connection's tables (bit i: the i-th table it met; the 64th and after share
    // the last bit), so an event-loop batch can tell, without a lookup, whether a read sees a table it wrote.

    ulong[] _reads = new ulong[64], _writes = new ulong[64];
    readonly Dictionary<string, int> _tableBits = new(StringComparer.OrdinalIgnoreCase);
    ulong _preparingReads, _preparingWrites;
    GCHandle _self;

    /// <summary>The tables <paramref name="s"/> reads, prepared now if it wasn't.</summary>
    public ulong ReadsOf(SqliteStatement s)
    {
        _ = Statement(s);
        return _reads[s.Id];
    }

    /// <summary>The tables <paramref name="s"/> writes, prepared now if it wasn't.</summary>
    public ulong WritesOf(SqliteStatement s)
    {
        _ = Statement(s);
        return _writes[s.Id];
    }

    ulong Bit(byte* name)
    {
        string table = Marshal.PtrToStringUTF8((nint)name) ?? "";
        if (!_tableBits.TryGetValue(table, out int bit))
        {
            bit = Math.Min(_tableBits.Count, 63);
            _tableBits[table] = bit;
        }
        return 1UL << bit;
    }

    [UnmanagedCallersOnly]
    static int Authorize(nint self, int action, byte* first, byte* second, byte* database, byte* trigger)
    {
        var conn = (SqliteConn)GCHandle.FromIntPtr(self).Target!;
        switch (action)
        {
            case Sqlite3.AuthRead:
                if (first is not null) conn._preparingReads |= conn.Bit(first);
                break;
            case Sqlite3.AuthInsert or Sqlite3.AuthUpdate or Sqlite3.AuthDelete:
                if (first is not null) conn._preparingWrites |= conn.Bit(first);
                break;
        }
        return Sqlite3.Ok;
    }

    nint Prepare(SqliteStatement s)
    {
        if (s.Id >= _statements.Length) Array.Resize(ref _statements, Math.Max(s.Id + 1, _statements.Length * 2));
        if (s.Id >= _more.Length) Array.Resize(ref _more, _statements.Length);
        if (s.Id >= _reads.Length)
        {
            Array.Resize(ref _reads, _statements.Length);
            Array.Resize(ref _writes, _statements.Length);
        }
        var pieces = new List<nint>(1);
        if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
        _preparingReads = _preparingWrites = 0;
        _ = Sqlite3.SetAuthorizer(_db, &Authorize, GCHandle.ToIntPtr(_self));
        try
        {
            fixed (byte* sql = s.Utf8)
            {
                byte* at = sql, end = sql + s.Utf8.Length - 1; // the NUL
                while (at < end)
                {
                    int rc = Sqlite3.Prepare(_db, at, (int)(end - at), Sqlite3.PreparePersistent, out nint stmt, out byte* tail);
                    if (rc != Sqlite3.Ok) throw new SqliteException($"{Sqlite3.CodeName(rc)}: {Sqlite3.Message(_db)} in: {s.Sql}", rc);
                    if (stmt != 0) pieces.Add(stmt); // 0: only whitespace or a comment was left
                    if (tail is null || tail <= at) break;
                    at = tail;
                }
            }
            if (pieces.Count == 0) throw new InvalidOperationException($"no SQL in: {s.Sql}");
            // each statement binds the parameters it names (?1, ?2, ...); together they use all the statement declares
            int max = 0;
            foreach (var p in pieces) max = Math.Max(max, Sqlite3.BindParameterCount(p));
            if (max != s.ParamCount) throw new InvalidOperationException($"the SQL takes {max} parameters, its statement declares {s.ParamCount}: {s.Sql}");
        }
        catch
        {
            foreach (var p in pieces) _ = Sqlite3.Finalize(p);
            throw;
        }
        finally
        {
            _ = Sqlite3.SetAuthorizer(_db, null, 0); // only while preparing: nothing else pays for it
        }
        _reads[s.Id] = _preparingReads;
        _writes[s.Id] = _preparingWrites;
        _statements[s.Id] = pieces[0];
        if (pieces.Count > 1) _more[s.Id] = pieces.GetRange(1, pieces.Count - 1).ToArray();
        return pieces[0];
    }

    /// <summary>Steps a statement without parameters or rows (BEGIN, COMMIT, SAVEPOINT): SQLITE_OK or the extended code.</summary>
    public int Once(SqliteStatement s)
    {
        nint stmt = Statement(s);
        int rc = Sqlite3.Step(stmt);
        _ = Sqlite3.Reset(stmt);
        return rc is Sqlite3.Done or Sqlite3.Row ? Sqlite3.Ok : Sqlite3.ExtendedErrCode(_db);
    }

    /// <summary>cf_now() is the time of each call again, not a group's start (a read on the writer's connection).</summary>
    public void ClearClock() => *Clock = 0;

    /// <summary>A job starts on this connection (for the watchdog): numbered, its start time first.</summary>
    public void Started()
    {
        Volatile.Write(ref _startedAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _running, ++_jobs); // only this connection's thread runs jobs
    }

    /// <summary>The job ended: its number is cleared, after the watchdog's claim on it if one is in progress.</summary>
    public void Ended()
    {
        long job = _jobs; // the job this thread started last: never a claim's marker
        SpinWait wait = default;
        while (Interlocked.CompareExchange(ref _running, 0, job) != job)
        {
            if (Volatile.Read(ref _running) == 0) return; // ended already
            wait.SpinOnce(); // claimed: the watchdog is interrupting it, and gives it back at once
        }
    }

    /// <summary>The running job was the one the watchdog interrupted (an SQLITE_INTERRUPT is its timeout, not another's).</summary>
    public bool InterruptedThis => Volatile.Read(ref _interrupted) == Volatile.Read(ref _running);

    /// <summary>Interrupts the running job if it started more than <paramref name="limit"/> ticks before <paramref name="now"/>,
    /// and only that job: its number is read once and claimed with a compare-exchange before the interrupt, so a job that
    /// ended meanwhile is left alone and the next one can't take the interrupt (it waits in <see cref="Ended"/> or hasn't
    /// a number yet). An interrupt that finds no statement running is cleared by SQLite as the next one starts.</summary>
    public bool InterruptIfOver(long now, long limit)
    {
        long job = Volatile.Read(ref _running);
        if (job <= 0 || now - Volatile.Read(ref _startedAt) <= limit || _db == 0) return false;
#if DEBUG
        BeforeInterrupt?.Invoke();
#endif
        if (Interlocked.CompareExchange(ref _running, Claimed, job) != job) return false; // that job ended; another may run
        try
        {
            Volatile.Write(ref _interrupted, job);
            Sqlite3.InterruptRunning(_db);
        }
        finally
        {
            Volatile.Write(ref _running, job);
        }
        return true;
    }

#if DEBUG
    /// <summary>Debug, tests: runs on the watchdog's thread once it decided to interrupt, before it does.</summary>
    internal static Action? BeforeInterrupt;
#endif

    void Check(int rc)
    {
        if (rc != Sqlite3.Ok) throw SqliteException.From(_db, rc);
    }

    public void Dispose()
    {
        nint db = _db;
        if (db == 0) return;
        _db = 0;
        foreach (var s in _statements)
            if (s != 0) _ = Sqlite3.Finalize(s);
        foreach (var more in _more)
            if (more is not null)
                foreach (var s in more) _ = Sqlite3.Finalize(s);
        _ = Sqlite3.Close(db);
        if (_self.IsAllocated) _self.Free();
    }
}
