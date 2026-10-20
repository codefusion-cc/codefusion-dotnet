using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>The server over real loopback sockets, against the behavior Kestrel gave the Conduit app.</summary>
public class ServerTests
{
    const string Get = "GET /api/tags HTTP/1.1\r\nHost: x\r\n\r\n";

    [Fact(Timeout = 10_000)]
    public async Task AnswersWithStatusHeadersAndBody()
    {
        await using var t = TestServer.Start(x =>
        {
            x.Status = 201;
            TestServer.Write(x, "{\"a\":1}");
            return default;
        });
        using var c = await t.ConnectAsync();
        await c.SendAsync(Get);
        var r = await c.ReadAsync();
        Assert.Equal("HTTP/1.1 201 Created", r.StatusLine);
        Assert.Equal("application/json", r.Header("Content-Type"));
        Assert.Equal("7", r.Header("Content-Length"));
        Assert.Equal("{\"a\":1}", r.Text);
        var date = DateTime.ParseExact(r.Header("Date")!, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        Assert.InRange(date, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        Assert.Null(r.Header("Server"));
        Assert.Null(r.Header("Transfer-Encoding"));
        Assert.Null(r.Header("Connection"));
        Assert.Equal(3, r.Headers.Count); // Content-Type, Content-Length, Date
    }

    [Fact(Timeout = 10_000)]
    public async Task TheHandlerSeesMethodPathQueryAuthorizationAndBody()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        await c.SendAsync("PUT /api/profiles/J%C3%BCrgen/./follow?a=%20b&c HTTP/1.1\r\nHost: x\r\nAuthorization: Token abc\r\nContent-Length: 4\r\n\r\nbody");
        Assert.Equal("PUT /api/profiles/Jürgen/follow q=a=%20b&c auth=[Token abc] body=body", (await c.ReadAsync()).Text);
        await c.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\nAuthorization:\r\n\r\n");
        Assert.Equal("GET /x q= auth=[] body=", (await c.ReadAsync()).Text);
        await c.SendAsync("DELETE /x HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal("DELETE /x q= auth=none body=", (await c.ReadAsync()).Text);
    }

    [Fact(Timeout = 10_000)]
    public async Task KeepAliveServesManyRequestsOnOneConnection()
    {
        int calls = 0;
        await using var t = TestServer.Start(x =>
        {
            TestServer.Write(x, Interlocked.Increment(ref calls).ToString());
            return default;
        });
        using var c = await t.ConnectAsync();
        for (int i = 1; i <= 200; i++)
        {
            await c.SendAsync(Get);
            Assert.Equal(i.ToString(), (await c.ReadAsync()).Text);
        }
        Assert.Equal(1, t.Server.Connections);
    }

    [Theory(Timeout = 10_000)]
    [InlineData("GET / HTTP/1.0\r\n\r\n", "close")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n", "close")]
    [InlineData("GET / HTTP/1.0\r\nConnection: close, keep-alive\r\n\r\n", "close")]
    public async Task ClosesAfterTheResponseWhenAsked(string request, string connection)
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        await c.SendAsync(request + Get); // what follows a closing request is never answered
        var r = await c.ReadAsync();
        Assert.Equal(200, r.Status);
        Assert.Equal(connection, r.Header("Connection"));
        Assert.True(await c.ClosedAsync());
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 10_000)]
    public async Task Http10KeepAliveStaysOpenAndSaysSo()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        for (int i = 0; i < 3; i++)
        {
            await c.SendAsync("GET /a HTTP/1.0\r\nConnection: keep-alive\r\n\r\n");
            var r = await c.ReadAsync();
            Assert.Equal("keep-alive", r.Header("Connection"));
            Assert.StartsWith("GET /a", r.Text);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task PipelinedRequestsAreAnsweredInOrderWhateverOrderHandlersFinish()
    {
        // Earlier requests take longer, so their handlers would finish last if they ran at once.
        await using var t = TestServer.Start(async x =>
        {
            int i = int.Parse(Encoding.ASCII.GetString(x.Path[1..]));
            if (i % 3 != 0) await Task.Delay(40 - i);
            TestServer.Write(x, i.ToString());
        });
        using var c = await t.ConnectAsync();
        var sb = new StringBuilder();
        for (int i = 0; i < 30; i++) sb.Append($"GET /{i} HTTP/1.1\r\nHost: x\r\n\r\n");
        await c.SendAsync(sb.ToString());
        for (int i = 0; i < 30; i++) Assert.Equal(i.ToString(), (await c.ReadAsync()).Text);
    }

    [Fact(Timeout = 10_000)]
    public async Task PipelinedBodiesAndAPartialNextRequest()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        await c.SendAsync("POST /a HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\n\r\nabc"
                          + "POST /b HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nde\r\n0\r\n\r\n"
                          + "POST /c HTTP/1.1\r\nHost: x\r\nContent-Le");
        Assert.EndsWith("body=abc", (await c.ReadAsync()).Text);
        Assert.EndsWith("body=de", (await c.ReadAsync()).Text);
        await Task.Delay(20);
        await c.SendAsync("ngth: 1\r\n\r\nf");
        Assert.Equal("POST /c q= auth=none body=f", (await c.ReadAsync()).Text);
    }

    const string Chunked = "POST /p/%C3%A9?q=1 HTTP/1.1\r\nHost: x\r\nAuthorization: Token t\r\nTransfer-Encoding: chunked\r\n\r\n"
                           + "3;ext=1\r\nabc\r\n2\r\nde\r\n0\r\nTrailer: v\r\n\r\n";
    const string ChunkedAnswer = "POST /p/é q=q=1 auth=[Token t] body=abcde";

    [Fact(Timeout = 20_000)]
    public async Task OneByteAtATime()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        foreach (byte b in Encoding.ASCII.GetBytes(Chunked))
        {
            await c.SendAsync([b]);
            await Task.Delay(1);
        }
        Assert.Equal(ChunkedAnswer, (await c.ReadAsync()).Text);
    }

