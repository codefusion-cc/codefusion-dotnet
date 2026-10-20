using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// <see cref="SqliteDatabase.Exclusive"/>: one connection for every read and write, the file locked to this process.
/// Reads and the writer's groups take turns on it: a read never sees a group half done and sees every write that
/// completed before it started; a continuation on the writer's thread can read; the watchdog and Stop work as with
/// readers of their own.
/// </summary>
public sealed class ExclusiveTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TheFileIsThisProcesssAloneAndItsWalIndexIsInMemory()
    {
        var db = _db.Database(exclusive: true);
        var s = _db.Open(db);
        Assert.True((await Kv.Write(s, "k", 1)).IsOk);
        Assert.Equal(1, Kv.Read(s, "k").Value);
        // no shared memory: SQLite keeps the WAL index in the process when the lock is exclusive from the start
        Assert.True(File.Exists(db.Path + "-wal"));
        Assert.False(File.Exists(db.Path + "-shm"));
        // another connection can't read the file
        int rc;
        try
        {
            using var other = SqliteConn.Open(db.Path, new SqliteDatabase(db.Path) { BusyTimeout = TimeSpan.FromMilliseconds(50) }, writer: false);
            rc = other.Once(Kv.CountAll);
        }
        catch (SqliteException e)
        {
            rc = e.Code;
        }
        Assert.Equal(Sqlite3.Busy, rc & 0xFF);
    }

    [Fact]
    public async Task AReadSeesEveryWriteThatCompletedBeforeItAndNoUnitHalfDone()
    {
        var s = _db.Open(_db.Database(exclusive: true));
        int missed = 0;
        long odd = 0;
        using var stop = new CancellationTokenSource();
        var counting = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (Kv.Count(s, "pair-%") % 2 != 0) Interlocked.Increment(ref odd);
        }, TestContext.Current.CancellationToken);
        await Parallel.ForAsync(0, 4, async (t, ct) =>
        {
            for (int i = 0; i < 300; i++)
            {
                string key = $"ryw-{t}-{i}";
                Assert.True((await Kv.Write(s, key, i, ct)).IsOk);
                if (Kv.Read(s, key).Rows != 1) Interlocked.Increment(ref missed);
                // two rows in one unit: a read sees both or neither
                var c = s.Write(Kv.Put);
                c.Text($"pair-a-{t}-{i}");
                c.Int64(i);
                c.Then(Kv.Put);
                c.Text($"pair-b-{t}-{i}");
                c.Int64(i);
                Assert.True((await Sqlite.Run<Value>(c.Hold(), null, ct)).IsOk);
            }
        });
        stop.Cancel();
        await counting;
        Assert.Equal(0, missed);
        Assert.Equal(0, odd);
        Assert.Equal(2400, Kv.Count(s, "pair-%"));
    }

    [Fact(Timeout = 30_000)]
    public async Task AContinuationOnTheWritersThreadReadsAndWrites()
    {
        var s = _db.Open(_db.Database(exclusive: true, completeOnWriter: true));
        for (int i = 0; i < 200; i++)
        {
            // the continuation runs on the writer's thread, between groups: the connection is free for its read
            Assert.True((await Kv.Write(s, "c" + i, i)).IsOk);
            Assert.Equal(i, Kv.Read(s, "c" + i).Value);
        }
        // a cancelled write completes after its group too, not inside it
        using var cancelled = new CancellationTokenSource();
        var blocker = Kv.WriteMany(s, "blocker-", 200_000);
        cancelled.Cancel();
        var r = await Kv.Write(s, "never", 1, cancelled.Token);
        Assert.Equal(DataStatus.Canceled, r.Status);
        Assert.Equal(0, Kv.Read(s, "never").Rows);
        Assert.True((await blocker).IsOk);
    }

    [Fact]
    public async Task NowInAReadIsTheTimeOfTheRead()
    {
        var s = _db.Open(_db.Database(exclusive: true));
        Assert.True((await Kv.Write(s, "k", 1)).IsOk); // the writer's group set its time on the shared connection
        await Task.Delay(50, TestContext.Current.CancellationToken);
        long before = UnixMicroseconds();
        var c = s.Read(Now);
        long now = Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default)).Value;
        Assert.InRange(now, before, UnixMicroseconds());
    }

    static readonly SqliteStatement Now = new("SELECT cf_now()");

    static long UnixMicroseconds() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;

    [Fact(Timeout = 30_000)]
    public async Task AReadPastTheTimeoutIsInterruptedAndAWriteWaitingForTheConnectionCommits()
    {
        var s = _db.Open(_db.Database(exclusive: true, timeout: TimeSpan.FromMilliseconds(200)));
        var reading = Task.Run(() =>
        {
            var c = s.Read(Kv.Spin);
            c.Int64(10_000_000_000);
            return Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default));
        }, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var write = Kv.Write(s, "after", 1);
        var r = await reading;
        Assert.Equal(DataStatus.StatementTimeout, r.Status);
        Assert.True((await write).IsOk);
        Assert.Equal(1, Kv.Read(s, "after").Value);
        Assert.True(s.Interrupted >= 1);
    }

    [Fact]
    public async Task StopWaitsForTheReadsRunningAndRefusesTheRest()
    {
        var db = _db.Database(exclusive: true);
        var s = db.OpenSource();
        Assert.True((await Kv.WriteMany(s, "r", 1000)).IsOk);
        using var go = new CountdownEvent(4);
        long stopped = 0, read = 0;
        var readers = new Task[4];
        for (int t = 0; t < readers.Length; t++)
            readers[t] = Task.Run(() =>
            {
                go.Signal();
                while (true)
                {
                    var c = s.Read(Kv.CountAll);
                    var r = Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default));
                    if (r.Status == DataStatus.Stopped)
                    {
                        Interlocked.Increment(ref stopped);
                        return;
                    }
                    Assert.InRange(r.Value, 1000, 1001); // "last" may commit meanwhile
                    Interlocked.Increment(ref read);
                }
            }, TestContext.Current.CancellationToken);
        go.Wait(TestContext.Current.CancellationToken);
        var queued = Kv.Write(s, "last", 1);
        s.Stop();
        await Task.WhenAll(readers);
        Assert.Equal(4, stopped);
        Assert.True(read > 0);
        Assert.True((await queued).IsOk); // queued before the stop: committed
        Assert.Equal(DataStatus.Stopped, (await Kv.Write(s, "late", 1)).Status);
        SqliteChecks.StopOne(s); // stopped already: every job back, nothing unexpected

        // the file is whole, and another source can open it now
        var again = _db.Open(_db.Database(exclusive: true));
        Assert.Equal(1001, Kv.Count(again));
    }

    [Fact]
    public async Task QueriesAllocateNothingOnceWarm()
    {
        Assert.SkipWhen(typeof(SqliteDataSource).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            is [DebuggableAttribute { IsJITOptimizerDisabled: true }], "Debug builds make every async state machine an object: run in Release");
        var s = _db.Open(_db.Database(exclusive: true));
        Assert.True((await Kv.WriteMany(s, "k", 100)).IsOk);
        await Run(s, 2000, 200); // warm
        long once = long.MaxValue, twice = long.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            once = Math.Min(once, await Measure(s, 10_000, 1_000));
            twice = Math.Min(twice, await Measure(s, 20_000, 2_000));
        }
        // the two loops' async boxes are the test's own: the same for both sizes
        Assert.True(twice - once < 500, $"{once} B for 11,000 queries, {twice} B for 22,000");
        Assert.True(once < 2_000, $"{once} B for 11,000 queries");
    }

    static async Task<long> Measure(SqliteDataSource s, int reads, int writes)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await Run(s, reads, writes);
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    static Task Run(SqliteDataSource s, int reads, int writes) => Task.WhenAll(Task.Run(() => ReadLoop(s, reads)), Task.Run(() => WriteLoop(s, writes)));

    static readonly byte[] Key = "k42"u8.ToArray();
    static readonly SqliteStatement Set = new("UPDATE kv SET v = ?2 WHERE k = ?1", DataType.Text, DataType.Int64);

    static void ReadLoop(SqliteDataSource s, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var c = s.Read(Kv.Get);
            c.Text(Key);
            var t = Sqlite.Run<Value>(c.Hold(), null, default);
            if (!t.IsCompleted || t.Result.Value != 42) throw new InvalidOperationException("a read failed");
        }
    }

    static async Task WriteLoop(SqliteDataSource s, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var c = s.Write(Set);
            c.Text(Key);
            c.Int64(42);
            // the same value again: a real write, and the file doesn't grow
            if (!(await Sqlite.Run<Value>(c.Hold(), null, default).ConfigureAwait(false)).IsOk) throw new InvalidOperationException("a write failed");
        }
    }
}
