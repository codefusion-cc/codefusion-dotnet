using System;
using System.Buffers.Binary;
using System.Threading;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// Group commit. Writes from concurrent requests queue up, and each loop sends what has queued as one batch: one
/// round trip and one implicit transaction, so a single WAL flush commits them all. Each write's rows are read
/// inside the batch and kept; its handler gets them, and the write completes, only after the commit.
/// </summary>
public sealed class PgWriter : PgLoops
{
    /// <summary>The most writes in one transaction.</summary>
    public const int MaxGroup = 128;

    /// <summary>The most writes waiting for a loop by default; more are refused at once (<see cref="PgStatus.Busy"/>),
    /// so a stalled database can't pile up requests and their bodies. As many as the reader's: a request may start
    /// all its writes at once (TechEmpower's updates test queues 512 times 20), and only a database whose commits have
    /// stopped fills it.</summary>
    public const int MaxQueued = 65536;

    /// <summary>How often a write that lost a deadlock or a serialization check runs again alone.</summary>
    const int Retries = 3;

    /// <param name="settings">With lock_timeout among its parameters: past it only the blocked write fails, and the
    /// rest of its group commits without it, instead of all waiting for the statement timeout.</param>
    public PgWriter(PgSettings settings) : this(settings, MaxQueued) { }

    /// <summary>A writer whose queue holds at most <paramref name="maxQueued"/> writes waiting for a loop; more are
    /// refused at once (<see cref="PgStatus.Busy"/>).</summary>
    public PgWriter(PgSettings settings, int maxQueued) : base(settings, MaxGroup, maxQueued > 0 ? maxQueued : throw new ArgumentOutOfRangeException(nameof(maxQueued)), "pg writer") { }

    /// <summary>
    /// Commits the group with the outcome of running its writes one by one in order. A failing statement rolls back
    /// the whole transaction; the writes before it ran without error, so they commit together, then the failing one
    /// runs again alone, and the writes after it are tried as the next group. Its error can't be kept: what the writes
    /// before it wrote the second time needn't be what they wrote in the attempt that rolled back (a sequence keeps
    /// its step, now() moves). Only a write that failed first in its batch gets its error at once: it saw nothing of
    /// the group. So a write runs at most twice before it commits (three times when the writes before the failing
    /// one then fail together, and each runs alone), and of two writes that conflict the one queued first wins.
    /// </summary>
    private protected override void Run(PgConnection conn, PgJob?[] group, int n)
    {
        int from = 0;
        while (from < n)
        {
            int failed = TryCommit(conn, group, from, n, out var error, rerun: from > 0);
            if (failed < 0) return;
            if (failed == n)
            {
                // The commit itself failed after every statement succeeded: no write to blame.
                for (int i = from; i < n; i++) Alone(conn, group, i);
                return;
            }
            bool before = failed == from || TryCommit(conn, group, from, failed, out _) < 0;
            if (!before)
                for (int i = from; i < failed; i++) Alone(conn, group, i);
            Alone(conn, group, failed, failed == from ? error : null);
            from = failed + 1;
        }
    }

    /// <summary>
    /// Commits write <paramref name="i"/> in its own transaction, after <paramref name="error"/> if it already
    /// failed once on the same data. A deadlock or serialization failure is not the write's own error (the other
    /// loop's group held the locks it needed), so it runs again, up to <see cref="Retries"/> times.
    /// </summary>
    void Alone(PgConnection conn, PgJob?[] group, int i, PgException? error = null)
    {
        for (int retries = 0; error is null || Transient(error) && retries++ < Retries;)
            if (TryCommit(conn, group, i, i + 1, out error) < 0) return;
        var job = group[i]!;
        group[i] = null;
        job.Complete(new PgResult(PgStatus.ServerError, runs: job.Runs, exception: error));
    }

    static bool Transient(PgException e) => e.SqlState is PgErrorCodes.DeadlockDetected or PgErrorCodes.SerializationFailure;

    /// <summary>
    /// Runs writes <paramref name="from"/> to <paramref name="to"/> (exclusive) as one batch and, once committed,
    /// hands each its rows and completes it. On an error from Postgres returns the slot of the write whose statement
    /// failed (<paramref name="to"/> if the commit failed) with the error; nothing committed. -1 when all committed.
    /// </summary>
    int TryCommit(PgConnection conn, PgJob?[] group, int from, int to, out PgException? error, bool rerun = true)
    {
        if (rerun) Interlocked.Increment(ref Counters.Reruns);
        conn.Begin();
        for (int j = from; j < to; j++)
        {
            group[j]!.Restart();
            conn.Add(group[j]!, j);
        }
        conn.Flush();
        int failed = conn.ReadReplies(group, keep: true, out error);
        if (failed == PgConnection.CommitFailed) return to;
        if (failed >= 0) return failed;
        for (int j = from; j < to; j++)
        {
            var job = group[j]!;
            group[j] = null;
            HandRows(conn, job);
            job.Complete(job.Answer());
        }
        return -1;
    }

    /// <summary>The committed write's kept rows, to its handler.</summary>
    static void HandRows(PgConnection conn, PgJob job)
    {
        job.CheckIs(PgJob.Life.Queued); // before its completion: after it, the job may be someone else's
        if (job.Handler is not { } handler) return;
        var saved = job.Saved!;
        int last = job.Statements[job.Count - 1].Id;
        for (int at = 0, index = 0; at < job.SavedLen; index++)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(saved.AsSpan(at));
            try
            {
                var row = PgRow.Parse(saved.AsSpan(at + 4, len), ref conn.ColumnStart, ref conn.ColumnLength, index, conn.Columns(last));
                job.Value = handler(in row, job.State);
            }
            catch (Exception e)
            {
                // committed all the same: the write reports its handler's error (or the malformed row) and still completes
                job.Failure = e;
                return;
            }
            at += 4 + len;
        }
    }
}
