using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using CodeFusion.Http;
using Xunit;

// Every test sets its own timeout, so a server that hangs fails the test instead of the run; the tests talk to
// their own server over loopback and don't take the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// A client that leaves, or a server that stops, at each place a connection waits: for the rest of a request's head,
/// for the rest of its body, for its handler, and for the socket to take its response. Debug builds hold the
/// connection exactly there (<see cref="HttpServer.PhaseHook"/>) while the test acts; Release builds have no hook and
/// act after a short wait, when the connection is most likely there. Either way the server must stop clean
/// (<see cref="ServerChecks"/>: no loop threw, every buffer back, every run and handler call ended once) and read
/// the handler's ValueTask exactly once.
/// </summary>
public sealed class RaceTests
{
    public enum Where { ReadingHead, ReadingBody, HandlerWaiting, Sending }

    public enum Act { ClientResets, ClientCloses, ServerStops, ServerDisposes }

    public static TheoryData<Where, Act> Cases()
    {
        var data = new TheoryData<Where, Act>();
        foreach (Where w in Enum.GetValues<Where>())
            foreach (Act a in Enum.GetValues<Act>())
                data.Add(w, a);
        return data;
    }

    /// <summary>More than the loopback socket buffers hold, so the send has to wait for a client that doesn't read.</summary>
    const int BigBody = 16 << 20;

    [Theory(Timeout = 20_000)]
    [MemberData(nameof(Cases))]
    public async Task TheServerStopsCleanWhateverHappensWhereAConnectionWaits(Where where, Act act)
    {
        var gate = new Gate(where switch
        {
            Where.ReadingHead => ExchangePhase.ReadingHead,
            Where.ReadingBody => ExchangePhase.ReadingBody,
            Where.HandlerWaiting => ExchangePhase.HandlerWaiting,
            _ => ExchangePhase.Sending,
        });
        var handler = new Handler(where == Where.Sending);
        // a stalled request or send is closed after this, so a stop doesn't wait for the hard abort
        await using var t = TestServer.Start(handler.Run, o => o.RequestTimeout = TimeSpan.FromMilliseconds(500));
        t.Server.PhaseHook = gate.Hook;
        var c = await t.ConnectAsync();
        await c.SendAsync(where switch
        {
            Where.ReadingHead => "GET /x HTTP/1.1\r\nHost: x\r\n",
            Where.ReadingBody => "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 100\r\n\r\nabc",
            _ => "GET /x HTTP/1.1\r\nHost: x\r\n\r\n",
        });
        await gate.Reached(where switch
        {
            Where.HandlerWaiting => handler.Started.Task,
            Where.Sending => handler.Returned.Task,
            _ => null,
        });

        Task stop = act switch
        {
            Act.ClientResets => Run(c.Reset),
            Act.ClientCloses => Run(c.Dispose),
            Act.ServerStops => t.Server.StopAsync(TimeSpan.FromSeconds(5)),
            _ => t.Server.DisposeAsync().AsTask(),
        };
        gate.Go();
        if (where == Where.HandlerWaiting)
        {
            // a reset or a FIN reaches the handler as its Aborted token; a stop lets it finish
            if (act is Act.ClientResets or Act.ClientCloses) await handler.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.Probe.Complete();
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        if (act is Act.ServerStops or Act.ServerDisposes)
        {
            if (where == Where.HandlerWaiting)
            {
                // the request in flight is answered, and the connection closed after it
                Response r = await c.ReadAsync();
                Assert.Equal((200, "close"), (r.Status, r.Header("Connection")));
                Assert.True(await c.ClosedAsync());
            }
            else if (where == Where.Sending) Assert.True(await DrainedToTheEnd(c), "the stalled response's connection was closed");
            else Assert.True(await c.ClosedAsync(), "a request still arriving gets no answer, only the close");
            c.Dispose();
        }
        await t.Server.StopAsync(TimeSpan.FromSeconds(5));
        await ServerChecks.AssertStoppedCleanAsync(t.Server);
        Assert.Equal(where is Where.HandlerWaiting or Where.Sending ? 1 : 0, handler.Calls);
        Assert.Equal(where == Where.HandlerWaiting ? 1 : 0, handler.Probe.Reads);
        Assert.Empty(t.Errors);
    }

    static Task Run(Action a)
    {
        a();
        return Task.CompletedTask;
    }

    /// <summary>Reads whatever comes until the server closes the connection: false if it stays open.</summary>
    static async Task<bool> DrainedToTheEnd(Client c)
    {
        var buffer = new byte[1 << 16];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (await c.Socket.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None, cts.Token) > 0) { }
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true; // a reset is a close too
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Holds the first connection to reach the phase until <see cref="Go"/>, so the test acts while it is
    /// there. Only Debug builds call the hook.</summary>
    sealed class Gate(ExchangePhase phase)
    {
        readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _go = new();
        int _fired;

        public void Hook(HttpExchange x, ExchangePhase p)
        {
            if (p != phase || Interlocked.Exchange(ref _fired, 1) == 1) return;
            _reached.SetResult();
            _go.Wait(TimeSpan.FromSeconds(10));
        }

        /// <summary>Debug: until a connection is held at the phase. Release: until <paramref name="after"/> and a moment more.</summary>
        public async Task Reached(Task? after)
        {
            if (AllocationTests.Debugger())
            {
                await _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }
            if (after is not null) await after.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(50);
        }

        public void Go() => _go.Set();
    }

    /// <summary>Answers with a body too big to send at once, or waits on <see cref="Probe"/> until the test lets it go.</summary>
    sealed class Handler(bool big)
    {
        public readonly Probe Probe = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Returned = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public ValueTask Run(HttpExchange x)
        {
            Interlocked.Increment(ref Calls);
            if (big)
            {
                x.GetSpan(BigBody)[..BigBody].Fill((byte)'a');
                x.Advance(BigBody);
                Returned.SetResult();
                return default;
            }
            x.Aborted.Register(() => Aborted.TrySetResult());
            Started.SetResult();
            return Probe.Task;
        }
    }

    /// <summary>A handler's ValueTask that counts how often the server reads it: exactly once, or a pooled source
    /// would be read after its reuse.</summary>
    sealed class Probe : IValueTaskSource
    {
        ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
        public int Reads, Completions;

        public ValueTask Task => new(this, _core.Version);

        public void Complete()
        {
            if (Interlocked.Increment(ref Completions) == 1) _core.SetResult(true);
        }

        void IValueTaskSource.GetResult(short token)
        {
            Interlocked.Increment(ref Reads);
            _core.GetResult(token);
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}
