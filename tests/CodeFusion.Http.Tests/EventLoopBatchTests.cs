using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using CodeFusion;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// One wake-up of an event loop is a batch: the loop handles every ready connection, calls
/// <see cref="HttpServerOptions.OnEventLoopBatch"/>, and only then sends the answers. So a hook that commits a
/// transaction runs before any answer of its batch leaves, and a handler waiting for that commit, completed in the
/// hook, answers in the same batch.
/// </summary>
public sealed class EventLoopBatchTests
{
    static string Get(string path) => $"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n";

    static ValueTask Path(HttpExchange x)
    {
        TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        return default;
    }

    static TestServer Start(HttpHandler handler, Action<int> hook) => TestServer.Start(handler, o =>
    {
        TestTransport.Use(o, HttpTransport.EventLoop);
        o.EventLoops = 1;
        o.OnEventLoopBatch = hook;
    });

    /// <summary>A first request each, answered off the loop; the next ones arrive through it.</summary>
    static async Task Warm(params Client[] clients)
    {
        foreach (Client c in clients)
        {
            await c.SendAsync(Get("/warm"));
            await c.ReadAsync();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task AWakeUpsAnswersLeaveOnlyAfterItsHook()
    {
        int handled = 0;
        Client? client = null;
        var seen = new ConcurrentQueue<(int Handled, int Waiting, int Loop)>();
        await using var t = Start(x =>
        {
            Interlocked.Increment(ref handled);
            return Path(x);
        }, loop =>
        {
            int h = Interlocked.Exchange(ref handled, 0);
            if (h > 0 && Volatile.Read(ref client) is { } c) seen.Enqueue((h, c.Socket.Available, EventLoopThread.Current));
        });
        using Client c = await t.ConnectAsync();
        await Warm(c);
        Interlocked.Exchange(ref handled, 0);
        Volatile.Write(ref client, c);
        for (int i = 0; i < 50; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            Assert.Equal($"/{i}", (await c.ReadAsync()).Text);
        }
        Assert.Equal(50, seen.Count);
        Assert.All(seen, s => Assert.Equal((1, 0, 0), s));
    }

    // Two connections' requests come in one wake-up while the loop is held by a third connection's handler: both are
    // handled, then the hook runs with neither answer sent.
    [Fact(Timeout = 20_000)]
    public async Task TheAnswersOfEveryConnectionInAWakeUpWaitForTheHook()
    {
        using var hold = new ManualResetEventSlim();
        int handled = 0, holding = 0;
        Client? a = null, b = null;
        var seen = new ConcurrentQueue<(int Handled, int WaitingA, int WaitingB)>();
        await using var t = Start(x =>
        {
            if (x.Path.SequenceEqual("/hold"u8))
            {
                Volatile.Write(ref holding, 1);
                hold.Wait(TimeSpan.FromSeconds(10));
            }
            else Interlocked.Increment(ref handled);
            return Path(x);
        }, loop =>
        {
            int h = Interlocked.Exchange(ref handled, 0);
            if (h > 0 && Volatile.Read(ref a) is { } ca && Volatile.Read(ref b) is { } cb)
                seen.Enqueue((h, ca.Socket.Available, cb.Socket.Available));
        });
        using Client holder = await t.ConnectAsync(), first = await t.ConnectAsync(), second = await t.ConnectAsync();
        await Warm(holder, first, second);
        Interlocked.Exchange(ref handled, 0);
        Volatile.Write(ref a, first);
        Volatile.Write(ref b, second);
        await holder.SendAsync(Get("/hold"));
        while (Volatile.Read(ref holding) == 0) await Task.Delay(5);
        await first.SendAsync(Get("/a"));
        await second.SendAsync(Get("/b"));
        await Task.Delay(100);
        hold.Set();
        Assert.Equal("/hold", (await holder.ReadAsync()).Text);
        Assert.Equal("/a", (await first.ReadAsync()).Text);
        Assert.Equal("/b", (await second.ReadAsync()).Text);
        Assert.Equal([(2, 0, 0)], seen.ToArray());
    }

    // What a SQLite provider does: the handler waits for its write's commit, the hook commits and completes it, and
    // the answer goes out in the same batch, after the commit.
    [Fact(Timeout = 20_000)]
    public async Task AHandlerTheHookCompletesAnswersInTheSameBatch()
    {
        var commit = new Commit();
        Client? client = null;
        var seen = new ConcurrentQueue<(int Waiting, int Pending)>();
        await using var t = Start(async x =>
        {
            if (x.Path.SequenceEqual("/write"u8)) await commit.Wait();
            TestServer.Write(x, $"{Encoding.UTF8.GetString(x.Path)} on loop {EventLoopThread.Current}");
        }, loop =>
        {
            if (commit.Waiting && Volatile.Read(ref client) is { } c)
            {
                seen.Enqueue((c.Socket.Available, 1));
                commit.Complete(); // runs the handler's rest here, on the loop
            }
        });
        using Client c = await t.ConnectAsync();
        await Warm(c);
        Volatile.Write(ref client, c);
        for (int i = 0; i < 20; i++)
        {
            await c.SendAsync(Get("/write"));
            Assert.Equal("/write on loop 0", (await c.ReadAsync()).Text);
        }
        Assert.Equal(20, seen.Count);
        Assert.All(seen, s => Assert.Equal((0, 1), s));
    }

    [Fact(Timeout = 20_000)]
    public async Task CurrentEventLoopIsTheLoopsIndexOnItsThreadAndMinusOneElsewhere()
    {
        var loops = new ConcurrentDictionary<int, bool>();
        await using var t = TestServer.Start(x =>
        {
            TestServer.Write(x, EventLoopThread.Current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return default;
        }, o =>
        {
            TestTransport.Use(o, HttpTransport.EventLoop);
            o.EventLoops = 3;
            o.OnEventLoopBatch = loop => loops[loop] = EventLoopThread.Current == loop;
        });
        Assert.Equal(-1, EventLoopThread.Current);
        // on Linux the kernel spreads connections over the loops' listeners by hash: 30 miss a loop once in 60,000 runs
        var clients = new Client[30];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = await t.ConnectAsync();
            await Warm(clients[i]);
        }
        var answers = new System.Collections.Generic.List<string>();
        foreach (Client c in clients)
        {
            await c.SendAsync(Get("/a"));
            answers.Add((await c.ReadAsync()).Text);
            c.Dispose();
        }
        Assert.Equal(["0", "1", "2"], answers.Distinct().Order().ToArray());
        Assert.Equal([0, 1, 2], loops.Keys.Order().ToArray());
        Assert.All(loops.Values, Assert.True);
    }

    [Fact(Timeout = 20_000)]
    public async Task AHookThatThrowsIsCountedAndTheAnswersStillGo()
    {
        int calls = 0;
        bool throwing = true;
        await using var t = Start(Path, loop =>
        {
            if (!Volatile.Read(ref throwing)) return;
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("the hook failed");
        });
        using Client c = await t.ConnectAsync();
        await Warm(c);
        for (int i = 0; i < 5; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            Assert.Equal($"/{i}", (await c.ReadAsync()).Text);
        }
        Volatile.Write(ref throwing, false);
        await Task.Delay(50); // a wake-up already in its hook finishes
        Assert.True(Volatile.Read(ref calls) >= 5);
        Assert.Equal(Volatile.Read(ref calls), Interlocked.Exchange(ref t.Server.CallbackFailures, 0));
    }

    [Fact(Timeout = 20_000)]
    public async Task SocketsNeverCallsTheHook()
    {
        int calls = 0;
        await using var t = TestServer.Start(Path, o =>
        {
            TestTransport.Use(o, HttpTransport.Sockets);
            o.OnEventLoopBatch = _ => Interlocked.Increment(ref calls);
        });
        using Client c = await t.ConnectAsync();
        for (int i = 0; i < 5; i++)
        {
            await c.SendAsync(Get($"/{i}"));
            await c.ReadAsync();
        }
        Assert.Equal(0, calls);
    }

    // Every handler that starts on the loop is inside a batch whose hook runs: here the second of two pipelined writes
    // starts while the first one's answer is sent, after the hook that committed the first.
    [Fact(Timeout = 20_000)]
    public async Task PipelinedWritesThatOnlyTheHookCompletesAreAllAnswered()
    {
        var waiting = new List<TaskCompletionSource>();
        await using var t = Start(async x =>
        {
            if (x.Path.SequenceEqual("/write"u8))
            {
                var commit = new TaskCompletionSource(); // completed inline, in the hook, as a provider's commit is
                lock (waiting) waiting.Add(commit);
                await commit.Task;
            }
            TestServer.Write(x, Encoding.UTF8.GetString(x.Path));
        }, loop =>
        {
            TaskCompletionSource[] now;
            lock (waiting)
            {
                now = [.. waiting];
                waiting.Clear();
            }
            foreach (TaskCompletionSource c in now) c.SetResult();
        });
        using Client c = await t.ConnectAsync();
        await Warm(c);
        for (int round = 0; round < 10; round++)
        {
            await c.SendAsync(Get("/write") + Get("/write") + Get("/write"));
            for (int i = 0; i < 3; i++) Assert.Equal("/write", (await c.ReadAsync(timeoutMs: 1000)).Text);
        }
    }

    // A connection's first request is handled on its loop, in a batch, like the rest: whoever starts the connection,
    // the loop is told of bytes already waiting when it adds the socket.
    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConnectionsFirstRequestIsHandledInABatch(bool listenerPerLoop)
    {
        var loops = new ConcurrentQueue<int>();
        await using var t = TestServer.Start(x =>
        {
            loops.Enqueue(EventLoopThread.Current);
            return Path(x);
        }, o =>
        {
            TestTransport.Use(o, HttpTransport.EventLoop);
            o.EventLoops = 2;
            o.ListenerPerLoop = listenerPerLoop;
        });
        for (int i = 0; i < 20; i++)
        {
            using Client c = await t.ConnectAsync();
            await c.SendAsync(Get("/first"));
            Assert.Equal("/first", (await c.ReadAsync()).Text);
        }
        Assert.Equal(20, loops.Count);
        Assert.DoesNotContain(-1, loops);
    }

    // A batch opened on the loop by code that runs as a held answer is sent (not a handler: here the connection's idle
    // hook, as a write after a handler's own flush would) asks for its end, and gets it before the loop waits again.
    [Fact(Timeout = 20_000)]
    public async Task ABatchOpenedWhileAnswersAreSentEndsBeforeTheLoopWaitsAgain()
    {
        Assert.SkipUnless(AllocationTests.Debugger(), "Debug builds count the loop's waits");
        int waits = 0, opened = -1, ended = -1;
        bool open = false;
        await using var t = Start(Path, loop =>
        {
            if (!Volatile.Read(ref open)) return;
            Volatile.Write(ref open, false);
            Volatile.Write(ref ended, Volatile.Read(ref waits));
        });
        t.Server.LoopWaitHook = _ =>
        {
            Interlocked.Increment(ref waits);
            return false;
        };
        using Client c = await t.ConnectAsync();
        await Warm(c);
        t.Server.ConnectionIdle = _ =>
        {
            if (EventLoopThread.Current < 0 || Volatile.Read(ref opened) >= 0) return;
            Volatile.Write(ref open, true);
            Volatile.Write(ref opened, Volatile.Read(ref waits));
            EventLoopThread.RequestBatchEnd();
        };
        await c.SendAsync(Get("/a"));
        Assert.Equal("/a", (await c.ReadAsync()).Text);
        await TestServer.Until(() => Volatile.Read(ref ended) >= 0, 2000, () => "the batch never ended");
        Assert.True(opened >= 0);
        Assert.Equal(opened, ended);
    }

    // A loop whose batches keep asking for another end runs its rounds, and past them goes on without waiting for an
    // event: no batch is left open until some unrelated request arrives.
    [Fact(Timeout = 20_000)]
    public async Task BatchEndsAskedForPastTheRoundsGoOnWithoutAnEvent()
    {
        int calls = 0, asking = 0;
        await using var t = Start(Path, loop =>
        {
            Interlocked.Increment(ref calls);
            if (Volatile.Read(ref asking) > 0 && Interlocked.Decrement(ref asking) >= 0) EventLoopThread.RequestBatchEnd();
        });
        using Client c = await t.ConnectAsync();
        await Warm(c);
        Volatile.Write(ref asking, 30); // 30 more ends asked for: over three wake-ups' rounds
        await c.SendAsync(Get("/a"));
        Assert.Equal("/a", (await c.ReadAsync()).Text);
        await TestServer.Until(() => Volatile.Read(ref asking) <= 0, 2000, () => $"{asking} batch ends still asked for after {calls} calls");
    }

    // What CodeFusion.Data's providers use: actions an app's services register, run after the options' hook in their
    // order, each with the loop's index, on that loop's thread; none after Build.
    [Fact(Timeout = 20_000)]
    public async Task AnAppsBatchActionsRunInOrderOnTheLoopWithItsIndex()
    {
        var seen = new ConcurrentQueue<string>();
        HttpAppBuilder? builder = null;
        await using HttpApp app = Sample.SampleApp.Build(b =>
        {
            builder = b;
            TestTransport.Use(b.Options, HttpTransport.EventLoop);
            b.Options.EventLoops = 1;
            b.Options.OnEventLoopBatch = loop => seen.Enqueue($"options {loop}");
            b.OnEventLoopBatch(loop => seen.Enqueue($"first {loop} on {EventLoopThread.Current}"));
            b.OnEventLoopBatch(loop => seen.Enqueue($"second {loop}"));
        });
        await using var check = new AppCheck(app);
        app.Start();
        Assert.Throws<InvalidOperationException>(() => builder!.OnEventLoopBatch(static _ => { }));
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        for (int i = 0; i < 5; i++)
        {
            await c.SendAsync(Get("/api/nothing/here"));
            Assert.Equal(404, (await c.ReadAsync()).Status);
        }
        string[] calls = seen.ToArray();
        Assert.True(calls.Length >= 3 && calls.Length % 3 == 0, string.Join(", ", calls));
        for (int i = 0; i < calls.Length; i += 3)
            Assert.Equal(["options 0", "first 0 on 0", "second 0"], calls[i..(i + 3)]);
    }

    [Fact(Timeout = 20_000)]
    public async Task AnAppsBatchActionThatThrowsIsReportedOnceAndTheOthersStillRun()
    {
        var reports = new ConcurrentQueue<string>();
        int after = 0;
        bool throwing = true;
        await using HttpApp app = Sample.SampleApp.Build(b =>
        {
            TestTransport.Use(b.Options, HttpTransport.EventLoop);
            b.Options.EventLoops = 1;
            b.Options.OnError = (what, _) => reports.Enqueue(what);
            b.OnEventLoopBatch(_ =>
            {
                if (Volatile.Read(ref throwing)) throw new InvalidOperationException("the commit failed");
            });
            b.OnEventLoopBatch(_ => Interlocked.Increment(ref after));
        });
        await using var check = new AppCheck(app);
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        // the first request may run on the pool thread that starts the connection, outside any batch
        for (int i = 0; i < 6; i++)
        {
            await c.SendAsync(Get("/api/nothing/here"));
            Assert.Equal(404, (await c.ReadAsync()).Status);
        }
        Volatile.Write(ref throwing, false);
        await Task.Delay(50); // a wake-up already in its actions finishes
        int failures = Interlocked.Exchange(ref app.Server!.CallbackFailures, 0);
        Assert.True(failures >= 5, $"{failures} failures counted");
        Assert.True(Volatile.Read(ref after) >= failures, $"the action after the failing one ran {after} times");
        Assert.Equal(["http event loop hook failed"], reports.ToArray());
    }

    /// <summary>A commit the hook completes, running its waiter's continuation inline, as a provider's would.</summary>
    sealed class Commit : IValueTaskSource
    {
        ManualResetValueTaskSourceCore<bool> _core;
        volatile bool _waiting;

        public bool Waiting => _waiting;

        public ValueTask Wait()
        {
            _core.Reset();
            _waiting = true;
            return new ValueTask(this, _core.Version);
        }

        public void Complete()
        {
            _waiting = false;
            _core.SetResult(true);
        }

        void IValueTaskSource.GetResult(short token) => _core.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> c, object? s, short token, ValueTaskSourceOnCompletedFlags f) => _core.OnCompleted(c, s, token, f);
    }
}
