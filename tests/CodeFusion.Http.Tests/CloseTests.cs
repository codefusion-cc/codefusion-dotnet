using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// How a connection ends: a client's FIN after its request still gets the answer, a close with pipelined bytes
/// unread ends with a FIN rather than a reset, and the watch for a client that left neither aborts the wrong request
/// nor leaves a handler running under a closed connection.
/// </summary>
public sealed class CloseTests
{
    static void DebugOnly() => Assert.SkipUnless(AllocationTests.Debugger(), "needs the hooks only Debug builds call");

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    const string Get = "GET /x HTTP/1.1\r\nHost: x\r\n\r\n";

    // A FIN cancels Aborted, as in Kestrel, so work a client (or a proxy whose client) left stops; the answer still goes
    // out if the handler finishes anyway, to a client that may only have half-closed.
    [Fact(Timeout = 20_000)]
    public async Task AClientThatHalfClosesAfterItsRequestStillGetsAnAsyncHandlersAnswer()
    {
        await using var t = TestServer.Start(static async x =>
        {
            await Task.Delay(200);
            TestServer.Write(x, $"late aborted={x.IsAborted} canceled={x.Aborted.IsCancellationRequested}");
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync(Get);
        c.Socket.Shutdown(SocketShutdown.Send); // no more requests
        Response r = await c.ReadAsync();
        Assert.Equal((200, "late aborted=False canceled=True"), (r.Status, r.Text));
        Assert.True(await c.EndsWithFinAsync(), "no FIN after the answer");
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, true)] // the FIN came while the handler waited
    [InlineData(true, false)] // it came behind a pipelined request read with the first
    [InlineData(true, true)]  // behind a pipelined request that came while the handler waited: only the heartbeat sees it
    public async Task AFinCancelsAbortedAndTheAnswersAreStillSent(bool pipelined, bool later)
    {
        TaskCompletionSource started = Signal();
        TaskCompletionSource<long>[] canceled = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        int calls = 0;
        await using var t = TestServer.Start(async x =>
        {
            int n = Interlocked.Increment(ref calls);
            started.TrySetResult();
            try { await Task.Delay(5000, x.Aborted); }
            catch (OperationCanceledException)
            {
                canceled[n - 1].TrySetResult(Environment.TickCount64);
                if (n == 1) throw; // what a FIN cancels isn't a handler error: nothing reaches OnHandlerError
            }
            TestServer.Write(x, $"{n} aborted={x.IsAborted}");
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync(pipelined && !later ? Get + Get : Get);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (pipelined && later) await c.SendAsync(Get);
        await Task.Delay(100);
        Assert.False(canceled[0].Task.IsCompleted, "Aborted was canceled before the FIN");
        long fin = Environment.TickCount64;
        c.Socket.Shutdown(SocketShutdown.Send);
        Assert.InRange(await canceled[0].Task.WaitAsync(TimeSpan.FromSeconds(3)) - fin, 0, 1000);
        Response first = await c.ReadAsync();
        Assert.Equal(500, first.Status); // the handler threw, and its answer still went out
        if (pipelined)
        {
            Response second = await c.ReadAsync();
            Assert.Equal((200, "2 aborted=False"), (second.Status, second.Text));
            Assert.True(canceled[1].Task.IsCompleted, "the pipelined request's Aborted wasn't canceled");
        }
        Assert.True(await c.EndsWithFinAsync(), "no FIN after the answers");
        Assert.Empty(t.Errors);
    }

    // After a FIN the heartbeat keeps reading the socket's error, so a reset that follows still drops the answer.
    [Fact(Timeout = 20_000)]
    public async Task AResetAfterAFinStillAbandonsTheRequest()
    {
        TaskCompletionSource canceled = Signal(), release = Signal();
        bool? aborted = null;
        await using var t = TestServer.Start(async x =>
        {
            x.Aborted.Register(() => canceled.TrySetResult());
            await release.Task;
            // the heartbeat finds the reset within a few of its rounds; 5 s of them on a loaded machine
            long until = Environment.TickCount64 + 5000;
            while (!x.IsAborted && Environment.TickCount64 < until) await Task.Delay(10);
            aborted = x.IsAborted;
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync(Get);
        await Task.Delay(100);
        c.Socket.Shutdown(SocketShutdown.Send);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        c.Reset();
        release.SetResult();
        await t.ConnectionsReach(0);
        Assert.True(aborted);
    }

    // A gone the heartbeat found is canceled after its compare-exchange. If the handler ends in between, the connection
    // closes and the pool hands the object to the next socket: that request mustn't get the late cancel.
    [Fact(Timeout = 20_000)]
    public async Task ALateCancelOfAnAbandonedRequestDoesNotReachTheNextConnection()
    {
        DebugOnly();
        TaskCompletionSource firstStarted = Signal(), releaseFirst = Signal(), secondStarted = Signal();
        using var held = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        int calls = 0, hooked = 0;
        await using var t = TestServer.Start(async x =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task; // doesn't listen to Aborted: it ends on its own
                return;
            }
            secondStarted.TrySetResult();
            try { await Task.Delay(500, x.Aborted); }
            catch (OperationCanceledException) { }
            TestServer.Write(x, $"second canceled={x.Aborted.IsCancellationRequested} aborted={x.IsAborted}");
        }, o => o.MaxPooledConnections = 1); // so the next connection gets the same object
        t.Server.GoneHook = x =>
        {
            if (Interlocked.Exchange(ref hooked, 1) == 1) return;
            held.Set();
            go.Wait(TimeSpan.FromSeconds(10));
        };
        using (Client a = await t.ConnectAsync())
        {
            await a.SendAsync(Get);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await a.SendAsync(Get); // pipelined, so the heartbeat is the one to find the reset
            await Task.Delay(100);
            a.Reset();
        }
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "the reset wasn't found");
        releaseFirst.SetResult();
        await t.ConnectionsReach(0);
        using Client b = await t.ConnectAsync();
        await b.SendAsync(Get);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        go.Set();
        Response r = await b.ReadAsync();
        Assert.Equal((200, "second canceled=False aborted=False"), (r.Status, r.Text));
    }

    [Fact(Timeout = 20_000)]
    public async Task AClosingResponseWithAPipelinedRequestUnreadEndsWithAFinNotAReset()
    {
        TaskCompletionSource started = Signal(), release = Signal();
        int calls = 0;
        await using var t = TestServer.Start(async x =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            TestServer.Write(x, "first");
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /1 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // arrives while the first handler waits, so it is still in the socket when the server closes
        await c.SendAsync("GET /2 HTTP/1.1\r\nHost: x\r\n\r\n");
        await Task.Delay(100);
        release.SetResult();
        Response r = await c.ReadAsync();
        Assert.Equal((200, "first", "close"), (r.Status, r.Text, r.Header("Connection")));
        Assert.True(await c.EndsWithFinAsync(), "a reset, not a FIN, after the last response");
        Assert.Equal(1, calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AStopWithAPipelinedRequestUnreadEndsWithAFinNotAReset()
    {
        TaskCompletionSource started = Signal(), release = Signal();
        await using var t = TestServer.Start(async x =>
        {
            started.TrySetResult();
            await release.Task;
            TestServer.Write(x, "first");
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /1 HTTP/1.1\r\nHost: x\r\n\r\n");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await c.SendAsync("GET /2 HTTP/1.1\r\nHost: x\r\n\r\n");
        await Task.Delay(100);
        Task stop = t.Server.StopAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        release.SetResult();
        Response r = await c.ReadAsync();
        Assert.Equal((200, "first", "close"), (r.Status, r.Text, r.Header("Connection")));
        Assert.True(await c.EndsWithFinAsync(), "a reset, not a FIN, after the last response");
        c.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // The watch says "readable" once: after a pipelined request it has nothing more to tell, and after a reset a peek
    // still finds the waiting bytes. Before the fix nothing noticed the reset.
    [Theory(Timeout = 20_000)]
    [InlineData(false)] // the next request came with the first: both were read, so the socket is quiet
    [InlineData(true)]  // it came while the handler waited
    public async Task AResetAfterAPipelinedRequestStillAbortsTheHandler(bool later)
    {
        TaskCompletionSource started = Signal(), aborted = Signal();
        int calls = 0;
        await using var t = TestServer.Start(async x =>
        {
            Interlocked.Increment(ref calls);
            x.Aborted.Register(() => aborted.TrySetResult());
            started.TrySetResult();
            try { await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            TestServer.Write(x, "late");
        });
        using Client c = await t.ConnectAsync();
        const string First = "GET /1 HTTP/1.1\r\nHost: x\r\n\r\n", Next = "GET /2 HTTP/1.1\r\nHost: x\r\n\r\n";
        await c.SendAsync(later ? First : First + Next);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (later) await c.SendAsync(Next);
        await Task.Delay(100);
        Assert.False(aborted.Task.IsCompleted, "the handler was aborted before the reset");
        c.Reset();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await t.ConnectionsReach(0);
        Assert.Equal(1, calls); // the pipelined request went with the connection
    }

    [Fact(Timeout = 20_000)]
    public async Task AResetIsNoticedWhenTheSocketWasReadableBeforeTheHandlerStarted()
    {
        TaskCompletionSource firstStarted = Signal(), release = Signal(), secondStarted = Signal(), aborted = Signal();
        int calls = 0;
        await using var t = TestServer.Start(async x =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await release.Task;
                TestServer.Write(x, "first");
                return;
            }
            x.Aborted.Register(() => aborted.TrySetResult());
            secondStarted.TrySetResult();
            try { await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync(Get + Get); // both read at once: the second is parsed from the buffer
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await c.SendAsync(Get); // the third waits in the socket, so the second handler starts on a readable one
        await Task.Delay(100);
        release.SetResult();
        Response first = await c.ReadAsync();
        Assert.Equal((200, "first"), (first.Status, first.Text));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(aborted.Task.IsCompleted, "the handler was aborted before the reset");
        c.Reset();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await t.ConnectionsReach(0);
        Assert.Equal(2, calls);
    }

    // A stop's last step closes what is left with a reset, whatever the connection is doing, so the client learns the
    // connection broke rather than that it ended. A plain close already reset whenever a receive or send was pending
    // (.NET closes abortively when it cancels one): a handler awaiting has the watch pending. Not one that blocks its
    // thread, and before the fix its client got a FIN. (A client whose window is shut when the reset comes drops it,
    // its sequence lying past the window, and hears only the answer to its own next segment.)
    [Fact(Timeout = 30_000)]
    public async Task AHardAbortResetsTheConnection()
    {
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var t = TestServer.Start(x =>
        {
            blocked.Set();
            release.Wait(TimeSpan.FromSeconds(10)); // blocks the connection's thread: nothing is pending
            TestServer.Write(x, "late");
            return default;
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)));
        Task stop = t.Server.StopAsync(TimeSpan.FromMilliseconds(300));
        try
        {
            var b = new byte[1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var e = await Assert.ThrowsAsync<SocketException>(async () => await c.Socket.ReceiveAsync(b, SocketFlags.None, cts.Token));
            Assert.Equal(SocketError.ConnectionReset, e.SocketErrorCode);
        }
        finally
        {
            release.Set();
        }
        await stop;
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 20_000)]
    public async Task AGoneFoundAfterItsHandlerEndedDoesNotAbortTheNextRequest()
    {
        DebugOnly();
        TaskCompletionSource waiting = Signal(), release = Signal();
        using var secondRuns = new ManualResetEventSlim();
        int forced = 0, thirdSent = 0;
        await using var t = TestServer.Start(x => x.Path.SequenceEqual("/1"u8) ? First(x) : x.Path.SequenceEqual("/2"u8) ? Second(x) : Third(x));
        async ValueTask First(HttpExchange x)
        {
            await release.Task;
            TestServer.Write(x, "first");
        }
        async ValueTask Second(HttpExchange x)
        {
            secondRuns.Set();
            await Task.Delay(300); // the late report lands now, while this handler runs
            TestServer.Write(x, $"second aborted={x.IsAborted} canceled={x.Aborted.IsCancellationRequested}");
        }
        static ValueTask Third(HttpExchange x)
        {
            TestServer.Write(x, "third");
            return default;
        }
        // the watch's peek "finds" the first request's client gone (as a reset would), but only reports it once the
        // second request's handler runs: the handler count tells the two apart. Only the completion /3 makes: an
        // event loop's edge that came before the watch armed completes it on the connection's own thread, which the
        // hook would hold before the handler's wait began (the warm-up below spends the connection's first edge)
        t.Server.WatchHook = (x, gone) =>
        {
            if (Volatile.Read(ref thirdSent) == 0 || Interlocked.Exchange(ref forced, 1) == 1) return gone;
            secondRuns.Wait(TimeSpan.FromSeconds(5));
            return true;
        };
        // the watch is pending once the loop waits for the handler: a request sent before then would complete it on
        // the loop's own thread, which the hook would hold
        t.Server.PhaseHook = (x, p) =>
        {
            if (p == ExchangePhase.HandlerWaiting) waiting.TrySetResult();
        };
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /warm HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal("third", (await c.ReadAsync()).Text);
        // the second is read with the first, so its handler starts while the watch's completion is held
        await c.SendAsync("GET /1 HTTP/1.1\r\nHost: x\r\n\r\nGET /2 HTTP/1.1\r\nHost: x\r\n\r\n");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref thirdSent, 1);
        await c.SendAsync("GET /3 HTTP/1.1\r\nHost: x\r\n\r\n"); // completes the watch while the first handler waits
        await Task.Delay(100);
        release.SetResult();
        Response a = await c.ReadAsync(), b = await c.ReadAsync(), d = await c.ReadAsync();
        Assert.Equal((200, "first"), (a.Status, a.Text));
        Assert.Equal((200, "second aborted=False canceled=False"), (b.Status, b.Text));
        Assert.Equal((200, "third"), (d.Status, d.Text));
        Assert.True(secondRuns.IsSet);
        Assert.Equal(1, forced);
    }

    [Fact(Timeout = 20_000)]
    public async Task AWatchThatFailsToStartAbortsTheRequestAndStillAwaitsItsHandler()
    {
        DebugOnly();
        TaskCompletionSource started = Signal(), release = Signal(), aborted = Signal(), finished = Signal();
        int armed = 0, thrown = 0;
        await using var t = TestServer.Start(async x =>
        {
            x.Aborted.Register(() => aborted.TrySetResult());
            Volatile.Write(ref armed, 1);
            started.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        });
        t.Server.PhaseHook = (x, p) =>
        {
            if (p == ExchangePhase.Watching && Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref thrown, 1) == 0)
                throw new InvalidOperationException("the watch receive failed to start");
        };
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\n\r\n");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // the request counts as gone: its handler hears it, and is still awaited before the connection lets go
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(1, t.Server.Connections);
        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await c.ClosedAsync(), "the aborted request was answered");
        await t.ConnectionsReach(0);
        Assert.Equal(1, thrown);
    }
}

/// <summary>A callback that blocks a thread-pool thread for seconds when its client resets: alone, so it can't starve
/// the timing tests of the other classes (SlowClientTests, CloseTests) of threads.</summary>
[Collection(Quiet.Name)]
public sealed class BlockingCloseTests
{
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    const string Get = "GET /x HTTP/1.1\r\nHost: x\r\n\r\n";

    // The heartbeat finds a reset behind a pipelined request, and cancels from the thread pool: a registration that
    // blocks (or the handler's code after its await) would otherwise stall every connection's timeouts.
    [Fact(Timeout = 30_000)]
    public async Task ACallbackThatBlocksWhenItsClientResetsDoesNotStallTheHeartbeat()
    {
        TaskCompletionSource started = Signal(), blocking = Signal(), slept = Signal();
        await using var t = TestServer.Start(async x =>
        {
            x.Aborted.Register(() =>
            {
                blocking.TrySetResult();
                Thread.Sleep(3000);
                slept.TrySetResult();
            });
            started.TrySetResult();
            try { await Task.Delay(10_000, x.Aborted); }
            catch (OperationCanceledException) { }
        }, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        using Client a = await t.ConnectAsync();
        await a.SendAsync(Get);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await a.SendAsync(Get); // pipelined: the watch has nothing more to tell, so the heartbeat finds the reset
        await Task.Delay(100);
        a.Reset();
        await blocking.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // the heartbeat closes a new connection that sends nothing after the request timeout
        using Client b = await t.ConnectAsync();
        long start = Environment.TickCount64;
        Assert.True(await b.ClosedAsync(2000), "a silent connection still open after 2 s");
        Assert.InRange(Environment.TickCount64 - start, 0, 1500);
        // the blocked thread and the handler are done before the next test of the collection, which may count allocations
        await slept.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await t.ConnectionsReach(0);
    }
}
