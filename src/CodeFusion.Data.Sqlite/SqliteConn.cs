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
    /// <summary>When the running job started (Stopwatch ticks), 0 between jobs: the watchdog interrupts one that runs too long.</summary>
    long _started;

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
            if (Unfit(Sqlite3.LibVersionNumber(), Sqlite3.ThreadSafe(), static o => Sqlite3.CompileOptionUsed(o) != 0) is { } why)
                throw new InvalidOperationException(why);
            Volatile.Write(ref _initialized, 1);
        }
    }

    /// <summary>Why a SQLite of <paramref name="version"/>, <paramref name="threadSafe"/> (sqlite3_threadsafe) and the
    /// compile options <paramref name="used"/> says it has can't serve the provider, or null when it can.</summary>
    internal static string? Unfit(int version, int threadSafe, Func<string, bool> used)
    {
        if (version < Sqlite3.MinVersion) return $"SQLite {version} is older than {Sqlite3.MinVersion}, which the generated SQL needs";
        // connections move between threads (readers, the writer, an event loop's batch) and the watchdog interrupts
        // from its own: THREADSAFE=1 or 2
        if (threadSafe == 0) return "SQLite was built with SQLITE_THREADSAFE=0; the provider uses connections from several threads";
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

    nint Prepare(SqliteStatement s)
    {
        if (s.Id >= _statements.Length) Array.Resize(ref _statements, Math.Max(s.Id + 1, _statements.Length * 2));
        if (s.Id >= _more.Length) Array.Resize(ref _more, _statements.Length);
        var pieces = new List<nint>(1);
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

    /// <summary>A job starts on this connection (for the watchdog).</summary>
    public void Started() => Volatile.Write(ref _started, Stopwatch.GetTimestamp());

    /// <summary>The job ended.</summary>
    public void Ended() => Volatile.Write(ref _started, 0);

    /// <summary>Interrupts the running job if it started more than <paramref name="limit"/> ticks before <paramref name="now"/>:
    /// the job it timed, if that is still the one running (its start, a timestamp, is the job's mark). An interrupt that
    /// finds no statement running does nothing, and every path that ends a job clears its start.</summary>
    public bool InterruptIfOver(long now, long limit)
    {
        long started = Volatile.Read(ref _started);
        if (started == 0 || now - started <= limit || _db == 0) return false;
        if (Volatile.Read(ref _started) != started) return false; // that job ended; another may have begun
        Sqlite3.InterruptRunning(_db);
        return true;
    }

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
    }
}
