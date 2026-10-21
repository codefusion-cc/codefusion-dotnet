using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

[CollectionDefinition(nameof(AllocationRuns), DisableParallelization = true)]
public sealed class AllocationRuns;

/// <summary>
/// Allocations of the provider's modes, counted over the whole process (<c>GC.GetTotalAllocatedBytes</c>), so they run
/// alone: another test class running beside them would count too. Release only.
/// </summary>
[Collection(nameof(AllocationRuns))]
public sealed class ProviderAllocationTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    static bool Debug() => typeof(SqliteDataSource).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
        is [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    // ---------- Exclusive: reads and writes on the one connection ----------

    [Fact]
    public async Task ExclusiveQueriesAllocateNothingOnceWarm()
    {
        Assert.SkipWhen(Debug(), "Debug builds make every async state machine an object: run in Release");
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

    // ---------- expected failures ----------

    [Fact]
    public async Task UniqueViolationsAllocateNothingOnceWarm()
    {
        Assert.SkipWhen(Debug(), "Debug builds make every async state machine an object: run in Release");
        var s = _db.Open(_db.Database());
        Assert.True((await Kv.WriteMany(s, "k", 100)).IsOk);
        static async Task<long> Taken(SqliteDataSource s, int n)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < n; i++)
            {
                var c = s.Write(Kv.Put);
                c.Text(Key);
                c.Int64(i);
                // a 409: the key is there
                if ((await Sqlite.Run<Value>(c.Hold(), null, default).ConfigureAwait(false)).Status != DataStatus.UniqueViolation)
                    throw new InvalidOperationException("the write wasn't refused");
            }
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }
        await Taken(s, 2_000); // warm
        long once = long.MaxValue, twice = long.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            once = Math.Min(once, await Taken(s, 2_000));
            twice = Math.Min(twice, await Taken(s, 4_000));
        }
        Assert.True(twice - once < 400, $"{once} B for 2,000 refused writes, {twice} B for 4,000");
    }

    // ---------- waiting for the connection ----------

    /// <summary>A loop's batch holds the Exclusive connection for its whole wake-up. A write off the loop (the writer
    /// thread's) and a read on another thread wait for it, parked: measured over the whole process, as the CPU the
    /// process uses while they wait.</summary>
    [Fact(Timeout = 30_000)]
    public async Task NothingSpinsWhileALoopsBatchHoldsTheConnection()
    {
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        Assert.True((await Kv.Write(s, "k", 1)).IsOk);
        using var loop = new TestLoop(s);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ValueTask<DataResult> batched = default;
        var wakeUp = loop.WakeUp(() =>
        {
            batched = Kv.Write(s, "batched", 1);
            holding.Set();
            release.Wait();
        });
        holding.Wait(TestContext.Current.CancellationToken);
        // what the process uses anyway (other tests' sources, the GC, the JIT), then with the two waiting
        TimeSpan cpu = Cpu();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        double idle = (Cpu() - cpu).TotalMilliseconds;
        var queued = Kv.Write(s, "queued", 2).AsTask();
        var read = Task.Run(() => Kv.Read(s, "k"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // both are waiting now
        cpu = Cpu();
        var wall = Stopwatch.StartNew();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        double used = (Cpu() - cpu).TotalMilliseconds;
        long waited = wall.ElapsedMilliseconds;
        Assert.False(queued.IsCompleted || read.IsCompleted, "nothing waited for the batch");
        var woken = Stopwatch.StartNew();
        release.Set();
        await wakeUp;
        Assert.True((await batched).IsOk);
        Assert.True((await queued).IsOk);
        Assert.Equal(1, (await read).Rows);
        // two spinning threads would use two cores' worth, about 1,000 ms
        Assert.True(used - idle < 300, $"{used:F0} ms of CPU in {waited} ms while two threads waited for the connection, {idle:F0} ms without them");
        // woken by the batch's end, not found by a parked thread's own second look
        Assert.True(woken.ElapsedMilliseconds < 200, $"the waiters took {woken.ElapsedMilliseconds} ms after the batch ended");
    }

    static TimeSpan Cpu()
    {
        using var me = Process.GetCurrentProcess();
        return me.TotalProcessorTime;
    }

    // ---------- event-loop batches ----------

    [Fact(Timeout = 30_000)]
    public async Task BatchedWritesAllocateNothingOnceWarm()
    {
        Assert.SkipWhen(Debug(), "Debug builds make every async state machine an object: run in Release");
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        Assert.True((await Kv.WriteMany(s, "k", 100)).IsOk);
        using var loop = new TestLoop(s);
        var writes = new ValueTask<DataResult>[32];
        int perWakeUp = 16;
        Action wakeUp = () =>
        {
            for (int i = 0; i < perWakeUp; i++)
            {
                var c = s.Write(Set);
                c.Text(Key);
                c.Int64(i);
                writes[i] = Sqlite.Run<Value>(c.Hold(), null, default);
            }
        };
        async Task<long> Measure(int wakeUps, int writesEach)
        {
            perWakeUp = writesEach;
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int n = 0; n < wakeUps; n++)
            {
                await loop.WakeUp(wakeUp);
                for (int i = 0; i < writesEach; i++)
                    if (!writes[i].Result.IsOk) throw new InvalidOperationException("a batched write failed");
            }
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }
        await Measure(200, 32); // warm
        long once = long.MaxValue, twice = long.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            once = Math.Min(once, await Measure(500, 16));
            twice = Math.Min(twice, await Measure(500, 32));
        }
        // the same 500 wake-ups (the test loop's own task each), twice as many writes: the 8,000 extra add nothing
        Assert.True(twice - once < 8_000 / 20, $"{once} B for 500 wake-ups of 16 writes, {twice} B of 32");
    }
}
