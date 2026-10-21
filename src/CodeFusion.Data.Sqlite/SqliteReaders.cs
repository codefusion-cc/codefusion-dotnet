using System;
using System.Buffers.Binary;
using System.Threading;

namespace CodeFusion.Data.Sqlite;

/// <summary>Runs one job's statements on a connection.</summary>
internal static unsafe class SqliteRun
{
    /// <summary>
    /// Binds and steps each statement of <paramref name="job"/>. SQL that holds several statements runs each, in order. Every row any statement returns goes to
    /// <typeparamref name="TReader"/> (a read) or is kept in the job (a write: <paramref name="save"/>). Text parameters
    /// are bound in place (SQLITE_STATIC) from the job's buffer, pinned for the whole run; every parameter is bound again
    /// before every step, so no binding left over from an earlier run is ever read. Returns SQLITE_OK or the failing
    /// step's extended code; <paramref name="rows"/> and <paramref name="value"/> say what the reader saw, and
    /// <paramref name="failure"/> what it threw (the rest of the rows are stepped past).
    /// </summary>
    public static int Steps<TReader>(SqliteConn conn, SqliteJob job, object? state, bool save, out int rows, out long value, out Exception? failure)
        where TReader : IRowReader
    {
        rows = 0;
        value = 0;
        failure = null;
        Span<int> offsets = stackalloc int[MaxStackParams];
        fixed (byte* buf = job.Buf)
        {
            for (int i = 0; i < job.Count; i++)
            {
                var s = job.Statements[i];
                nint stmt = conn.Statement(s);
                var more = conn.More(s);
                int at = job.Starts[i];
                if (more is null)
                {
                    for (int p = 1; p <= s.ParamCount; p++) at = Bind(stmt, p, buf, at);
                    int rc = Rows<TReader>(conn, stmt, job, state, save, ref rows, ref value, ref failure);
                    if (rc != Sqlite3.Ok) return rc;
                    continue;
                }
                // SQL of several statements: each binds the parameters it names, from where each lies in the buffer
                var where = s.ParamCount <= offsets.Length ? offsets[..s.ParamCount] : new int[s.ParamCount];
                for (int p = 0; p < where.Length; p++)
                {
                    where[p] = at;
                    at = Skip(buf, at);
                }
                for (int k = -1; k < more.Length; k++)
                {
                    nint piece = k < 0 ? stmt : more[k];
                    int count = Sqlite3.BindParameterCount(piece);
                    for (int p = 1; p <= count; p++) Bind(piece, p, buf, where[p - 1]);
                    int rc = Rows<TReader>(conn, piece, job, state, save, ref rows, ref value, ref failure);
                    if (rc != Sqlite3.Ok) return rc;
                }
            }
        }
        return Sqlite3.Ok;
    }

    const int MaxStackParams = 64;

