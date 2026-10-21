using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

// The test talks to its own server over loopback, with a timeout of its own on each read.
#pragma warning disable xUnit1051

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// SQLite's event-loop batches under the real transport (<see cref="HttpTransport.EventLoop"/>), not a test's copy of its
/// rounds: the server calls each database's batch end once a wake-up's events are handled, and runs them again when a
/// batch opens after its end ran.
/// </summary>
public sealed class ServerLoopTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact(Timeout = 30_000)]
    public async Task ABatchOpenedAfterItsEndRanStillEndsInItsWakeUp()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "event loops run on Linux and macOS");
        var a = _db.Open(_db.Database("a.db", exclusive: true, batches: true));
        var b = _db.Open(_db.Database("b.db", exclusive: true, batches: true));
        int n = 0;
        await using var server = new HttpServer(new HttpServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Transport = HttpTransport.EventLoop,
            EventLoops = 1,
            // nothing else wakes the loop: a batch left open waits for the next event, which here never comes
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            // a's end runs before b's: the write to a that b's completion makes opens a's batch after its end ran
            OnEventLoopBatch = loop =>
            {
                a.EndEventLoopBatch(loop);
                b.EndEventLoopBatch(loop);
            },
        }, async x =>
        {
            int i = System.Threading.Interlocked.Increment(ref n);
            var first = await Kv.Write(b, "b" + i, i);
            var second = await Kv.Write(a, "a" + i, i);
            Encoding.UTF8.GetBytes($"{first.Status} {second.Status}", x);
        });
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(server.LocalEndPoint);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        for (int request = 1; request <= 3; request++)
        {
            var clock = Stopwatch.StartNew();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /w HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n"));
            string reply = "";
            while (!reply.EndsWith("Ok Ok", StringComparison.Ordinal))
            {
                using var wait = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                int read = await stream.ReadAsync(buffer, wait.Token);
                Assert.True(read > 0, "the connection closed");
                reply += Encoding.ASCII.GetString(buffer, 0, read);
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"request {request} took {clock.ElapsedMilliseconds} ms: its write to a waited for another event");
        }
        Assert.Equal(3, Kv.Count(a, "a%"));
        Assert.Equal(3, Kv.Count(b, "b%"));
    }
}
