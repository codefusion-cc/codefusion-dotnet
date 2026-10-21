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

    [Fact(Timeout = 60_000)]
    public Task ReadsAndWritesThatNeverStopTakeTurns() => TakeTurns(readers: 4);

    /// <summary>More readers than cores: waiters that only spun would take the CPU the read holding the connection, and
    /// the writer woken for its turn, need; and a writer waiting for a CPU must still not be overtaken.</summary>
    [Fact(Timeout = 60_000)]
    public Task ReadsAndWritesThatNeverStopTakeTurnsWithMoreReadersThanCores() => TakeTurns(Environment.ProcessorCount + 4);

    async Task TakeTurns(int readers)
    {
        var s = _db.Open(_db.Database(exclusive: true));
        Assert.True((await Kv.WriteMany(s, "seed", 20_000)).IsOk);
        // the same work alone: 200 writes, and a read (the median of five), each a scan of 20,000 rows
        var alone = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Assert.True((await Kv.Write(s, "a" + i, i)).IsOk);
        alone.Stop();
        var reads = new double[5];
        for (int k = 0; k < reads.Length; k++)
        {
            long start = Stopwatch.GetTimestamp();
            Kv.Count(s, "seed%");
            reads[k] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(reads);
        double read = reads[reads.Length / 2];
        long readsBeside = 0;
        int writing = 0, stop = 0;
        // threads of their own, reading back to back: blocked pool threads would hold back the writes' continuations
        var threads = new Thread[readers];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    Kv.Count(s, "seed%");
                    if (Volatile.Read(ref writing) != 0) Interlocked.Increment(ref readsBeside);
                }
            }) { IsBackground = true };
            threads[t].Start();
        }
        // the writer is first in line: a write waits for the read running, not for every read that starts after it
        await Task.Delay(100, TestContext.Current.CancellationToken); // the readers are reading
        Volatile.Write(ref writing, 1);
        var took = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Assert.True((await Kv.Write(s, "w" + i, i)).IsOk);
        took.Stop();
        Volatile.Write(ref writing, 0);
        Volatile.Write(ref stop, 1);
        foreach (var t in threads) t.Join();
#if DEBUG
        // the turns: while a write waits, at most two reads start, one that took the connection as the writer came and
        // one let through after it, however long the writer waits for a CPU
        int most = s.Readers.MostReadsBeforeAWrite;
        Assert.True(most <= 2, $"{readers} readers: {most} reads started while one write waited");