    /// <summary>Binds parameter <paramref name="p"/> from the buffer at <paramref name="at"/>; where the next one starts.</summary>
    static int Bind(nint stmt, int p, byte* buf, int at)
    {
        switch (buf[at++])
        {
            case SqliteJob.TagNull:
                _ = Sqlite3.BindNull(stmt, p);
                return at;
            case SqliteJob.TagInt64:
                _ = Sqlite3.BindInt64(stmt, p, BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buf + at, 8)));
                return at + 8;
            case SqliteJob.TagDouble:
                _ = Sqlite3.BindDouble(stmt, p, BinaryPrimitives.ReadDoubleLittleEndian(new ReadOnlySpan<byte>(buf + at, 8)));
                return at + 8;
            default:
                int len = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf + at, 4));
                _ = Sqlite3.BindText(stmt, p, buf + at + 4, len, 0); // SQLITE_STATIC: the buffer stays pinned until the reset
                return at + 4 + len;
        }
    }

    /// <summary>Where the parameter after the one at <paramref name="at"/> starts.</summary>
    static int Skip(byte* buf, int at) => buf[at] switch
    {
        SqliteJob.TagNull => at + 1,
        SqliteJob.TagInt64 or SqliteJob.TagDouble => at + 9,
        _ => at + 5 + BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf + at + 1, 4)),
    };

    /// <summary>Steps one statement to its end: SQLITE_OK, or the failing step's extended code.</summary>
    static int Rows<TReader>(SqliteConn conn, nint stmt, SqliteJob job, object? state, bool save, ref int rows, ref long value, ref Exception? failure)
        where TReader : IRowReader
    {
        int rc;
        while ((rc = Sqlite3.Step(stmt)) == Sqlite3.Row)
        {
            rows++;
            if (save) job.Save(stmt);
            else if (failure is null)
            {
                try
                {
                    value = TReader.Read(new SqliteRow(stmt), state);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }
        }
        _ = Sqlite3.Reset(stmt);
        if (rc == Sqlite3.Done) return Sqlite3.Ok;
        int code = Sqlite3.ExtendedErrCode(conn.Db);
        return code != Sqlite3.Ok ? code : rc;
    }

    /// <summary>A failed statement as CodeFusion.Data's result.</summary>
    public static DataResult Failed(SqliteConn conn, int code, int rows = 0)
    {
        var status = code switch
        {
            Sqlite3.ConstraintUnique or Sqlite3.ConstraintPrimaryKey => DataStatus.UniqueViolation,
            _ when (code & 0xFF) is Sqlite3.Busy or Sqlite3.Locked => DataStatus.Busy,
            _ when (code & 0xFF) == Sqlite3.Interrupt => DataStatus.StatementTimeout,
            _ => DataStatus.Failed,
        };
        return new DataResult(status, rows, 0, SqliteException.From(conn.Db, code), Sqlite3.CodeName(code));
    }
}

/// <summary>
/// The reader connections. A read takes a free one, runs on the caller's thread and gives it back: no queue and no
/// thread hop, which would cost more than the read.
/// <para>
/// Snapshots are kept: a connection stays in its read transaction (one WAL snapshot) from read to read until the writer
/// commits, and only then starts a new one. Starting and ending a read transaction per statement takes the WAL index's
/// locks, shared by every connection to the file: that cost most of a point read and kept 8 threads at the speed of
/// one. A read still sees every write that completed before it started: the writer moves the commit clock after its
/// COMMIT and before it completes the group's writes, and a read whose connection's snapshot is older than the clock
/// starts a new one first. The writer ends idle connections' old snapshots after each group (<see cref="Sweep"/>), so
/// none keeps the WAL from being checkpointed and restarted.
/// </para>
/// </summary>
internal sealed class SqliteReaders : IDisposable
{
    /// <summary>Per connection at [i * Stride]: 0 free, 1 taken. A cache line each (128 bytes: Apple's line, two of
    /// x64's), so threads taking different connections don't pass one line between their cores.</summary>
    readonly int[] _taken;
    const int Stride = 32;
    readonly SqliteConn[] _conns;
    /// <summary>Per connection: the commit clock when its read transaction began; -1 outside one.</summary>
    readonly long[] _snapshot;
    readonly CommitClock _clock;
    readonly SqliteStatement _begin = new("BEGIN"), _end = new("COMMIT");
    int _stopped;
    /// <summary>Exclusive mode (<see cref="SqliteDatabase.Exclusive"/>): the one connection is the writer's, which takes it
    /// for each group as a read takes it (<see cref="Enter"/>). Reads keep their read transaction from read to read; the
    /// writer ends it before its own BEGIN.</summary>
    readonly bool _shared;

    public SqliteReaders(string path, SqliteDatabase options, CommitClock clock)
    {
        _clock = clock;
        int n = options.Readers;
        _conns = new SqliteConn[n];
        _taken = new int[n * Stride];
        _snapshot = new long[n];
        Array.Fill(_snapshot, -1);
        try
        {
            for (int i = 0; i < n; i++) _conns[i] = SqliteConn.Open(path, options, writer: false);
        }
        catch
        {
            foreach (var c in _conns) c?.Dispose();
            throw;
        }
    }

