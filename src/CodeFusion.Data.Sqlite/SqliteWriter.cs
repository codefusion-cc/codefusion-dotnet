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
        CloseConnection(); // the loop's own connection: closed on its thread, after the last group
    }

    void RunGroup(int n)
    {
        // in exclusive mode the connection is the readers' too: taken for the transaction, and given back before any of
        // the group's writes completes, whose continuation may read
        Enter();
        DataResult? failed;
        try
        {
            failed = Transaction(_group, n);
        }
        finally
        {
            Exit();
        }
        Complete(_group, n, failed, Inline);
        AfterGroup();
    }

    // ---------- one transaction's writes: a group's, or a wake-up's ----------
    //
    // Every write runs in a savepoint of its own, so one that fails, or can't be prepared, is undone alone and the rest
    // commit. When SQLite rolls the whole transaction back by itself (out of memory, an I/O error, a full disk, an
    // interrupt), the writes it held run again once in a new one; if that is rolled back too, they fail. No write is
    // answered Ok unless the COMMIT that holds it returned SQLITE_OK.

    /// <summary>
    /// The group's writes in one transaction: null once committed (the commit clock moved), else the failure of every
    /// write that has none of its own. Completes nothing.
    /// </summary>
    DataResult? Transaction(SqliteJob?[] jobs, int n)
    {
        int rc = Begin();
        if (rc != Sqlite3.Ok) return FailedGroup(SqliteRun.Failed(_conn, rc));
        try
        {
            if (Run(jobs, 0, n, attempts: 2) is { } lost) return FailedGroup(lost);
            return Commit();
        }
        finally
        {
            RollbackOpen(); // after an exception: no transaction stays open
        }
    }

    /// <summary>Runs the writes in jobs[from..to) that are still standing, in the transaction open now, and again in a new
    /// one while SQLite rolls it back, up to <paramref name="attempts"/> in all. Null while a transaction is open with them;
    /// else the failure that lost the last one (none is open).</summary>
    DataResult? Run(SqliteJob?[] jobs, int from, int to, int attempts)
    {
        for (int attempt = 1; ; attempt++)
        {
            int lostAt = -1;
            for (int i = from; i < to && lostAt < 0; i++)
            {
                var job = jobs[i];
                if (job is null || job.Failed) continue;
                if (job.Aborted.IsCancellationRequested)
                {
                    job.Failed = true; // completed with the rest, after the transaction
                    job.Failure = DataResult.Of(DataStatus.Canceled);
                    continue;
                }
                if (!RunOne(job)) lostAt = i;
            }
            if (lostAt < 0) return null;
            // SQLite rolled the transaction back: what ran in it is gone. The write it failed on keeps its error.
            var lost = jobs[lostAt]!.Failure;
            if (attempt == attempts) return lost;
            Interlocked.Increment(ref Counters.Reruns);
            for (int i = from; i < to; i++)
                if (jobs[i] is { Failed: false } again) again.Restart();
            int rc = Begin();
            if (rc != Sqlite3.Ok) return SqliteRun.Failed(_conn, rc);
        }
    }

    /// <summary>One write in its savepoint, inside the open transaction. Its failure, if it fails, is on the job; false
    /// when the transaction is gone (SQLite rolled it back).</summary>
    bool RunOne(SqliteJob job)
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
                    rc = _conn.Once(_release);
                    if (rc == Sqlite3.Ok) return true;
                }
                else Interlocked.Increment(ref Counters.FailedAlone);
            }
            job.Failed = true;
            job.Failure = SqliteRun.Failed(_conn, rc, job.SavedRows);
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            // a statement that can't be prepared, or bound with the wrong parameters: this write fails, alone
            Interlocked.Increment(ref Counters.FailedAlone);
            job.Failed = true;
            job.Failure = new DataResult(DataStatus.Failed, job.SavedRows, 0, e);
        }
        finally
        {
            _conn.Ended();
        }
        return Undo();
    }

    /// <summary>After a write failed: its savepoint undone, the transaction kept. False when the transaction is gone.</summary>
    bool Undo()
    {
        if (Sqlite3.GetAutocommit(_conn.Db) != 0) return false;
        try
        {
            if (_conn.Once(_rollbackTo) == Sqlite3.Ok && _conn.Once(_release) == Sqlite3.Ok) return true;
        }
        catch (SqliteException)
        {
        }
        RollbackOpen(); // a savepoint that can't be undone: the whole transaction goes
        return false;
    }

    /// <summary>BEGIN IMMEDIATE, after the reads' transaction in Exclusive mode, with cf_now() set to now.</summary>
    int Begin()
    {
        EndRead();
        *_conn.Clock = Sqlite.ClockNow(); // cf_now(): the transaction's start, for every write in it
        _conn.Started();
        try
        {
            return _conn.Once(_beginGroup);
        }
        finally
        {
            _conn.Ended();
        }
    }

    /// <summary>COMMIT: null once committed (the commit clock moved before any write completes), else the failure of every
    /// write in it, rolled back.</summary>
    DataResult? Commit()
    {
        if (Sqlite3.GetAutocommit(_conn.Db) != 0) return FailedGroup(DataResult.Of(DataStatus.Failed)); // gone: nothing to commit
        int rc;
        _conn.Started();
        try
        {
            rc = _conn.Once(_commit);
        }
        finally
        {
            _conn.Ended();
        }
        if (rc != Sqlite3.Ok)
        {
            var failed = SqliteRun.Failed(_conn, rc);
            RollbackOpen();
            return FailedGroup(failed);
        }
        Interlocked.Increment(ref Counters.Commits);
        Volatile.Write(ref Clock.Seq, Clock.Seq + 1); // before any write completes: a read that starts after sees it
        return null;
    }

    DataResult FailedGroup(in DataResult failure)
    {
        RollbackOpen();
        Interlocked.Increment(ref Counters.FailedGroups);
        return failure;
    }

    void RollbackOpen()
    {
        if (Sqlite3.GetAutocommit(_conn.Db) == 0) _ = _conn.Once(_rollback);
    }

    /// <summary>Completes jobs[0..n): each with its own failure, else <paramref name="failed"/> when the transaction
    /// failed, else its rows. The list is cleared.</summary>
    void Complete(SqliteJob?[] jobs, int n, DataResult? failed, bool inline)
    {
        for (int i = 0; i < n; i++)
        {
            if (jobs[i] is not { } job) continue;
            jobs[i] = null;
            if (job.Failed) job.Complete(job.Failure, inline);
            else if (failed is { } f) job.Complete(f, inline);
            else
            {
                Interlocked.Increment(ref Counters.Writes);
                job.Complete(job.HandRows(), inline);
            }
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
        if (*_conn.WalFrames < _options.CheckpointFrames || Volatile.Read(ref _closed)) return;
        Enter();
        try
        {
            if (Volatile.Read(ref _closed)) return;
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

    /// <summary>The thread whose wake-up holds the connection; 0 when none does.</summary>
    int _batchThread;
    /// <summary>The batch's writes waiting to complete; the spare takes new ones while those complete.</summary>
    SqliteJob?[] _batch = new SqliteJob?[64], _spare = new SqliteJob?[64];
    int _batchCount;
    /// <summary>The batch's writes before this index are committed; those after it are in the open transaction.</summary>
    int _batchCommitted;
    /// <summary>The batch's transaction is open (BEGIN ran and nothing ended it).</summary>
    bool _batchOpen;

    /// <summary>
    /// A write made on an event loop's thread: runs it now, in the transaction this wake-up opened (opening it if this
    /// is its first write since the last commit), and keeps it until <see cref="EndBatch"/> completes it. False when
    /// another loop's batch holds the connection, or the source stops: the write then goes to the writer thread.
    /// </summary>
    bool Batch(SqliteJob job)
    {
        int me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _batchThread) != me)
        {
            if (Volatile.Read(ref _stopped) || Interlocked.CompareExchange(ref _batchThread, me, 0) != 0) return false;
            Readers!.Enter(); // after the read or group running, in turn
            if (Volatile.Read(ref _stopped) || Volatile.Read(ref _closed))
            {
                // stopped while it waited: the writer refuses it, and the connection may be closing
                Volatile.Write(ref _batchThread, 0);
                Readers.Exit();
                return false;
            }
            Readers.Owner = me;
        }
        job.Queued();
        if (_batchCount == _batch.Length)
        {
            Array.Resize(ref _batch, _batch.Length * 2);
            Array.Resize(ref _spare, _batch.Length);
        }
        _batch[_batchCount++] = job;
        if (!_batchOpen)
        {
            int rc = Begin();
            if (rc != Sqlite3.Ok)
            {
                job.Failed = true;
                job.Failure = FailedGroup(SqliteRun.Failed(_conn, rc));
                _batchCommitted = _batchCount;
                return true;
            }
            _batchOpen = true;
        }
        // rolled back: the writes the transaction held since the last commit run again, once, in a new one
        if (Run(_batch, _batchCount - 1, _batchCount, attempts: 1) is not null) Rerun();
        return true;
    }

    /// <summary>The batch's transaction is gone: its writes since the last commit run again once in a new one, or fail
    /// with the loss.</summary>
    void Rerun()
    {
        _batchOpen = false;
        int rc = Begin();
        DataResult? lost = rc != Sqlite3.Ok ? FailedGroup(SqliteRun.Failed(_conn, rc)) : null;
        if (lost is null)
        {
            _batchOpen = true;
            Interlocked.Increment(ref Counters.Reruns);
            for (int i = _batchCommitted; i < _batchCount; i++)
                if (_batch[i] is { Failed: false } again) again.Restart();
            lost = Run(_batch, _batchCommitted, _batchCount, attempts: 1);
            if (lost is null) return;
            lost = FailedGroup(lost.Value);
        }
        _batchOpen = false;
        for (int i = _batchCommitted; i < _batchCount; i++)
            if (_batch[i] is { Failed: false } job)
            {
                job.Failed = true;
                job.Failure = lost.Value;
            }
        _batchCommitted = _batchCount;
    }

    /// <summary>Commits the batch's open transaction now: before a read on its loop, so the read never sees a write that
    /// isn't committed, and at the batch's end. The writes stay waiting to complete.</summary>
    internal void CommitBatch()
    {
        if (!_batchOpen) return;
        if (Sqlite3.GetAutocommit(_conn.Db) != 0) Rerun(); // gone, and not by a write of its own: run them again
        if (!_batchOpen) return;
        _batchOpen = false;
        if (Commit() is { } failed)
            for (int i = _batchCommitted; i < _batchCount; i++)
                if (_batch[i] is { Failed: false } job)
                {
                    job.Failed = true;
                    job.Failure = failed;
                }
        _batchCommitted = _batchCount;
    }

    /// <summary>
    /// The end of this thread's wake-up: commits its batch, gives the connection back, and completes the batch's writes
    /// here, so their answers join the wake-up's sends. A completion that writes again opens a new batch, which is
    /// committed the same way before this returns. Nothing to do on a thread with no batch.
    /// </summary>
    internal void EndBatch()
    {
        int me = Environment.CurrentManagedThreadId;
        while (Volatile.Read(ref _batchThread) == me)
        {
            CommitBatch();
            // the batch's jobs, and a fresh list for any batch their completions open
            var jobs = _batch;
            int n = _batchCount;
            _batch = _spare;
            _spare = jobs;
            _batchCount = _batchCommitted = 0;
            ReleaseBatch();
            Complete(jobs, n, null, inline: true);
            if (Volatile.Read(ref _batchThread) == 0) AfterGroup(); // no new batch holds the connection: checkpoint now
        }
    }

    void ReleaseBatch()
    {
        Readers!.Owner = 0;
        Volatile.Write(ref _batchThread, 0);
        Readers.Exit();
        if (Volatile.Read(ref _closeWhenFree)) CloseIfFree(); // the source stopped while this batch held the connection
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

    // ---------- closing ----------

    /// <summary>The connection is closed.</summary>
    bool _closed;
    /// <summary>Stop couldn't take the connection in time (a loop held its batch): the holder closes it on release.</summary>
    bool _closeWhenFree;
    static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    /// <summary>Closes the connection once nothing uses it. In Exclusive mode a loop's batch may hold it: waits for it at
    /// most <see cref="CloseWait"/>, then leaves the closing to the batch's end.</summary>
    void CloseConnection()
    {
        if (!_options.Exclusive)
        {
            _closed = true;
            _conn.Dispose();
            return;
        }
        Volatile.Write(ref _closeWhenFree, true);
        if (!Readers!.TryEnter(CloseWait)) return; // the batch holding it closes it
        Close();
    }

    void CloseIfFree()
    {
        if (Readers!.TryEnter(TimeSpan.Zero)) Close();
    }

    /// <summary>Holding the connection: closes it, once.</summary>
    void Close()
    {
        try
        {
            if (Volatile.Read(ref _closed)) return;
            RollbackOpen();
            Volatile.Write(ref _closed, true);
            _conn.Dispose();
        }
        finally
        {
            Readers!.Exit();
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
