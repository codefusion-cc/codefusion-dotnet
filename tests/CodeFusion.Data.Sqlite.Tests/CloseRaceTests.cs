using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// Stop in Exclusive mode while a loop's batch holds the connection: the batch's end closes it. A read that takes the
/// connection for a moment as the batch gives it back (it sees the stop and gives it back) must not leave it open: whoever
/// gives it back last closes it. Debug hooks hold the read there, so the race is certain, not lucky. Process-wide hooks:
/// the class runs alone.
/// </summary>
[Collection(nameof(AllocationRuns))]
public sealed class CloseRaceTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose()
    {
#if DEBUG
        SqliteReaders.AfterExit = null;
        SqliteReaders.StoppedRead = null;
#endif
        _db.Dispose();
    }

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
}
