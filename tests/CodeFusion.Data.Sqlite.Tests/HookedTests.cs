using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// What only a Debug hook makes certain, not lucky: a race held open, a fault where nothing would fail on purpose. The
/// hooks are process-wide, so the class runs alone.
/// </summary>
[Collection(nameof(AllocationRuns))]
public sealed class HookedTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose()
    {
#if DEBUG
        SqliteReaders.AfterExit = null;
        SqliteReaders.StoppedRead = null;
        SqliteJob.SaveFault = null;
        SqliteConn.BeforeInterrupt = null;
#endif
        _db.Dispose();
    }

    /// <summary>Stop in Exclusive mode while a loop's batch holds the connection: the batch's end closes it. A read that
    /// takes the connection for a moment as the batch gives it back (it sees the stop and gives it back) must not leave
    /// it open: whoever gives it back last closes it.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AReadAsTheBatchGivesTheConnectionBackDoesntKeepItOpen()
    {
#if !DEBUG
        Assert.Skip("needs the Debug hooks");
#else
        var db = _db.Database(exclusive: true, batches: true);
        var s = db.OpenSource();
        using var loop = new TestLoop(s);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readHolds = new ManualResetEventSlim();
        ValueTask<DataResult> written = default;
        var wake = loop.WakeUp(() =>
        {
            written = Kv.Write(s, "w", 1);
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        });
        holding.Wait(TestContext.Current.CancellationToken);
        var stopping = Task.Run(s.Stop, TestContext.Current.CancellationToken);
        // Stop gave up on the batch: the readers' wait (2 s), then the writer thread's close (2 s); the batch's end closes
        await Task.Delay(4_500, TestContext.Current.CancellationToken);
        Assert.True(stopping.IsCompleted, "Stop still waits for the batch");
        Task<DataResult>? read = null;
        int batchThread = loop.ThreadId;
        SqliteReaders.AfterExit = () =>
        {
            if (Environment.CurrentManagedThreadId != batchThread || read is not null) return;
            // the batch just gave the connection back: a read takes it before anyone closes it, and holds it a moment
            read = Task.Run(() => Kv.Read(s, "w"));
            readHolds.Wait(TimeSpan.FromSeconds(5));
        };
        SqliteReaders.StoppedRead = () =>
        {
            readHolds.Set();
            Thread.Sleep(200); // the batch's end tries to close meanwhile
        };
        release.Set();
        await wake;
        _ = await written;
        await stopping;
        Assert.NotNull(read);
        Assert.Equal(DataStatus.Stopped, (await read!).Status);
        SqliteReaders.AfterExit = null;
        SqliteReaders.StoppedRead = null;
        // closed: its exclusive lock is gone, so the file opens again
        var again = db.OpenSource();
        try
        {
            Assert.True(Kv.Count(again) >= 0);
        }
        finally
        {
            again.Stop();
        }
#endif
    }

    /// <summary>An exception no write expects (out of memory keeping a row) inside a loop's batch: the transaction is
    /// rolled back, the caller gets the exception, and nothing the transaction held is answered Ok.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AnExceptionInABatchUndoesItsTransactionAndAnswersNothingOkThatIsntThere()
    {
#if !DEBUG
        Assert.Skip("needs the Debug hook");
#else
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        using var loop = new TestLoop(s);
        ValueTask<DataResult> a = default, after = default;
        Exception? thrown = null;
        await loop.WakeUp(() =>
        {
            a = Kv.Write(s, "a", 1);
            // a unit: u1, then a statement whose row can't be kept
            var unit = s.Write(Kv.Put);
            unit.Text("u1");
            unit.Int64(1);
            unit.Then(DurabilityTests.Statement("INSERT INTO kv (k, v) VALUES ('u2', 2) RETURNING k"));
#pragma warning disable CA2201 // standing in for the runtime's own, as Array.Resize would throw it
            SqliteJob.SaveFault = static () => throw new OutOfMemoryException("keeping a row");
#pragma warning restore CA2201
            try
            {
                _ = Sqlite.Run<Value>(unit.Hold(), null, default);
            }
            catch (OutOfMemoryException e)
            {
                thrown = e;
            }
            finally
            {
                SqliteJob.SaveFault = null;
            }
            after = Kv.Write(s, "after", 3); // the wake-up goes on
        });
        Assert.NotNull(thrown);
        Assert.Equal(0, Kv.Count(s, "u%"));
        var afterResult = await after;
        Assert.True(afterResult.IsOk, afterResult.ToString());
        await DurabilityTests.AnsweredAsStored(s, ("a", a));
        Assert.Equal(1, Kv.Count(s, "after"));
#endif
    }

    /// <summary>The watchdog decides to interrupt a statement past its timeout, and is held a moment before it does: the
    /// statement ends on its own meanwhile, and the next one starts. The interrupt must not land on that one.</summary>
    [Fact(Timeout = 30_000)]
    public void TheWatchdogInterruptsOnlyTheStatementItTimed()
    {
#if !DEBUG
        Assert.Skip("needs the Debug hook");
#else
        var s = _db.Open(_db.Database(readers: 1, timeout: TimeSpan.FromMilliseconds(200)));
        // how many steps take 100 ms here
        Assert.True(Spin(s, 10_000).IsOk); // prepared, warm
        var clock = Stopwatch.StartNew();
        Assert.True(Spin(s, 1_000_000).IsOk);
        long per100ms = Math.Max(1, (long)(1_000_000 * 100 / Math.Max(1, clock.Elapsed.TotalMilliseconds)));
        int held = 0;
        SqliteConn.BeforeInterrupt = () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0) Thread.Sleep(300); // the first decision waits: its statement ends
        };
        clock.Restart();
        var slow = Spin(s, per100ms * 4);    // about 400 ms: past the timeout, so the watchdog decides on it
        long slowMs = clock.ElapsedMilliseconds;
        var next = Spin(s, per100ms * 3 / 2); // about 150 ms, under the timeout: running as the held interrupt goes out
        long nextMs = clock.ElapsedMilliseconds - slowMs;
        SqliteConn.BeforeInterrupt = null;
        Assert.True(slowMs > 250 && nextMs < 190, $"the statements took {slowMs} and {nextMs} ms");
        Assert.Equal(1, held);
        Assert.True(next.IsOk, $"the statement after the one timed was interrupted: {next}");
        Assert.Equal(DataStatus.Ok, slow.Status);
        Assert.Equal(0, s.Interrupted);
#endif
    }

    static DataResult Spin(SqliteDataSource s, long steps)
    {
        var c = s.Read(Kv.Spin);
        c.Int64(steps);
        return Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default));
    }
}
