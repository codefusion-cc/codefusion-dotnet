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
        SqliteReaders.OwnReadFailed = null;
        SqliteConn.AfterInterrupt = null;
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
        // Stop gives up on the batch (the readers' wait, then the writer thread's close), and leaves the close to its end
        await stopping.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
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
        var s = _db.Open(_db.Database(readers: 1, timeout: TimeSpan.FromSeconds(1)));
        Holding.Register(s.Readers.Connections[0].Db);
        using var slowMayEnd = new ManualResetEventSlim();
        using var nextRuns = new ManualResetEventSlim();
        Holding.SlowMayEnd = slowMayEnd;
        Holding.NextRuns = nextRuns;
        int decisions = 0;
        SqliteConn.BeforeInterrupt = () =>
        {
            if (Interlocked.Increment(ref decisions) != 1) return;
            // the first decision is on the slow statement: let it end, and act only once the next one runs
            slowMayEnd.Set();
            Assert.True(nextRuns.Wait(TimeSpan.FromSeconds(10)));
        };
        var slow = Read(s, "SELECT cf_hold(1)"); // past the timeout: ends when the watchdog has decided on it
        var next = Read(s, "SELECT cf_hold(2)"); // 300 ms, under the timeout, running as the decision is acted on
        SqliteConn.BeforeInterrupt = null;
        Assert.True(decisions >= 1);
        Assert.Equal(DataStatus.Ok, slow.Status);
        Assert.True(next.IsOk, $"the statement after the one timed was interrupted: {next}");
        Assert.Equal(0, s.Interrupted);
#endif
    }

    /// <summary>A read on the loop that doesn't join the batch runs in its transaction; when its error makes SQLite roll
    /// that transaction back (an I/O error can), nothing written before it is answered Ok, and a write after it isn't
    /// committed on its own and answered Failed.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AReadThatRollsTheBatchBackLeavesEveryWriteAnsweredAsStored()
    {
#if !DEBUG
        Assert.Skip("needs the Debug hook");
#else
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        Failing.Register(s.Writer.Connection.Db);
        using var loop = new TestLoop(s);
        ValueTask<DataResult> a = default, read = default, b = default;
        // as SQLite does for some errors (3.54 for an I/O error in any statement): the transaction is gone
        SqliteReaders.OwnReadFailed = static conn => conn.Exec("ROLLBACK");
        await loop.WakeUp(() =>
        {
            a = Kv.Write(s, "a", 1);
            read = Sqlite.Run<Value>(s.Read(DurabilityTests.Statement("SELECT cf_ioerr()")).Hold(), null, default);
            b = Kv.Write(s, "b", 2);
        });
        SqliteReaders.OwnReadFailed = null;
        Assert.False((await read).IsOk);
        await DurabilityTests.AnsweredAsStored(s, ("a", a), ("b", b));
#endif
    }

    /// <summary>A statement the watchdog interrupted answers StatementTimeout, though the watchdog hasn't yet given back
    /// its claim on it when the statement sees the interrupt.</summary>
    [Fact(Timeout = 30_000)]
    public void AnInterruptedStatementIsATimeoutWhileTheWatchdogStillHoldsIt()
    {
#if !DEBUG
        Assert.Skip("needs the Debug hook");
#else
        var s = _db.Open(_db.Database(readers: 1, timeout: TimeSpan.FromMilliseconds(100)));
        using var interrupted = new ManualResetEventSlim();
        SqliteConn.AfterInterrupt = () =>
        {
            interrupted.Set();
            Thread.Sleep(200); // the statement sees the interrupt and answers while the claim is still on
        };
        var r = Spin(s, 1_000_000_000); // runs till interrupted
        SqliteConn.AfterInterrupt = null;
        Assert.True(interrupted.IsSet);
        Assert.Equal(DataStatus.StatementTimeout, r.Status);
#endif
    }

    static DataResult Read(SqliteDataSource s, string sql) =>
        Kv.Done(Sqlite.Run<Value>(s.Read(DurabilityTests.Statement(sql)).Hold(), null, default));

    static DataResult Spin(SqliteDataSource s, long steps)
    {
        var c = s.Read(Kv.Spin);
        c.Int64(steps);
        return Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default));
    }
}

#if DEBUG
/// <summary><c>cf_hold(1)</c> waits until <see cref="SlowMayEnd"/> is set; <c>cf_hold(2)</c> sets
/// <see cref="NextRuns"/>, then takes 300 ms.</summary>
static unsafe class Holding
{
    public static ManualResetEventSlim? SlowMayEnd, NextRuns;

    public static void Register(nint db)
    {
        fixed (byte* name = "cf_hold\0"u8)
            Assert.Equal(0, Sqlite3.CreateFunction(db, name, 1, Sqlite3.Utf8, 0, &Hold, 0, 0, 0));
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void Hold(nint context, int argc, nint* argv)
    {
        if (Sqlite3.ValueInt64(argv[0]) == 1) SlowMayEnd!.Wait(TimeSpan.FromSeconds(10));
        else
        {
            NextRuns!.Set();
            Thread.Sleep(300);
        }
        Sqlite3.ResultInt64(context, 1);
    }
}
#endif
