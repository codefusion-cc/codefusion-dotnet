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
            await Task.Yield(); // the rest, the answer and the buffers' return run on the pool
            TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        });
        using Client c = await t.ConnectAsync();
        const int N = 50;
        for (int i = 0; i < N; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            Assert.Equal($"/{i}", (await c.ReadAsync()).Text);
        }
        // the response buffer is rented and given back on the pool thread that finishes the request
        Assert.InRange(t.Server.ResponseBuffers.LoopReturns, 0, 1);
        Assert.InRange(t.Server.ResponseBuffers.SharedReturns, N - 1, int.MaxValue);
    }

    [Fact(Timeout = 30_000)]
    public async Task EachLoopsPoolKeepsAtMostItsShareOfTheCapacity()
    {
        await using var t = Start(Path, o => o.MaxPooledBuffers = 8); // 2 loops: each keeps at most 8 / (2 x 2) = 2 of each size
        // 12 connections whose buffers are all out at once, then all given back
        var clients = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => t.ConnectAsync()));
        foreach (Client c in clients) await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\n\r\nab");
        await Task.Delay(200); // each read its partial body: a request buffer each, on its loop
        foreach (Client c in clients) await c.SendAsync("cd");
        foreach (Client c in clients) Assert.Equal(200, (await c.ReadAsync()).Status);
        await TestServer.Until(() => t.Server.RequestBuffers.Out == 0, 5000, () => $"{t.Server.RequestBuffers.Out} request buffers out");
        for (int loop = 0; loop < 2; loop++) Assert.InRange(t.Server.RequestBuffers.LoopAvailable(loop), 0, 2);
        foreach (Client c in clients) c.Dispose();
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
