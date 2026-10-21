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
            if (CheckLoaded(Sqlite3.SourceId()) is { } why)
                throw new InvalidOperationException(why);
            Volatile.Write(ref _initialized, 1);
        }
    }

    /// <summary>The SQLite build this provider was tested with (SourceGear.sqlite3, pinned exactly in
    /// Directory.Packages.props, its file's hash checked at build): its sqlite3_libversion_number and sqlite3_sourceid,
    /// as the library loaded says, wherever it was loaded from.</summary>
    internal const int TestedVersion = 3_050_004;

    /// <summary>The AppContext switch an app sets (true) when it ships a SQLite build of its own: one that isn't the build
    /// tested is then accepted if it has what the provider uses.</summary>
    internal const string OtherBuildSwitch = "CodeFusion.Data.Sqlite.OwnSqliteBuild";

    internal const string TestedSourceId = "2025-07-30 19:33:53 4d8adfb30e03f9cf27f800a2c1ba3c48fb4ca1b08b0f5ed59a4d5ecbf45e20a3";

    /// <summary>Why the library loaded, saying it was built from <paramref name="sourceId"/>, can't serve the provider, by
    /// the app's <see cref="OtherBuildSwitch"/> and what the library reports; null when it can.</summary>
    internal static string? CheckLoaded(string sourceId) =>
        Unfit(Sqlite3.LibVersionNumber(), sourceId, AppContext.TryGetSwitch(OtherBuildSwitch, out bool own) && own, Sqlite3.ThreadSafe(), Sqlite3.CompileOptions());

    /// <summary>Why a SQLite of <paramref name="version"/> and <paramref name="sourceId"/>, <paramref name="threadSafe"/>
    /// (sqlite3_threadsafe) and the compile options <paramref name="used"/> says it has (null: it can't say), can't serve
    /// the provider; null when it can. It must be the build tested, unless the app ships its own
    /// (<paramref name="otherBuildAllowed"/>, <see cref="OtherBuildSwitch"/>); any build must have what's used.</summary>
    internal static string? Unfit(int version, string sourceId, bool otherBuildAllowed, int threadSafe, Func<string, bool>? used)
    {
        if (version < Sqlite3.MinVersion) return $"SQLite {version} is older than {Sqlite3.MinVersion}, which the generated SQL needs";
        if (!otherBuildAllowed && (version != TestedVersion || sourceId != TestedSourceId))
            return $"the SQLite loaded is {version} from {sourceId}, not the build CodeFusion.Data.Sqlite was tested with ({TestedVersion} from " +
                $"{TestedSourceId}). An app that ships its own SQLite on purpose, and tests it, sets the AppContext switch " +
                $"{OtherBuildSwitch} to true; one that didn't mean to (a newer SourceGear.sqlite3 through a direct reference) pins " +
                "that reference to the tested version instead";
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
    // the last bit), so an event-loop batch can tell, without a lookup, whether a read sees a table it wrote. Where SQLite
    // reports nothing, a statement counts as reading or writing every table: AUTOINCREMENT's sqlite_sequence, the
    // connection's own state (last_insert_rowid(), changes()), pragmas, virtual tables (a module's own SQL), and every
    // statement once the schema changed at run time (SQLite prepares older ones again, unreported).
    //
    // The authorizer is set once (WatchTables), on the one connection whose sets are read: setting it expires every
    // prepared statement, and while it is set SQLite asks it only as it compiles, so it costs nothing as statements run.

    ulong[] _reads = new ulong[64], _writes = new ulong[64];
    readonly Dictionary<string, int> _tableBits = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _newTables = [];
    ulong _preparingReads, _preparingWrites, _virtualTables;
    bool _watching, _collecting, _quiet, _schemaChanged;
    GCHandle _self;

    /// <summary>From now on, every statement this connection prepares records the tables it reads and writes. Once,
    /// after the migrations.</summary>
    internal void WatchTables()
    {
        if (_watching) return;
        // setting it expires the statements prepared so far, which SQLite would prepare again unwatched (as after a
        // schema change): they go, and each is prepared afresh, watched, the next time it is used
        FinalizeAll();
        _self = GCHandle.Alloc(this);
        _watching = true;
        _ = Sqlite3.SetAuthorizer(_db, &Authorize, GCHandle.ToIntPtr(_self));
    }

    void FinalizeAll()
    {
        for (int i = 0; i < _statements.Length; i++)
            if (_statements[i] != 0)
            {
                _ = Sqlite3.Finalize(_statements[i]);
                _statements[i] = 0;
            }
        for (int i = 0; i < _more.Length; i++)
            if (_more[i] is { } more)
            {
                foreach (var s in more) _ = Sqlite3.Finalize(s);
                _more[i] = null;
            }
    }

    /// <summary>The tables <paramref name="s"/> reads, prepared now if it wasn't; every table when it can't be known.</summary>
    public ulong ReadsOf(SqliteStatement s)
    {
        _ = Statement(s);
        return _schemaChanged ? ulong.MaxValue : _reads[s.Id];
    }

    /// <summary>The tables <paramref name="s"/> writes, prepared now if it wasn't; every table when it can't be known.</summary>
    public ulong WritesOf(SqliteStatement s)
    {
        _ = Statement(s);
        return _schemaChanged ? ulong.MaxValue : _writes[s.Id];
    }

    ulong Bit(byte* name)
    {
        string table = Marshal.PtrToStringUTF8((nint)name) ?? "";
        if (!_tableBits.TryGetValue(table, out int bit))
        {
            bit = Math.Min(_tableBits.Count, 63);
            _tableBits[table] = bit;
            _newTables.Add(table);
        }
        return 1UL << bit;
    }

    /// <summary>What the authorizer threw while preparing: the prepare fails with it (<see cref="Prepare"/>).</summary>
    Exception? _authorizerFailure;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Authorize(nint self, int action, byte* first, byte* second, byte* database, byte* trigger)
    {
        var conn = (SqliteConn)GCHandle.FromIntPtr(self).Target!;
        try
        {
#if DEBUG
            AuthorizeFault?.Invoke();
#endif
            return conn.Record(action, first, second);
        }
#pragma warning disable CA1031 // nothing may leave an UnmanagedCallersOnly method: it would end the process
        catch (Exception e)
#pragma warning restore CA1031
        {
            conn._authorizerFailure ??= e;
            return Sqlite3.Deny; // the prepare fails, and Prepare throws what was caught
        }
    }

#if DEBUG
    /// <summary>Debug, tests: runs in the authorizer, as an out-of-memory there would.</summary>
    internal static Action? AuthorizeFault;
#endif

    int Record(int action, byte* first, byte* second)
    {
        var conn = this;
        if (conn._quiet) return Sqlite3.Ok;
        if (!conn._collecting)
        {
            // SQLite compiles outside Prepare: a statement prepared again after the schema changed, or DDL run directly
            conn._schemaChanged = true;
            return Sqlite3.Ok;
        }
        switch (action)
        {
            case Sqlite3.AuthRead when first is not null:
                conn._preparingReads |= IsSequence(first) ? ulong.MaxValue : conn.Bit(first);
                break;
            case Sqlite3.AuthInsert or Sqlite3.AuthUpdate or Sqlite3.AuthDelete when first is not null:
                conn._preparingWrites |= conn.Bit(first);
                break;
            case Sqlite3.AuthFunction when second is not null && IsConnectionState(second):
                conn._preparingReads = ulong.MaxValue;
                break;
            case Sqlite3.AuthPragma:
                conn._preparingReads = conn._preparingWrites = ulong.MaxValue;
                break;
            case >= Sqlite3.AuthCreateIndex and <= Sqlite3.AuthCreateView or >= Sqlite3.AuthDropIndex and <= Sqlite3.AuthDropView
                or >= Sqlite3.AuthAttach and <= Sqlite3.AuthDropVtable:
                // DDL: once it runs, statements prepared before it may read and write more than they said
                conn._schemaChanged = true;
                conn._preparingWrites = ulong.MaxValue;
                break;
        }
        return Sqlite3.Ok;
    }

    /// <summary>sqlite_sequence: AUTOINCREMENT writes it without telling the authorizer.</summary>
    static bool IsSequence(byte* table) => MemoryMarshal.CreateReadOnlySpanFromNullTerminated(table).SequenceEqual("sqlite_sequence"u8);

    /// <summary>A function that reads the connection's own state, which no table holds.</summary>
    static bool IsConnectionState(byte* function)
    {
        var name = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(function);
        return name.SequenceEqual("last_insert_rowid"u8) || name.SequenceEqual("changes"u8) || name.SequenceEqual("total_changes"u8)
            || name.SequenceEqual("changes64"u8) || name.SequenceEqual("total_changes64"u8);
    }

    /// <summary>Marks the tables first met while preparing that are virtual: their module reads and writes with SQL of
    /// its own, which the authorizer never sees, so a statement that touches one touches everything.</summary>
    void ClassifyNewTables()
    {
        if (_newTables.Count == 0) return;
        ReadOnlySpan<byte> sql = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = ?1 AND sql LIKE 'CREATE VIRTUAL TABLE%'\0"u8;
        _quiet = true;
        try
        {
            foreach (string table in _newTables)
            {
                nint stmt;
                fixed (byte* p = sql)
                    if (Sqlite3.Prepare(_db, p, -1, 0, out stmt, out _) != Sqlite3.Ok) continue;
                try
                {
                    byte[] name = System.Text.Encoding.UTF8.GetBytes(table);
                    fixed (byte* n = name)
                    {
                        _ = Sqlite3.BindText(stmt, 1, n, name.Length, -1); // SQLITE_TRANSIENT: copied
                        if (Sqlite3.Step(stmt) == Sqlite3.Row) _virtualTables |= 1UL << _tableBits[table];
                    }
                }
                finally
                {
                    _ = Sqlite3.Finalize(stmt);
                }
            }
        }
        finally
        {
            _quiet = false;
            _newTables.Clear();
        }
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
        // kept and put back after: a prepare made while this one runs (none is today) must not take this one's tables
        var (outerReads, outerWrites, outerCollecting) = (_preparingReads, _preparingWrites, _collecting);
        _preparingReads = _preparingWrites = 0;
        _collecting = _watching;
        ulong reads, writes;
        try
        {
            fixed (byte* sql = s.Utf8)
            {
                byte* at = sql, end = sql + s.Utf8.Length - 1; // the NUL
                while (at < end)
                {
                    int rc = Sqlite3.Prepare(_db, at, (int)(end - at), Sqlite3.PreparePersistent, out nint stmt, out byte* tail);
                    if (_authorizerFailure is { } failure)
                    {
                        // the table tracking threw (it can't let an exception out to SQLite): this prepare fails with it
                        _authorizerFailure = null;
                        if (stmt != 0) _ = Sqlite3.Finalize(stmt);
                        throw new SqliteException($"keeping the tables of {s.Sql} failed", failure);
                    }
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
            reads = _preparingReads;
            writes = _preparingWrites;
            (_preparingReads, _preparingWrites, _collecting) = (outerReads, outerWrites, outerCollecting);
        }
        if (_watching)
        {
            ClassifyNewTables();
            bool touchesVirtual = ((reads | writes) & _virtualTables) != 0;
            _reads[s.Id] = touchesVirtual ? ulong.MaxValue : reads;
            _writes[s.Id] = touchesVirtual ? ulong.MaxValue : writes;
        }
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
    /// <remarks>Against the job this thread started (<see cref="_jobs"/>), not <see cref="_running"/>, which reads
    /// <see cref="Claimed"/> while the watchdog still holds its claim after the interrupt.</remarks>
    public bool InterruptedThis => Volatile.Read(ref _interrupted) == _jobs;

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
#if DEBUG
            AfterInterrupt?.Invoke();
#endif
        }
        finally
        {
            Volatile.Write(ref _running, job);
        }
        return true;
    }

#if DEBUG
    /// <summary>Debug, tests: runs on the watchdog's thread right after its interrupt, while it still holds its claim.</summary>
    internal static Action? AfterInterrupt;

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
