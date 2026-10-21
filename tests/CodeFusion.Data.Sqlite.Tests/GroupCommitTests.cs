using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// The writer: writes queued together commit as one transaction; a failing write fails alone (a unit of several
/// statements whole), the first queued of two conflicting writes wins, a transaction SQLite rolls back itself runs
/// again once without the write that failed, and cancels, a full queue and a stop end each write once.
/// </summary>
public sealed class GroupCommitTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task WritesQueuedTogetherShareTransactionsAndAFailingOneFailsAlone()
    {
        var s = _db.Open(_db.Database());
        Assert.True((await Kv.Write(s, "taken", 0)).IsOk);
        long before = s.Writer.Counters.Commits;
        var tasks = new ValueTask<DataResult>[400];
        for (int i = 0; i < tasks.Length; i++) tasks[i] = Kv.Write(s, i % 10 == 0 ? "taken" : "k" + i, i);
        int ok = 0, dup = 0;
        foreach (var t in tasks)
        {
            var r = await t;
            if (r.IsOk) ok++;
            else
            {
                Assert.Equal(DataStatus.UniqueViolation, r.Status);
                Assert.Equal("SQLITE_CONSTRAINT_PRIMARYKEY", r.NativeCode); // the key of a WITHOUT ROWID table
                dup++;
            }
        }
        Assert.Equal(360, ok);
        Assert.Equal(40, dup);
        Assert.Equal(361, Kv.Count(s));
        Assert.True(s.Writer.Counters.Commits - before < 400, $"{s.Writer.Counters.Commits - before} transactions for 400 writes");
        Assert.Equal(40, s.Writer.Counters.FailedAlone);
    }

    [Fact]
    public async Task AUnitWhoseSecondStatementFailsIsUndoneWhole()
    {
        var s = _db.Open(_db.Database());
        Assert.True((await Kv.Write(s, "taken", 0)).IsOk);
        var before = Kv.Write(s, "before", 1);
        var unit = s.Write(Kv.Put);
        unit.Text("unit-first");
        unit.Int64(1);
        unit.Then(Kv.Put);
        unit.Text("taken");
        unit.Int64(2);
        var u = Sqlite.Run<Value>(unit.Hold(), null, default);
        var after = Kv.Write(s, "after", 1);
        Assert.True((await before).IsOk);
        Assert.Equal(DataStatus.UniqueViolation, (await u).Status);
        Assert.True((await after).IsOk);
        Assert.Equal(0, Kv.Read(s, "unit-first").Rows);
        Assert.Equal(1, Kv.Read(s, "before").Rows);
        Assert.Equal(1, Kv.Read(s, "after").Rows);
    }

    [Fact]
    public async Task OfTwoWritesOfOneKeyInAGroupTheFirstQueuedWins()
    {
        var s = _db.Open(_db.Database());
        // a write that takes a while, so the next ones queue behind it as one group
        var blocker = Kv.WriteMany(s, "b", 300_000);
        var first = Kv.Write(s, "same", 1);
        var second = Kv.Write(s, "same", 2);
        Assert.True((await blocker).IsOk);
        Assert.True((await first).IsOk);
        Assert.Equal(DataStatus.UniqueViolation, (await second).Status);
        Assert.Equal(1, Kv.Read(s, "same").Value);
    }

    [Fact]
    public async Task AFullDiskFailsTheWriteThatNeedsTheSpaceAndTheWriterGoesOn()
    {
        var s = _db.Open(_db.Database());
        Assert.True((await Kv.WriteMany(s, "seed", 100).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsOk);
        // the writer's connection may grow the file by only a few pages
        s.Writer.Execute($"PRAGMA max_page_count = {ReadPageCount(s) + 4}");
        var big = await Kv.WriteMany(s, "big", 200_000).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(DataStatus.Failed, big.Status);
        Assert.Equal("SQLITE_FULL", big.NativeCode);
        Assert.Equal(0, Kv.Count(s, "big%"));
        // room again: the writer takes writes as before
        s.Writer.Execute("PRAGMA max_page_count = 1073741823");
        Assert.True((await Kv.Write(s, "after", 1).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsOk);
        Assert.Equal(100 + 1, Kv.Count(s));
    }

    static long ReadPageCount(SqliteDataSource s)
    {
        var c = s.Read(new SqliteStatement("SELECT page_count FROM pragma_page_count()"));
        return Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default)).Value;
    }

    [Fact]
    public async Task ATransactionSqliteRollsBackRunsAgainWithoutTheWriteThatFailed()
    {
        // an interrupted write rolls back its whole transaction: the writes before it in the group run again
        var db = _db.Database(timeout: TimeSpan.FromSeconds(2));
        var s = _db.Open(db);
        // another connection holds the file's write lock, so the four queue behind the first as one group (a long first
        // write did this, and on a busy machine the timeout meant for the slow one interrupted it too)
        using var other = SqliteConn.Open(db.Path, db, writer: true);
        Assert.Equal(0, other.Once(new SqliteStatement("BEGIN IMMEDIATE")));
        var first = Kv.Write(s, "first", 0);
        // it waits on the lock with it under the 2 s timeout from now: the four join and the lock goes at once
        var waited = System.Diagnostics.Stopwatch.StartNew();
        SpinWait spin = default;
        while (s.Writer.Queued > 0)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the writer never took the first write");
            spin.SpinOnce(); // yields: on one core this thread would take the CPU the writer needs
        }
        var a = Kv.Write(s, "a", 1);
        var b = Kv.Write(s, "b-one", 1);
        var slow = Kv.WriteMany(s, "slow", 100_000_000);
        var c = Kv.Write(s, "c", 1);
        Assert.Equal(4, s.Writer.Queued);
        Assert.Equal(0, other.Once(new SqliteStatement("ROLLBACK")));
        Assert.True((await first).IsOk);
        Assert.True((await a).IsOk);
        Assert.True((await b).IsOk);
        Assert.Equal(DataStatus.StatementTimeout, (await slow).Status);
        Assert.True((await c).IsOk);
        Assert.Equal(1, Kv.Read(s, "a").Rows);
        Assert.Equal(1, Kv.Read(s, "b-one").Rows);
        Assert.Equal(1, Kv.Read(s, "c").Rows);
        Assert.Equal(0, Kv.Count(s, "slow%"));
        Assert.True(s.Writer.Counters.Reruns >= 1);
        Assert.True(s.Interrupted >= 1);
    }

    [Fact]
    public async Task ACancelledWriteIsCanceledAndWritesNothing()
    {
        var s = _db.Open(_db.Database());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(DataStatus.Canceled, (await Kv.Write(s, "now", 1, cancelled.Token)).Status);
        // cancelled while it waits behind another write
        using var later = new CancellationTokenSource();
        var blocker = Kv.WriteMany(s, "b", 300_000);
        var queued = Kv.Write(s, "queued", 1, later.Token);
        later.Cancel();
        Assert.True((await blocker).IsOk);
        Assert.Equal(DataStatus.Canceled, (await queued).Status);
        Assert.Equal(0, Kv.Read(s, "now").Rows);
        Assert.Equal(0, Kv.Read(s, "queued").Rows);
    }

    [Fact]
    public async Task WritesPastAFullQueueAreBusyAtOnce()
    {
        var db = _db.Database(maxQueued: 4);
        var s = _db.Open(db);
        // another connection holds the file's write lock: the writer waits on it with what it took, so the queue
        // stays full (taken as one group, the writes behind a long one would free it)
        using var other = SqliteConn.Open(db.Path, db, writer: true);
        Assert.Equal(0, other.Once(new SqliteStatement("BEGIN IMMEDIATE")));
        var first = Kv.Write(s, "first", 0);
        // the writer took it alone, and waits on the lock with it
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (s.Writer.Queued > 0) Assert.True(waited.Elapsed < TimeSpan.FromSeconds(2), "the writer never took the first write");
        var pending = new List<ValueTask<DataResult>>();
        int busy = 0, ok = 0;
        for (int i = 0; i < 10; i++)
        {
            var t = Kv.Write(s, "q" + i, i);
            if (!t.IsCompleted)
            {
                pending.Add(t);
                continue;
            }
            var r = await t; // refused at once, while the writer still waits
            Assert.True(r.Status == DataStatus.Busy, $"q{i}: {r}");
            busy++;
        }
        // the queue holds four of them
        Assert.Equal(6, busy);
        Assert.Equal(4, s.Writer.Queued);
        Assert.False(first.IsCompleted);
        Assert.Equal(0, other.Once(new SqliteStatement("ROLLBACK")));
        foreach (var t in pending)
            if ((await t).IsOk) ok++;
        Assert.True((await first).IsOk);
        Assert.Equal(10, busy + ok);
        Assert.Equal(ok, Kv.Count(s, "q%"));
    }

    [Fact]
    public async Task StopCommitsWhatIsQueuedAndRefusesTheRest()
    {
        var db = _db.Database();
        var s = db.OpenSource();
        var blocker = Kv.WriteMany(s, "b", 300_000);
        var queued = new List<ValueTask<DataResult>>();
        for (int i = 0; i < 20; i++) queued.Add(Kv.Write(s, "q" + i, i));
        var stop = Task.Run(s.Stop);
        Assert.True((await blocker).IsOk);
        foreach (var t in queued) Assert.True((await t).IsOk);
        await stop;
        Assert.Equal(DataStatus.Stopped, (await Kv.Write(s, "late", 1)).Status);
        Assert.Equal(DataStatus.Stopped, Kv.Read(s, "q1").Status);
        // the file has them all
        var again = _db.Open(new SqliteDatabase(db.Path) { Synchronous = SqliteSync.Normal, Migrations = db.Migrations });
        Assert.Equal(300_000 + 20, Kv.Count(again));
    }

    [Fact]
    public async Task ALeaderCommitsOnItsOwnThreadAndTheRestStillGroup()
    {
        var s = _db.Open(_db.Database(leader: true));
        // alone: committed before Run returns, on this thread
        var alone = Kv.Write(s, "alone", 1);
        Assert.True(alone.IsCompleted);
        Assert.True((await alone).IsOk);
        // from 8 threads: every write once, reads see their writes
        int missed = 0;
        await Parallel.ForAsync(0, 8, async (t, ct) =>
        {
            for (int i = 0; i < 300; i++)
            {
                string key = $"t{t}-{i}";
                Assert.True((await Kv.Write(s, key, i, ct)).IsOk);
                if (Kv.Read(s, key).Rows != 1) Interlocked.Increment(ref missed);
            }
        });
        Assert.Equal(0, missed);
        Assert.Equal(1 + 8 * 300, Kv.Count(s));
    }
}
