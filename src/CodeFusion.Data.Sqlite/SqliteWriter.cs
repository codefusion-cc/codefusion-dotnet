using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.Data.Sqlite;

/// <summary>
/// Group commit on one thread and one connection. Writes queue up; the writer takes what has queued (up to
/// <see cref="SqliteDatabase.MaxGroup"/>), runs them in one transaction and commits once, so the group shares one WAL
/// append and, with synchronous=FULL, one sync. A write of several statements runs inside a savepoint; when one of its
/// statements fails only that write is undone (a single statement SQLite undoes itself) and the rest of the group
/// commits, so nothing reruns: of two writes that conflict, the first queued wins. A write's rows are kept until the
/// commit, then handed to its reader, then it completes, on this thread.
/// </summary>
public sealed unsafe class SqliteWriter
{
    readonly SqliteConn _conn;
    readonly SqliteDatabase _options;
    readonly Thread _thread;
    readonly Lock _lock = new();
    readonly SqliteJob?[] _ring;
    int _head, _count;
    bool _stopped, _sleeping;
    /// <summary>A group is running, on the writer's thread or a leader's: the connection is taken.</summary>
    bool _busy;
    readonly SqliteJob?[] _group;
    readonly SqliteStatement _beginGroup = new("BEGIN IMMEDIATE"), _commit = new("COMMIT"), _rollback = new("ROLLBACK"),
        _savepoint = new("SAVEPOINT w"), _release = new("RELEASE w"), _rollbackTo = new("ROLLBACK TO w");

    /// <summary>The writer's counters.</summary>
    public SqliteWriterCounters Counters { get; } = new();

    /// <summary>Writes waiting for the writer, not counting the group it runs: up to <see cref="SqliteDatabase.MaxQueued"/>,
    /// past which a write is refused as busy at once. A measure of load, for shedding it before the queue is full.</summary>
    public int Queued => Volatile.Read(ref _count);

    /// <summary>Moves after each commit, before the group's writes complete; the readers compare their snapshots with it.</summary>
    internal readonly CommitClock Clock = new();
    internal SqliteReaders? Readers;
    internal SqliteConn Connection => _conn;

    [ThreadStatic] static SqliteWriter? t_writer;

    /// <summary>A completion's continuation threw: counted against this writer.</summary>
    internal void Unexpected(Exception e) => Record(e);

    /// <summary>Whether a completion runs its continuation here: on a leader's thread always (it is the caller's own), on
    /// the writer's only when <see cref="SqliteDatabase.CompleteOnWriterThread"/> says so.</summary>
    bool Inline => _options.CompleteOnWriterThread || Thread.CurrentThread != _thread;

    void Record(Exception e)
    {
        Interlocked.CompareExchange(ref Counters.FirstUnexpected, e, null);
        Interlocked.Increment(ref Counters.Unexpected);
    }

    internal SqliteWriter(SqliteConn conn, SqliteDatabase options, string name)
    {
        _conn = conn;
        _options = options;
        _ring = new SqliteJob?[options.MaxQueued];
        _group = new SqliteJob?[options.MaxGroup];
        _thread = new Thread(Loop) { IsBackground = true, Name = "sqlite writer " + name };
    }

    bool _started;

    internal void Start()
    {
        _started = true;
        _thread.Start();
    }

    internal ValueTask<DataResult> Submit<TReader>(SqliteJob job, object? state, CancellationToken aborted) where TReader : IRowReader
    {
        job.Reader = &Rows<TReader>.Read;
        job.Writer = this;
        job.State = state;
        job.Aborted = aborted;
        var task = job.Task;
        if (_options.EventLoopBatches && EventLoopThread.Current >= 0 && Batch(job)) return task;
        bool wake = false, lead = false;
        lock (_lock)
        {
            if (_stopped || _count == _ring.Length)
            {
                var status = _stopped ? DataStatus.Stopped : DataStatus.Busy;
                job.Return();
                return new ValueTask<DataResult>(DataResult.Of(status));
            }
            job.Queued();
            if (_options.LeaderCommit && _count == 0 && !_busy)
            {
                _busy = true;
                lead = true;
            }
            else
            {
                _ring[(_head + _count) % _ring.Length] = job;
                _count++;
                wake = _sleeping && !_busy; // while a leader commits, it wakes the writer when it is done
                if (wake) _sleeping = false;
            }
        }
        if (lead) Lead(job);
        else if (wake)
            lock (_ring) Monitor.Pulse(_ring);
        return task;
    }

