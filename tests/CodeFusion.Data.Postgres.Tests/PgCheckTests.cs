using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;

// The timeouts turn a command that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051, xUnit1031

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// The runtime checks themselves fire: a job's life in Debug builds (a second return, a completion out of order, a
/// buffer written after its return, 0xDD in a returned one), and in every build an exception a loop caught that no
/// failing connection explains, counted where it would otherwise only turn into Broken. Alone: they use the process's
/// job pool, and one test leaves a job the pool must not hand to another test.
/// </summary>
[Collection(nameof(Alone))]
public sealed class PgCheckTests
{
    static readonly PgStatement Echo = new("echo for checks", PgType.Int8);

    static void DebugOnly() => Assert.SkipUnless(
        typeof(PgJob).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is [DebuggableAttribute { IsJITOptimizerDisabled: true }],
        "a check of Debug builds: Release ones carry none");

    [Fact]
    public void AReturnedJobsBuffersAreFilledWithDDAndASecondReturnThrows()
    {
        DebugOnly();
        var job = PgJob.Rent(Echo);
        job.Int8(42);
        job.Discard();
        Assert.All(job.Buf, b => Assert.Equal(0xDD, b));
        Assert.Equal("a Postgres job was returned twice", Assert.Throws<InvalidOperationException>(job.Discard).Message);
    }

    [Fact]
    public void AJobCompletedBeforeItWasQueuedThrows()
    {
        DebugOnly();
        var job = PgJob.Rent(Echo);
        job.Int8(1);
        Assert.Contains("completed while Building", Assert.Throws<InvalidOperationException>(() => job.Complete(new PgResult(PgStatus.Ok))).Message,
            StringComparison.Ordinal);
        job.Discard();
    }

    [Fact]
    public void AJobsBufferWrittenToAfterItsReturnThrowsWhenRentedAgain()
    {
        DebugOnly();
        var job = PgJob.Rent(Echo);
        job.Discard();
        job.Buf[3] = 1; // a writer that kept it
        // the pool is a stack: the next rent takes this job, and the test runs alone, so no other test gets it
        Assert.Equal("a pooled Postgres job's buffer was written to after its return",
            Assert.Throws<InvalidOperationException>(() => PgJob.Rent(Echo)).Message);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExceptionNoFailingConnectionExplainsIsCountedAndAFailingConnectionIsNot(bool bug)
    {
        var server = new FakeServer();
        var settings = server.Settings();
        var open = settings.Transport!;
        settings.Transport = s => new FailsOnSend(open(s), bug);
        var r = new PgReader(settings);
        r.Start(1);
        Assert.Equal(PgStatus.Broken, (await r.Command(Echo).Int8(1).Run(null, null)).Status);
        Assert.Equal(bug ? 1 : 0, Volatile.Read(ref r.UnexpectedFailures));
        if (bug)
        {
            Assert.Equal("a transport's bug", r.FirstUnexpected!.Message);
            Volatile.Write(ref r.UnexpectedFailures, 0); // seen: the stop's check would fail the test on it
        }
        LoopChecks.Stop(r);
    }

    [Fact(Timeout = 20_000)]
    public async Task AContinuationThatThrowsIsCountedAndTheLoopGoesOn()
    {
        var server = new FakeServer { Rows = static b => [[ServerWriter.BE64(b.Int8(0))]] };
        var r = new PgReader(server.Settings());
        var awaiter = r.Command(Echo).Int8(7).Run(static (in PgRow row, object? _) => row.GetInt64(0), null).GetAwaiter();
        awaiter.UnsafeOnCompleted(() => throw new InvalidOperationException("a continuation's bug"));
        r.Start(1);
        for (long until = Environment.TickCount64 + 10_000; !awaiter.IsCompleted; await Task.Delay(5))
            Assert.True(Environment.TickCount64 < until, "the command didn't complete");
        Assert.Equal(7, awaiter.GetResult().Value);
        Assert.Equal(8, (await r.Command(Echo).Int8(8).Run(static (in PgRow row, object? _) => row.GetInt64(0), null)).Value);
        Assert.Equal(1, Volatile.Read(ref r.UnexpectedFailures));
        Assert.Equal("a continuation's bug", r.FirstUnexpected!.Message);
        Volatile.Write(ref r.UnexpectedFailures, 0);
        LoopChecks.Stop(r);
    }

    /// <summary>A connection whose first send after the startup fails: with a bug (an exception no network failure
    /// throws), or as a dropped connection does. The goodbye after it goes through.</summary>
    sealed class FailsOnSend(PgTransport inner, bool bug) : PgTransport
    {
        bool _started, _failed;

        public override int Receive(Span<byte> buffer) => inner.Receive(buffer);
        public override int ReceiveWaiting(Span<byte> buffer) => inner.ReceiveWaiting(buffer);

        public override void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null)
        {
            if (_started && !_failed)
            {
                _failed = true;
                throw bug ? new InvalidOperationException("a transport's bug") : new SocketException((int)SocketError.ConnectionReset);
            }
            inner.Send(data, whileBlocked);
        }

        public override void Started()
        {
            _started = true;
            inner.Started();
        }

        public override void Dispose() => inner.Dispose();
    }
}
