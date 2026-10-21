using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using System.Threading;
using Ops.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// <c>Each</c>: a request's reads started together, so the provider sends them in one
/// batch, and their rows given back in the order they were asked for.
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class EachTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    static readonly Utf8[] People = [U("ann"), U("ben"), U("cid"), U("dee")];
    static readonly Utf8[] WithMissing = [U("ann"), U("nobody"), U("cid"), U("nobody either")];

    OpsDb Db()
    {
        TestDb.Require();
        var db = fixture.Reads.CreateContext<OpsDb>();
        Next(db);
        return db;
    }

    void Next(OpsDb db)
    {
        _response.Next();
        db.Bind(_response);
    }

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    static string[] Names(Rows<Name> rows) => rows.Span.ToArray().Select(n => n.Text.ToString()).ToArray();

    [Fact]
    public async Task TheRowsComeInTheOrderTheyWereAskedFor()
    {
        var rows = await Db().Each(4, static (db, i) => db.ByName(People[3 - i]));
        Assert.True(rows.Found);
        Assert.False(rows.Failed);
        Assert.Equal(["dee", "cid", "ben", "ann"], Names(rows));
        Assert.Equal([4L, 3, 2, 1], rows.Span.ToArray().Select(n => n.Id));
    }

    [Fact]
    public async Task NoneAskedForIsAnEmptyList()
    {
        var rows = await Db().Each<OpsDb, Name>(0, static (db, i) => throw new InvalidOperationException("never called"));
        Assert.True(rows.Found);
        Assert.Equal(0, rows.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => Db().Each(-1, static (db, i) => db.ByName(People[0])));
        Assert.Throws<ArgumentNullException>(() => Db().Each<OpsDb, Name>(1, null!));
    }

    [Fact]
    public async Task AReadThatFindsNoRowIsLeftOut()
    {
        var rows = await Db().Each(4, static (db, i) => db.ByName(WithMissing[i]));
        Assert.False(rows.Failed);
        Assert.Equal(["ann", "cid"], Names(rows));
    }

    [Fact]
    public async Task FiveHundredReadsInOneRequest()
    {
        var db = Db();
        for (int round = 0; round < 3; round++)
        {
            Next(db);
            var rows = await db.Each(500, static (db, i) => db.ByName(People[i % 4]));
            Assert.Equal(500, rows.Count);
            for (int i = 0; i < 500; i++) Assert.Equal(i % 4 + 1, rows[i].Id);
        }
    }

    // An Each's reads finish on the provider's threads, while the handler may still be running: the gathering (the
    // slots, the list, the rows' text) must wait for the handler to resume, or it races the handler's own work.
    // Before, each read's slot went back to its free list on the provider's thread as its read finished.
    [Fact]
    public async Task AnEachGathersOnlyWhereItIsAwaited()
    {
        var db = Db();
        var pending = db.Each(4, static (db, i) => db.ByName(People[i]));
        int started = EachPool<Name>.Slots; // its four reads have their slots
        while (!pending.IsCompleted) await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.Equal(started, EachPool<Name>.Slots); // finished, and none given back yet
        Assert.Equal(["ann", "ben", "cid", "dee"], Names(await pending));
        Assert.Equal(started + 4, EachPool<Name>.Slots);
    }

    // The rows' text goes into the Each's own arena (its reads finish on several threads at once), never into the
    // request's, which the handler may be using meanwhile
    [Fact]
    public async Task AnEachTakesNothingFromTheRequestsArena()
    {
        var db = Db();
        var rows = await db.Each(4, static (db, i) => db.ByName(People[i]));
        Assert.Equal(["ann", "ben", "cid", "dee"], Names(rows));
        Assert.Equal(0, _response.Arena.Allocations);
    }

    internal static bool Debug() => typeof(DataContext).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
        is [System.Diagnostics.DebuggableAttribute { IsJITOptimizerDisabled: true }];

    [Fact]
    public async Task AnEachAwaitedTwiceGivesItsRunBackOnce()
    {
        var db = Db();
        var each = db.Each(2, static (db, i) => db.ByName(People[i]));
        Assert.Equal(["ann", "ben"], Names(await each));
#pragma warning disable xUnit1031 // the misuse under test: the result is read a second time
        Assert.Throws<InvalidOperationException>(() => each.GetAwaiter().GetResult());
#pragma warning restore xUnit1031
        // a run given back twice would be lent to both of these, and the second's start would end the first's
        var a = db.Each(1, static (db, i) => db.ByName(People[2]));
        var b = db.Each(1, static (db, i) => db.ByName(People[3]));
        Assert.Equal(["cid"], Names(await a));
        Assert.Equal(["dee"], Names(await b));
    }

