using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using CodeFusion.Http;
using Xunit;

#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Quiet
{
    /// <summary>Tests that measure the whole process's allocations run alone.</summary>
    public const string Name = "quiet";
}

/// <summary>
/// After warm-up, a keep-alive request through the server allocates nothing: GC.GetTotalAllocatedBytes counts
/// every thread in the process, the client's included, and the client here allocates nothing itself.
/// </summary>
[Collection(Quiet.Name)]
public class AllocationTests(ITestOutputHelper output)
{
    static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    /// <summary>Like the app: one Utf8JsonWriter per connection, kept in State and reset per response. The
    /// state is also how a request finishes on another thread without an async method.</summary>
    sealed class PerConnection : IValueTaskSource, IThreadPoolWorkItem
    {
        Utf8JsonWriter? _w;
        HttpExchange? _x;
        ManualResetValueTaskSourceCore<bool> _done;

        public static PerConnection Of(HttpExchange x) => x.State as PerConnection ?? (PerConnection)(x.State = new PerConnection());

        public void WriteJson(HttpExchange x)
        {
            if (_w is null) _w = new Utf8JsonWriter(x, JsonOptions);
            else _w.Reset(x);
            _w.WriteStartObject();
            _w.WriteStartArray("tags"u8);
            _w.WriteStringValue("dragons"u8);
            _w.WriteStringValue(x.Path);
            _w.WriteEndArray();
            _w.WriteBoolean("auth"u8, x.HasAuthorization);
            _w.WriteNumber("body"u8, x.Body.Length);
            _w.WriteEndObject();
            _w.Flush();
        }

        /// <summary>Answers from a thread-pool thread, as the app's database replies do.</summary>
        public ValueTask Later(HttpExchange x)
        {
            _x = x;
            _done.Reset();
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return new ValueTask(this, _done.Version);
        }

        void IThreadPoolWorkItem.Execute()
        {
            WriteJson(_x!);
            _done.SetResult(true);
        }

