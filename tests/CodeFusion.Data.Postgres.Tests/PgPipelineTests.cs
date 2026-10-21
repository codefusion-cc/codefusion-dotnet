using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Npgsql;
using Xunit;

// The timeouts turn a read that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051, xUnit1031

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// Pipelined read groups: a read loop sends the next group while Postgres still answers the one before (at most
/// <see cref="PgReader.MaxInFlight"/> on a connection), and a burst queued while other loops sleep is shared out
/// among them. Each outcome must be what one group at a time gives.
/// </summary>
public sealed class PgPipelineTests : IAsyncDisposable
{
    static readonly PgStatement Sleep = new("SELECT 1 FROM pg_sleep(0.3)");
    static readonly PgStatement SleepThenFail = new("SELECT 1 / 0 FROM pg_sleep(0.3)");
    static readonly PgStatement Times10 = new("SELECT $1::bigint * 10", PgType.Int8);
    static readonly PgStatement Pid = new("SELECT pg_backend_pid()");
    static readonly PgStatement PlusOne = new("SELECT $1::bigint + 1", PgType.Int8);

    NpgsqlDataSource? _admin;
    PgReader? _reader;

    PgReader NewReader()
    {
        var settings = PgTestDb.Settings();
        _admin = PgTestDb.Admin();
        return _reader = new PgReader(settings);
    }

    static readonly PgRowHandler First = static (in PgRow r, object? _) => r.Index == 0 ? r.GetInt64(0) : throw new InvalidOperationException("a second row");

    static Task<PgResult> Try(PgReader reader, PgStatement s, params long[] args)
    {
        var cmd = reader.Command(s);
        foreach (long a in args) cmd.Int8(a);
        return cmd.Run(First, null).AsTask();
    }

    static async Task<long> Read(PgReader reader, PgStatement s, params long[] args)
    {
        var r = (await Try(reader, s, args)).ThrowIfFailed();
        Assert.Equal(1, r.Rows);
        return r.Value;
    }

    // A loop sends a group, then takes what has queued meanwhile and sends that too before it reads the first's
    // replies. Each test parses its statements first (a group binding a statement whose Parse is still unanswered
    // waits, see AGroupNeedingAParse...), then keeps the loop on a slow read while more than a group holds
    // (PgReader.MaxGroup) queues: once that read is answered, two groups go out at once.

    static Task<PgResult>[] Fillers(PgReader reader, int n, int first) => Enumerable.Range(first, n).Select(i => Try(reader, Times10, i)).ToArray();

    static async Task Values(Task<PgResult>[] reads, int first)
    {
        var got = await Task.WhenAll(reads);
        for (int i = 0; i < got.Length; i++) Assert.Equal((first + i) * 10L, got[i].ThrowIfFailed().Value);
    }

    /// <summary>A started reader whose connection has parsed the tests' statements, and a slow read it is now
    /// waiting on.</summary>
    async Task<(PgReader Reader, Task<PgResult> Slow)> Busy(params PgStatement[] parse)
    {
        var reader = NewReader();
        reader.Start(1);
        Assert.Equal(10, await Read(reader, Times10, 1));
        foreach (var p in parse) await Try(reader, p);
        var slow = Try(reader, Sleep);
        for (int i = 0; i < 200 && reader.Queued > 0; i++) await Task.Delay(5);
        await Task.Delay(50);
        return (reader, slow);
    }

