using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// The event-loop transport (epoll on Linux, kqueue on macOS): one receive per request, no buffer while idle, a
/// readiness edge that is never lost (a read that fills the buffer, bytes that came before the wait, a socket the
/// heartbeat or a stop closed), and connection objects reused under events still in flight. The rest of the suite
/// runs over it too with CODEFUSION_HTTP_TRANSPORT=EventLoop.
/// </summary>
public sealed class EventLoopTests
{
    static TestServer Start(HttpHandler handler, Action<HttpServerOptions>? configure = null) => TestServer.Start(handler, o =>
    {
        TestTransport.Use(o, HttpTransport.EventLoop);
        o.EventLoops = 1;
        configure?.Invoke(o);
    });

    static string Get(string path) => $"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n";

    static ValueTask Path(HttpExchange x)
    {
        TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        return default;
    }

    static async Task Until(Func<bool> done, int timeoutMs, Func<string> what)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > until) Assert.Fail(what());
            await Task.Delay(5);
        }
    }

    // The point of the transport: Sockets waits for each request with a zero-byte receive, which on Linux is a
    // receive call of its own before the one that reads the request. The loop is told by epoll or kqueue instead.
    [Theory(Timeout = 20_000)]
    [InlineData(HttpTransport.EventLoop, 1, 1)]
    [InlineData(HttpTransport.Sockets, 2, 2)]
    public async Task EachKeepAliveRequestCostsThisManyReceives(HttpTransport transport, int least, int most)
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count the receives");
        await using var t = TestServer.Start(Path, o => TestTransport.Use(o, transport));
        using Client c = await t.ConnectAsync();
        await c.SendAsync(Get("/warm"));
        await c.ReadAsync();
        const int N = 200;
        int before = Volatile.Read(ref t.Server.SocketReceives);
        for (int i = 0; i < N; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            Response r = await c.ReadAsync();
            Assert.Equal($"/{i}", r.Text);
        }
        int receives = Volatile.Read(ref t.Server.SocketReceives) - before;
        // the wait after the warm-up and the one after the last request may each start before or after a count is
        // read: on Sockets each is a receive call, on the loop none
        Assert.InRange(receives, least * N, most * N + 2);
    }

    // The loop reads and sends with recv and send themselves, not through Socket's wrappers; Sockets keeps those.
    [Theory(Timeout = 20_000)]
    [InlineData(HttpTransport.EventLoop, 2)]
    [InlineData(HttpTransport.Sockets, 0)]
    public async Task EachKeepAliveRequestMakesThisManyDirectCalls(HttpTransport transport, int perRequest)
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count the calls");
        await using var t = TestServer.Start(Path, o => TestTransport.Use(o, transport));
        using Client c = await t.ConnectAsync();
        await c.SendAsync(Get("/warm"));
        await c.ReadAsync();
        const int N = 200;
        int before = Volatile.Read(ref t.Server.DirectCalls);
        for (int i = 0; i < N; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            Assert.Equal($"/{i}", (await c.ReadAsync()).Text);
        }
        Assert.InRange(Volatile.Read(ref t.Server.DirectCalls) - before, perRequest * N, perRequest * N + 1);
    }

    [Fact(Timeout = 20_000)]
    public async Task IdleConnectionsHoldNoBuffers()
    {
        await using var t = Start(Path);
        var clients = new List<Client>();
        for (int i = 0; i < 20; i++) clients.Add(await t.ConnectAsync());
        foreach (Client c in clients) await c.SendAsync(Get("/a"));
        foreach (Client c in clients) Assert.Equal("/a", (await c.ReadAsync()).Text);
        await Until(() => t.Server.RequestBuffers.Out == 0 && t.Server.ResponseBuffers.Out == 0, 5000,
            () => $"{t.Server.RequestBuffers.Out} request and {t.Server.ResponseBuffers.Out} response buffers held by idle connections");
        // and they still answer
        foreach (Client c in clients) await c.SendAsync(Get("/b"));
        foreach (Client c in clients) Assert.Equal("/b", (await c.ReadAsync()).Text);
        foreach (Client c in clients) c.Dispose();
    }

    // Edge-triggered: a read that fills the buffer may leave bytes the kernel won't announce again. The whole request
    // is in the socket before the first read here, so a missed edge hangs the connection.
    [Theory(Timeout = 20_000)]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(64 << 10)]
    public async Task ARequestLargerThanTheBufferIsReadWhole(int bodyBytes)
    {
        await using var t = Start(static x =>
        {
            ReadOnlySpan<byte> b = x.Body.Span;
            int sum = 0;
            foreach (byte v in b) sum += v;
            TestServer.Write(x, $"{b.Length} {sum}");
            return default;
        }, o => o.RequestBufferSize = 256);
        using Client c = await t.ConnectAsync();
        byte[] body = new byte[bodyBytes];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(i * 7);
        byte[] head = Encoding.ASCII.GetBytes($"POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: {bodyBytes}\r\n\r\n");
        await c.SendAsync([.. head, .. body]);
        Response r = await c.ReadAsync();
        Assert.Equal($"{bodyBytes} {body.Sum(v => (int)v)}", r.Text);
    }

    [Fact(Timeout = 20_000)]
    public async Task PipelinedRequestsBeyondTheBufferAreAllAnswered()
    {
        await using var t = Start(Path, o => o.RequestBufferSize = 256);
        using Client c = await t.ConnectAsync();
        const int N = 100;
        await c.SendAsync(string.Concat(Enumerable.Range(0, N).Select(i => Get($"/{i}"))));
        for (int i = 0; i < N; i++) Assert.Equal($"/{i}", (await c.ReadAsync()).Text);
    }

    // The client's last bytes and its FIN or reset can come in one edge: the read that takes the bytes falls short of
    // the buffer without seeing the end, and no later edge says it. The loop is held by another connection's handler
    // while both arrive, so they do come together.
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BytesAndTheCloseInOneEdgeStillCloseTheConnection(bool reset)
    {
        using var hold = new ManualResetEventSlim();
        string? blockedOn = null;
        await using var t = Start(x =>
        {
            if (x.Path.SequenceEqual("/block"u8))
            {
                Volatile.Write(ref blockedOn, Thread.CurrentThread.Name ?? "a pool thread");
                // only the loop's thread is held: the run may have read /block on the pool thread that finished
                // /warm, if that thread hadn't armed its wait yet when the request came
                if (Thread.CurrentThread.Name?.StartsWith("http event loop", StringComparison.Ordinal) == true) hold.Wait(TimeSpan.FromSeconds(10));
            }
            return Path(x);
        });
        using Client blocker = await t.ConnectAsync(), leaver = await t.ConnectAsync();
        // a first request each, so the next ones wait on the loop
        foreach (Client c in new[] { blocker, leaver })
        {
            await c.SendAsync(Get("/warm"));
            await c.ReadAsync();
        }
        for (int attempt = 0; ; attempt++)
        {
            Volatile.Write(ref blockedOn, null);
            await blocker.SendAsync(Get("/block"));
            await Until(() => Volatile.Read(ref blockedOn) is not null, 5000, () => "the blocking request never started");
            if (Volatile.Read(ref blockedOn)!.StartsWith("http event loop", StringComparison.Ordinal)) break;
            Assert.Equal("/block", (await blocker.ReadAsync()).Text);
            Assert.True(attempt < 20, "/block never ran on the loop's thread");
        }
        await leaver.SendAsync("GET /half");
        if (reset) leaver.Reset();
        else leaver.Socket.Shutdown(SocketShutdown.Send);
        await Task.Delay(100);
        hold.Set();
        Assert.Equal("/block", (await blocker.ReadAsync()).Text);
        await t.ConnectionsReach(1);
        if (!reset) Assert.True(await leaver.ClosedAsync(2000));
    }

    // Closing a socket removes it from epoll and kqueue without an event: the heartbeat and the stop wake its
    // connection themselves.
    [Fact(Timeout = 20_000)]
    public async Task AnIdleConnectionPastItsKeepAliveTimeoutIsClosed()
    {
        await using var t = Start(Path, o => o.KeepAliveTimeout = TimeSpan.FromMilliseconds(200));
        using Client c = await t.ConnectAsync();
        await c.SendAsync(Get("/a"));
        Assert.Equal("/a", (await c.ReadAsync()).Text);
        Assert.True(await c.ClosedAsync(3000), "the idle connection stayed open");
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 20_000)]
    public async Task AHeadThatStallsIsClosedAfterTheRequestTimeout()
    {
        await using var t = Start(Path, o => o.RequestTimeout = TimeSpan.FromMilliseconds(200));
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /a HTTP/1.1\r\nHo");
        Assert.True(await c.ClosedAsync(3000), "the stalled request stayed open");
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 20_000)]
    public async Task AStopClosesIdleConnectionsAtOnce()
    {
        await using var t = Start(Path);
        var clients = new List<Client>();
        for (int i = 0; i < 4; i++)
        {
            Client c = await t.ConnectAsync();
            await c.SendAsync(Get("/a"));
            await c.ReadAsync();
            clients.Add(c);
        }
        long at = Environment.TickCount64;
        await t.Server.StopAsync(TimeSpan.FromSeconds(10));
        Assert.True(Environment.TickCount64 - at < 2000, $"the stop took {Environment.TickCount64 - at} ms");
        foreach (Client c in clients)
        {
            Assert.True(await c.ClosedAsync(2000));
            c.Dispose();
        }
    }

    // An async handler leaves the wait for its client armed; the close that follows its answer must take it back,
    // since the loop sees nothing of a socket it closed itself.
    [Fact(Timeout = 20_000)]
    public async Task AnAsyncHandlersLastAnswerEndsTheConnection()
    {
        await using var t = Start(static async x =>
        {
            await Task.Yield();
            TestServer.Write(x, "bye");
        });
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /a HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        Response r = await c.ReadAsync();
        Assert.Equal(("bye", "close"), (r.Text, r.Header("Connection")));
        Assert.True(await c.ClosedAsync(2000));
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 20_000)]
    public async Task AClientThatResetsDuringAnAsyncHandlerAbortsIt()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var t = Start(async x =>
        {
            x.Aborted.Register(() => aborted.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, x.Aborted).ContinueWith(static _ => { }, TaskScheduler.Default);
        });
        Client c = await t.ConnectAsync();
        await c.SendAsync(Get("/a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        c.Reset();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task ManyConnectionsOverSeveralLoopsAreEachAnsweredInOrder()
    {
        await using var t = Start(static async x =>
        {
            // every third request finishes on another thread, so loops and thread-pool threads both send
            if (x.Path[^1] % 3 == 0) await Task.Yield();
            TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        }, o => o.EventLoops = 3);
        await Task.WhenAll(Enumerable.Range(0, 48).Select(async n =>
        {
            using Client c = await t.ConnectAsync();
            for (int i = 0; i < 200; i += 2)
            {
                // two at a time, pipelined
                await c.SendAsync(Get($"/{n}/{i}") + Get($"/{n}/{i + 1}"));
                Assert.Equal($"/{n}/{i}", (await c.ReadAsync()).Text);
                Assert.Equal($"/{n}/{i + 1}", (await c.ReadAsync()).Text);
            }
        }));
    }

    // Pooled connection objects go to new sockets while events for their last one may still be on their way: a
    // late event must neither answer nor stall the new connection.
    [Fact(Timeout = 60_000)]
    public async Task ConnectionsThatComeAndGoAreEachAnsweredWhole()
    {
        await using var t = Start(Path, o => o.MaxPooledConnections = 4);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async n =>
        {
            for (int i = 0; i < 150; i++)
            {
                Client c = await t.ConnectAsync();
                switch (i % 3)
                {
                    case 0:
                        await c.SendAsync(Get($"/{n}/{i}/a") + Get($"/{n}/{i}/b"));
                        Assert.Equal($"/{n}/{i}/a", (await c.ReadAsync()).Text);
                        Assert.Equal($"/{n}/{i}/b", (await c.ReadAsync()).Text);
                        c.Dispose();
                        break;
                    case 1:
                        await c.SendAsync(Get($"/{n}/{i}"));
                        Assert.Equal($"/{n}/{i}", (await c.ReadAsync()).Text);
                        c.Reset();
                        break;
                    default:
                        await c.SendAsync("GET /half"); // leaves mid-request
                        c.Reset();
                        break;
                }
            }
        }));
        await t.ConnectionsReach(0);
    }

    // Linux: a listening socket per loop (SO_REUSEPORT), so the kernel spreads connections over the loops and each
    // loop accepts its own on its thread: even a connection's first request is answered there. macOS: one listener.
    [Fact(Timeout = 20_000)]
    public async Task OnLinuxEachLoopListensAndAcceptsForItself()
    {
        await using var t = Start(static x =>
        {
            TestServer.Write(x, EventLoopThread.Current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return default;
        }, o => o.EventLoops = 3);
        Assert.Equal(OperatingSystem.IsLinux() ? 3 : 0, t.Server.LoopListeners);
        var answers = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            using Client c = await t.ConnectAsync();
            await c.SendAsync(Get("/first"));
            answers.Add((await c.ReadAsync()).Text);
        }
        if (OperatingSystem.IsLinux()) Assert.Equal(["0", "1", "2"], answers.Distinct().Order().ToArray());
    }

    [Theory(Timeout = 20_000)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AStoppedServerTakesNoMoreConnections(int loops)
    {
        var t = Start(Path, o => o.EventLoops = loops);
        using (Client c = await t.ConnectAsync())
        {
            await c.SendAsync(Get("/a"));
            Assert.Equal("/a", (await c.ReadAsync()).Text);
        }
        await t.DisposeAsync();
        await Assert.ThrowsAsync<SocketException>(async () =>
        {
            for (int i = 0; i < 10; i++) (await t.ConnectAsync()).Dispose();
        });
    }

    [Fact]
    public void TheLoopCountIsChecked()
    {
        Assert.Equal(HttpTransport.Sockets, new HttpServerOptions().Transport);
        Assert.Equal(Environment.ProcessorCount, new HttpServerOptions().EventLoops);
        Assert.Throws<ArgumentException>(() => new HttpServer(new HttpServerOptions { EventLoops = 0 }, Path));
        Assert.Throws<ArgumentException>(() => new HttpServer(new HttpServerOptions { Transport = (HttpTransport)7 }, Path));
    }

    [Fact(Timeout = 20_000)]
    public async Task TheLoopsEndWithTheServer()
    {
        var t = Start(Path, o => o.EventLoops = 2);
        Assert.Equal(2, t.Server.EventLoopsRunning);
        using (Client c = await t.ConnectAsync())
        {
            await c.SendAsync(Get("/a"));
            await c.ReadAsync();
        }
        await t.DisposeAsync();
        Assert.Equal(0, t.Server.EventLoopsRunning);
    }
}
