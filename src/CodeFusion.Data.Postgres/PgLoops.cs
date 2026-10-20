using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data;

namespace CodeFusion.Data.Postgres;

/// <summary>What the loops did, for a stats line. Only the loops write, once per group, never per command.</summary>
public sealed class PgCounters
{
    /// <summary>Groups run, commands in them, the largest group, groups run again after a failure, and commands refused because the queue was full.</summary>
    public long Groups, Grouped, GroupMax, Reruns, Refused;
    /// <summary>Writer: writes that ran merged with others of their statement into one (keyed updates).</summary>
    public long Merged;
    /// <summary>Writer: time from taking a group to its last commit, the most of it, and the longest a write waited in the queue.</summary>
    public long Ticks, TicksMax, WaitTicksMax;

    internal static void Max(ref long field, long value)
    {
        for (long seen = Volatile.Read(ref field); value > seen; seen = Volatile.Read(ref field))
            if (Interlocked.CompareExchange(ref field, value, seen) == seen) return;
    }
}

/// <summary>
/// The queue and the loops of a <see cref="PgReader"/> or <see cref="PgWriter"/>. Each loop is a thread of its own
/// with a connection of its own: it takes what has queued, sends it as one batch, sleeps in the kernel until the
/// replies come, and completes each command on its own thread. So a group costs one send and a few receives, and a
/// command costs no thread-pool work item or wakeup. Requests never hold a connection, so Postgres runs few busy
/// backends.
/// </summary>
public abstract class PgLoops
{
    readonly PgSettings _settings;
    readonly PgQueue _queue;
    readonly int _maxGroup;
    readonly string _name;
    int _started;
    readonly List<Thread> _threads = [];

    /// <summary>What the loops did so far.</summary>
    public readonly PgCounters Counters = new();

    /// <summary>Exceptions a loop caught that no failing connection explains (a bug here, in a transport, or in a
    /// continuation that threw), and the first of them. The commands they hit still end (<see cref="PgStatus.Broken"/>),
    /// so nothing else shows them: tests fail on any.</summary>
    internal int UnexpectedFailures;
    internal Exception? FirstUnexpected;

    /// <summary>Debug builds count the jobs rented for these loops and not yet back in the pool (Release ones
    /// don't): 0 once every command's task was read. For tests.</summary>
    internal int JobsOut;

    /// <summary>The loops asleep, waiting for commands: a connection is then idle, and the next group checks it first.
    /// For tests.</summary>
    internal int Sleeping => _queue.Sleeping;

    /// <summary>The loops this thread runs, for a failure caught where only the thread knows them.</summary>
    [ThreadStatic] static PgLoops? t_loops;

    private protected PgLoops(PgSettings settings, int maxGroup, int maxQueued, string name)
    {
        _settings = settings;
        _maxGroup = maxGroup;
        _queue = new PgQueue(maxQueued);
        _name = name;
    }

    /// <summary>Commands waiting for a loop.</summary>
    public int Queued => _queue.Count;

    /// <summary>Starts building a command of <paramref name="statement"/>; its parameters follow, then Run.</summary>
    public PgCommand Command(PgStatement statement)
    {
        var job = PgJob.Rent(statement);
        job.RentedBy(this);
        return new(this, job);
    }

    /// <summary>Starts <paramref name="loops"/> more loops. Commands queued before this are grouped from the start.</summary>
    public void Start(int loops)
    {
        for (int i = 0; i < loops; i++)
        {
            int n = Interlocked.Increment(ref _started);
            var t = new Thread(Loop) { IsBackground = true, Name = $"{_name} {n}" };
            lock (_threads) _threads.Add(t);
            t.Start();
        }
    }