    /// <summary>Exclusive mode: the writer's connection, shared with it.</summary>
    public SqliteReaders(SqliteConn writer, CommitClock clock)
    {
        _clock = clock;
        _conns = [writer];
        _taken = new int[Stride];
        _snapshot = [-1];
        _shared = true;
    }

    internal SqliteConn[] Connections => _conns;

    /// <summary>Exclusive mode: the writer takes the connection for a group, waiting for a read running on it, stopped or
    /// not (the writer commits what has queued after a stop).</summary>
    internal void Enter()
    {
        // turns: a read that waited goes before the next group, and no read starts while the writer waits, so neither
        // side waits for more than one of the other's. No 1 ms sleeps: a group a millisecond late would pay it at every
        // commit.
        if (Volatile.Read(ref _readersWaiting) > 0)
        {
            long seen = Volatile.Read(ref _readsDone);
            SpinWait turn = default;
            while (Volatile.Read(ref _readsDone) == seen && Volatile.Read(ref _readersWaiting) > 0) turn.SpinOnce(sleep1Threshold: -1);
        }
        Volatile.Write(ref _writerWaits, 1);
        SpinWait spin = default;
        while (Interlocked.CompareExchange(ref _taken[0], 1, 0) != 0) spin.SpinOnce(sleep1Threshold: -1);
        Volatile.Write(ref _writerWaits, 0);
    }

    /// <summary>Exclusive mode: the writer wants the connection; reads wait to start.</summary>
    int _writerWaits;

    /// <summary>Exclusive mode: reads waiting for the connection, and reads done; the writer lets one through after a
    /// group.</summary>
    int _readersWaiting;
    long _readsDone;