        void IValueTaskSource.GetResult(short token) => _done.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _done.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> c, object? s, short token, ValueTaskSourceOnCompletedFlags f) => _done.OnCompleted(c, s, token, f);
    }

    static ValueTask Sync(HttpExchange x)
    {
        PerConnection.Of(x).WriteJson(x);
        return default;
    }

    /// <summary>Goes asynchronous on every request, so the close watch runs and the response is finished on
    /// another thread. Not an async method: the pooled box of an async ValueTask method is itself allocated
    /// again on a share of calls, which would hide what the server does.</summary>
    static ValueTask Async(HttpExchange x) => PerConnection.Of(x).Later(x);

    const string Request = "POST /api/articles/some-slug/comments HTTP/1.1\r\nHost: localhost:3000\r\nUser-Agent: k6/0.50\r\n"
                           + "Authorization: Token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJlc2lnbmF0dXJlc2lnbmF0dXJl\r\n"
                           + "Content-Type: application/json\r\nContent-Length: 30\r\n\r\n{\"comment\":{\"body\":\"hello!!\"}}";

    [Theory(Timeout = 120_000)]
    [InlineData(false, 1, HttpTransport.Sockets)]
    [InlineData(true, 1, HttpTransport.Sockets)]
    [InlineData(true, 8, HttpTransport.Sockets)]
    [InlineData(false, 1, HttpTransport.EventLoop)]
    [InlineData(true, 1, HttpTransport.EventLoop)]
    [InlineData(false, 8, HttpTransport.EventLoop)]
    [InlineData(true, 8, HttpTransport.EventLoop)]
    public async Task KeepAliveRequestsAllocateNothing(bool async, int connections, HttpTransport transport)
    {
        Assert.SkipWhen(Debugger(), "Debug builds make every async method's state machine a class: run in Release");
        await using var t = TestServer.Start(async ? Async : Sync, o => EventLoopTests.Use(o, transport));
        byte[] request = System.Text.Encoding.ASCII.GetBytes(Request);
        var sockets = new Socket[connections];
        var responseLength = new int[connections];
        for (int i = 0; i < connections; i++)
        {
            sockets[i] = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            sockets[i].Connect(t.EndPoint);
            responseLength[i] = FirstResponseLength(sockets[i], request);
        }

        const int warm = 20_000, measured = 50_000;
        RunClients(sockets, request, responseLength, warm).Go();
        Thread.Sleep(200); // let tiered compilation's background work settle
        RunClients(sockets, request, responseLength, warm).Go();
        var clients = RunClients(sockets, request, responseLength, measured);
        GC.Collect();
        using var ticks = new AllocationTicks();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        clients.Go();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        ticks.Dispose();
        foreach (var s in sockets) s.Dispose();

        double perRequest = (double)allocated / (measured * connections);
        output.WriteLine($"{allocated} bytes over {measured * connections} requests: {perRequest:F3} B/request");
        // The runtime samples an allocation about every 100 KB: what it saw names what still allocates.
        foreach (var g in ticks.Types.GroupBy(t => t).OrderByDescending(g => g.Count())) output.WriteLine($"  {g.Count(),4} samples: {g.Key}");
        // Whole-process noise (the listener's own event payloads, the test runner) adds a few KB in all; one
        // allocation per request would add at least 24 bytes for each of them.
        Assert.True(perRequest < 0.5, $"{perRequest:F3} bytes per request");
    }

    internal static bool Debugger() => typeof(HttpServer).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
        is [System.Diagnostics.DebuggableAttribute { IsJITOptimizerDisabled: true }];

    /// <summary>Sends one request and reads its response, to learn the length each later response will have.</summary>
    internal static int FirstResponseLength(Socket s, byte[] request)
    {
        s.Send(request);
        var buf = new byte[4096];
        int n = 0;
        while (true)
        {
            n += s.Receive(buf.AsSpan(n));
            int head = buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8);
            if (head < 0) continue;
            string text = System.Text.Encoding.ASCII.GetString(buf, 0, head);
            int at = text.IndexOf("Content-Length: ", StringComparison.Ordinal);
            int cl = at < 0 ? 0 : int.Parse(text[(at + 16)..].Split('\r')[0]); // a 204 has none
            while (n < head + 4 + cl) n += s.Receive(buf.AsSpan(n));
            return head + 4 + cl;
        }
    }

    /// <summary>One thread per connection, blocking sends and receives into buffers made beforehand. The threads
    /// start now and wait, so making them isn't counted.</summary>
    internal static Clients RunClients(Socket[] sockets, byte[] request, int[] responseLength, int requests)
    {
        var go = new ManualResetEventSlim();
        var done = new CountdownEvent(sockets.Length);
        for (int i = 0; i < sockets.Length; i++)
        {
            int c = i;
            new Thread(() =>
            {
                var buf = new byte[4096];
                go.Wait();
                for (int r = 0; r < requests; r++)
                {
                    sockets[c].Send(request);
                    for (int n = 0; n < responseLength[c];) n += sockets[c].Receive(buf.AsSpan(n, responseLength[c] - n));
                }
                done.Signal();
            }).Start();
        }
        Thread.Sleep(50);
        return new Clients(go, done);
    }

    internal sealed record Clients(ManualResetEventSlim Start, CountdownEvent Done)
    {
        public void Go()
        {
            Start.Set();
            Done.Wait();
        }
    }

    /// <summary>The types the runtime's GC allocation-tick events name while it listens.</summary>
    internal sealed class AllocationTicks : EventListener
    {
        public readonly ConcurrentQueue<string> Types = new();
        volatile bool _on = true;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!_on || e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null) return;
            int i = e.PayloadNames!.IndexOf("TypeName");
            if (i >= 0) Types.Enqueue(e.Payload[i] as string ?? "?");
        }

        public override void Dispose()
        {
            _on = false;
            base.Dispose();
        }
    }
}
