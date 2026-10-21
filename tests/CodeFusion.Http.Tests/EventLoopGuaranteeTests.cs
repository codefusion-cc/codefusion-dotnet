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
            // the stop's own 200 ms, the handler's 1 s and the loop's 1 s, with room for a loaded runner
            Assert.InRange(clock.ElapsedMilliseconds, 0, 4000);
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

    // The bound is on requests, not reads: one read of small pipelined requests holds a hundred. On a loop, a neighbour's
    // request waits for at most the flood's share of one wake-up and the rest of the one under way: 32 slow requests,
    // counted, so the machine's load can't fail it. Sockets serves the two on threads of their own; there a neighbour
    // stuck behind the flood would wait seconds.
    [Theory(Timeout = 30_000)]
    [InlineData(HttpTransport.EventLoop)]
    [InlineData(HttpTransport.Sockets)]
    public async Task ANeighbourOfAFloodOfSlowRequestsWaitsAtMostOneShare(HttpTransport transport)
    {
        int slowRuns = 0, slowAtNeighbour = 0;
        await using var t = TestServer.Start(x =>
        {
            if (x.Path.SequenceEqual("/slow"u8))
            {
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
                while (Stopwatch.GetTimestamp() < until) { }
                Interlocked.Increment(ref slowRuns);
            }
            else Volatile.Write(ref slowAtNeighbour, Volatile.Read(ref slowRuns));
            TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
            return default;
        }, o =>
        {
            TestTransport.Use(o, transport);
            o.EventLoops = 1;
        });
        using Client busy = await t.ConnectAsync(), other = await t.ConnectAsync();
        Assert.Equal("/warm", await Ask(other, "/warm"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        byte[] burst = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(Get("/slow"), 120))); // about 4 KB
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
        await TestServer.Until(() => Volatile.Read(ref slowRuns) > 100, 5000, () => "the flood never started");
        long worstMs = 0, worstSlow = 0;
        for (int i = 0; i < 5; i++)
        {
            var clock = Stopwatch.StartNew();
            int before = Volatile.Read(ref slowRuns);
            Assert.Equal($"/b{i}", await Ask(other, $"/b{i}", timeoutMs: 3000));
            worstMs = Math.Max(worstMs, clock.ElapsedMilliseconds);
            worstSlow = Math.Max(worstSlow, Volatile.Read(ref slowAtNeighbour) - before);
        }
        if (transport == HttpTransport.EventLoop)
            Assert.True(worstSlow <= 32, $"the neighbour waited for {worstSlow} slow requests");
        else
            Assert.True(worstMs < 1000, $"the neighbour waited {worstMs} ms");
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

    // Linux: each loop accepts from its own listener (accept4). An error that belongs to one connection skips it; out
    // of descriptors the loop turns its listener off, reports once, and the heartbeat turns it on again, even when
    // that resume lands while the loop is still turning it off.
    [Fact(Timeout = 20_000)]
    public async Task OnLinuxAnAcceptErrorOfOneConnectionSkipsOnlyIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && AllocationTests.Debugger(), "Linux loops accept for themselves; Debug builds can fail an accept");
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var t = Start(Path, o => o.OnError = (what, _) => reports.Enqueue(what));
        int failed = 0;
        t.Server.AcceptHook = _ => Interlocked.Exchange(ref failed, 1) == 0 ? 103 : 0; // ECONNABORTED
        using Client c = await t.ConnectAsync();
        Assert.Equal("/a", await Ask(c, "/a", timeoutMs: 2000));
        Assert.Equal(1, failed);
        Assert.Empty(reports);
    }

    // Past a resource limit an accept can throw what no one expects: macOS's runtime said "Out of memory." at about
    // 10k sockets, and accepting stopped for good without a word. On every path the failure is reported once, the
    // accept backs off and tries again, and the server goes on taking connections.
    [Theory(Timeout = 20_000)]
    [InlineData(HttpTransport.EventLoop)]
    [InlineData(HttpTransport.Sockets)]
    public async Task AnAcceptThatThrowsIsReportedAndAcceptingGoesOn(HttpTransport transport)
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make an accept throw");
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var t = TestServer.Start(Path, o =>
        {
            TestTransport.Use(o, transport);
            o.EventLoops = 1;
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(20); // resumes a paused loop's listener
            o.OnError = (what, _) => reports.Enqueue(what);
        });
        int throws = 2;
        // a loop's own accept4 (Linux), or the runtime's accept (macOS's loops, Sockets)
        t.Server.AcceptHook = _ => Interlocked.Decrement(ref throws) >= 0 ? throw new InsufficientMemoryException() : 0;
        t.Server.RuntimeAcceptHook = () =>
        {
            if (Interlocked.Decrement(ref throws) >= 0) throw new InsufficientMemoryException();
        };
        // the runtime's accept may be waiting already: it throws from the next one on
        using Client first = await t.ConnectAsync(), second = await t.ConnectAsync();
        Assert.Equal("/a", await Ask(first, "/a", timeoutMs: 5000));
        Assert.Equal("/b", await Ask(second, "/b", timeoutMs: 5000));
        Assert.True(Volatile.Read(ref throws) < 0, "the accept never threw");
        Assert.Equal(["http accept failing", "http accept works again"], reports.ToArray());
    }

    // Admitting an accepted connection can throw too (a new connection object, out of memory): that socket is closed,
    // not leaked, and the next connection is admitted.
    [Theory(Timeout = 20_000)]
    [InlineData(HttpTransport.EventLoop)]
    [InlineData(HttpTransport.Sockets)]
    public async Task AConnectionWhoseAdmissionThrowsIsClosedAndAcceptingGoesOn(HttpTransport transport)
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make an admission throw");
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var t = TestServer.Start(Path, o =>
        {
            TestTransport.Use(o, transport);
            o.EventLoops = 1;
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(20);
            o.OnError = (what, _) => reports.Enqueue(what);
        });
        int throws = 1;
        t.Server.AdmitHook = _ =>
        {
            if (Interlocked.Decrement(ref throws) >= 0) throw new InsufficientMemoryException();
        };
        using (Client first = await t.ConnectAsync())
            Assert.Equal(0, await first.Socket.ReceiveAsync(new byte[1], System.Net.Sockets.SocketFlags.None)); // closed
        using Client second = await t.ConnectAsync();
        Assert.Equal("/b", await Ask(second, "/b", timeoutMs: 5000));
        Assert.Equal(["http accept failing", "http accept works again"], reports.ToArray());
    }

    [Fact(Timeout = 20_000)]
    public async Task OnLinuxALoopOutOfDescriptorsPausesAndTheHeartbeatResumesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && AllocationTests.Debugger(), "Linux loops accept for themselves; Debug builds can fail an accept");
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var t = Start(Path, o => o.OnError = (what, _) => reports.Enqueue(what));
        int failed = 0;
        t.Server.AcceptHook = _ => Interlocked.Exchange(ref failed, 1) == 0 ? 24 : 0; // EMFILE
        t.Server.PauseHook = loop => loop.ResumeAccept(); // a heartbeat's resume, landing mid-pause
        var clients = new List<Client>();
        try
        {
            for (int i = 0; i < 20; i++)
            {
                Client c = await t.ConnectAsync();
                clients.Add(c);
                Assert.Equal($"/{i}", await Ask(c, $"/{i}", timeoutMs: 2000));
            }
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
        }
        Assert.Equal(["http accept failing", "http accept works again"], reports.ToArray());
    }

    // A stop that the failure report starts (a hook that shuts down on it) disposes the listener the loop is about to
    // turn off: the loop must neither crash nor count a failed connection.
    [Fact(Timeout = 20_000)]
    public async Task OnLinuxAStopStartedByTheAcceptFailureReportEndsClean()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && AllocationTests.Debugger(), "Linux loops accept for themselves; Debug builds can fail an accept");
        Task? stop = null;
        TestServer? t = null;
        t = Start(Path, o => o.OnError = (what, _) =>
        {
            if (what == "http accept failing") stop = t!.Server.StopAsync(TimeSpan.FromSeconds(1));
        });
        t.Server.AcceptHook = _ => 24; // EMFILE, every time
        // the connection only makes the accept fail: the stop its report starts closes the listener, which can reset
        // this handshake before it completes, so how the connect ends isn't what this test checks
        try
        {
            using Client c = await t.ConnectAsync();
        }
        catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionReset)
        {
        }
        await TestServer.Until(() => Volatile.Read(ref stop) is not null, 5000, () => "the accept never failed");
        await stop!.WaitAsync(TimeSpan.FromSeconds(5));
        await t.DisposeAsync(); // ServerChecks: no failed connection, every loop ended
    }

    // Linux loops admit connections at once: the limit holds across them, not only within one.
    [Fact(Timeout = 30_000)]
    public async Task OnLinuxMaxConnectionsHoldsAcrossLoops()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && AllocationTests.Debugger(), "Linux loops accept for themselves; Debug builds can hold an admission");
        await using var t = Start(Path, o =>
        {
            o.EventLoops = 2;
            o.MaxConnections = 1;
        });
        // the first admission waits until another loop has admitted one (or a moment, if both landed on one loop)
        using var other = new ManualResetEventSlim();
        int admissions = 0;
        t.Server.AdmitHook = _ =>
        {
            if (Interlocked.Increment(ref admissions) == 1) other.Wait(TimeSpan.FromMilliseconds(500));
            else other.Set();
        };
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Interlocked.Exchange(ref admissions, 0);
            other.Reset();
            using Client a = await t.ConnectAsync(), b = await t.ConnectAsync();
            await Task.Delay(700);
            Assert.InRange(t.Server.Connections, 0, 1);
        }
    }

    // A loop whose wait fails (only a broken descriptor does that) is reported once and gets no more connections:
    // they go to the loops still running.
    // On Linux with a listener per loop, the failed loop's listener goes to the runtime's accepts: closing it would
    // reset the connections queued on it, and the kernel goes on sending it its share.
    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALoopThatFailsTakesNoNewConnections(bool listenerPerLoop)
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make a loop's wait fail");
        var reports = new List<string>();
        await using var t = Start(Path, o =>
        {
            o.EventLoops = 2;
            o.ListenerPerLoop = listenerPerLoop;
            o.OnError = (what, _) => { lock (reports) reports.Add(what); };
        });
        int failed = 0;
        t.Server.LoopWaitHook = loop => loop == 0 && Interlocked.Exchange(ref failed, 1) == 0;
        // wake loop 0 so its next wait fails: a connection on it (by the kernel's hash, with a listener per loop)
        for (int i = 0; i < 40 && Volatile.Read(ref failed) == 0; i++)
        {
            using Client c = await t.ConnectAsync();
            try
            {
                await Ask(c, "/wake");
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or System.IO.EndOfStreamException)
            {
                // the connection that woke loop 0 is closed with it
            }
        }
        await TestServer.Until(() => t.Server.EventLoopsRunning == 1, 5000, () => $"{t.Server.EventLoopsRunning} loops running");
        for (int i = 0; i < 20; i++)
        {
            using Client c = await t.ConnectAsync();
            Assert.Equal($"/{i}", await Ask(c, $"/{i}"));
        }
        lock (reports) Assert.Equal(["http event loop failed"], reports.Distinct().ToArray());
    }

    // A failed loop closes its connections with a FIN, as a timeout does, not a reset: a reset can reach a connection
    // it has just accepted before the client's own connect has finished, and fail that connect.
    [Fact(Timeout = 20_000)]
    public async Task AFailedLoopClosesItsConnectionsWithAFin()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make a loop's wait fail");
        await using var t = Start(Path, o =>
        {
            o.EventLoops = 1;
            o.OnError = null;
        });
        using Client idle = await t.ConnectAsync();
        Assert.Equal("/a", await Ask(idle, "/a"));
        t.Server.LoopWaitHook = _ => true;
        using (Client waker = await t.ConnectAsync()) { } // wakes the loop, whose next wait fails
        await TestServer.Until(() => t.Server.EventLoopsRunning == 0, 5000, () => $"{t.Server.EventLoopsRunning} loops running");
        // a FIN reads as the end; a reset throws
        Assert.Equal(0, await idle.Socket.ReceiveAsync(new byte[1], System.Net.Sockets.SocketFlags.None));
    }

    // With every loop failed, new connections wait on their sockets as Sockets' do.
    [Fact(Timeout = 20_000)]
    public async Task WhenEveryLoopFailsConnectionsFallBackToSockets()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds can make a loop's wait fail");
        await using var t = Start(Path, o =>
        {
            o.EventLoops = 2;
            o.OnError = null;
        });
        t.Server.LoopWaitHook = _ => true;
        for (int i = 0; i < 40 && t.Server.EventLoopsRunning > 0; i++)
        {
            using Client c = await t.ConnectAsync();
            try
            {
                await Ask(c, "/wake");
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or System.IO.EndOfStreamException)
            {
                // closed with its failed loop
            }
        }
        await TestServer.Until(() => t.Server.EventLoopsRunning == 0, 5000, () => $"{t.Server.EventLoopsRunning} loops running");
        for (int i = 0; i < 10; i++)
        {
            using Client c = await t.ConnectAsync();
            Assert.Equal($"/{i}", await Ask(c, $"/{i}"));
        }
    }

    // One listener: the loops take connections in turn, exactly, whatever the kernel's hash would do.
    [Fact(Timeout = 20_000)]
    public async Task WithOneListenerConnectionsGoToTheLoopsInTurn()
    {
        await using var t = Start(static x =>
        {
            TestServer.Write(x, CodeFusion.EventLoopThread.Current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return default;
        }, o =>
        {
            o.EventLoops = 4;
            o.ListenerPerLoop = false;
        });
        var clients = new List<Client>();
        var loops = new List<string>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                Client c = await t.ConnectAsync();
                clients.Add(c);
                await Ask(c, "/warm"); // the first request may run on the pool thread that starts the connection
                loops.Add(await Ask(c, "/which"));
            }
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
        }
        Assert.Equal(["0", "0", "1", "1", "2", "2", "3", "3"], loops.Order().ToArray());
    }

    // The direct calls run without a GC transition: under steady collections a connection must neither close for
    // nothing (a lost errno would turn EAGAIN into a failure) nor fail.
    [Fact(Timeout = 30_000)]
    public async Task ConnectionsOnTheLoopsKeepWorkingThroughCollections()
    {
        await using var t = Start(Path, o => o.EventLoops = 2);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Task collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) GC.Collect();
        });
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async n =>
        {
            using Client c = await t.ConnectAsync();
            for (int i = 0; !stop.IsCancellationRequested; i++) Assert.Equal($"/{n}/{i}", await Ask(c, $"/{n}/{i}", timeoutMs: 5000));
        }));
        await collector;
    }
}
