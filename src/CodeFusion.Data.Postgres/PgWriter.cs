using System;
using System.Buffers.Binary;
using System.Diagnostics;
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

    /// <summary>How long a keyed update's statement goes unmerged after its table refused merging or its merged run
    /// failed with no write at fault: then it is tried merged again, so a trigger dropped later doesn't cost merging
    /// until a restart.</summary>
    internal static TimeSpan UnmergedFor = TimeSpan.FromMinutes(5);

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
    /// A deadlock or serialization failure adds up to <see cref="Retries"/> runs of its write alone. When a run of
    /// keyed updates merged into one statement fails, the group's writes from there go once more unmerged
    /// (<see cref="EachInASavepoint"/>), each with its own outcome.
    /// </summary>
    private protected override void Run(PgConnection conn, PgJob?[] group, int n) => Run(conn, group, 0, n, rerun: false);

    void Run(PgConnection conn, PgJob?[] group, int from, int to, bool rerun)
    {
        while (from < to)
        {
            int failed = TryCommit(conn, group, from, to, out var error, rerun: rerun || from > 0);
            if (failed < 0) return;
            if (conn.FailedInBatch)
            {
                var statement = group[failed]!.Statements[0];
                // the table refused merging (its check, or Postgres: a rule on it can't take an update in a WITH)
                bool refused = error is not null && (error.SqlState == "0A000" || error.Message.Contains(Postgres.NotMergedMarker, StringComparison.Ordinal));
                if (refused) Unmerge(statement);
                // and a merged run that failed with no write at fault failed for the merging itself (a permission on
                // the catalog, say): it stops merging too. Not one that another transaction failed (a deadlock, a
                // serialization failure, a lock or statement timeout): its writes just go again
                bool others = error is not null && (Transient(error) || error.SqlState is PgErrorCodes.LockNotAvailable or "57014");
                int failures = 0;
                // rounds of at most RoundMax writes: each write's savepoint takes a subtransaction, and past 64 of them
                // the backend's cache of them overflows, which every other session's snapshot pays for
                for (int at = from; at < to; at += RoundMax) failures += EachInASavepoint(conn, group, at, Math.Min(at + RoundMax, to));
                if (failures == 0 && !refused && !others) Unmerge(statement);
                return;
            }
            if (failed == to)
            {
                // The commit itself failed after every statement succeeded: no write to blame.
                for (int i = from; i < to; i++) Alone(conn, group, i);
                return;
            }
            bool before = failed == from || TryCommit(conn, group, from, failed, out _) < 0;
            if (!before)
                for (int i = from; i < failed; i++) Alone(conn, group, i);
            Alone(conn, group, failed, failed == from ? error : null);
            from = failed + 1;
        }
    }

    /// <summary>The most writes a fallback round runs in one transaction (see <see cref="EachInASavepoint"/>).</summary>
    const int RoundMax = 64;

    /// <summary>A keyed update's statement goes unmerged for <see cref="UnmergedFor"/>, and is counted.</summary>
    void Unmerge(PgStatement statement)
    {
        statement.UnmergedUntil = Stopwatch.GetTimestamp() + (long)(UnmergedFor.TotalSeconds * Stopwatch.Frequency);
        Interlocked.Increment(ref Counters.Unmerged);
    }

    /// <summary>
    /// Runs writes <paramref name="from"/> to <paramref name="to"/> (exclusive, at most <see cref="RoundMax"/>)
    /// unmerged in one transaction, each in a savepoint of its own: <c>ROLLBACK TO SAVEPOINT</c> (undoing the write
    /// before only if it failed: after a write that ran, the savepoint is set again), the write, <c>RELEASE</c> and
    /// <c>SAVEPOINT</c>, then a Sync, so an error skips nothing of the writes after it. The writes go in one round trip
    /// (two when a write binds a statement whose Parse an earlier write of the round carries: that one's replies are
    /// read first), the commit in another. Each gets its own outcome, as running them one by one would give, and they
    /// commit together. A row locked elsewhere costs its write one lock timeout and no other write any. A failed commit
    /// (a deferred constraint) committed nothing: each write runs again alone. A write that fails on a deadlock or
    /// serialization failure (another loop's locks, not its own error) rolls the transaction back: the writes before
    /// it run as a group, it runs alone, then the rest, so the order holds. Returns how many writes failed.
    /// </summary>
    int EachInASavepoint(PgConnection conn, PgJob?[] group, int from, int to)
    {
        Interlocked.Increment(ref Counters.Reruns);
        int transient = -1, failures = 0, read = from;
        var errors = conn.Errors;
        void ReadUpTo(int end)
        {
            for (; read < end; read++)
            {
                int failed = conn.ReadReplies(group, keep: true, inTransaction: true, out var error);
                if (failed == PgConnection.CommitFailed) throw new PgConnectionException("a savepoint failed: " + error?.Message, error);
                errors[read] = failed >= 0 ? error : null;
                if (failed < 0) continue;
                failures++;
                if (transient < 0 && error is not null && Transient(error)) transient = read;
            }
        }
        conn.Begin();
        for (int j = from; j < to; j++)
        {
            // a statement whose Parse went out with an earlier write of this round: that write's failure may have
            // skipped it, so its replies are read first (and the Parse goes again if it was skipped)
            if (j > read && conn.BindsPendingParse(group, j, j + 1))
            {
                conn.SendSealed();
                ReadUpTo(j);
                conn.Begin();
            }
            group[j]!.Restart();
            if (j == from)
            {
                conn.AddControl(PgConnection.Controls.Begin, j);
                conn.AddControl(PgConnection.Controls.Savepoint, j);
            }
            else conn.AddControl(PgConnection.Controls.RollbackTo, j);
            conn.Add(group[j]!, j);
            conn.AddControl(PgConnection.Controls.Release, j);
            conn.AddControl(PgConnection.Controls.Savepoint, j);
            conn.Seal();
        }
        conn.SendSealed();
        ReadUpTo(to);
        conn.Begin();
        if (transient >= 0)
        {
            conn.AddControl(PgConnection.Controls.Rollback, from);
            conn.Flush();
            if (conn.ReadReplies(group, keep: true, out var rolledBack) != -1) throw new PgConnectionException("ROLLBACK failed: " + rolledBack?.Message, rolledBack);
            Array.Clear(errors, from, to - from);
            // in order: the writes before it together, it alone, then the rest
            if (transient > from) Run(conn, group, from, transient, rerun: true);
            Alone(conn, group, transient);
            if (transient + 1 < to) Run(conn, group, transient + 1, to, rerun: true);
            return failures;
        }
        conn.AddControl(PgConnection.Controls.RollbackTo, from); // undoes the last write if it failed
        conn.AddControl(PgConnection.Controls.Commit, from);
        conn.Flush();
        // -1: committed; anything else (CommitFailed for the COMMIT, a deferred constraint) committed nothing
        if (conn.ReadReplies(group, keep: true, out _) != -1)
        {
            // the commit itself failed (a deferred constraint): nothing committed, and no write to blame
            Array.Clear(errors, from, to - from);
            for (int j = from; j < to; j++) Alone(conn, group, j);
            return failures;
        }
        Interlocked.Increment(ref Counters.Commits);
        for (int j = from; j < to; j++)
        {
            var job = group[j]!;
            group[j] = null;
            if (errors[j] is { } e) job.Complete(new PgResult(PgStatus.ServerError, runs: job.Runs, exception: e));
            else
            {
                HandRows(conn, job);
                job.Complete(job.Answer());
            }
            errors[j] = null;
        }
        return failures;
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
    /// Runs of keyed updates go merged.
    /// </summary>
    int TryCommit(PgConnection conn, PgJob?[] group, int from, int to, out PgException? error, bool rerun = true)
    {
        if (rerun) Interlocked.Increment(ref Counters.Reruns);
        conn.Begin();
        long now = Stopwatch.GetTimestamp();
        for (int j = from; j < to;)
        {
            var job = group[j]!;
            job.Restart();
            // a run of one keyed update goes as one statement
            int end = j + 1;
            if (job.Count == 1 && job.Statements[0].Batch is { } batch && now >= job.Statements[0].UnmergedUntil
                && PgConnection.TryKey(job, batch, out long key))
            {
                // the run ends where a key comes again: each write updates its row once, and every one is checked
                var keys = conn.RunKeys;
                keys.Clear();
                keys.Add(key);
                while (end < to && group[end]!.Count == 1 && ReferenceEquals(group[end]!.Statements[0], job.Statements[0])
                    && PgConnection.TryKey(group[end]!, batch, out key) && keys.Add(key))
                    group[end++]!.Restart();
                if (end - j > 1)
                {
                    conn.AddBatch(group, j, end, batch);
                    Interlocked.Add(ref Counters.Merged, end - j);
                    j = end;
                    continue;
                }
            }
            conn.Add(job, j);
            j++;
        }
        conn.Flush();
        int failed = conn.ReadReplies(group, keep: true, out error);
        if (failed == PgConnection.CommitFailed) return to;
        if (failed >= 0) return failed;
        Interlocked.Increment(ref Counters.Commits);
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
