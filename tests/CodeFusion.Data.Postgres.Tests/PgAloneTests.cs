using System;
using System.Linq;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Npgsql;
using Xunit;

// The timeouts turn a command that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051, xUnit1031

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// Statements that run alone (<see cref="PgStatement.RunsAlone"/>): on a connection of their own, each command in a
/// transaction of its own, so a slow one holds up no group, a write's group commit or a read group.
/// </summary>
public sealed class PgAloneTests : IAsyncDisposable
{
    static readonly PgStatement TxId = new("SELECT txid_current()");
    static readonly PgStatement SlowTxId = new("SELECT txid_current() FROM pg_sleep(0.5)") { RunsAlone = true };
    static readonly PgStatement Times10 = new("SELECT $1::bigint * 10", PgType.Int8);
    static readonly PgStatement SlowRead = new("SELECT $1::bigint FROM pg_sleep(0.5)", PgType.Int8) { RunsAlone = true };
    static readonly PgStatement Pid = new("SELECT pg_backend_pid()");
    static readonly PgStatement AlonePid = new("SELECT pg_backend_pid()") { RunsAlone = true };

    PgWriter? _writer;
    PgReader? _reader;

    static readonly PgRowHandler First = static (in PgRow r, object? _) => r.GetInt64(0);

    static Task<PgResult> Run(PgLoops loops, PgStatement s, params long[] args)
    {
        var cmd = loops.Command(s);
        foreach (long a in args) cmd.Int8(a);
        return cmd.Run(First, null).AsTask();
    }

    [Fact(Timeout = 30_000)]
    public async Task ASlowWriteThatRunsAloneHoldsUpNoGroupCommit()
    {
        var w = _writer = new PgWriter(PgTestDb.Settings());
        w.Start(1);
        var slow = Run(w, SlowTxId);
        await Task.Delay(50);
        var group = Enumerable.Range(0, 10).Select(_ => Run(w, TxId)).ToArray();
        var done = await Task.WhenAll(group);
        Assert.False(slow.IsCompleted, "the group waited for the slow write");
        Assert.All(done, r => Assert.True(r.IsOk));
        var s = (await slow).ThrowIfFailed();
        Assert.DoesNotContain(s.Value, done.Select(r => r.Value));
        Assert.Equal(1, w.Counters.Alone);
    }

    [Fact(Timeout = 30_000)]
    public async Task WritesThatRunAloneEachCommitInATransactionOfTheirOwn()
    {
        var w = _writer = new PgWriter(PgTestDb.Settings());
        var alone = Enumerable.Range(0, 3).Select(_ => Run(w, SlowTxId)).ToArray();
        var grouped = Enumerable.Range(0, 3).Select(_ => Run(w, TxId)).ToArray();
        w.Start(1);
        var a = (await Task.WhenAll(alone)).Select(r => r.ThrowIfFailed().Value).ToArray();
        var g = (await Task.WhenAll(grouped)).Select(r => r.ThrowIfFailed().Value).ToArray();
        Assert.Equal(3, a.Distinct().Count());
        Assert.Single(g.Distinct()); // the others still share one group commit
        Assert.Empty(a.Intersect(g));
        Assert.Equal(3, w.Counters.Alone);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASlowReadThatRunsAloneHoldsUpNoReadGroup()
    {
        var r = _reader = new PgReader(PgTestDb.Settings());
        r.Start(1);
        var slow = Run(r, SlowRead, 7);
        await Task.Delay(50);
        var reads = Enumerable.Range(0, 20).Select(i => Run(r, Times10, i)).ToArray();
        var got = await Task.WhenAll(reads);
        Assert.False(slow.IsCompleted, "the reads waited for the slow one");
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i * 10L), got.Select(x => x.ThrowIfFailed().Value));
        Assert.Equal(7, (await slow).ThrowIfFailed().Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task CommandsThatRunAloneUseAConnectionOfTheirOwn()
    {
        var r = _reader = new PgReader(PgTestDb.Settings());
        r.Start(1);
        long loop = (await Run(r, Pid)).ThrowIfFailed().Value;
        long alone = (await Run(r, AlonePid)).ThrowIfFailed().Value;
        Assert.NotEqual(loop, alone);
        Assert.Equal(alone, (await Run(r, AlonePid)).ThrowIfFailed().Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task AfterCompleteCommandsThatRunAloneAreStoppedToo()
    {
        var w = _writer = new PgWriter(PgTestDb.Settings());
        w.Start(1);
        Assert.True((await Run(w, SlowTxId)).IsOk);
        w.Complete();
        Assert.Equal(PgStatus.Stopped, (await Run(w, SlowTxId)).Status);
        Assert.True(w.WaitStopped(TimeSpan.FromSeconds(10)));
        _writer = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_writer is not null) LoopChecks.Stop(_writer);
        if (_reader is not null) LoopChecks.Stop(_reader);
        return default;
    }
}
