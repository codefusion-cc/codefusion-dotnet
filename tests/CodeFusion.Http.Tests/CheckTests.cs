using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// The runtime checks themselves fire: the pools' Debug hygiene (0xDD fill, a second return, a write after the
/// return), a pooled ValueTask read after its return, the request or response used after its handler, and an
/// exception from a handler's callback counted instead of dropped unseen.
/// </summary>
public sealed class CheckTests
{
    const string Get = "GET /x HTTP/1.1\r\nHost: x\r\n\r\n";

    static void DebugOnly() => Assert.SkipUnless(AllocationTests.Debugger(), "a check of Debug builds: Release ones carry none");

    [Fact]
    public void AReturnedBufferIsFilledWithDDAndASecondReturnThrows()
    {
        DebugOnly();
        var pool = new BufferPool(64, 4);
        byte[] a = pool.Rent();
        a.AsSpan().Fill(1);
        pool.Return(a);
        Assert.All(a, b => Assert.Equal(PoolCheck.Fill, b));
        Assert.Equal("a pooled buffer was returned twice", Assert.Throws<InvalidOperationException>(() => pool.Return(a)).Message);
    }

    [Fact]
    public void ABufferWrittenToAfterItsReturnThrowsWhenRentedAgain()
    {
        DebugOnly();
        var pool = new BufferPool(64, 4);
        byte[] a = pool.Rent();
        pool.Return(a);
        a[10] = 1; // a writer that kept it
        Assert.Equal("a pooled buffer was written to after its return", Assert.Throws<InvalidOperationException>(() => pool.Rent()).Message);
    }

    [Fact]
    public void ALargerArrayFromTheSharedPoolIsCountedOutAndFilledWhenBack()
    {
        DebugOnly();
        var pool = new BufferPool(64, 4);
        byte[] big = pool.RentLarger(1000);
        Assert.Equal(1, pool.Out);
        pool.Return(big);
        Assert.Equal(0, pool.Out);
        Assert.All(big, b => Assert.Equal(PoolCheck.Fill, b));
        Assert.Throws<InvalidOperationException>(() => pool.Return(big));
    }

    [Fact]
    public void ArraysOutAreCountedInReleaseBuildsToo()
    {
        var pool = new BufferPool(64, 1); // one array per shard, and at most 64 shards
        var rented = new byte[100][];
        for (int i = 0; i < rented.Length; i++) rented[i] = pool.Rent();
        Assert.Equal(100, pool.Out);
        foreach (byte[] a in rented) pool.Return(a);
        Assert.InRange(pool.Available, 1, 64); // the rest were dropped, and aren't out either
        Assert.Equal(0, pool.Out);
    }

    [Fact]
    public async Task APooledValueTaskReadAfterItsReturnThrows()
    {
        ValueTask<int> vt = WaitsThenAnswers();
        Assert.Equal(5, await vt); // reading it gave its box back
        // Debug builds: the box is in the pool; Release ones: its version moved on
        Assert.Throws<InvalidOperationException>(() => vt.GetAwaiter().GetResult());
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    static async ValueTask<int> WaitsThenAnswers()
    {
        await Task.Yield();
        return 5;
    }

    [Fact(Timeout = 10_000)]
    public async Task TheRequestReadOrTheResponseWrittenAfterItsHandlerThrows()
    {
        DebugOnly();
        HttpExchange? kept = null;
        await using var t = TestServer.Start(x =>
        {
            kept = x;
            return default;
        });
        using var c = await t.ConnectAsync();
        await c.SendAsync(Get);
        Assert.Equal(200, (await c.ReadAsync()).Status);
        Assert.Equal("the request was read outside its handler", Assert.Throws<InvalidOperationException>(() => { _ = kept!.Path; }).Message);
        Assert.Equal("the response was written after it was finished", Assert.Throws<InvalidOperationException>(() => { _ = kept!.GetSpan(); }).Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task AnAbortedCallbackThatThrowsIsCountedNotDroppedUnseen()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var t = TestServer.Start(async x =>
        {
            x.Aborted.Register(() => aborted.TrySetResult());
            x.Aborted.Register(() => throw new InvalidOperationException("a handler's callback"));
            started.SetResult();
            await release.Task;
        });
        var c = await t.ConnectAsync();
        await c.SendAsync(Get);
        await started.Task;
        c.Reset();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await t.ConnectionsReach(0);
        Assert.Equal(1, Volatile.Read(ref t.Server.CallbackFailures));
        Volatile.Write(ref t.Server.CallbackFailures, 0); // seen: the stop's check would fail the test on it
    }
}
