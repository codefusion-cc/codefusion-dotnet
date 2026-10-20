using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// What the Sockets transport guarantees and the event loop must keep although its connections share a thread: a
/// stale edge can't make a reused connection spin, a stop ends within its hard abort, one busy client can't hold its
/// loop from the others, a handler's cancel callbacks never run on the loop, and a loop that fails takes no new
/// connections.
/// </summary>
public sealed class EventLoopGuaranteeTests
{
    static string Get(string path) => $"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n";

    static ValueTask Path(HttpExchange x)
    {
        TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        return default;
    }

    static TestServer Start(HttpHandler handler, Action<HttpServerOptions>? configure = null) => TestServer.Start(handler, o =>
    {
        TestTransport.Use(o, HttpTransport.EventLoop);
        o.EventLoops = 1;
        configure?.Invoke(o);
    });

    static async Task<string> Ask(Client c, string path, int timeoutMs = 1000)
    {
        await c.SendAsync(Get(path));
        return (await c.ReadAsync(timeoutMs: timeoutMs)).Text;
    }

    // A made-up "closed" edge (a timeout's Kick, or a stale event) that reaches the pooled object after it went to
    // the next connection: it costs that connection one empty read, not a wait that completes forever.
    [Fact(Timeout = 20_000)]
    public async Task ALateClosedEdgeOnAReusedConnectionCostsOneReadNotASpin()
    {
        HttpExchange? seen = null;
        await using var t = Start(Path, o => o.MaxPooledConnections = 1);
        t.Server.HandlerDone = x => Volatile.Write(ref seen, x);
        using (Client a = await t.ConnectAsync()) Assert.Equal("/a", await Ask(a, "/a"));
        await t.ConnectionsReach(0);
        HttpExchange first = Volatile.Read(ref seen)!;
        using Client b = await t.ConnectAsync();
        Assert.Equal("/b", await Ask(b, "/b"));
        Assert.Same(first, Volatile.Read(ref seen));
        await Task.Delay(50); // b waits for its next request
        int before = Volatile.Read(ref t.Server.SocketReceives);
        Task late = Task.Run(() => first.Readable(closed: true));
        Assert.True(await Task.WhenAny(late, Task.Delay(1000)) == late, "the late edge never returned");
        await Task.Delay(200);
        if (AllocationTests.Debugger()) Assert.InRange(Volatile.Read(ref t.Server.SocketReceives) - before, 0, 4);
        Assert.Equal("/b2", await Ask(b, "/b2"));
        using Client c = await t.ConnectAsync();
        Assert.Equal("/c", await Ask(c, "/c"));
    }