    /// <summary>Waits for every loop started to end (after <see cref="Complete"/>); false if one is still running
    /// when <paramref name="timeout"/> is up. Called from a loop's own thread (a command's continuation runs there),
    /// it doesn't wait for that loop, which ends once the caller returns. For tests.</summary>
    internal bool WaitStopped(TimeSpan timeout)
    {
        Thread[] all;
        lock (_threads) all = [.. _threads];
        long until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        foreach (var t in all)
            if (t != Thread.CurrentThread && !t.Join(TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64)))) return false;
        return true;
    }

    internal void Unexpected(Exception e)
    {
        Interlocked.CompareExchange(ref FirstUnexpected, e, null);
        Interlocked.Increment(ref UnexpectedFailures);
    }

    /// <summary>Counts <paramref name="e"/> against the loops running on this thread, if it is a loop's.</summary>
    internal static void UnexpectedOnThisThread(Exception e) => t_loops?.Unexpected(e);

    /// <summary>What a connection throws when the network or the server fails, or refuses the login: the loop
    /// fails the group's commands as Broken and connects again. Anything else is a bug.</summary>
    internal static bool Expected(Exception e)
    {
        switch (e)
        {
            case PgConnectionException c:
                return c.InnerException is null || Expected(c.InnerException);
            case AggregateException a: // a host name lookup that failed
                foreach (var inner in a.InnerExceptions)
                    if (!Expected(inner)) return false;
                return true;
            default:
                return e is PgException or SocketException or IOException or TimeoutException or ObjectDisposedException;
        }
    }

    /// <summary>Takes no more commands (<see cref="PgStatus.Stopped"/>). Each loop runs what has queued, closes its
    /// connection and ends.</summary>
    public void Complete() => _queue.Stop();

    internal ValueTask<PgResult> Submit(PgJob job, PgRowHandler? handler, object? state, CancellationToken aborted) =>
        Enqueue(job, handler, state, aborted) switch
        {
            Queueing.Ok => job.Task, // a loop may have completed it already, but only its task's reader recycles it
            Queueing.Canceled => new(PgResult.Canceled),
            Queueing.Busy => new(PgResult.Busy),
            _ => new(PgResult.Stopped),
        };

    /// <summary>As <see cref="Submit"/>, its outcome read as CodeFusion.Data's.</summary>
    internal ValueTask<DataResult> SubmitData(PgJob job, PgRowHandler? handler, object? state, CancellationToken aborted) =>
        Enqueue(job, handler, state, aborted) switch
        {
            Queueing.Ok => job.DataTask,
            Queueing.Canceled => new(DataResult.Of(DataStatus.Canceled)),
            Queueing.Busy => new(DataResult.Of(DataStatus.Busy)),
            _ => new(DataResult.Of(DataStatus.Stopped)),
        };

    enum Queueing { Ok, Canceled, Busy, Stopped }

    /// <summary>Queues the job; anything but <see cref="Queueing.Ok"/> gave it back to the pool unrun.</summary>
    Queueing Enqueue(PgJob job, PgRowHandler? handler, object? state, CancellationToken aborted)
    {
        try
        {
            job.Seal();
        }
        catch
        {
            job.Discard(); // a build mistake: the caller's bug, thrown at them
            throw;
        }
        if (aborted.IsCancellationRequested)
        {
            job.Discard();
            return Queueing.Canceled;
        }
        job.Submitted = true;
        job.To(PgJob.Life.Queued);
        job.Handler = handler;
        job.State = state;
        job.Aborted = aborted;
        job.Enqueued = Stopwatch.GetTimestamp();
        switch (_queue.TryAdd(job))
        {
            case PgQueue.Added.Ok:
                return Queueing.Ok;
            case PgQueue.Added.Full:
                Interlocked.Increment(ref Counters.Refused);
                job.Discard();
                return Queueing.Busy;
            default:
                job.Discard();
                return Queueing.Stopped;
        }
    }

    void Loop()
    {
        var group = new PgJob?[_maxGroup];
        var me = new PgQueue.Waiter();
        PgConnection? conn = null;
        var connects = new Reconnect(_settings);
        bool idled = false;
        t_loops = this;
        try
        {
            while (true)
            {
                int n = _queue.Take(me, group, _maxGroup, out bool slept);
                if (n == 0) return;
                idled |= slept; // kept past a group whose commands all left before it ran
                long taken = Stopwatch.GetTimestamp();
                n = DropAborted(group, n, taken);
                if (n == 0) continue;
                Interlocked.Increment(ref Counters.Groups);
                Interlocked.Add(ref Counters.Grouped, n);
                PgCounters.Max(ref Counters.GroupMax, n);
                // A connection that sat idle may have been ended meanwhile (a restart, idle_session_timeout,
                // pg_terminate_backend): nothing of this group went out on it, so it goes to a new one instead
                if (idled && conn is not null && conn.EndedWhileIdle())
                {
                    conn.Dispose();
                    conn = null;
                }
                idled = false;
                try
                {
                    conn ??= connects.Open(taken);
                    Run(conn, group, n);
                }
                catch (Exception e)
                {
                    // The connection broke or never opened: every command not answered yet fails (a write's
                    // outcome is unknown), and the next group reconnects (after a wait, if connecting failed).
                    if (!Expected(e)) Unexpected(e);
                    var broken = new PgResult(PgStatus.Broken, exception: e);
                    for (int i = 0; i < n; i++)
                        if (group[i] is { } job)
                        {
                            group[i] = null;
                            try
                            {
                                job.Complete(broken);
                            }
                            catch (Exception again)
                            {
                                Unexpected(again); // a Debug check on the job: the loop goes on
                            }
                        }
                    conn?.Dispose();
                    conn = null;
                }
                long took = Stopwatch.GetTimestamp() - taken;
                Interlocked.Add(ref Counters.Ticks, took);
                PgCounters.Max(ref Counters.TicksMax, took);
                Array.Clear(group, 0, n);
            }
        }
        finally
        {
            conn?.Dispose();
        }
    }

    /// <summary>A loop's connecting: after a failure the next try waits (see <see cref="PgSettings.ReconnectDelay"/>),
    /// and until then each group fails at once with that failure, so a database that is down costs a group nothing and
    /// gets a few attempts a second at most, not one per group.</summary>
    sealed class Reconnect(PgSettings settings)
    {
        Exception? _failed;
        long _retryAt;
        TimeSpan _delay;

        public PgConnection Open(long now)
        {
            if (_failed is not null && now < _retryAt)
                throw new PgConnectionException($"connecting failed; the next try is in {(_retryAt - now) * 1000 / Stopwatch.Frequency} ms: {_failed.Message}", _failed);
            try
            {
                var conn = PgConnection.Open(settings);
                _failed = null;
                _delay = TimeSpan.Zero;
                return conn;
            }
            catch (Exception e)
            {
                _delay = _delay == TimeSpan.Zero ? settings.ReconnectDelay
                    : TimeSpan.FromTicks(Math.Min(_delay.Ticks * 2, Math.Max(settings.MaxReconnectDelay.Ticks, settings.ReconnectDelay.Ticks)));
                // a quarter shorter or longer, so loops that failed together don't try again together
#pragma warning disable CA5394 // spreads retries, no secret
                double spread = 0.75 + Random.Shared.NextDouble() / 2;
#pragma warning restore CA5394
                _retryAt = Stopwatch.GetTimestamp() + (long)(_delay.TotalSeconds * spread * Stopwatch.Frequency);
                _failed = e;
                throw;
            }
        }
    }

    /// <summary>Drops the commands whose client left before a loop took them; nothing of theirs ran. The rest keep their order.</summary>
    int DropAborted(PgJob?[] group, int n, long taken)
    {
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            var job = group[i]!;
            group[i] = null;
            PgCounters.Max(ref Counters.WaitTicksMax, taken - job.Enqueued);
            if (job.Aborted.IsCancellationRequested) job.Complete(PgResult.Canceled);
            else group[kept++] = job;
        }
        return kept;
    }

    /// <summary>Runs a group of <paramref name="n"/> commands on <paramref name="conn"/>, completing each and
    /// clearing its slot. A <see cref="PgConnectionException"/> (or any other) leaves the rest to fail.</summary>
    private protected abstract void Run(PgConnection conn, PgJob?[] group, int n);
}
