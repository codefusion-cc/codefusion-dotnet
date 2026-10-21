using System;
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
/// On an event loop a connection's buffers come from and go back to its loop's own pool, which only that loop's
/// thread touches, so no lock: the request, response and arena buffers alike. A buffer given back on any other thread
/// goes to the shared pool. Each loop's pool is capped, so what they keep doesn't grow with the number of loops.
/// </summary>
public sealed class LoopPoolTests
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
        o.EventLoops = 2;
        configure?.Invoke(o);
    });

    [Fact(Timeout = 20_000)]
    public async Task OnALoopEachRequestRentsAndReturnsThroughItsLoopsOwnPool()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count where the buffers come from");
        await using var t = Start(Path);
        var clients = new Client[4];
        for (int i = 0; i < clients.Length; i++) clients[i] = await t.ConnectAsync();
        const int N = 100;
        for (int r = 0; r < N; r++)
            foreach (Client c in clients)
            {
                await c.SendAsync(Get("/a"));
                Assert.Equal("/a", (await c.ReadAsync()).Text);
            }
        foreach (Client c in clients) c.Dispose();
        // every request rents a request and a response buffer on its loop and gives both back there
        int requests = N * clients.Length;
        Assert.InRange(t.Server.RequestBuffers.LoopRents, requests - clients.Length, int.MaxValue);
        Assert.InRange(t.Server.ResponseBuffers.LoopRents, requests - clients.Length, int.MaxValue);
        Assert.InRange(t.Server.RequestBuffers.LoopReturns, requests - clients.Length, int.MaxValue);
    }

    [Fact(Timeout = 20_000)]
    public async Task ABufferGivenBackOffItsLoopGoesToTheSharedPool()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count where the buffers go");
        await using var t = Start(static async x =>
        {
            // a timer completes the wait, so the loop has gone on before the rest, the answer and the buffers' return run
            // on the pool (a wait that completes at once can be found done on the loop, and finished there)
            await Task.Delay(1);
            TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        });
        using Client c = await t.ConnectAsync();
        const int N = 50;
        for (int i = 0; i < N; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            Assert.Equal($"/{i}", (await c.ReadAsync()).Text);
        }
        // the response buffer is rented and given back on the pool thread that finishes the request: a few found done on
        // the loop at most, and every other one to the shards
        int onLoop = t.Server.ResponseBuffers.LoopReturns;
        Assert.InRange(onLoop, 0, N / 5);
        Assert.InRange(t.Server.ResponseBuffers.SharedReturns, N - onLoop - 1, int.MaxValue);
    }

    [Fact(Timeout = 30_000)]
    public async Task EachLoopsPoolKeepsExactlyItsShareOfTheCapacity()
    {
        // 2 loops, one listener (connections go to the loops in turn): each loop keeps 8 / (2 x 2) = 2 of each size
        await using var t = Start(Path, o =>
        {
            o.MaxPooledBuffers = 8;
            o.ListenerPerLoop = false;
        });
        // 12 connections whose request buffers are all out at once, 6 on each loop, then all given back on their loops
        var clients = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => t.ConnectAsync()));
        foreach (Client c in clients) await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\n\r\nab");
        await TestServer.Until(() => t.Server.RequestBuffers.Out == 12, 5000, () => $"{t.Server.RequestBuffers.Out} request buffers out");
        foreach (Client c in clients) await c.SendAsync("cd");
        foreach (Client c in clients) Assert.Equal(200, (await c.ReadAsync()).Status);
        await TestServer.Until(() => t.Server.RequestBuffers.Out == 0, 5000, () => $"{t.Server.RequestBuffers.Out} request buffers out");
        for (int loop = 0; loop < 2; loop++) Assert.Equal(2, t.Server.RequestBuffers.LoopAvailable(loop));
        foreach (Client c in clients) c.Dispose();
    }

    // MaxPooledBuffers is the whole bound: the loops' stacks are taken out of the shared shards, not kept on top.
    [Fact(Timeout = 30_000)]
    public async Task ThePoolsKeepNoMoreThanMaxPooledBuffersInAll()
    {
        await using var t = Start(Path, o =>
        {
            o.MaxPooledBuffers = 64;
            o.ListenerPerLoop = false;
        });
        var clients = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => t.ConnectAsync()));
        try
        {
            foreach (Client c in clients) await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\n\r\nab");
            await TestServer.Until(() => t.Server.RequestBuffers.Out == 100, 10_000, () => $"{t.Server.RequestBuffers.Out} request buffers out");
            foreach (Client c in clients) await c.SendAsync("cd");
            foreach (Client c in clients) Assert.Equal(200, (await c.ReadAsync()).Status);
            await TestServer.Until(() => t.Server.RequestBuffers.Out == 0, 10_000, () => $"{t.Server.RequestBuffers.Out} request buffers out");
            // the shards round their share up per shard: at most one more per shard
            Assert.InRange(t.Server.RequestBuffers.Available, 0, 64 + Environment.ProcessorCount);
            // and each loop holds its share, 64 / (2 x 2)
            for (int loop = 0; loop < 2; loop++) Assert.Equal(16, t.Server.RequestBuffers.LoopAvailable(loop));
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
        }
    }

    // A loop whose wait failed gives its stacks back to the shards as it ends: nothing could pop them any more.
    [Fact(Timeout = 20_000)]
    public async Task AFailedLoopGivesItsBuffersBackToTheShards()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make a loop's wait fail");
        await using var t = Start(Path, o =>
        {
            o.ListenerPerLoop = false;
            o.OnError = null;
        });
        using (Client c = await t.ConnectAsync())
            for (int i = 0; i < 3; i++)
            {
                await c.SendAsync(Get("/a"));
                Assert.Equal("/a", (await c.ReadAsync()).Text);
            }
        // the server gives its buffers back as the connection goes idle, just after the answer it read
        await TestServer.Until(() => t.Server.RequestBuffers.LoopAvailable(0) + t.Server.RequestBuffers.LoopAvailable(1) > 0, 5000,
            () => "neither loop kept a buffer");
        int target = t.Server.RequestBuffers.LoopAvailable(0) > 0 ? 0 : 1;
        int failed = 0;
        t.Server.LoopWaitHook = loop => loop == target && Interlocked.Exchange(ref failed, 1) == 0;
        for (int i = 0; i < 4 && t.Server.EventLoopsRunning == 2; i++)
        {
            using Client c = await t.ConnectAsync(); // wakes the loop it goes to; every other one is the target's
            try
            {
                await c.SendAsync(Get("/wake"));
                await c.ReadAsync();
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or System.IO.EndOfStreamException)
            {
                // closed with its failed loop
            }
        }
        await TestServer.Until(() => t.Server.EventLoopsRunning == 1, 5000, () => $"{t.Server.EventLoopsRunning} loops running");
        Assert.Equal(0, t.Server.RequestBuffers.LoopAvailable(target));
        Assert.Equal(0, t.Server.ResponseBuffers.LoopAvailable(target));
    }


    // The app's arena chunks (a token issued, a body's strings) come from the loop's own pool too.
    [Fact(Timeout = 20_000)]
    public async Task AnAppsArenaChunksComeFromTheLoopsOwnPool()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count where the buffers come from");
        await using HttpApp app = Sample.SampleApp.Build(b =>
        {
            TestTransport.Use(b.Options, HttpTransport.EventLoop);
            b.Options.EventLoops = 1;
        });
        await using var check = new AppCheck(app);
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        string body = """{"article":{"title":"t","description":"d","tagList":["a"],"meta":{"pinned":true,"score":1.5,"rank":1},"count":1}}""";
        string request = $"POST /api/articles HTTP/1.1\r\nHost: x\r\nAuthorization: Token {SampleAppTests.Token(7)}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        for (int i = 0; i < 20; i++)
        {
            await c.SendAsync(request);
            Assert.Equal(201, (await c.ReadAsync()).Status);
        }
        Assert.InRange(app.ArenaChunks.LoopRents, 18, int.MaxValue);
    }
}