    // Sockets gives up on a handler that never returns about a second after the hard abort; the loop's thread may be
    // the one that handler holds, and the stop must not wait for it.
    [Fact(Timeout = 20_000)]
    public async Task AStopEndsWithinItsHardAbortWhileAHandlerHoldsTheLoop()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = Start(x =>
        {
            if (x.Path.SequenceEqual("/hold"u8))
            {
                started.TrySetResult();
                release.Wait();
            }
            return Path(x);
        });
        try
        {
            using Client c = await t.ConnectAsync();
            Assert.Equal("/warm", await Ask(c, "/warm"));
            await c.SendAsync(Get("/hold"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clock = Stopwatch.StartNew();
            Task stop = t.Server.StopAsync(TimeSpan.FromMilliseconds(200));
            Assert.True(await Task.WhenAny(stop, Task.Delay(5000)) == stop, "the stop waited for the blocked loop");
            Assert.InRange(clock.ElapsedMilliseconds, 0, 3000);
        }
        finally
        {
            release.Set();
        }
        await t.DisposeAsync();
    }

    // A client that pipelines without pause and reads its answers keeps every read full: the loop must still come
    // back to its other connections, as Sockets' thread pool does.
    [Fact(Timeout = 30_000)]
    public async Task AClientThatPipelinesWithoutPauseDoesNotHoldItsLoop()
    {
        await using var t = Start(Path, o => o.RequestBufferSize = 256);
        using Client busy = await t.ConnectAsync(), other = await t.ConnectAsync();
        Assert.Equal("/warm", await Ask(other, "/warm"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] burst = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(Get("/a"), 64)));
        Task writer = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested) await busy.Socket.SendAsync(burst, System.Net.Sockets.SocketFlags.None, stop.Token);
            }
            catch (OperationCanceledException) { }
        });
        Task reader = Task.Run(async () =>
        {
            var sink = new byte[1 << 16];
            try
            {
                while (!stop.IsCancellationRequested) await busy.Socket.ReceiveAsync(sink, System.Net.Sockets.SocketFlags.None, stop.Token);
            }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(300); // the flood is on
        var clock = Stopwatch.StartNew();
        Assert.Equal("/b", await Ask(other, "/b", timeoutMs: 2000));
        Assert.InRange(clock.ElapsedMilliseconds, 0, 500);
        await stop.CancelAsync();
        await Task.WhenAll(writer, reader);
        busy.Reset();
    }

    // When the loop finds the client gone, Aborted's callbacks (and what the handler runs after the cancel) go to the
    // thread pool, as on Sockets: one that blocks holds a pool thread, not every connection on the loop.
    [Fact(Timeout = 20_000)]
    public async Task ACancelCallbackThatBlocksDoesNotHoldTheLoop()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var t = Start(async x =>
        {
            if (x.Path.SequenceEqual("/slow"u8))
            {
                x.Aborted.Register(() =>
                {
                    canceled.TrySetResult();
                    Thread.Sleep(3000);
                });
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, x.Aborted).ContinueWith(static _ => { }, TaskScheduler.Default);
                return;
            }
            TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        });
        using Client other = await t.ConnectAsync();
        Assert.Equal("/warm", await Ask(other, "/warm"));
        Client leaver = await t.ConnectAsync();
        Assert.Equal("/warm", await Ask(leaver, "/warm"));
        await leaver.SendAsync(Get("/slow"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        leaver.Reset();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        Assert.Equal("/b", await Ask(other, "/b", timeoutMs: 2500));
        Assert.InRange(clock.ElapsedMilliseconds, 0, 1000);
    }

    // A send the socket can't take at once waits on the loop too: on the runtime's SendAsync it would register the
    // socket with the runtime's own epoll for good, which then wakes for every later request on it.
    [Theory(Timeout = 30_000)]
    [InlineData(HttpTransport.EventLoop, 0)]
    [InlineData(HttpTransport.Sockets, 1)]
    public async Task ASendThatWaitsWaitsOnTheLoop(HttpTransport transport, int runtimeWaitsAtLeast)
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count the send waits");
        const int Big = 8 << 20;
        await using var t = TestServer.Start(static x =>
        {
            x.GetSpan(Big)[..Big].Fill((byte)'b');
            x.Advance(Big);
            return default;
        }, o => TestTransport.Use(o, transport));
        using Client c = await t.ConnectAsync();
        for (int round = 0; round < 2; round++)
        {
            await c.SendAsync(Get("/big"));
            await Task.Delay(200); // the socket fills, and the send waits
            Response r = await c.ReadAsync(timeoutMs: 10_000);
            Assert.Equal(Big, r.Body.Length);
        }
        int runtime = Volatile.Read(ref t.Server.RuntimeSendWaits), loop = Volatile.Read(ref t.Server.LoopSendWaits);
        if (transport == HttpTransport.EventLoop) Assert.True(runtime == 0 && loop > 0, $"{runtime} runtime and {loop} loop send waits");
        else Assert.True(runtime >= runtimeWaitsAtLeast && loop == 0, $"{runtime} runtime and {loop} loop send waits");
    }

    // A loop whose wait fails (only a broken descriptor does that) is reported once and gets no more connections:
    // they go to the loops still running.
    [Fact(Timeout = 20_000)]
    public async Task ALoopThatFailsTakesNoNewConnections()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make a loop's wait fail");
        var reports = new List<string>();
        await using var t = Start(Path, o =>
        {
            o.EventLoops = 2;
            o.OnError = (what, _) => { lock (reports) reports.Add(what); };
        });
        int failed = 0;
        t.Server.LoopWaitHook = loop => loop == 0 && Interlocked.Exchange(ref failed, 1) == 0;
        // wake loop 0 so its next wait fails: a connection on it
        for (int i = 0; i < 4 && Volatile.Read(ref failed) == 0; i++)
        {
            using Client c = await t.ConnectAsync();
            await Ask(c, "/wake");
        }
        await TestServer.Until(() => t.Server.EventLoopsRunning == 1, 5000, () => $"{t.Server.EventLoopsRunning} loops running");
        for (int i = 0; i < 10; i++)
        {
            using Client c = await t.ConnectAsync();
            Assert.Equal($"/{i}", await Ask(c, $"/{i}"));
        }
        lock (reports) Assert.Equal(["http event loop failed"], reports.Distinct().ToArray());
    }
}