    [Fact(Timeout = 30_000)]
    public async Task AGroupGoesOutWhileTheOneBeforeItIsStillBeingAnswered()
    {
        var (reader, slow) = await Busy();
        long before = reader.Counters.Pipelined;
        var first = Fillers(reader, PgReader.MaxGroup, 0);
        var second = Fillers(reader, 8, 1000);
        Assert.Equal(1, (await slow).Value);
        await Values(first, 0);
        await Values(second, 1000);
        Assert.Equal(1, reader.Counters.Pipelined - before);
        Assert.Equal(2, reader.InFlightMax);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailureInTheEarlierGroupLeavesTheLaterOneAloneAndItsSkippedReadsRunAgain()
    {
        var (reader, slow) = await Busy(SleepThenFail);
        long pipelined = reader.Counters.Pipelined, reruns = reader.Counters.Reruns;
        var failing = Try(reader, SleepThenFail);
        var skipped = Fillers(reader, PgReader.MaxGroup - 1, 0);
        var later = Fillers(reader, 5, 1000);
        await slow;

        var failed = await failing;
        Assert.Equal(PgStatus.ServerError, failed.Status);
        Assert.Equal(PgErrorCodes.DivisionByZero, failed.SqlState);
        await Values(later, 1000);
        await Values(skipped, 0);
        Assert.Equal(1, reader.Counters.Reruns - reruns);
        Assert.Equal(1, reader.Counters.Pipelined - pipelined);
    }

    [Fact(Timeout = 30_000)]
    public async Task AGroupNeedingAParseAnEarlierGroupSentWaitsForThatGroup()
    {
        var (reader, slow) = await Busy(SleepThenFail);
        long pipelined = reader.Counters.Pipelined;
        // a statement this connection never parsed: its Parse goes out in the first group, after a read that fails,
        // so Postgres skips it; the second group binds it too, and sent at once it would bind a statement that
        // doesn't exist
        var fresh = new PgStatement("SELECT $1::bigint * 100", PgType.Int8);
        var failing = Try(reader, SleepThenFail);
        var inFirst = Try(reader, fresh, 1);
        var fillers = Fillers(reader, PgReader.MaxGroup - 2, 0);
        var inSecond = Try(reader, fresh, 2);
        await slow;

        Assert.Equal(PgStatus.ServerError, (await failing).Status);
        Assert.Equal(100, (await inFirst).ThrowIfFailed().Value);
        Assert.Equal(200, (await inSecond).ThrowIfFailed().Value);
        await Values(fillers, 0);
        Assert.Equal(0, reader.Counters.Pipelined - pipelined); // the second group waited
    }

    [Fact(Timeout = 30_000)]
    public async Task ABrokenConnectionFailsEveryGroupInFlightAndTheNextReconnects()
    {
        var (reader, slow) = await Busy();
        long pid = await Read(reader, Pid);
        var longer = Try(reader, new PgStatement("SELECT 1 FROM pg_sleep(5)"));
        var first = Fillers(reader, PgReader.MaxGroup - 1, 0);
        var second = Fillers(reader, 5, 1000);
        for (int i = 0; i < 200 && reader.Queued > 0; i++) await Task.Delay(5);
        await Task.Delay(100);
        Assert.Equal(2, reader.InFlightMax);
        await _admin!.Scalar($"SELECT pg_terminate_backend({pid})");

        Assert.Equal(PgStatus.Broken, (await longer).Status);
        foreach (var r in await Task.WhenAll(first.Concat(second))) Assert.Equal(PgStatus.Broken, r.Status);
        Assert.NotEqual(pid, await Read(reader, Pid));
        Assert.Equal(70, await Read(reader, Times10, 7));
        Assert.Equal(1, (await slow).Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABurstWhileOtherLoopsSleepIsSharedOutAmongThem()
    {
        var reader = NewReader();
        reader.Start(4);
        for (int i = 0; i < 400 && reader.Sleeping < 4; i++) await Task.Delay(5);
        Assert.Equal(4, reader.Sleeping);
        long groups = reader.Counters.Groups;
        var burst = Enumerable.Range(0, 32).Select(i => Try(reader, PlusOne, i)).ToArray();
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (long)i), (await Task.WhenAll(burst)).Select(r => r.Value));
        Assert.True(reader.Counters.Groups - groups >= 2, $"{reader.Counters.Groups - groups} groups for a burst of 32");
    }

    [Fact(Timeout = 60_000)]
    public async Task UnderLoadNoConnectionHasMoreGroupsInFlightThanTheLimitAndEveryReadGetsItsAnswer()
    {
        var reader = NewReader();
        reader.Start(2);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(c => Task.Run(async () =>
        {
            long sum = 0;
            for (int i = 0; i < 300; i++) sum += await Read(reader, PlusOne, c * 1000 + i) - (c * 1000 + i);
            return sum;
        })));
        Assert.All(results, s => Assert.Equal(300, s));
        Assert.InRange(reader.InFlightMax, 1, PgReader.MaxInFlight);
        Assert.True(reader.Counters.Pipelined > 0, "pipelined under load");
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null) LoopChecks.Stop(_reader);
        if (_admin is not null) await _admin.DisposeAsync();
    }
}
