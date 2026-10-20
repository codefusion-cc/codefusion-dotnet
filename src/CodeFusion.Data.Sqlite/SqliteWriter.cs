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

    /// <summary>Moves after each commit, before the group's writes complete; the readers compare their snapshots with it.</summary>
    internal readonly CommitClock Clock = new();
    internal SqliteReaders? Readers;
    internal SqliteConn Connection => _conn;

    [ThreadStatic] static SqliteWriter? t_writer;

    /// <summary>A completion's continuation threw on a writer's thread: counted against that writer.</summary>
    internal static void Unexpected(Exception e)
    {
        if (t_writer is { } w) w.Record(e);
    }

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
        job.State = state;
        job.Aborted = aborted;
        var task = job.Task;
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
        for (int attempt = 0; ; attempt++)
        {
            *_conn.Clock = Sqlite.ClockNow(); // cf_now(): the group's start
            _conn.Started();
            int rc = _conn.Once(_beginGroup);
            if (rc != Sqlite3.Ok)
            {
                _conn.Ended();
                Interlocked.Increment(ref Counters.FailedGroups);
                FailAll(n, SqliteRun.Failed(_conn, rc));
                return;
            }
            bool rolledBack = false;
            for (int i = 0; i < n && !rolledBack; i++)
            {
                var job = _group[i];
                if (job is null || job.Failed) continue;
                if (job.Aborted.IsCancellationRequested)
                {
                    _group[i] = null;
                    job.Complete(DataResult.Of(DataStatus.Canceled));
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
                FailAll(n, SqliteRun.Failed(_conn, Sqlite3.ExtendedErrCode(_conn.Db)));
                return;
            }
            rc = _conn.Once(_commit);
            _conn.Ended();
            if (rc != Sqlite3.Ok)
            {
                var failed = SqliteRun.Failed(_conn, rc);
                if (Sqlite3.GetAutocommit(_conn.Db) == 0) _ = _conn.Once(_rollback);
                Interlocked.Increment(ref Counters.FailedGroups);
                FailAll(n, failed);
                return;
            }
            Interlocked.Increment(ref Counters.Commits);
            Volatile.Write(ref Clock.Seq, Clock.Seq + 1); // before any write completes: a read that starts after sees it
            for (int i = 0; i < n; i++)
            {
                if (_group[i] is not { } job) continue;
                _group[i] = null;
                if (job.Failed) job.Complete(job.Failure);
                else
                {
                    Interlocked.Increment(ref Counters.Writes);
                    job.Complete(job.HandRows());
                }
            }
            AfterGroup();
            return;
        }
    }

    /// <summary>After a group's writes have completed: idle readers let go of the snapshots this commit made old (so none
    /// keeps the WAL from being checkpointed and restarted), and the WAL is checkpointed once it is long, outside the
    /// group's latency.</summary>
    void AfterGroup()
    {
        Readers?.Sweep();
        if (*_conn.WalFrames < _options.CheckpointFrames) return;
        _ = Sqlite3.WalCheckpoint(_conn.Db, null, 0, out _, out _); // PASSIVE: never waits on a reader
        Interlocked.Increment(ref Counters.Checkpoints);
        *_conn.WalFrames = 0;
    }

    void FailAll(int n, in DataResult result)
    {
        for (int i = 0; i < n; i++)
        {
            if (_group[i] is not { } job) continue;
            _group[i] = null;
            job.Complete(job.Failed ? job.Failure : result);
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
        try
        {
            _conn.Exec(sql);
            Volatile.Write(ref Clock.Seq, Clock.Seq + 1); // readers see it, as after a commit
        }
        finally
        {
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