#if DEBUG
    // A run whose waiting fails while its reads are still in flight can't go back to the pool: the reads would
    // finish into the next Each that takes it
    [Fact]
    public async Task AnEachWhoseStepFailsKeepsItsRunOutOfThePool()
    {
        var db = Db();
        await db.Each(2, static (db, i) => db.ByName(People[i])); // a run in the pool
        int pooled = RunPool<EachRun<Name>>.Count;
        EachRun<Name>.StepFault = static () => throw new InvalidOperationException("step");
        try
        {
            var each = db.Each(4, static (db, i) => db.ByName(People[i]));
            Assert.Equal("step", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await each)).Message);
        }
        finally
        {
            EachRun<Name>.StepFault = null;
        }
        Assert.Equal(pooled - 1, RunPool<EachRun<Name>>.Count);
        Next(db);
        Assert.Equal(["ann", "ben"], Names(await db.Each(2, static (db, i) => db.ByName(People[i]))));
    }

    [Fact]
    public async Task AWriteEachWhoseStepFailsKeepsItsRunOutOfThePool()
    {
        var db = Writer();
        await db.Each(1, Make(9501), static (db, s, i) => db.AddLabelOnce(s.Ids[i], s.Names[i])); // a run kept
        int kept = RunPool<WriteEachRun>.Count;
        WriteEachRun.StepFault = static () => throw new InvalidOperationException("step");
        try
        {
            var each = db.Each(2, Make(9502, 9503), static (db, s, i) => db.AddLabelOnce(s.Ids[i], s.Names[i]));
            Assert.Equal("step", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await each)).Message);
        }
        finally
        {
            WriteEachRun.StepFault = null;
        }
        Assert.Equal(kept - 1, RunPool<WriteEachRun>.Count);
    }
