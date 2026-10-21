using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
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
/// Clients too slow to be worth their connection: a body or a response that crawls, a refused request fed a byte at
/// a time, a connection that never sends its first request, and more connections than allowed. A byte now and then
/// must not restart the clock. The server's open connections say when it let one go.
/// </summary>
public sealed class SlowClientTests
{
    /// <summary>Sends <paramref name="bytes"/> bytes every <paramref name="everyMs"/> ms, <paramref name="times"/>
    /// times, until the server closes the connection.</summary>
    static async Task Drip(Socket s, int bytes, int everyMs, int times)
    {
        var b = new byte[bytes];
        b.AsSpan().Fill((byte)'a');
        try
        {
            for (int i = 0; i < times; i++)
            {
                for (int sent = 0; sent < b.Length;) sent += await s.SendAsync(b.AsMemory(sent), SocketFlags.None);
                await Task.Delay(everyMs);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // the server closed it
        }
    }

    /// <summary>From <paramref name="since"/> until the server lets the connection go (once it has taken it).</summary>
    static async Task<long> MillisecondsUntilClosed(TestServer t, int timeoutMs, long since)
    {
        await t.ConnectionsReach(1);
        await t.ConnectionsReach(0, timeoutMs);
        return Environment.TickCount64 - since;
    }

    [Fact(Timeout = 20_000)]
    public async Task ABodyDrippedBelowTheMinimumRateIsCutOff()
    {
        await using var t = TestServer.Start(TestServer.Echo,
            o => o.MinRequestBodyDataRate = new MinDataRate(1000, TimeSpan.FromMilliseconds(500)));
        using Client c = await t.ConnectAsync();
        long start = Environment.TickCount64;
        // a 1 MB body announced, 1 KB of it sent, then 20 bytes every 100 ms: 200 B/s
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 1048576\r\n\r\n" + new string('a', 1000));
        Task drip = Drip(c.Socket, 20, 100, 80);
        // the first 1000 bytes buy a second at 1000 B/s, and each 200 B/s second a fifth more
        long took = await MillisecondsUntilClosed(t, 5000, start);
        Assert.InRange(took, 900, 3000);
        Assert.True(await c.ClosedAsync(), "the cut-off request got an answer");
        await drip;
    }

    [Fact(Timeout = 20_000)]
    public async Task ABodyAboveTheMinimumRateIsReadWhateverItsLength()
    {
        await using var t = TestServer.Start(x =>
        {
            TestServer.Write(x, x.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return default;
        }, o => o.MinRequestBodyDataRate = new MinDataRate(150, TimeSpan.FromMilliseconds(500)));
        using Client c = await t.ConnectAsync();
        // 150 bytes every 100 ms, ten times the rate (five on a busy machine), for 2 s: longer than the grace period
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 3000\r\n\r\n");
        await Drip(c.Socket, 150, 100, 20);
        Response r = await c.ReadAsync();
        Assert.Equal((200, "3000"), (r.Status, r.Text));
    }

    // Each byte pays for 1/rate seconds, but no more than the request timeout (or the grace period, if longer) ahead.
    // Before, the rate was the body's average: 99,999 bytes at once bought 100 s at 1000 B/s, then a byte every 900 ms
    // kept the request timeout away.
    [Fact(Timeout = 20_000)]
    public async Task BytesSentEarlyDoNotBuyABodyALongCrawl()
    {
        await using var t = TestServer.Start(TestServer.Echo, o =>
        {
            o.MinRequestBodyDataRate = new MinDataRate(1000, TimeSpan.FromMilliseconds(500));
            o.RequestTimeout = TimeSpan.FromSeconds(1);
        });
        using Client c = await t.ConnectAsync();
        long start = Environment.TickCount64;
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 200000\r\n\r\n" + new string('a', 99_999));
        Task drip = Drip(c.Socket, 1, 900, 20);
        long took = await MillisecondsUntilClosed(t, 5000, start);
        Assert.InRange(took, 900, 3000);
        Assert.True(await c.ClosedAsync(), "the cut-off request got an answer");
        await drip;
    }

    // Each receive's bytes add to what the body has paid for. Before, the sum was cut to whole milliseconds on every
    // receive, so bytes worth less than one each earned nothing: 1000 one-byte receives at 0.5 ms a byte paid for 0.
    [Fact]
    public void BytesWorthLessThanAMillisecondEachStillAddUp()
    {
        var paid = HttpExchange.Paid(0, 0, 0.5, long.MaxValue / 2);
        for (int i = 0; i < 1000; i++) paid = HttpExchange.Paid(paid, 1, 0.5, long.MaxValue / 2);
        Assert.Equal(500, paid);
        // and the cap still holds
        Assert.Equal(100, HttpExchange.Paid(paid, 1000, 0.5, 100));
    }

    // A rate so low a byte would pay for more milliseconds than a long holds made the deadline wrap negative, which
    // reads as no deadline: not even the request timeout ended the wait.
    [Theory(Timeout = 20_000)]
    [InlineData(1e-300)]
    [InlineData(double.Epsilon)]
    public async Task NoRateIsSoLowThatItTurnsTheBodyTimeoutOff(double rate)
    {
        await using var t = TestServer.Start(TestServer.Echo, o =>
        {
            o.MinRequestBodyDataRate = new MinDataRate(rate, TimeSpan.Zero);
            o.RequestTimeout = TimeSpan.FromMilliseconds(300);
        });
        using Client c = await t.ConnectAsync();
        long start = Environment.TickCount64;
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 1000\r\n\r\n" + new string('a', 100));
        long took = await MillisecondsUntilClosed(t, 3000, start);
        Assert.InRange(took, 250, 2000);
        Assert.True(await c.ClosedAsync(), "the stalled request got an answer");
    }

    // What the socket takes counts, and its buffers take the first part of a response however slowly the client
    // reads (about 660 KB on macOS loopback, then more in steps as they grow): at 1 MB/s that buys well under a second.
    [Theory(Timeout = 30_000)]
    [InlineData(true)]  // 1 KB every 50 ms: 20 KB/s
    [InlineData(false)] // nothing at all
    public async Task AResponseReadBelowTheMinimumRateIsCutOff(bool reads)
    {
        const int Size = 8 << 20;
        await using var t = TestServer.Start(Sized(Size), o => o.MinResponseDataRate = new MinDataRate(1_000_000, TimeSpan.FromMilliseconds(500)));
        using var s = new Socket(t.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 8192 };
        await s.ConnectAsync(t.EndPoint);
        await s.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), SocketFlags.None);
        long start = Environment.TickCount64;
        using var stop = new CancellationTokenSource();
        Task read = Task.Run(async () =>
        {
            var b = new byte[1024];
            try
            {
                while (reads && !stop.IsCancellationRequested && await s.ReceiveAsync(b, SocketFlags.None) > 0) await Task.Delay(50);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        });
        long took = await MillisecondsUntilClosed(t, 6000, start);
        Assert.InRange(took, 400, 4000);
        stop.Cancel();
        s.Dispose();
        await read;
    }

    /// <summary>Asks for a response and never reads it: milliseconds from the request until the server lets the
    /// connection go.</summary>
    static async Task<long> MillisecondsUntilANonReaderIsCutOff(TestServer t, int timeoutMs)
    {
        using var s = new Socket(t.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 8192 };
        await s.ConnectAsync(t.EndPoint);
        await s.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), SocketFlags.None);
        return await MillisecondsUntilClosed(t, timeoutMs, Environment.TickCount64);
    }

    // What the client's TCP acknowledges counts, and a client that reads nothing stops acknowledging once its own
    // buffer is full. Before, what the server's socket took counted, and that buffer grows on its own: at 240 B/s the
    // 660 KB it took bought 45 minutes.
    [Fact(Timeout = 20_000)]
    public async Task AClientThatReadsNothingIsCutOffOnceItAcknowledgesNothingForTheRequestTimeout()
    {
        await using var t = TestServer.Start(Sized(8 << 20), o =>
        {
            o.MinResponseDataRate = new MinDataRate(240, TimeSpan.FromMilliseconds(500));
            o.RequestTimeout = TimeSpan.FromSeconds(1);
        });
        // its buffer fills within a few hundred ms, then nothing comes for the second
        Assert.InRange(await MillisecondsUntilANonReaderIsCutOff(t, 6000), 900, 3000);
    }

    // The rate counts only what the client's TCP acknowledged, not what the server's socket buffered: on macOS the
    // socket takes about 660 KB at once and the client's buffer about 320 KB, which at 400 KB/s is the difference
    // between 1.8 s and the 1 s grace period.
    [Fact(Timeout = 20_000)]
    public async Task TheServersOwnSocketBufferEarnsAClientNoCredit()
    {
        await using var t = TestServer.Start(Sized(8 << 20),
            o => o.MinResponseDataRate = new MinDataRate(400_000, TimeSpan.FromSeconds(1)));
        Assert.InRange(await MillisecondsUntilANonReaderIsCutOff(t, 6000), 900, 1600);
    }

    [Fact(Timeout = 20_000)]
    public async Task ALingerFedAByteEveryOneAndAHalfSecondsEndsWithinItsCap()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.MaxRequestBodySize = 8);
        using Client c = await t.ConnectAsync();
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 100\r\n\r\n");
        Response r = await c.ReadAsync();
        Assert.Equal(413, r.Status);
        long start = Environment.TickCount64;
        // each byte would restart a two-second linger; the cap counts from the refusal
        Task drip = Drip(c.Socket, 1, 1500, 6);
        long took = await MillisecondsUntilClosed(t, 5000, start);
        Assert.InRange(took, 1500, 3200);
        await drip;
    }

    [Fact(Timeout = 20_000)]
    public async Task ANewConnectionThatSendsNothingIsClosedAfterTheRequestTimeout()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        using Client c = await t.ConnectAsync();
        long start = Environment.TickCount64;
        long took = await MillisecondsUntilClosed(t, 3000, start);
        Assert.InRange(took, 250, 2000);
        Assert.True(await c.ClosedAsync());
    }

    [Fact(Timeout = 20_000)]
    public async Task AfterItsFirstRequestAConnectionWaitsTheKeepAliveTimeout()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.RequestTimeout = TimeSpan.FromMilliseconds(300));
        using Client c = await t.ConnectAsync();
        await c.SendAsync("GET /a HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal(200, (await c.ReadAsync()).Status);
        await Task.Delay(800);
        await c.SendAsync("GET /b HTTP/1.1\r\nHost: x\r\n\r\n");
        Response r = await c.ReadAsync();
        Assert.Equal((200, "GET /b q= auth=none body="), (r.Status, r.Text));
    }

    [Fact(Timeout = 20_000)]
    public async Task PastMaxConnectionsANewConnectionIsClosedAtOnce()
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.MaxConnections = 2);
        using Client a = await t.ConnectAsync(), b = await t.ConnectAsync();
        foreach (Client open in new[] { a, b })
        {
            await open.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.Equal(200, (await open.ReadAsync()).Status);
        }
        using (Client over = await t.ConnectAsync())
        {
            await over.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.True(await over.ClosedAsync(2000), "the third connection was served");
        }
        Assert.Equal(2, t.Server.Connections);
        a.Dispose();
        await t.ConnectionsReach(1);
        using Client next = await t.ConnectAsync();
        await next.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal(200, (await next.ReadAsync()).Status);
    }

    // ---- A request's buffer follows what came, not what its head declared ----

    /// <summary>Answers with the body's length and its SHA-256.</summary>
    static ValueTask LengthAndHash(HttpExchange x)
    {
        TestServer.Write(x, x.Body.Length.ToString(CultureInfo.InvariantCulture) + " " + Convert.ToHexString(SHA256.HashData(x.Body.Span)));
        return default;
    }

    static async Task Until(Func<bool> done, int timeoutMs, Func<string> what)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException(what());
            await Task.Delay(5);
        }
    }

    // 50 connections each declare a body of `declared` bytes and send a few KB of it. Before the fix, the first
    // growth rented the whole declared body: 2 MB each for 1 MB (an LOH array once the shared pool runs dry).
    [Theory(Timeout = 30_000)]
    [InlineData(1 << 20, 1 << 20)]               // the default limit, declared whole
    [InlineData(int.MaxValue, 2_000_000_000)]    // a limit raised as far as it goes, and nearly 2 GB declared
    public async Task ConnectionsThatDeclareALargeBodyHoldBuffersSizedByWhatTheySent(int limit, int declared)
    {
        const int N = 50;
        await using var t = TestServer.Start(LengthAndHash, o => o.MaxRequestBodySize = limit);
        var clients = new List<Client>();
        try
        {
            string head = $"POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: {declared}\r\n\r\n";
            for (int i = 0; i < N; i++)
            {
                Client c = await t.ConnectAsync();
                clients.Add(c);
                await c.SendAsync(head + new string('a', 3700)); // fits the first 4 KB buffer
            }
            // With the head, about 8.8 KB and then 20 KB have come: the buffer doubles to 16 KB, then to 32 KB.
            foreach ((int more, long each) in new[] { (5000, 16384L), (11240, 32768L) })
            {
                foreach (Client c in clients) await c.SendAsync(new string('b', more));
                await Until(() => t.Server.RequestBufferBytes >= N * each, 5000, () => $"{t.Server.RequestBufferBytes} bytes held, expected {N * each}");
                Assert.Equal(N * each, t.Server.RequestBufferBytes);
            }
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
        }
        await t.ConnectionsReach(0);
        Assert.Equal(0, t.Server.RequestBufferBytes);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4000)]    // with the head, past the first buffer
    [InlineData(1 << 20)] // exactly the limit
    public async Task ABodyUpToTheLimitIsReadWholeInABufferNeverTwiceWhatCame(int length)
    {
        await using var t = TestServer.Start(LengthAndHash);
        using Client c = await t.ConnectAsync();
        byte[] head = Encoding.ASCII.GetBytes($"POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: {length}\r\n\r\n");
        byte[] body = new byte[length];
        Random.Shared.NextBytes(body);
        await c.SendAsync(head);
        long sent = head.Length;
        for (int at = 0; at < length;)
        {
            int n = Math.Min(16 << 10, length - at);
            await c.SendAsync(body[at..(at + n)]);
            at += n;
            sent += n;
            // what the server has read is at most what was sent, and it grows only when nearly full
            long held = t.Server.RequestBufferBytes;
            Assert.True(held <= Math.Max(4096, 2 * (sent + 512)), $"{held} bytes held after {sent} sent");
        }
        Response r = await c.ReadAsync();
        Assert.Equal((200, $"{length} {Convert.ToHexString(SHA256.HashData(body))}"), (r.Status, r.Text));
    }

    // ---- A response read faster than the limits allow is never cut off ----

    /// <summary>A response whose body is <paramref name="size"/> bytes of 'a'.</summary>
    static HttpHandler Sized(int size) => x =>
    {
        x.GetSpan(size)[..size].Fill((byte)'a');
        x.Advance(size);
        return default;
    };

    /// <summary>Asks for the response, then reads it: nothing for <paramref name="pauseMs"/>, then
    /// <paramref name="chunk"/> bytes every <paramref name="everyMs"/> ms for <paramref name="slowMs"/>, then as fast
    /// as it comes. Returns the bytes that came before the server closed or the response was whole.</summary>
    static async Task<long> ReadThrottled(TestServer t, int size, int pauseMs, int slowMs, int chunk, int everyMs)
    {
        using var s = new Socket(t.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 8192 };
        await s.ConnectAsync(t.EndPoint);
        await s.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), SocketFlags.None);
        string headEnd = $"Content-Length: {size}\r\n";
        var b = new byte[64 << 10];
        long got = 0, total = -1;
        var head = new StringBuilder();
        try
        {
            await Task.Delay(pauseMs);
            long slowUntil = Environment.TickCount64 + slowMs;
            while (total < 0 || got < total)
            {
                bool slow = Environment.TickCount64 < slowUntil;
                int n = await s.ReceiveAsync(b.AsMemory(0, slow ? chunk : b.Length), SocketFlags.None);
                if (n == 0) break;
                got += n;
                if (total < 0)
                {
                    head.Append(Encoding.ASCII.GetString(b, 0, n));
                    int end = head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        Assert.Contains(headEnd, head.ToString(), StringComparison.Ordinal);
                        total = end + 4 + size;
                    }
                }
                if (slow) await Task.Delay(everyMs);
            }
        }
        catch (SocketException)
        {
            // the server cut it off
        }
        return total < 0 ? 0 : got - (total - size);
    }

    [Fact(Timeout = 30_000)]
    public async Task AResponseReadWellAboveTheMinimumRateIsSentWhole()
    {
        const int Size = 4 << 20;
        // 50 KB/s after 300 ms; the client takes 32 KB every 10 ms, over 1 MB/s even on a busy machine. Before the
        // fix one send call waited for the whole rest, under a deadline computed once: the grace period.
        await using var t = TestServer.Start(Sized(Size), o => o.MinResponseDataRate = new MinDataRate(50_000, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(Size, await ReadThrottled(t, Size, 0, int.MaxValue, 32 << 10, 10));
    }

    [Fact(Timeout = 30_000)]
    public async Task AResponseReadAboveTheMinimumRateMayTakeLongerThanTheRequestTimeout()
    {
        const int Size = 2 << 20;
        // 25 KB/s after 300 ms, and a 1.5 s request timeout; the client takes 16 KB every 20 ms (800 KB/s, thirty times
        // the rate), so the send takes seconds, but what it acknowledges keeps growing. A pause of a second (a GC, a
        // starved thread pool) still leaves it within both limits.
        await using var t = TestServer.Start(Sized(Size), o =>
        {
            o.MinResponseDataRate = new MinDataRate(25_000, TimeSpan.FromMilliseconds(300));
            o.RequestTimeout = TimeSpan.FromMilliseconds(1500);
        });
        long start = Environment.TickCount64;
        Assert.Equal(Size, await ReadThrottled(t, Size, 0, int.MaxValue, 16 << 10, 20));
        Assert.True(Environment.TickCount64 - start > 1500, "the response took less than the request timeout");
    }

    [Fact(Timeout = 30_000)]
    public async Task WithoutARateAResponseThatKeepsMovingIsNotCutOffByTheRequestTimeout()
    {
        const int Size = 2 << 20;
        // 16 KB every 20 ms (800 KB/s), about 2.6 s in all: each 64 KB goes in well under the 1.5 s timeout
        await using var t = TestServer.Start(Sized(Size), o =>
        {
            o.MinResponseDataRate = null;
            o.RequestTimeout = TimeSpan.FromMilliseconds(1500);
        });
        Assert.Equal(Size, await ReadThrottled(t, Size, 0, 3000, 16 << 10, 20));
    }

    // Each acknowledged byte pays for 1/rate seconds of waiting, but no more than the request timeout (or the grace
    // period) ahead. Before, the rate was the run's average: 8 MB read at once bought 8 s at 1 MB/s, and a client then
    // reading 16 KB every 30 ms (about half the rate) kept the connection until its response ended, over 14 s. The 3 s
    // timeout lets the fast part pause (a GC, a starved thread pool) without being cut.
    [Fact(Timeout = 30_000)]
    public async Task AResponseReadFastAtFirstDoesNotBuyALongCrawl()
    {
        const int Size = 16 << 20, Fast = 8 << 20;
        await using var t = TestServer.Start(Sized(Size), o =>
        {
            o.MinResponseDataRate = new MinDataRate(1_000_000, TimeSpan.FromMilliseconds(500));
            o.RequestTimeout = TimeSpan.FromSeconds(3);
        });
        using var s = new Socket(t.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 65536 };
        await s.ConnectAsync(t.EndPoint);
        await s.SendAsync("GET /x HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), SocketFlags.None);
        var b = new byte[64 << 10];
        long got = 0;
        while (got < Fast)
        {
            int n = await s.ReceiveAsync(b, SocketFlags.None);
            Assert.True(n > 0, "the server closed while the client read fast");
            got += n;
        }
        long slow = Environment.TickCount64;
        using var stop = new CancellationTokenSource();
        Task crawl = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested && await s.ReceiveAsync(b.AsMemory(0, 16 << 10), SocketFlags.None) > 0) await Task.Delay(30);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        });
        // about 7 s: the credit is the 3 s timeout, and the crawl pays back half of what passes (less on a busy machine,
        // which only cuts it sooner); the 8 MB left take the crawl over 14 s
        Assert.InRange(await MillisecondsUntilClosed(t, 11_000, slow), 0, 11_000);
        stop.Cancel();
        s.Dispose();
        await crawl;
    }

    // Each send wait has its own deadline, so a check that read one wait can't end the next: before, every wait's was
    // long.MaxValue, and a heartbeat that found a wait too slow cut whichever wait held the connection by then.
    [Fact(Timeout = 30_000)]
    public async Task ACheckOfOneSendWaitDoesNotEndALaterOne()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "needs the hooks only Debug builds call");
        const int Size = 4 << 20;
        await using var t = TestServer.Start(Sized(Size), o => o.MinResponseDataRate = new MinDataRate(50_000, TimeSpan.FromMilliseconds(300)));
        int waits = 0, checks = 0, laterWait = 0;
        t.Server.PhaseHook = (x, p) =>
        {
            if (p == ExchangePhase.Sending) Interlocked.Increment(ref waits);
        };
        // the first check is held, once it has read its wait's deadline, until the next wait has begun, and then finds
        // its wait too slow
        t.Server.SendCheckHook = x =>
        {
            if (Interlocked.Increment(ref checks) != 1) return false;
            int at = Volatile.Read(ref waits);
            if (SpinWait.SpinUntil(() => Volatile.Read(ref waits) > at, TimeSpan.FromSeconds(10))) Volatile.Write(ref laterWait, 1);
            return true;
        };
        Assert.Equal(Size, await ReadThrottled(t, Size, 0, int.MaxValue, 32 << 10, 10));
        Assert.Equal(1, Volatile.Read(ref laterWait));
    }

    // ---- Only a body's payload counts toward its rate ----

    /// <summary>Sends <paramref name="times"/> chunks of <paramref name="payload"/> bytes, each with an extension of
    /// <paramref name="extension"/> bytes, one every <paramref name="everyMs"/> ms, until the server closes.</summary>
    static async Task DripChunks(Socket s, int payload, int extension, int everyMs, int times)
    {
        byte[] chunk = Encoding.ASCII.GetBytes($"{payload:x};{new string('p', extension)}\r\n{new string('d', payload)}\r\n");
        try
        {
            for (int i = 0; i < times; i++)
            {
                for (int sent = 0; sent < chunk.Length;) sent += await s.SendAsync(chunk.AsMemory(sent), SocketFlags.None);
                await Task.Delay(everyMs);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // the server closed it
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task ChunkFramingEarnsNoBodyRateCredit()
    {
        await using var t = TestServer.Start(LengthAndHash,
            o => o.MinRequestBodyDataRate = new MinDataRate(1000, TimeSpan.FromMilliseconds(300)));
        using Client c = await t.ConnectAsync();
        long start = Environment.TickCount64;
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
        // a byte of payload behind 3000 bytes of extension every 50 ms: 60 KB/s on the wire, 20 B/s of body
        Task drip = DripChunks(c.Socket, 1, 3000, 50, 400);
        long took = await MillisecondsUntilClosed(t, 5000, start);
        Assert.InRange(took, 250, 2500);
        Assert.True(await c.ClosedAsync(), "the cut-off request got an answer");
        await drip;
    }

    [Fact(Timeout = 20_000)]
    public async Task AChunkedBodyAboveTheMinimumRateIsReadWholeWhateverItsFraming()
    {
        await using var t = TestServer.Start(LengthAndHash,
            o => o.MinRequestBodyDataRate = new MinDataRate(1000, TimeSpan.FromMilliseconds(300)));
        using Client c = await t.ConnectAsync();
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
        // 300 bytes of payload, each chunk behind a 200-byte extension, every 100 ms: 3000 B/s, or 2000 on a busy
        // machine, for 1 s
        await DripChunks(c.Socket, 300, 200, 100, 10);
        await c.SendAsync("0\r\n\r\n");
        Response r = await c.ReadAsync();
        string body = new('d', 3000);
        Assert.Equal((200, $"3000 {Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(body)))}"), (r.Status, r.Text));
    }

    [Fact]
    public async Task RatesAndTheConnectionLimitAreChecked()
    {
        Assert.Equal((240, TimeSpan.FromSeconds(5)), (new HttpServerOptions().MinRequestBodyDataRate!.BytesPerSecond, new HttpServerOptions().MinResponseDataRate!.GracePeriod));
        Assert.Null(new HttpServerOptions().MaxConnections);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinDataRate(0, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinDataRate(double.NaN, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinDataRate(double.PositiveInfinity, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinDataRate(1, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => new HttpServer(new HttpServerOptions { MaxConnections = 0 }, TestServer.Echo));
        await using var off = new HttpServer(TestTransport.Apply(new HttpServerOptions { MaxConnections = 1, MinRequestBodyDataRate = null, MinResponseDataRate = null }), TestServer.Echo);
    }
}