#endif
        // and nobody waits for ever: a write waits for about two reads, and time on a shared machine is only a guard
        Assert.True(took.Elapsed.TotalMilliseconds < 10 * (2 * alone.Elapsed.TotalMilliseconds + 200 * 6 * read + 200),
            $"{readers} readers: 200 writes took {took.Elapsed.TotalMilliseconds:F0} ms, beside reads of {read:F2} ms alone; {alone.Elapsed.TotalMilliseconds:F0} ms alone");
        // and the reads go on beside the writes: a group lets a waiting read through before the next one
        Assert.True(readsBeside >= 100, $"{readsBeside} reads beside 200 writes");
    }

    /// <summary>Two takers at once, as the writer's group and a loop's batch (or two loops' batches) are: each keeps its
    /// turn. Marks one cleared as it took the connection left the other waiting unmarked, overtaken by every read.</summary>
    [Fact(Timeout = 60_000)]
    public async Task TwoTakersWaitingAtOnceEachKeepTheirTurn()
    {
        var s = _db.Open(_db.Database(exclusive: true));
        Assert.True((await Kv.WriteMany(s, "seed", 2_000)).IsOk);
        var r = s.Readers;
        // one clock for both sides: a read counts against a wait if it was asked for after the wait began and ended
        // before the wait did, so a read already running, or one whose thread was slow to say it ended, never counts
        long clock = 0;
        int stop = 0;
        var reads = new System.Collections.Generic.List<(long Asked, long Ended)>[4];
        var threads = new Thread[reads.Length];
        for (int t = 0; t < threads.Length; t++)
        {
            var mine = reads[t] = [];
            threads[t] = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    long asked = Interlocked.Increment(ref clock);
                    Kv.Count(s, "seed%");
                    mine.Add((asked, Interlocked.Increment(ref clock)));
                }
            }) { IsBackground = true };
            threads[t].Start();
        }
        await Task.Delay(50, TestContext.Current.CancellationToken); // the readers are reading
        var waits = new System.Collections.Generic.List<(long Began, long Ended)>[2];
        var takers = new Thread[waits.Length];
        for (int t = 0; t < takers.Length; t++)
        {
            var mine = waits[t] = [];
            takers[t] = new Thread(() =>
            {
                for (int i = 0; i < 300; i++)
                {
                    long began = Interlocked.Increment(ref clock);
                    if (!r.Enter()) return;
                    mine.Add((began, Interlocked.Increment(ref clock)));
                    r.Exit();
                }
            }) { IsBackground = true };
            takers[t].Start();
        }
        foreach (var t in takers) t.Join();
        Volatile.Write(ref stop, 1);
        foreach (var t in threads) t.Join();
        var all = new System.Collections.Generic.List<(long Asked, long Ended)>();
        foreach (var l in reads) all.AddRange(l);
        int most = 0;
        foreach (var l in waits)
        {
            Assert.Equal(300, l.Count);
            foreach (var (began, ended) in l)
            {
                int within = 0;
                foreach (var (asked, done) in all)
                    if (asked > began && done < ended) within++;
                most = Math.Max(most, within);
            }
        }
        // each wait lets through at most one read for its own turn and one for the taker ahead of it, and each of those
        // turns one more read that took the connection as the marks went up
        Assert.True(most <= 4, $"{most} reads asked for and done while one taker waited beside another");
#if DEBUG
        // and once its marks are up, at most the two of its own turn
        Assert.True(r.MostReadsBeforeAWrite <= 2, $"{r.MostReadsBeforeAWrite} reads started while one taker's marks were up");
#endif
    }

    /// <summary>The $12 entry's setup: the writer thread and a loop's batch wait at once beside reads that never stop (a
    /// batch on one loop sends the other loops' writes to the writer thread), and each keeps its turn.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheWriterAndALoopsBatchWaitingBesideReadsEachKeepTheirTurn()
    {
        const int Writes = 1_000;
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        Assert.True((await Kv.WriteMany(s, "seed", 20_000)).IsOk);
        int stop = 0;
        var threads = new Thread[Environment.ProcessorCount + 2];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0) Kv.Count(s, "seed%");
            }) { IsBackground = true };
            threads[t].Start();
        }
        using var loop = new TestLoop(s);
        await Task.Delay(50, TestContext.Current.CancellationToken); // the readers are reading
        // the loop wakes back to back with a write each, in its batch; a plain thread's writes go to the writer thread
        var looping = Task.Run(async () =>
        {
            for (int i = 0; i < Writes; i++)
            {
                ValueTask<DataResult> w = default;
                await loop.WakeUp(() => w = Kv.Write(s, "loop-" + i, i));
                Assert.True((await w).IsOk);
            }
        }, TestContext.Current.CancellationToken);
        var writing = Task.Run(async () =>
        {
            for (int i = 0; i < Writes; i++) Assert.True((await Kv.Write(s, "thread-" + i, i)).IsOk);
        }, TestContext.Current.CancellationToken);
        try
        {
            // a waiter overtaken by every read finishes too, only late: this is a guard against a hang
            await Task.WhenAll(looping, writing).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var t in threads) t.Join();
        }
        Assert.Equal(Writes, Kv.Count(s, "loop-%"));
        Assert.Equal(Writes, Kv.Count(s, "thread-%"));
#if DEBUG
        int most = s.Readers.MostReadsBeforeAWrite;
        Assert.True(most <= 2, $"{threads.Length} readers: {most} reads started while the writer or the batch waited");
#endif
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
}