    [Fact(Timeout = 30_000)]
    public async Task SplitAtEveryPosition()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        byte[] all = Encoding.ASCII.GetBytes(Chunked);
        for (int cut = 1; cut < all.Length; cut++)
        {
            await c.SendAsync(all[..cut]);
            await Task.Delay(2); // lets the first piece arrive on its own
            await c.SendAsync(all[cut..]);
            Assert.Equal(ChunkedAnswer, (await c.ReadAsync()).Text);
        }
        Assert.Equal(1, t.Server.Connections);
    }

    [Fact(Timeout = 10_000)]
    public async Task ExpectContinueGetsA100BeforeTheBody()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        await c.SendAsync("POST /e HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\nContent-Length: 5\r\n\r\n");
        var interim = await c.ReadAsync();
        Assert.Equal("HTTP/1.1 100 Continue", interim.StatusLine);
        Assert.Empty(interim.Headers);
        await c.SendAsync("hello");
        Assert.Equal("POST /e q= auth=none body=hello", (await c.ReadAsync()).Text);
    }

    [Fact(Timeout = 10_000)]
    public async Task ExpectContinueWithTheBodyAlreadyHereGetsNo100()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        await c.SendAsync("POST /e HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\nContent-Length: 5\r\n\r\nhello" + Get);
        Assert.Equal(200, (await c.ReadAsync()).Status);
        Assert.Equal(200, (await c.ReadAsync()).Status);
    }

    [Fact(Timeout = 10_000)]
    public async Task ExpectContinueOverTheLimitGets413AndNo100()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        using var c = await t.ConnectAsync();
        await c.SendAsync($"POST /e HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\nContent-Length: {(1 << 20) + 1}\r\n\r\n");
        var r = await c.ReadAsync();
        Assert.Equal(413, r.Status);
        Assert.Equal("close", r.Header("Connection"));
        Assert.Equal("0", r.Header("Content-Length"));
        Assert.True(await c.ClosedAsync());
    }

    [Fact(Timeout = 20_000)]
    public async Task A413ReachesAClientStillSendingItsBody()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        for (int i = 0; i < 5; i++)
        {
            using var c = await t.ConnectAsync();
            // No Expect: the body follows the head at once, and the server refuses it unread.
            byte[] body = new byte[2 << 20];
            await c.SendAsync($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {body.Length}\r\n\r\n");
            Task send = c.SendAsync(body);
            var r = await c.ReadAsync();
            Assert.Equal(413, r.Status);
            Assert.Equal("close", r.Header("Connection"));
            await send;
            Assert.True(await c.ClosedAsync());
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task A413ToAChunkedBodyGoesOnReadingWhatTheClientSendsAndEndsWithAFin()
    {
        await using var t = TestServer.Start(TestServer.Echo);
        // 64 KiB chunks: the 17th chunk's size line passes the 1 MiB limit after the server has decoded the framing
        // of the ones before out of its buffer.
        byte[] chunk = [.. "10000\r\n"u8, .. new byte[1 << 16], .. "\r\n"u8];
        using var c = await t.ConnectAsync();
        await c.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
        Task send = Task.Run(async () =>
        {
            for (int k = 0; k < 17; k++) await c.SendAsync(chunk);
        }, TestContext.Current.CancellationToken);
        var r = await c.ReadAsync();
        Assert.Equal(413, r.Status);
        Assert.Equal("close", r.Header("Connection"));
        await send;
        // A client may not read the error until it has sent its whole body: the server drains, it doesn't reset. A
        // server that has closed answers the second round with a reset, and the third send fails.
        for (int round = 0; round < 4; round++)
        {
            await c.SendAsync(chunk);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        c.Socket.Shutdown(SocketShutdown.Send);
        Assert.True(await c.EndsWithFinAsync());
    }

    [Fact(Timeout = 20_000)]
    public async Task AClientThatStopsReadingAResponseTimesOut()
    {
        await using var t = TestServer.Start(x =>
        {
            Span<byte> s = x.GetSpan(16 << 20)[..(16 << 20)];
            s.Fill((byte)'x');
            x.Advance(s.Length);
            return default;
        }, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        using var c = await t.ConnectAsync();
        c.Socket.ReceiveBufferSize = 4096;
        await c.SendAsync(Get);
        // The server's send blocks on the full socket buffers, and the client acknowledges nothing for the timeout.
        await t.ConnectionsReach(0, 5000);
    }

    [Fact(Timeout = 20_000)]
    public async Task BodiesAtTheLimitPassAndOnePastAre413()
    {
        await using var t = TestServer.Start(x =>
        {
            TestServer.Write(x, x.Body.Length.ToString());
            return default;
        });
        foreach (bool chunked in new[] { false, true })
        {
            using var c = await t.ConnectAsync();
            byte[] body = new byte[1 << 20];
            Random.Shared.NextBytes(body);
            await c.SendAsync(chunked
                ? "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n100000\r\n"
                : "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 1048576\r\n\r\n");
            await c.SendAsync(body);
            if (chunked) await c.SendAsync("\r\n0\r\n\r\n");
            Assert.Equal("1048576", (await c.ReadAsync()).Text);

            await c.SendAsync(chunked
                ? "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n100000\r\n"
                : "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 1048577\r\n\r\n");
            if (chunked)
            {
                await c.SendAsync(body);
                await c.SendAsync("\r\n1\r\n");
            }
            var r = await c.ReadAsync();
            Assert.Equal(413, r.Status);
        }
    }

    static Action<HttpServerOptions> Small => o =>
    {
        o.MaxRequestLineSize = 64;
        o.MaxRequestHeadersTotalSize = 128;
        o.MaxRequestHeaderCount = 4;
    };

    [Theory(Timeout = 10_000)]
    [InlineData(64, 128, 4, 200)]
    [InlineData(65, 128, 4, 414)]
    [InlineData(64, 129, 4, 431)]
    [InlineData(64, 128, 5, 431)]
    public async Task LimitsOverTheWire(int lineLength, int headerBytes, int headerCount, int status)
    {
        await using var t = TestServer.Start(TestServer.Echo, Small);
        string line = "GET /" + new string('a', lineLength - 16) + " HTTP/1.1\r\n";
        var headers = new StringBuilder("Host: x\r\n");
        for (int i = 2; i < headerCount; i++) headers.Append($"{(char)('A' + i)}: {i}\r\n");
        int fill = headerBytes - headers.Length - "Z: \r\n\r\n".Length;
        headers.Append("Z: ").Append('z', fill).Append("\r\n\r\n");
        using var c = await t.ConnectAsync();
        await c.SendAsync(line + headers);
        var r = await c.ReadAsync();
        Assert.Equal(status, r.Status);
        if (status != 200) Assert.True(await c.ClosedAsync());
    }

    [Theory(Timeout = 10_000)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 4\r\n\r\n0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\nContent-Length: 44\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\nz\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nX: a\r\n b\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\n\n")]
    [InlineData("\0\0\0\r\n\r\n")]
    public async Task SmugglingShapesAre400AndCloseWithoutReachingTheHandler(string request)
    {
        int calls = 0;
        await using var t = TestServer.Start(x =>
        {
            Interlocked.Increment(ref calls);
            return default;
        });
        using var c = await t.ConnectAsync();
        await c.SendAsync(request + Get);
        var r = await c.ReadAsync();
        Assert.Equal(400, r.Status);
        Assert.Equal("close", r.Header("Connection"));
        Assert.True(await c.ClosedAsync());
        Assert.Equal(0, calls);
    }

    [Fact(Timeout = 30_000)]
    public async Task RandomBytesNeverHurtTheServer()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        var rng = new Random(1234);
        for (int i = 0; i < 100; i++)
        {
            using var c = await t.ConnectAsync();
            byte[] junk = new byte[rng.Next(1, 600)];
            rng.NextBytes(junk);
            if (i % 2 == 0) junk = [.. Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"), .. junk];
            await c.SendAsync(junk);
            // An answer and a close, or a close at the request timeout: never a hang or a crash.
            try
            {
                var r = await c.ReadAsync(timeoutMs: 3000);
                Assert.Contains(r.Status, new[] { 400, 413, 414, 431, 505 });
            }
            catch (Exception e) when (e is EndOfStreamException or SocketException)
            {
                // closed, or reset because the junk wasn't all read
            }
        }
        await t.ConnectionsReach(0);
        using var ok = await t.ConnectAsync();
        await ok.SendAsync(Get);
        Assert.Equal(200, (await ok.ReadAsync()).Status);
    }

    [Fact(Timeout = 10_000)]
    public async Task AClientThatLeavesMidRequestIsCleanedUp()
    {
        int calls = 0;
        await using var t = TestServer.Start(x =>
        {
            Interlocked.Increment(ref calls);
            return default;
        });
        for (int i = 0; i < 20; i++)
        {
            using var c = await t.ConnectAsync();
            await c.SendAsync(i % 2 == 0 ? "GET / HTTP/1.1\r\nHo" : "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\nabc");
            await Task.Delay(5);
            if (i % 4 < 2) c.Reset();
        }
        await t.ConnectionsReach(0);
        Assert.Equal(0, calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AClientThatLeavesMidResponseIsCleanedUp()
    {
        await using var t = TestServer.Start(x =>
        {
            Span<byte> s = x.GetSpan(8 << 20)[..(8 << 20)];
            s.Fill((byte)'x');
            x.Advance(s.Length);
            return default;
        });
        for (int i = 0; i < 4; i++)
        {
            var c = await t.ConnectAsync();
            await c.SendAsync(Get);
            var buf = new byte[1000];
            await c.Socket.ReceiveAsync(buf, SocketFlags.None);
            if (i % 2 == 0) c.Reset();
            else c.Dispose();
        }
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 20_000)]
    public async Task ALargeResponseToASlowReaderArrivesWhole()
    {
        byte[] big = new byte[3 << 20];
        Random.Shared.NextBytes(big);
        await using var t = TestServer.Start(x =>
        {
            Encoding.UTF8.GetBytes(Convert.ToBase64String(big), x);
            return default;
        });
        using var c = await t.ConnectAsync();
        await c.SendAsync(Get + Get);
        await Task.Delay(300); // the server's socket buffer fills and its send goes asynchronous
        Assert.Equal(big, Convert.FromBase64String((await c.ReadAsync(timeoutMs: 10_000)).Text));
        Assert.Equal(big, Convert.FromBase64String((await c.ReadAsync(timeoutMs: 10_000)).Text));
    }

    [Fact(Timeout = 10_000)]
    public async Task AThrowingHandlerGets500AndTheConnectionGoesOn()
    {
        await using var t = TestServer.Start(async x =>
        {
            TestServer.Write(x, "partial");
            if (x.Path.SequenceEqual("/sync"u8)) throw new InvalidOperationException("boom");
            await Task.Yield();
            if (x.Path.SequenceEqual("/async"u8)) throw new InvalidOperationException("boom");
            x.ResetResponse();
            TestServer.Write(x, "fine");
        });
        using var c = await t.ConnectAsync();
        foreach (string path in new[] { "/sync", "/ok", "/async", "/ok" })
        {
            await c.SendAsync($"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n");
            var r = await c.ReadAsync();
            if (path == "/ok") Assert.Equal("fine", r.Text);
            else
            {
                Assert.Equal(500, r.Status);
                Assert.Equal("0", r.Header("Content-Length"));
                Assert.Null(r.Header("Content-Type"));
                Assert.Null(r.Header("Connection"));
            }
        }
        Assert.Equal(2, t.Errors.Count);
    }

    [Fact(Timeout = 10_000)]
    public async Task AbortedFiresWhenTheClientResetsOrCloses()
    {
        foreach (bool reset in new[] { false, true })
        {
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool sawFlag = false;
            await using var t = TestServer.Start(async x =>
            {
                x.Aborted.Register(() => aborted.TrySetResult());
                await release.Task;
                sawFlag = x.IsAborted;
                TestServer.Write(x, "late");
            });
            var c = await t.ConnectAsync();
            await c.SendAsync(Get);
            await Task.Delay(50);
            Assert.False(aborted.Task.IsCompleted);
            // a reset drops the request; a FIN only cancels it, and the answer is still sent if the handler finishes
            if (reset) c.Reset();
            else c.Dispose();
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, t.Server.Connections); // held by the handler until it returns
            release.SetResult();
            await t.ConnectionsReach(0);
            Assert.Equal(reset, sawFlag);
            Assert.Empty(t.Errors);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task TheAbortTokenIsFreshForTheNextRequest()
    {
        var tokens = new ConcurrentQueue<bool>();
        await using var t = TestServer.Start(async x =>
        {
            await Task.Delay(5, x.Aborted);
            tokens.Enqueue(x.Aborted.IsCancellationRequested);
            TestServer.Write(x, "ok");
        });
        using var c = await t.ConnectAsync();
        for (int i = 0; i < 20; i++)
        {
            await c.SendAsync(Get);
            Assert.Equal("ok", (await c.ReadAsync()).Text);
        }
        Assert.All(tokens, Assert.False);
    }

    [Fact(Timeout = 60_000)]
    public async Task ThousandsOfConcurrentConnections()
    {
        const int N = 2000;
        await using var t = TestServer.Start(async x =>
        {
            await Task.Yield();
            TestServer.Write(x, Encoding.ASCII.GetString(x.Path));
        });
        var clients = new Client[N];
        // macOS caps the accept backlog at 128, so connect in waves rather than all at once.
        for (int i = 0; i < N; i += 100)
            await Task.WhenAll(Enumerable.Range(i, 100).Select(async j => clients[j] = await t.ConnectAsync()));
        await t.ConnectionsReach(N, 20_000);
        for (int round = 0; round < 2; round++)
            await Task.WhenAll(clients.Select(async (c, i) =>
            {
                await c.SendAsync($"GET /{i}/{round} HTTP/1.1\r\nHost: x\r\n\r\n");
                Assert.Equal($"/{i}/{round}", (await c.ReadAsync(timeoutMs: 30_000)).Text);
            }));
        foreach (var c in clients) c.Dispose();
        await t.ConnectionsReach(0, 20_000);
    }

    [Fact(Timeout = 10_000)]
    public async Task IdleConnectionsCloseAfterTheKeepAliveTimeout()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.KeepAliveTimeout = TimeSpan.FromMilliseconds(400));
        using var c = await t.ConnectAsync();
        await c.SendAsync(Get);
        await c.ReadAsync();
        long start = Environment.TickCount64;
        Assert.False(await c.ClosedAsync(250));
        Assert.True(await c.ClosedAsync(3000));
        Assert.InRange(Environment.TickCount64 - start, 380, 2000);
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 10_000)]
    public async Task ASlowlyDrippedHeadTimesOutEvenWhileBytesArrive()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.RequestTimeout = TimeSpan.FromMilliseconds(400));
        using var c = await t.ConnectAsync();
        long start = Environment.TickCount64;
        bool closed = false;
        foreach (char ch in "GET / HTTP/1.1\r\nHost: x\r\nX-Slow: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        {
            try { await c.SendAsync(ch.ToString()); }
            catch (SocketException) { closed = true; break; }
            if (await c.ClosedAsync(50)) { closed = true; break; }
        }
        Assert.True(closed || await c.ClosedAsync(2000));
        Assert.InRange(Environment.TickCount64 - start, 380, 2500);
    }

    [Fact(Timeout = 10_000)]
    public async Task AStalledBodyTimesOut()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        using var c = await t.ConnectAsync();
        await c.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\nabc");
        Assert.True(await c.ClosedAsync(3000));
        await t.ConnectionsReach(0);
    }

    [Fact(Timeout = 10_000)]
    public async Task HeadAnd204AndRetryAfterFrameCorrectly()
    {
        await using var t = TestServer.Start(x =>
        {
            TestServer.Write(x, "twelve bytes");
            if (x.Path.SequenceEqual("/204"u8)) x.Status = 204;
            if (x.Path.SequenceEqual("/503"u8))
            {
                x.Status = 503;
                x.RetryAfter = 1;
            }
            return default;
        });
        using var c = await t.ConnectAsync();
        await c.SendAsync("HEAD /h HTTP/1.1\r\nHost: x\r\n\r\nGET /204 HTTP/1.1\r\nHost: x\r\n\r\nGET /503 HTTP/1.1\r\nHost: x\r\n\r\n" + Get);
        var head = await c.ReadAsync(head: true);
        Assert.Equal("12", head.Header("Content-Length"));
        var noContent = await c.ReadAsync();
        Assert.Equal(204, noContent.Status);
        Assert.Null(noContent.Header("Content-Length"));
        Assert.Null(noContent.Header("Content-Type"));
        var busy = await c.ReadAsync();
        Assert.Equal(503, busy.Status);
        Assert.Equal("1", busy.Header("Retry-After"));
        Assert.Equal("twelve bytes", (await c.ReadAsync()).Text); // the framing held through all three
        Assert.Equal(0, c.Unread);
    }

    [Fact(Timeout = 10_000)]
    public async Task IdleConnectionsHoldNoBuffers()
    {
        await using var t = TestServer.Start(async x =>
        {
            await Task.Yield();
            TestServer.Write(x, "ok");
        });
        var clients = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => t.ConnectAsync()));
        await Task.WhenAll(clients.Select(async c =>
        {
            await c.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\n\r\nhi");
            Assert.Equal("ok", (await c.ReadAsync()).Text);
        }));
        await Task.Delay(50);
        // Every buffer the 50 requests used is back in its pool while their connections stay open.
        Assert.Equal(50, t.Server.Connections);
        Assert.InRange(t.Server.RequestBuffers.Created, 1, 50);
        Assert.Equal(t.Server.RequestBuffers.Created, t.Server.RequestBuffers.Available);
        Assert.Equal(t.Server.ResponseBuffers.Created, t.Server.ResponseBuffers.Available);
        foreach (var c in clients) c.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task StateLivesAsLongAsTheConnection()
    {
        await using var t = TestServer.Start(x =>
        {
            x.State ??= new int[1];
            TestServer.Write(x, (++((int[])x.State)[0]).ToString());
            return default;
        });
        using var c = await t.ConnectAsync();
        for (int i = 1; i <= 3; i++)
        {
            await c.SendAsync(Get);
            Assert.Equal(i.ToString(), (await c.ReadAsync()).Text);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ConcurrentStopsAllWaitForTheSameStop()
    {
        var t = TestServer.Start(async x =>
        {
            await Task.Delay(200);
            TestServer.Write(x, "done");
        });
        using var c = await t.ConnectAsync();
        await c.SendAsync(Get);
        await Task.Delay(50);
        Task first = t.Server.StopAsync(TimeSpan.FromSeconds(5));
        Task second = t.Server.DisposeAsync().AsTask();
        await Task.Delay(20);
        Assert.False(second.IsCompleted); // still waiting for the request in flight
        await Task.WhenAll(first, second);
        Assert.Equal("done", (await c.ReadAsync()).Text);
        Assert.Equal(0, t.Server.Connections);
    }

    [Fact(Timeout = 10_000)]
    public async Task StopClosesIdleConnectionsAndFinishesRequestsInFlight()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = TestServer.Start(async x =>
        {
            entered.TrySetResult();
            await Task.Delay(300);
            TestServer.Write(x, "done");
        });
        using var idle = await t.ConnectAsync();
        using var busy = await t.ConnectAsync();
        await busy.SendAsync(Get);
        await entered.Task;
        Task stop = t.Server.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(await idle.ClosedAsync(1000));
        var r = await busy.ReadAsync();
        Assert.Equal("done", r.Text);
        Assert.Equal("close", r.Header("Connection"));
        Assert.True(await busy.ClosedAsync());
        await stop;
        Assert.Equal(0, t.Server.Connections);
        await Assert.ThrowsAnyAsync<SocketException>(() => t.ConnectAsync());
    }
}