#endif

    [Fact]
    public async Task RowsUsedAfterTheirRequestFailInDebug()
    {
        Assert.SkipUnless(Debug(), "Release reads the rows unchecked: run in Debug");
        var db = Db();
        var each = await db.Each(4, static (db, i) => db.ByName(People[i]));
        var many = await db.Sleeps(0);
        Assert.Equal(4, each.Span.Length);
        db.Unbind(); // the lists go back to pools other connections take from
        Assert.Throws<InvalidOperationException>(() => each.Span.Length);
        Assert.Throws<InvalidOperationException>(() => many.Span.Length);
        Assert.Throws<InvalidOperationException>(() => each[0]);
    }

    [Fact]
    public async Task FiveHundredReadsAllocateNothingOnceWarm()
    {
        Assert.SkipWhen(Debug(), "Debug builds make every async method's state machine a class: run in Release");
        await Peak(); // the pools let go of the arrays of runs and lists not used since, not of the one in use
        var db = Db();
        async Task<long> Round()
        {
            var rows = await db.Each(500, static (db, i) => db.ByName(People[i % 4])).ConfigureAwait(false);
            Next(db);
            return rows.Count;
        }
        long sum = await Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++) await Round().ConfigureAwait(false);
            return 0L;
        });
        var (count, allocated) = await Task.Run(async () =>
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long n = 0;
            for (int i = 0; i < 4; i++) n += await Round().ConfigureAwait(false);
            return (n, GC.GetTotalAllocatedBytes(precise: true) - before);
        });
        Assert.Equal(2000, count);
        // the test's own async methods: a few hundred bytes; a slot made anew is about 100 B, per read
        Assert.True(allocated < 4096, $"{allocated} bytes for 4 requests of 500 reads");
    }

    // The pools are the process's: a peak of large Eaches must leave a bounded amount behind, not every run's and
    // list's array at its largest
    [Fact]
    public async Task APeakOfLargeEachesLeavesABoundedPoolBehind()
    {
        await Peak();
        Assert.InRange(RunPool<EachRun<Name>>.KeptEntries, 1000, RunPool<EachRun<Name>>.EntryBudget); // some large runs stay warm
        Assert.InRange(EachLists<Name>.KeptItems, 500, EachLists<Name>.ItemBudget);
    }

    /// <summary>40 connections' Eaches of 500 at once: more than the pools keep.</summary>
    async Task Peak()
    {
        var contexts = new OpsDb[40];
        var eaches = new ValueTask<Rows<Name>>[contexts.Length];
        for (int c = 0; c < contexts.Length; c++)
        {
            contexts[c] = Db();
            eaches[c] = contexts[c].Each(500, static (db, i) => db.ByName(People[i % 4]));
        }
        for (int c = 0; c < contexts.Length; c++)
        {
            Assert.Equal(500, (await eaches[c]).Count);
            contexts[c].Unbind();
        }
    }

    [Fact]
    public async Task AnIdleConnectionKeepsNothingOfALargeEach()
    {
        Assert.SkipWhen(Debug(), "Debug keeps more per object: run in Release");
        var warm = Db();
        await warm.Each(500, static (db, i) => db.ByName(People[i % 4])); // the shared pools warm
        warm.Unbind();
        var contexts = new OpsDb[50];
        long before = LiveBytes();
        for (int c = 0; c < contexts.Length; c++)
        {
            var db = contexts[c] = fixture.Reads.CreateContext<OpsDb>();
            Next(db);
            Assert.Equal(500, (await db.Each(500, static (db, i) => db.ByName(People[i % 4]))).Count);
            db.Unbind(); // the request ended, and no other came: the connection is idle
        }
        long perConnection = (LiveBytes() - before) / contexts.Length;
        GC.KeepAlive(contexts);
        // a context of its own and its few small arrays; keeping a large Each's slots, list and array was ~70 KB
        Assert.True(perConnection < 8 << 10, $"{perConnection} bytes kept per idle connection");
    }

    static long LiveBytes()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetGCMemoryInfo().HeapSizeBytes;
    }

    [Fact]
    public async Task AnEachThatFailsIsReportedOnce()
    {
        TestDb.Require();
        int reported = 0;
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops");
        settings.Port = 1; // nothing listens: every read fails Broken
        var source = new PostgresDatabase(settings) { OnFailure = (_, _) => Interlocked.Increment(ref reported) }.Open(1, 1);
        try
        {
            var db = source.CreateContext<OpsDb>();
            Next(db);
            var rows = await db.Each(5, static (db, i) => db.ByName(People[i % 4]));
            Assert.True(rows.Failed);
            Assert.Equal(DataStatus.Broken, rows.Status);
            Assert.Equal(1, Volatile.Read(ref reported));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task TwoAtOnceKeepTheirOwnRows()
    {
        var db = Db();
        var first = db.Each(2, static (db, i) => db.ByName(People[i]));
        var second = db.Each(3, static (db, i) => db.ByName(People[3 - i]));
        var b = await second;
        var a = await first;
        Assert.Equal(["ann", "ben"], Names(a));
        Assert.Equal(["dee", "cid", "ben"], Names(b));
    }

    [Fact]
    public async Task AReadThatThrowsFailsTheWholeAfterTheStartedOnesEnd()
    {
        var db = Db();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.Each(4, static (db, i) => i == 2 ? throw new InvalidOperationException("bad id") : db.ByName(People[i])));
        Assert.Equal("bad id", error.Message);
        Next(db);
        Assert.Equal(["ann", "ben", "cid", "dee"], Names(await db.Each(4, static (db, i) => db.ByName(People[i]))));
    }

    [Fact]
    public async Task AnAbortedRequestsReadsAreCanceled()
    {
        var db = Db();
        _response.Abort();
        try
        {
            var rows = await db.Each(3, static (db, i) => db.ByName(People[i]));
            Assert.True(rows.Failed);
            Assert.Equal(DataStatus.Canceled, rows.Status);
            Assert.NotNull(rows.Error);
            Assert.Equal(0, rows.Count);
        }
        finally
        {
            _response.Unabort();
        }
    }

    OpsDb Writer()
    {
        TestDb.Require();
        var db = fixture.Writes.CreateContext<OpsDb>();
        Next(db);
        return db;
    }

    static async Task<long> Count(string where)
    {
        await using var admin = TestDb.Admin("cfdata_ops_w");
        return (long)(await admin.Scalar("SELECT count(*) FROM labels WHERE " + where))!;
    }

    sealed record Labels(int[] Ids, Utf8[] Names);

    static Labels Make(params int[] ids) => new(ids, ids.Select(i => U("label-" + i)).ToArray());

    [Fact]
    public async Task WritesStartedTogetherAllCommit()
    {
        var labels = Make(9001, 9002, 9003, 9004, 9005);
        var result = await Writer().Each(5, labels, static (db, s, i) => db.AddLabelOnce(s.Ids[i], s.Names[i]));
        Assert.True(result.IsOk);
        Assert.Equal(5, await Count("id BETWEEN 9001 AND 9005"));
    }

    [Fact]
    public async Task AFailingWriteIsTheResultAndTheOthersStillCommit()
    {
        var db = Writer();
        Assert.True((await db.AddLabel(9101, U("label-9101"))).IsOk);
        Next(db);
        var labels = Make(9100, 9101, 9102);
        var result = await db.Each(3, labels, static (db, s, i) => db.AddLabel(s.Ids[i], s.Names[i]));
        Assert.Equal(DataStatus.UniqueViolation, result.Status);
        Assert.Equal(3, await Count("id BETWEEN 9100 AND 9102"));
    }

    [Fact]
    public async Task AWriteEachAwaitedTwiceGivesItsRunBackOnce()
    {
        var db = Writer();
        var first = db.Each(1, Make(9401), static (db, s, i) => db.AddLabelOnce(s.Ids[i], s.Names[i]));
        Assert.True((await first).IsOk);
#pragma warning disable xUnit1031 // the misuse under test: the result is read a second time
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
#pragma warning restore xUnit1031
        var a = db.Each(1, Make(9402), static (db, s, i) => db.AddLabelOnce(s.Ids[i], s.Names[i]));
        var b = db.Each(1, Make(9403), static (db, s, i) => db.AddLabelOnce(s.Ids[i], s.Names[i]));
        Assert.True((await a).IsOk);
        Assert.True((await b).IsOk);
        Assert.Equal(3, await Count("id BETWEEN 9401 AND 9403"));
    }

    // A write Each's run is pooled as the reads' are: a connection keeps none, and a peak of large ones leaves a
    // bounded pool behind (a connection kept up to 1,024 awaiters, about 40 KB, before)
    [Fact]
    public async Task APeakOfLargeWriteEachesLeavesABoundedPoolBehind()
    {
        var contexts = new OpsDb[12];
        var eaches = new ValueTask<DataResult>[contexts.Length];
        for (int c = 0; c < contexts.Length; c++)
        {
            contexts[c] = Writer();
            eaches[c] = contexts[c].Each(1000, Make(9601), static (db, s, i) => db.AddLabelOnce(s.Ids[0], s.Names[0]));
        }
        foreach (var each in eaches) Assert.True((await each).IsOk);
        Assert.InRange(RunPool<WriteEachRun>.KeptEntries, 1000, RunPool<WriteEachRun>.EntryBudget);
    }

    [Fact]
    public async Task NoWritesIsOk()
    {
        var result = await Writer().Each(0, Make(), static (db, s, i) => throw new InvalidOperationException("never called"));
        Assert.True(result.IsOk);
        Assert.Throws<ArgumentOutOfRangeException>(() => Writer().Each(-1, Make(), static (db, s, i) => db.AddLabelOnce(1, U("x"))));
    }

    [Fact]
    public async Task AWriteThatThrowsFailsTheWholeAfterTheStartedOnesEnd()
    {
        var db = Writer();
        var labels = Make(9201, 9202, 9203);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await db.Each(3, labels, static (db, s, i) => i == 1 ? throw new InvalidOperationException("bad") : db.AddLabelOnce(s.Ids[i], s.Names[i])));
        Assert.Equal("bad", error.Message);
        Assert.Equal(1, await Count("id BETWEEN 9201 AND 9203"));
        Next(db);
        Assert.True((await db.Each(1, labels, static (db, s, i) => db.AddLabelOnce(s.Ids[2], s.Names[2]))).IsOk);
    }
}