    static class Rows<TReader> where TReader : IRowReader
    {
        public static long Read(in SavedRow row, object? state) => TReader.Read(in row, state);
    }

    /// <summary>
    /// Leader commit (<see cref="SqliteDatabase.LeaderCommit"/>): the writer was idle and nothing was queued, so the caller
    /// commits its own write on its thread instead of waking the writer's and being woken back. Writes that arrive
    /// meanwhile queue, and the writer's thread takes them as the next group.
    /// </summary>
    void Lead(SqliteJob job)
    {
        var previous = t_writer;
        t_writer = this;
        _group[0] = job;
        try
        {
            RunGroup(1);
        }
        catch (Exception e)
        {
            Record(e);
            FailAll(1, new DataResult(DataStatus.Failed, error: e));
        }
        finally
        {
            t_writer = previous;
        }
        bool wake;
        lock (_lock)
        {
            _busy = false;
            wake = _count > 0 && _sleeping;
            if (wake) _sleeping = false;
        }
        if (wake)
            lock (_ring) Monitor.Pulse(_ring);
    }

    int Take()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_count > 0 && !_busy)
                {
                    _busy = true;
                    int n = Math.Min(_count, _group.Length);
                    for (int i = 0; i < n; i++)
                    {
                        int at = (_head + i) % _ring.Length;
                        _group[i] = _ring[at];
                        _ring[at] = null;
                    }
                    _head = (_head + n) % _ring.Length;
                    _count -= n;
                    return n;
                }
                if (_stopped && _count == 0 && !_busy) return 0;
                _sleeping = true;
            }
            lock (_ring)
            {
                // a pulse before this wait found the count raised; one after it ends the wait
                if (Volatile.Read(ref _count) == 0 || Volatile.Read(ref _busy)) Monitor.Wait(_ring, 100);
            }
        }
    }

    void Loop()
    {
        t_writer = this;
        int n;
        while ((n = Take()) > 0)
        {
            try
            {
                RunGroup(n);
            }
            catch (Exception e)
            {
                // a bug: fail what is left of the group rather than leave its callers waiting
                Record(e);
                FailAll(n, new DataResult(DataStatus.Failed, error: e));
            }
            lock (_lock) _busy = false;
        }
        _conn.Dispose(); // the loop's own connection: closed on its thread, after the last group
    }

    void RunGroup(int n)
    {
        // in exclusive mode the connection is the readers' too: taken for the transaction, and given back before any of
        // the group's writes completes, whose continuation may read
        Enter();
        DataResult? failed;
        try
        {
            failed = Transaction(n);
        }
        finally
        {
            Exit();
        }
        if (failed is { } all)
        {
            FailAll(n, all);
            return;
        }
        for (int i = 0; i < n; i++)
        {
            if (_group[i] is not { } job) continue;
            _group[i] = null;
            if (job.Failed) job.Complete(job.Failure, Inline);
            else
            {
                Interlocked.Increment(ref Counters.Writes);
                job.Complete(job.HandRows(), Inline);
            }
        }
        AfterGroup();
    }

    /// <summary>The group's writes in one transaction, run once more if SQLite rolled it back: null once committed (the
    /// commit clock moved), else the failure of every write that has none of its own. Completes nothing.</summary>
    DataResult? Transaction(int n)
    {
        for (int attempt = 0; ; attempt++)
        {
            *_conn.Clock = Sqlite.ClockNow(); // cf_now(): the group's start
            _conn.Started();
            EndRead();
            int rc = _conn.Once(_beginGroup);
            if (rc != Sqlite3.Ok)
            {
                _conn.Ended();
                Interlocked.Increment(ref Counters.FailedGroups);
                return SqliteRun.Failed(_conn, rc);
            }
            bool rolledBack = false;
            for (int i = 0; i < n && !rolledBack; i++)
            {
                var job = _group[i];
                if (job is null || job.Failed) continue;
                if (job.Aborted.IsCancellationRequested)
                {
                    // completed with the rest, after the transaction
                    job.Failed = true;
                    job.Failure = DataResult.Of(DataStatus.Canceled);
                    continue;
                }
                bool unit = job.Count > 1;
                if (unit && _conn.Once(_savepoint) is var sp && sp != Sqlite3.Ok)
                {
                    job.Failed = true;
                    job.Failure = SqliteRun.Failed(_conn, sp);
                    continue;
                }
                rc = SqliteRun.Steps<NoReader>(_conn, job, null, save: true, out _, out _, out _);
                if (rc == Sqlite3.Ok)
                {
                    if (unit) _ = _conn.Once(_release);
                    continue;
                }
                job.Failed = true;
                job.Failure = SqliteRun.Failed(_conn, rc, job.SavedRows);
                Interlocked.Increment(ref Counters.FailedAlone);
                if (Sqlite3.GetAutocommit(_conn.Db) != 0)
                {
                    // SQLite rolled the whole transaction back (an I/O error, disk full, out of memory, an interrupt):
                    // what ran before this write is gone too. This write keeps its error; the rest run again, once.
                    rolledBack = true;
                }
                else if (unit)
                {
                    _ = _conn.Once(_rollbackTo);
                    _ = _conn.Once(_release);
                }
            }
            if (rolledBack)
            {
                _conn.Ended();
                Interlocked.Increment(ref Counters.Reruns);
                for (int i = 0; i < n; i++)
                    if (_group[i] is { Failed: false } j) j.Restart();
                if (attempt == 0) continue;
                Interlocked.Increment(ref Counters.FailedGroups);
                return SqliteRun.Failed(_conn, Sqlite3.ExtendedErrCode(_conn.Db));
            }
            rc = _conn.Once(_commit);
            _conn.Ended();
            if (rc != Sqlite3.Ok)
            {
                var failed = SqliteRun.Failed(_conn, rc);
                if (Sqlite3.GetAutocommit(_conn.Db) == 0) _ = _conn.Once(_rollback);
                Interlocked.Increment(ref Counters.FailedGroups);
                return failed;
            }
            Interlocked.Increment(ref Counters.Commits);
            Volatile.Write(ref Clock.Seq, Clock.Seq + 1); // before any write completes: a read that starts after sees it
            return null;
        }
    }

    void Enter()
    {
        if (_options.Exclusive) Readers!.Enter();
    }

    void Exit()
    {
        if (_options.Exclusive) Readers!.Exit();
    }

    /// <summary>Exclusive mode: ends the read transaction the reads keep open on the connection, before a write
    /// transaction or a checkpoint. Holding the connection (<see cref="Enter"/>).</summary>
    void EndRead()
    {
        if (_options.Exclusive && Sqlite3.GetAutocommit(_conn.Db) == 0) _ = _conn.Once(_commit);
    }

    /// <summary>After a group's writes have completed: idle readers let go of the snapshots this commit made old (so none
    /// keeps the WAL from being checkpointed and restarted), and the WAL is checkpointed once it is long, outside the
    /// group's latency.</summary>
    void AfterGroup()
    {
        Readers?.Sweep();
        if (*_conn.WalFrames < _options.CheckpointFrames) return;
        Enter();
        try
        {
            EndRead();
            _ = Sqlite3.WalCheckpoint(_conn.Db, null, 0, out _, out _); // PASSIVE: never waits on a reader
        }
        finally
        {
            Exit();
        }
        Interlocked.Increment(ref Counters.Checkpoints);
        *_conn.WalFrames = 0;
    }

    // ---------- event-loop batches (SqliteDatabase.EventLoopBatches) ----------

    /// <summary>The thread whose wake-up's transaction holds the connection; 0 when none does.</summary>
    int _batchThread;
    /// <summary>The batch's writes, run and waiting for its COMMIT; the spare takes new ones while those complete.</summary>
    SqliteJob?[] _batch = new SqliteJob?[64], _spare = new SqliteJob?[64];
    int _batchCount;

    /// <summary>
    /// A write made on an event loop's thread: runs it now, in the transaction this wake-up opened (opening it if this
    /// is its first write), and keeps it until <see cref="EndBatch"/> commits. False when another loop's batch holds the
    /// connection: the write then queues for the writer thread like any other.
    /// </summary>
    bool Batch(SqliteJob job)
    {
        int me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _batchThread) != me)
        {
            if (Volatile.Read(ref _stopped) || Interlocked.CompareExchange(ref _batchThread, me, 0) != 0) return false;
            Readers!.Enter(); // after the read or group running, in turn
            Readers.Owner = me;
            EndRead();
            *_conn.Clock = Sqlite.ClockNow(); // cf_now(): the batch's start, for every write in it
            _conn.Started();
            int rc = _conn.Once(_beginGroup);
            _conn.Ended();
            if (rc != Sqlite3.Ok)
            {
                Interlocked.Increment(ref Counters.FailedGroups);
                var failed = SqliteRun.Failed(_conn, rc);
                ReleaseBatch();
                job.Queued();
                job.Complete(failed, inline: true);
                return true;
            }
        }
        job.Queued();
        if (_batchCount == _batch.Length)
        {
            Array.Resize(ref _batch, _batch.Length * 2);
            Array.Resize(ref _spare, _batch.Length);
        }
        _batch[_batchCount++] = job;
        if (job.Aborted.IsCancellationRequested)
        {
            // completed with the rest, after the batch
            job.Failed = true;
            job.Failure = DataResult.Of(DataStatus.Canceled);
            return true;
        }
        if (!RunInBatch(job)) Rerun();
        return true;
    }

    /// <summary>Runs one write of the batch in a savepoint; false when SQLite rolled the whole transaction back.</summary>
    bool RunInBatch(SqliteJob job)
    {
        _conn.Started();
        try
        {
            int rc = _conn.Once(_savepoint);
            if (rc == Sqlite3.Ok)
            {
                rc = SqliteRun.Steps<NoReader>(_conn, job, null, save: true, out _, out _, out _);
                if (rc == Sqlite3.Ok)
                {
                    _ = _conn.Once(_release);
                    return true;
                }
                Interlocked.Increment(ref Counters.FailedAlone);
            }
            job.Failed = true;
            job.Failure = SqliteRun.Failed(_conn, rc, job.SavedRows);
            if (Sqlite3.GetAutocommit(_conn.Db) != 0) return false; // an I/O error, disk full, an interrupt: all of it undone
            _ = _conn.Once(_rollbackTo);
            _ = _conn.Once(_release);
            return true;
        }
        finally
        {
            _conn.Ended();
        }
    }

    /// <summary>SQLite rolled the batch's transaction back: runs its other writes again, once, in a new one. If that
    /// fails too, they fail with it, and the batch ends empty.</summary>
    void Rerun()
    {
        Interlocked.Increment(ref Counters.Reruns);
        _conn.Started();
        int rc = _conn.Once(_beginGroup);
        _conn.Ended();
        bool ok = rc == Sqlite3.Ok;
        var failure = ok ? default : SqliteRun.Failed(_conn, rc);
        for (int i = 0; i < _batchCount; i++)
        {
            if (_batch[i] is not { Failed: false } job) continue;
            if (ok)
            {
                job.Restart();
                if (RunInBatch(job)) continue;
                ok = false;
                failure = job.Failure;
                continue;
            }
            job.Failed = true;
            job.Failure = failure;
        }
        if (!ok && Sqlite3.GetAutocommit(_conn.Db) == 0) _ = _conn.Once(_rollback);
    }

    /// <summary>
    /// The end of this thread's wake-up: commits its batch, gives the connection back, and completes the batch's writes
    /// here, so their answers join the wake-up's sends. A completion that writes again opens a new batch, which is
    /// committed the same way before this returns.
    /// </summary>
    internal void EndBatch()
    {
        int me = Environment.CurrentManagedThreadId;
        while (Volatile.Read(ref _batchThread) == me)
        {
            DataResult? failAll = null;
            if (Sqlite3.GetAutocommit(_conn.Db) == 0)
            {
                _conn.Started();
                int rc = _conn.Once(_commit);
                _conn.Ended();
                if (rc != Sqlite3.Ok)
                {
                    failAll = SqliteRun.Failed(_conn, rc);
                    if (Sqlite3.GetAutocommit(_conn.Db) == 0) _ = _conn.Once(_rollback);
                    Interlocked.Increment(ref Counters.FailedGroups);
                }
                else
                {
                    Interlocked.Increment(ref Counters.Commits);
                    Volatile.Write(ref Clock.Seq, Clock.Seq + 1);
                }
            }
            // the batch's jobs, and a fresh list for any batch their completions open
            var jobs = _batch;
            int n = _batchCount;
            _batch = _spare;
            _spare = jobs;
            _batchCount = 0;
            ReleaseBatch();
            for (int i = 0; i < n; i++)
            {
                var job = jobs[i]!;
                jobs[i] = null;
                if (job.Failed) job.Complete(job.Failure, inline: true);
                else if (failAll is { } f) job.Complete(f, inline: true);
                else
                {
                    Interlocked.Increment(ref Counters.Writes);
                    job.Complete(job.HandRows(), inline: true);
                }
            }
            if (Volatile.Read(ref _batchThread) == 0) AfterGroup(); // no new batch holds the connection: checkpoint now
        }
    }

    void ReleaseBatch()
    {
        Readers!.Owner = 0;
        Volatile.Write(ref _batchThread, 0);
        Readers.Exit();
    }

    void FailAll(int n, in DataResult result)
    {
        for (int i = 0; i < n; i++)
        {
            if (_group[i] is not { } job) continue;
            _group[i] = null;
            job.Complete(job.Failed ? job.Failure : result, Inline);
        }
    }

    /// <summary>No reader: a write's rows are kept, not read, while its transaction is open.</summary>
    readonly struct NoReader : IRowReader
    {
        public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct => 0;
    }

    /// <summary>Runs SQL on the writer's connection between groups (migrations; cold).</summary>
    internal void Execute(string sql)
    {
        if (t_writer == this)
        {
            // on the writer's own thread, in a completion: after the group's COMMIT, between transactions
            Enter();
            try
            {
                EndRead();
                _conn.Exec(sql);
            }
            finally
            {
                Exit();
            }
            Volatile.Write(ref Clock.Seq, Clock.Seq + 1);
            return;
        }
        while (true)
        {
            lock (_lock)
                if (!_busy)
                {
                    _busy = true;
                    break;
                }
            Thread.Sleep(1);
        }
        Enter();
        try
        {
            EndRead();
            _conn.Exec(sql);
            Volatile.Write(ref Clock.Seq, Clock.Seq + 1); // readers see it, as after a commit
        }
        finally
        {
            Exit();
            lock (_lock) _busy = false;
        }
    }

    /// <summary>Takes no more writes; the writer commits what has queued, then closes its connection.</summary>
    internal void Stop()
    {
        lock (_lock) _stopped = true;
        lock (_ring) Monitor.PulseAll(_ring);
        if (!_started) _conn.Dispose();
        else if (Thread.CurrentThread != _thread) _thread.Join(); // on its own thread (a continuation): it closes when its loop ends
    }

    /// <summary>Whether the writer's thread has ended.</summary>
    internal bool Stopped => !_thread.IsAlive;
}

/// <summary>A writer's counters: read them from any thread (they are updated with Interlocked).</summary>
public sealed class SqliteWriterCounters
{
    /// <summary>Transactions committed.</summary>
    public long Commits;
    /// <summary>Writes committed.</summary>
    public long Writes;
    /// <summary>Writes that failed alone while their group committed.</summary>
    public long FailedAlone;
    /// <summary>Groups run again because SQLite rolled their transaction back (an I/O error, disk full, an interrupt).</summary>
    public long Reruns;
    /// <summary>Groups that failed whole: BEGIN or COMMIT failed, or the rerun failed too.</summary>
    public long FailedGroups;
    /// <summary>Checkpoints the writer ran after a group.</summary>
    public long Checkpoints;
    /// <summary>Exceptions on the writer's thread that no failing database explains (a bug); see <see cref="FirstUnexpected"/>.</summary>
    public long Unexpected;
    /// <summary>The first of <see cref="Unexpected"/>.</summary>
    public Exception? FirstUnexpected;
}
