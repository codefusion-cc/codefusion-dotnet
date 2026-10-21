using System.Threading;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// Read groups. Reads from concurrent requests queue up, and each loop sends what has queued as one batch with one
/// Sync, so Postgres starts and ends one implicit transaction and flushes its replies once for the group instead of
/// once per statement. Read Committed takes a fresh snapshot for every statement, so each read sees what it would
/// alone. A read is answered once its statement has ended; a failing statement fails only its own read, and the
/// reads after it, which Postgres skipped, run again as the next group.
/// </summary>
public sealed class PgReader : PgLoops
{
    /// <summary>The most reads in one batch: the first read's reply waits until Postgres flushes, at the group's end
    /// or when its 8 KB send buffer fills.</summary>
    public const int MaxGroup = 32;

    /// <summary>The most reads waiting for a loop; more are refused at once (<see cref="PgStatus.Busy"/>). Far
    /// above the workload's few thousand concurrent reads: it only stops a stalled database from piling up requests.</summary>
    public const int MaxQueued = 65536;

    /// <summary>A reader that connects with <paramref name="settings"/>; <see cref="PgLoops.Start"/> opens its connections.</summary>
    public PgReader(PgSettings settings) : base(settings, MaxGroup, MaxQueued, "pg reader") { }

    private protected override void Run(PgConnection conn, PgJob?[] group, int n)
    {
        for (int from = 0; from < n;)
        {
            conn.Begin();
            for (int i = from; i < n; i++) conn.Add(group[i]!, i);
            conn.Flush();
            int failed = conn.ReadReplies(group, keep: false, out var error);
            if (failed < 0) return; // all answered (an error at the commit would have no read to blame)
            // Statement `failed` failed; Postgres skipped the ones after it (the batch has one Sync).
            var job = group[failed]!;
            group[failed] = null;
            job.Complete(job.Failure is null ? new PgResult(PgStatus.ServerError, job.Rows, exception: error) : job.Answer());
            Interlocked.Increment(ref Counters.Reruns);
            from = failed + 1;
        }
    }
}