    /// <summary>Exclusive mode: takes the one connection when the writer isn't waiting for it; -1 once stopped.</summary>
    int TakeShared()
    {
        bool counted = false;
        SpinWait spin = default;
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _writerWaits) == 0 && Volatile.Read(ref _taken[0]) == 0 && Interlocked.CompareExchange(ref _taken[0], 1, 0) == 0)
                {
                    if (Volatile.Read(ref _stopped) == 0) return 0;
                    Volatile.Write(ref _taken[0], 0);
                    return -1;
                }
                if (Volatile.Read(ref _stopped) != 0) return -1;
                if (!counted)
                {
                    Interlocked.Increment(ref _readersWaiting);
                    counted = true;
                }
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref _readersWaiting);
        }
    }

    /// <summary>Exclusive mode: the writer gives the connection back, before it completes the group's writes.</summary>
    internal void Exit() => Volatile.Write(ref _taken[0], 0);

    /// <summary>Exclusive mode: the thread whose event-loop batch holds the connection (it took it with <see cref="Enter"/>),
    /// 0 when none does. Its reads run in the batch's transaction.</summary>
    internal int Owner
    {
        get => Volatile.Read(ref _owner);
        set => Volatile.Write(ref _owner, value);
    }

    int _owner;

    /// <summary>Takes a free connection, starting from one picked by the thread so threads spread out; -1 once stopped.</summary>
    int Take()
    {
        if (_shared) return TakeShared();
        int n = _conns.Length;
        int start = (int)((uint)Environment.CurrentManagedThreadId % (uint)n);
        SpinWait spin = default;
        while (true)
        {
            for (int k = 0; k < n; k++)
            {
                int i = start + k;
                if (i >= n) i -= n;
                if (Volatile.Read(ref _taken[i * Stride]) == 0 && (!_shared || Volatile.Read(ref _writerWaits) == 0) && Interlocked.CompareExchange(ref _taken[i * Stride], 1, 0) == 0)
                {
                    if (Volatile.Read(ref _stopped) == 0) return i;
                    Volatile.Write(ref _taken[i * Stride], 0);
                    return -1;
                }
            }
            if (Volatile.Read(ref _stopped) != 0) return -1;
            spin.SpinOnce(); // more reads running than connections: one is free soon (SpinWait yields at once on one core)
        }
    }

    public DataResult Run<TReader>(SqliteJob job, object? state, CancellationToken aborted) where TReader : IRowReader
    {
        if (aborted.IsCancellationRequested) return DataResult.Of(DataStatus.Canceled);
        // the thread whose event-loop batch holds the connection reads in the batch's transaction, which it holds already
        bool own = _shared && Volatile.Read(ref _owner) == Environment.CurrentManagedThreadId;
        int i = own ? 0 : Take();
        if (i < 0) return DataResult.Of(DataStatus.Stopped);
        var conn = _conns[i];
        conn.Started();
        try
        {
            long now = Volatile.Read(ref _clock.Seq);
            if (_shared)
            {
                // in its own event-loop batch, a read keeps the batch's transaction and its time
                if (!own)
                {
                    conn.ClearClock();
                    // a read transaction kept from read to read, as the readers' snapshots are: the writer ends it before its BEGIN
                    if (Sqlite3.GetAutocommit(conn.Db) != 0 && conn.Once(_begin) is var b && b != Sqlite3.Ok) return SqliteRun.Failed(conn, b);
                }
            }
            else if (_snapshot[i] != now)
            {
                if (_snapshot[i] >= 0) _ = conn.Once(_end);
                _snapshot[i] = -1;
                int b = conn.Once(_begin);
                if (b != Sqlite3.Ok) return SqliteRun.Failed(conn, b);
                _snapshot[i] = now; // the snapshot itself is taken at the first read: as new as this, or newer
            }
            int rc = SqliteRun.Steps<TReader>(conn, job, state, save: false, out int rows, out long value, out var failure);
            if (rc != Sqlite3.Ok)
            {
                if (!_shared && Sqlite3.GetAutocommit(conn.Db) != 0) _snapshot[i] = -1; // the error ended the transaction
                return SqliteRun.Failed(conn, rc, rows);
            }
            return failure is null ? new DataResult(DataStatus.Ok, rows, value) : new DataResult(DataStatus.ReaderFailed, rows, value, failure);
        }
        finally
        {
            conn.Ended();
            if (!own)
            {
                if (_shared) Interlocked.Increment(ref _readsDone);
                Volatile.Write(ref _taken[i * Stride], 0);
            }
        }
    }

    /// <summary>Ends the read transactions of idle connections whose snapshot is older than the last commit, so they
    /// don't hold the WAL back from a checkpoint. The writer calls it after each group.</summary>
    public void Sweep()
    {
        if (_shared) return;
        long now = Volatile.Read(ref _clock.Seq);
        for (int i = 0; i < _conns.Length; i++)
        {
            if (Volatile.Read(ref _snapshot[i]) is var s && (s < 0 || s == now)) continue;
            if (Interlocked.CompareExchange(ref _taken[i * Stride], 1, 0) != 0) continue; // in use: it refreshes itself
            if (_snapshot[i] >= 0 && _snapshot[i] != now && Volatile.Read(ref _stopped) == 0)
            {
                _ = _conns[i].Once(_end);
                _snapshot[i] = -1;
            }
            Volatile.Write(ref _taken[i * Stride], 0);
        }
    }

    /// <summary>Takes no more reads, waits for those running, closes the connections.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        for (int i = 0; i < _conns.Length; i++)
        {
            SpinWait spin = default;
            while (Interlocked.CompareExchange(ref _taken[i * Stride], 1, 0) != 0) spin.SpinOnce();
            if (_shared) Volatile.Write(ref _taken[0], 0); // the writer's: it closes it after its last group
            else _conns[i].Dispose();
        }
    }
}

/// <summary>How many groups the writer has committed: the readers' snapshots are as new as the clock they began at.</summary>
internal sealed class CommitClock
{
    internal long Seq;
}
