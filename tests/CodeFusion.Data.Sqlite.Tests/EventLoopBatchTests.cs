using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// <see cref="SqliteDatabase.EventLoopBatches"/>: a write made on an event loop's thread runs there at once, inside the
/// loop wake-up's one transaction, and completes when the server ends the batch (the source's
/// <see cref="SqliteDataSource.EndEventLoopBatch"/>), after the COMMIT and before the batch's answers go out. A
/// <see cref="TestLoop"/> plays the server: a thread marked as loop 0 that runs a wake-up's handlers, then ends its batch.
/// </summary>
public sealed class EventLoopBatchTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    SqliteDataSource Open() => _db.Open(_db.Database(exclusive: true, batches: true));

    [Fact(Timeout = 30_000)]
    public async Task ABatchsCompletionsStayItsOwnWhileAnotherLoopBatches()
    {
        var s = Open();
        using var one = new TestLoop(0, s);
        using var two = new TestLoop(1, s);
        using var completing = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var beforeEnd = new ManualResetEventSlim();
        var r = new ValueTask<DataResult>[3];
        var y = new ValueTask<DataResult>[3];
        // loop one ends its batch and, completing r0, waits: the connection is free, r1 and r2 not completed yet
        var first = one.WakeUp(() =>
        {
            for (int i = 0; i < r.Length; i++) r[i] = Kv.Write(s, "r" + i, i);
            var r0 = r[0].GetAwaiter();
            r0.OnCompleted(() =>
            {
                completing.Set();
                resume.Wait();
            });
        });
        try
        {
            completing.Wait(TestContext.Current.CancellationToken);
            // loop two batches twice meanwhile; its second batch holds y0..y2, uncommitted, until beforeEnd
            ValueTask<DataResult> x = default;
            await two.WakeUp(() => x = Kv.Write(s, "x", 1));
            Assert.True((await x).IsOk);
            var second = two.WakeUp(() =>
            {
                for (int i = 0; i < y.Length; i++) y[i] = Kv.Write(s, "y" + i, i);
                beforeEnd.Wait();
            });
            await Task.Delay(100, TestContext.Current.CancellationToken);
            resume.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(Array.Exists(y, w => w.IsCompleted), "loop one completed loop two's writes before their COMMIT");
            beforeEnd.Set();
            await second;
            foreach (var w in r) Assert.True((await w.AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).IsOk);
            foreach (var w in y) Assert.True((await w).IsOk);
        }
        finally
        {
            resume.Set();
            beforeEnd.Set();
        }
        Assert.Equal(7, Kv.Count(s));
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteMadeAfterItsSourcesEndRanEndsInTheSameWakeUp()
    {
        var a = Open();
        var b = _db.Open(_db.Database("b.db", exclusive: true, batches: true));
        using var loop = new TestLoop(a, b); // a's end runs before b's
        ValueTask<DataResult> late = default;
        bool wrote = false;
        await loop.WakeUp(() =>
        {
            var first = Kv.Write(b, "first", 1).GetAwaiter();
            // b's end completes it, after a's end ran: its continuation's write opens a batch on a
            first.OnCompleted(() =>
            {
                Assert.True(first.GetResult().IsOk);
                late = Kv.Write(a, "late", 2);
                wrote = true;
            });
        });
        Assert.True(wrote);
        Assert.True(late.IsCompleted, "the write waits for the loop's next wake-up, which may never come");
        Assert.True((await late).IsOk);
        Assert.Equal(1, Kv.Read(a, "late").Rows);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWakeUpsWritesCommitTogetherAndCompleteOnlyAfterTheCommit()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        long commits = s.Writer.Counters.Commits;
        var writes = new ValueTask<DataResult>[3];
        bool anyDoneInside = true;
        await loop.WakeUp(() =>
        {
            for (int i = 0; i < writes.Length; i++) writes[i] = Kv.Write(s, "w" + i, i);
            anyDoneInside = Array.Exists(writes, w => w.IsCompleted);
        });
        Assert.False(anyDoneInside);
        foreach (var w in writes) Assert.True(w.IsCompleted); // completed in the batch's end, on the loop
        foreach (var w in writes) Assert.True((await w).IsOk);
        Assert.Equal(commits + 1, s.Writer.Counters.Commits);
        Assert.Equal(3, Kv.Count(s, "w%"));
    }

    [Fact(Timeout = 30_000)]
    public async Task AnotherThreadsReadWaitsForTheBatchAndThenSeesAllOfIt()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        using var written = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<long>? reading = null;
        ValueTask<DataResult> a = default, b = default;
        var wake = loop.WakeUp(() =>
        {
            a = Kv.Write(s, "a", 1);
            b = Kv.Write(s, "b", 2);
            written.Set();
            reading = Task.Run(() => Kv.Count(s));
            release.Wait(); // the batch stays open while the other thread tries to read
        });
        written.Wait(TestContext.Current.CancellationToken);
        while (reading is null) Thread.Yield();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(reading.IsCompleted);
        release.Set();
        await wake;
        Assert.Equal(2, await reading);
        Assert.True((await a).IsOk && (await b).IsOk);
    }

    [Fact(Timeout = 30_000)]
    public async Task AReadOnTheLoopSeesItsBatchsWritesAndAnswersAfterTheirCommit()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        long commits = s.Writer.Counters.Commits;
        ValueTask<DataResult> mine = default, read = default, before = default;
        bool doneInside = true;
        await loop.WakeUp(() =>
        {
            before = Kv.ReadLater(s, "mine"); // nothing written yet in this wake-up: answered at once
            mine = Kv.Write(s, "mine", 7);
            read = Kv.ReadLater(s, "mine");
            doneInside = read.IsCompleted;
        });
        Assert.True(before.IsCompleted);
        Assert.Equal(0, (await before).Rows);
        Assert.False(doneInside, "the read answered before the COMMIT of the write it saw");
        var r = await read;
        Assert.True(r.IsOk, r.ToString());
        Assert.Equal(7, r.Value);
        Assert.True((await mine).IsOk);
        Assert.Equal(commits + 1, s.Writer.Counters.Commits);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWakeUpOfReadsAndWritesCommitsOnce()
    {
        var s = Open();
        Assert.True((await Kv.Write(s, "k", 1)).IsOk);
        using var loop = new TestLoop(s);
        long commits = s.Writer.Counters.Commits;
        var all = new ValueTask<DataResult>[16];
        await loop.WakeUp(() =>
        {
            // eight requests, each a read then a write, as another request's handler would make them
            for (int i = 0; i < 8; i++)
            {
                all[2 * i] = Kv.ReadLater(s, "k");
                all[2 * i + 1] = Kv.Write(s, "w" + i, i);
            }
        });
        foreach (var t in all) Assert.True((await t).IsOk);
        Assert.Equal(commits + 1, s.Writer.Counters.Commits);
        Assert.Equal(9, Kv.Count(s));
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailingWriteFailsAloneAndAUnitIsUndoneWhole()
    {
        var s = Open();
        Assert.True((await Kv.Write(s, "taken", 0)).IsOk);
        using var loop = new TestLoop(s);
        ValueTask<DataResult> before = default, conflict = default, unit = default, after = default;
        await loop.WakeUp(() =>
        {
            before = Kv.Write(s, "before", 1);
            conflict = Kv.Write(s, "taken", 2);
            var c = s.Write(Kv.Put);
            c.Text("unit-a");
            c.Int64(3);
            c.Then(Kv.Put);
            c.Text("taken");
            c.Int64(4);
            unit = Sqlite.Run<Value>(c.Hold(), null, default);
            after = Kv.Write(s, "after", 5);
        });
        Assert.True((await before).IsOk);
        Assert.Equal(DataStatus.UniqueViolation, (await conflict).Status);
        Assert.Equal(DataStatus.UniqueViolation, (await unit).Status);
        Assert.True((await after).IsOk);
        Assert.Equal(1, Kv.Read(s, "before").Value);
        Assert.Equal(0, Kv.Read(s, "taken").Value);
        Assert.Equal(0, Kv.Read(s, "unit-a").Rows);
        Assert.Equal(5, Kv.Read(s, "after").Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteMadeByACompletionIsCommittedBeforeTheBatchEnds()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        Task? chained = null;
        await loop.WakeUp(() =>
        {
            chained = Chain(s);
        });
        // the first write's continuation ran inside the batch's end and wrote again; that write committed there too
        Assert.True(chained!.IsCompletedSuccessfully);
        Assert.Equal(1, Kv.Read(s, "second").Value);
    }

    static async Task Chain(SqliteDataSource s)
    {
        Assert.True((await Kv.Write(s, "first", 1)).IsOk);
        Assert.True(EventLoopThread.Current >= 0); // completed on the loop
        Assert.True((await Kv.Write(s, "second", 1)).IsOk);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteOffTheLoopGoesToTheWriterAndWaitsForTheBatch()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        using var release = new ManualResetEventSlim();
        ValueTask<DataResult> offLoop = default, onLoop = default;
        var wake = loop.WakeUp(() =>
        {
            onLoop = Kv.Write(s, "on", 1);
            offLoop = Task.Run(() => Kv.Write(s, "off", 2)).GetAwaiter().GetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(offLoop.IsCompleted); // the writer waits for the loop's transaction
        release.Set();
        await wake;
        Assert.True((await offLoop).IsOk);
        Assert.True((await onLoop).IsOk);
        Assert.Equal(2, Kv.Count(s));
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteOnNoLoopCommitsAtOnceWithNoBatchEnd()
    {
        var s = Open();
        // a thread no loop runs (EventLoopThread.Current is -1): the Sockets transport, or a handler resumed off its
        // loop. Nothing ever ends a batch here, and the write still commits, on the writer thread
        Assert.Equal(-1, EventLoopThread.Current);
        long commits = s.Writer.Counters.Commits;
        Assert.True((await Kv.Write(s, "plain", 1)).IsOk);
        Assert.Equal(commits + 1, s.Writer.Counters.Commits);
        Assert.Equal(1, Kv.Read(s, "plain").Value);
        // and the end of a batch on a thread with none open does nothing, however often the server calls it
        long after = s.Writer.Counters.Commits;
        for (int i = 0; i < 3; i++) s.EndEventLoopBatch(0);
        Assert.Equal(after, s.Writer.Counters.Commits);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABatchEndCalledAgainInTheSameWakeUpCommitsOnlyWhatIsNew()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        long commits = s.Writer.Counters.Commits;
        ValueTask<DataResult> first = default, second = default;
        await loop.WakeUp(() =>
        {
            first = Kv.Write(s, "round-1", 1);
            s.EndEventLoopBatch(0);  // the server's first round
            s.EndEventLoopBatch(0);  // again, nothing new: no transaction
            second = Kv.Write(s, "round-2", 2); // a pipelined request's handler, in a later round
        });
        Assert.True((await first).IsOk && (await second).IsOk);
        Assert.Equal(commits + 2, s.Writer.Counters.Commits);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACancelledWriteIsCanceledAndWritesNothing()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ValueTask<DataResult> w = default;
        await loop.WakeUp(() => w = Kv.Write(s, "never", 1, cancelled.Token));
        Assert.Equal(DataStatus.Canceled, (await w).Status);
        Assert.Equal(0, Kv.Count(s));
    }

    [Fact]
    public void BatchesNeedExclusiveMode() =>
        Assert.Throws<ArgumentException>(() => _db.Database(exclusive: false, batches: true).OpenSource());

    [Fact]
    public void AddDatabaseHandsTheServerTheBatchsEnd()
    {
        var registry = new Registry();
        var source = SqliteServices.AddDatabase<Ops.Data.OpsDb>(registry, _db.Database("batches.db", exclusive: true, batches: true));
        Assert.Single(registry.Batches);
        var plain = new Registry();
        var other = SqliteServices.AddDatabase<Ops.Data.OpsDb>(plain, _db.Database("plain.db", exclusive: true));
        Assert.Empty(plain.Batches);
        SqliteChecks.Stop(source, other);
    }

    sealed class Registry : IServiceRegistry
    {
        public readonly List<Action<int>> Batches = [];

        public void AddSingleton<TService>(TService instance) where TService : class { }

        public void AddPerConnection<TService>(Func<TService> factory) where TService : class, IRequestScoped { }

        public void OnStopping(Action onStopping) { }

        public void OnEventLoopBatch(Action<int> onBatch) => Batches.Add(onBatch);
    }
}

/// <summary>A server's event loop as the SQLite provider sees one: a thread marked as loop 0 that runs one wake-up's
/// handlers at a time, then ends its batch.</summary>
public sealed class TestLoop : IDisposable
{
    readonly BlockingCollection<(Action Handlers, TaskCompletionSource Done)> _wakeUps = [];
    readonly Thread _thread;

    /// <summary>The loop's thread.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>Loop 0, whose batch actions are <paramref name="sources"/>' ends, in that order.</summary>
    public TestLoop(params SqliteDataSource[] sources) : this(0, sources) { }

    /// <summary>Loop <paramref name="index"/>, whose batch actions are <paramref name="sources"/>' ends, in that order.</summary>
    public TestLoop(int index, params SqliteDataSource[] sources)
    {
        _thread = new Thread(() =>
        {
            EventLoopThread.Enter(index);
            foreach (var (handlers, done) in _wakeUps.GetConsumingEnumerable())
            {
                try
                {
                    handlers();
                    // as the transport does: the actions, again while something asked for another end, 8 rounds at most
                    for (int round = 0; round < 8; round++)
                    {
                        _ = EventLoopThread.TakeBatchEndRequest();
                        foreach (var source in sources) source.EndEventLoopBatch(index);
                        if (!EventLoopThread.BatchEndRequested) break;
                    }
                    done.SetResult();
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            }
        }) { IsBackground = true, Name = "test event loop" };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="handlers"/> on the loop, then the batch's ends; done when they are.</summary>
    public Task WakeUp(Action handlers)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wakeUps.Add((handlers, done));
        return done.Task;
    }

    public void Dispose()
    {
        _wakeUps.CompleteAdding();
        _thread.Join();
        _wakeUps.Dispose();
    }
}
