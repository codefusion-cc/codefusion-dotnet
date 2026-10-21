using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using CodeFusion.Http;
using CodeFusion.Benchmarks.Reference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CodeFusion.Benchmarks;

public enum HttpServerKind
{
    Kestrel,
    Own,
    /// <summary>Accepts, answers one request with blocking calls and holds the socket: the floor an idle
    /// connection costs the client and the kernel's socket objects.</summary>
    Sockets,
}

/// <summary>
/// Our server against Kestrel set up as the contest server (21abce6) set it up, both answering the same small JSON over loopback
/// keep-alive connections, driven by <see cref="HttpLoadClient"/> in the same process. One operation is one
/// request. CPU/req is the whole process's CPU time (the client's share included, the same for both) per
/// request, from the benchmark's start to its end.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(HttpConfig))]
public class HttpServerBenchmarks
{
    public const int RequestsPerInvoke = 8192;

    [Params(HttpServerKind.Kestrel, HttpServerKind.Own)]
    public HttpServerKind Server { get; set; }

    [Params(1, 64, 1024)]
    public int Connections { get; set; }

    RunningServer? _server;
    HttpLoadClient? _client;
    TimeSpan _cpu;
    long _requests;

    [GlobalSetup]
    public void Setup()
    {
        _server = RunningServer.Start(Server);
        _client = new HttpLoadClient(_server.EndPoint, Connections, RunningServer.Requests);
        for (int i = 0; i < 5; i++) Requests(); // the first tiers of JIT, before CPU is counted
        _cpu = Process.GetCurrentProcess().TotalProcessorTime;
        _requests = _client.Requests;
    }

    [Benchmark(OperationsPerInvoke = RequestsPerInvoke)]
    public void Requests() => _client!.Run(RequestsPerInvoke / Connections);

    [GlobalCleanup]
    public void Cleanup()
    {
        double cpuUs = (Process.GetCurrentProcess().TotalProcessorTime - _cpu).TotalMicroseconds / (_client!.Requests - _requests);
        SideResults.Write($"{Server}-{Connections}", ("cpu", cpuUs));
        _client.Dispose();
        _server!.Dispose();
    }
}

/// <summary>
/// Memory per idle keep-alive connection: 10,000 connections each make one request and then sit idle; working
/// set and GC heap are measured after a full collection before and after. The client's sockets are in the same
/// process, so the per-connection numbers include the client's share, which is the same for both servers. The
/// timed operation is one request on another connection while the idle ones are open. Subtract the Sockets row
/// for what the server itself keeps.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(HttpConfig))]
public class HttpIdleBenchmarks
{
    [ParamsSource(nameof(Servers))]
    public HttpServerKind Server { get; set; }

    /// <summary>All three, or the one HTTP_BENCH_SERVER names: an in-process run (--inProcess) measures memory
    /// truly only with one server per process, since what the one before left behind is freed during the next.</summary>
    public static HttpServerKind[] Servers => Enum.TryParse(Environment.GetEnvironmentVariable("HTTP_BENCH_SERVER"), out HttpServerKind k)
        ? [k]
        : [HttpServerKind.Kestrel, HttpServerKind.Own, HttpServerKind.Sockets];

    /// <summary>HTTP_BENCH_IDLE overrides the 10,000: macOS runs out of sockets system-wide (ENFILE) near 20,000,
    /// client and server sides together.</summary>
    public int Idle { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("HTTP_BENCH_IDLE"), out int n) ? n : 10_000;

    RunningServer? _server;
    HttpLoadClient? _idle, _active;

    [GlobalSetup]
    public void Setup()
    {
        _server = RunningServer.Start(Server);
        _active = new HttpLoadClient(_server.EndPoint, 1, RunningServer.Requests);
        _active.Run(1000);
        (long ws0, long heap0) = Memory();
        _idle = new HttpLoadClient(_server.EndPoint, Idle, RunningServer.Requests);
        Thread.Sleep(1000);
        (long ws1, long heap1) = Memory();
        SideResults.Write($"idle-{Server}", ("ws", (ws1 - ws0) / (double)Idle), ("heap", (heap1 - heap0) / (double)Idle));
    }

    /// <summary>Live GC bytes (not the gen0 budget) and working set, once full collections stop changing them.</summary>
    static (long WorkingSet, long Heap) Memory()
    {
        long heap = -1;
        for (int i = 0; i < 10; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            long now = GC.GetTotalMemory(forceFullCollection: false);
            if (Math.Abs(now - heap) < heap / 1000) break;
            heap = now;
            Thread.Sleep(100);
        }
        using var p = Process.GetCurrentProcess();
        return (p.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false));
    }

    [Benchmark]
    public void RequestBesideIdleConnections() => _active!.Run(1);

    [GlobalCleanup]
    public void Cleanup()
    {
        _idle!.Dispose();
        _active!.Dispose();
        _server!.Dispose();
    }
}

/// <summary>The same JSON answer from either server: a pooled writer per response under Kestrel, as the app does
/// today, and one writer per connection under ours.</summary>
public sealed class RunningServer : IDisposable
{
    /// <summary>A few paths in turn, as a user's requests vary, so neither server answers from a cache of the
    /// last request's strings. The answers are all the same length.</summary>
    public static readonly byte[][] Requests = new[] { "/api/articles?limit=20&offset=0", "/api/articles/how-to-train-your-dragon-1",
        "/api/profiles/jake", "/api/articles/feed?limit=10" }.Select(path => Encoding.ASCII.GetBytes(
        $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:3000\r\nUser-Agent: k6/0.50\r\n"
        + "Authorization: Token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJlc2lnbmF0dXJlc2lnbmF0dXJl\r\n"
        + "Accept: application/json\r\n\r\n")).ToArray();

    static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    public IPEndPoint EndPoint { get; private init; } = null!;
    Action _stop = null!;

    static void Json(Utf8JsonWriter w, bool auth)
    {
        w.WriteStartObject();
        w.WriteStartArray("tags"u8);
        w.WriteStringValue("dragons"u8);
        w.WriteStringValue("training"u8);
        w.WriteEndArray();
        w.WriteBoolean("auth"u8, auth);
        w.WriteEndObject();
        w.Flush();
    }

    public static RunningServer Start(HttpServerKind kind) => kind switch
    {
        HttpServerKind.Own => StartOwn(),
        HttpServerKind.Kestrel => StartKestrel(),
        _ => StartSockets(),
    };

    static RunningServer StartSockets()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1024);
        var held = new List<Socket>();
        var answer = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}");
        var thread = new Thread(() =>
        {
            var buf = new byte[4096];
            try
            {
                // The first connection is the timed one: it gets answers on its own thread for as long as it asks.
                Socket first = listener.Accept();
                held.Add(first);
                new Thread(() =>
                {
                    var b = new byte[4096];
                    try
                    {
                        while (true) Answer(first, b);
                    }
                    catch (Exception e) when (e is SocketException or ObjectDisposedException)
                    {
                    }
                }) { IsBackground = true }.Start();
                // The rest ask once, right after connecting, and then sit idle.
                while (true)
                {
                    Socket s = listener.Accept();
                    held.Add(s);
                    Answer(s, buf);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true };
        thread.Start();

        void Answer(Socket s, byte[] buf)
        {
            for (int n = 0; buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) < 0;)
            {
                int r = s.Receive(buf.AsSpan(n));
                if (r == 0) throw new SocketException((int)SocketError.ConnectionReset);
                n += r;
            }
            s.Send(answer);
        }

        return new RunningServer
        {
            EndPoint = (IPEndPoint)listener.LocalEndPoint!,
            _stop = () =>
            {
                listener.Dispose();
                thread.Join();
                foreach (var s in held) s.Dispose();
            },
        };
    }

    static RunningServer StartOwn()
    {
        var server = new HttpServer(new HttpServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, x =>
        {
            if (x.State is not Utf8JsonWriter w) x.State = w = new Utf8JsonWriter(x, JsonOptions);
            else w.Reset(x);
            Json(w, x.HasAuthorization);
            return default;
        });
        server.Start();
        return new RunningServer { EndPoint = server.LocalEndPoint, _stop = () => server.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() };
    }

    /// <summary>Kestrel as the contest server configured it.</summary>
    static RunningServer StartKestrel()
    {
        int port = FreePort();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseSockets(o => o.Backlog = 65535);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
            o.Limits.MaxRequestBodySize = 1 << 20;
            o.ConfigureEndpointDefaults(l => l.Protocols = HttpProtocols.Http1);
            o.Listen(IPAddress.Loopback, port);
        });
        var app = builder.Build();
        app.Run(ctx =>
        {
            // What Responses.Send does: a pooled buffer and writer, then one copy into the body.
            var j = OldJsonOut.Rent();
            Json(j.W, ctx.Request.Headers.Authorization.Count > 0);
            var res = ctx.Response;
            res.StatusCode = 200;
            res.ContentType = "application/json";
            var body = j.Written;
            res.ContentLength = body.Length;
            res.BodyWriter.Write(body);
            j.Dispose();
            return Task.CompletedTask;
        });
        app.StartAsync().GetAwaiter().GetResult();
        return new RunningServer { EndPoint = new IPEndPoint(IPAddress.Loopback, port), _stop = () =>
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult(); // or its host outlives the case in --inProcess runs
        } };
    }

    static int FreePort()
    {
        using var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)l.LocalEndPoint!).Port;
    }

    public void Dispose() => _stop();
}

/// <summary>
/// Settings the contest server ran with (thread-pool spinning off, a 64 MB gen0 budget; server GC comes from the
/// benchmarks' project file), plus the columns the benchmark processes leave in side files.
/// </summary>
public sealed class HttpConfig : ManualConfig
{
    public HttpConfig()
    {
        // A mutator: it adds these to whatever job runs (--job short while developing), not a job of its own.
        AddJob(Job.Default
            .WithEnvironmentVariable("DOTNET_ThreadPool_UnfairSemaphoreSpinLimit", "0")
            .WithEnvironmentVariable("DOTNET_GCgen0size", "0x4000000")
            .AsMutator());
        AddColumn(new RequestsPerSecond(), new SideColumn("cpu", "CPU/req", "process CPU time per request, µs (client included)", "F2"),
            new SideColumn("ws", "WS/idle", "working set per idle connection, bytes (client's sockets included)", "N0"),
            new SideColumn("heap", "Heap/idle", "GC heap per idle connection, bytes (client's sockets included)", "N0"));
    }
}

/// <summary>Numbers a benchmark process measured itself, passed to the host process through files.</summary>
public static class SideResults
{
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "codefusion-http-bench");

    public static void Write(string key, params (string Name, double Value)[] values)
    {
        Directory.CreateDirectory(Dir);
        foreach (var (name, value) in values)
            File.WriteAllText(Path.Combine(Dir, $"{key}.{name}"), value.ToString("R", CultureInfo.InvariantCulture));
    }

    public static double? Read(string key, string name)
    {
        string path = Path.Combine(Dir, $"{key}.{name}");
        return File.Exists(path) ? double.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture) : null;
    }

    public static string Key(BenchmarkCase c)
    {
        if (c.Descriptor.Type == typeof(MinimalApiBenchmarks))
            return $"api-{c.Parameters["Layer"]}-{c.Parameters["Endpoint"]}-{c.Parameters["Connections"]}";
        string server = c.Parameters["Server"]?.ToString() ?? "";
        return c.Descriptor.Type == typeof(HttpIdleBenchmarks) ? $"idle-{server}" : $"{server}-{c.Parameters["Connections"]}";
    }
}

public sealed class SideColumn(string name, string header, string legend, string format) : IColumn
{
    public string Id => "side-" + name;
    public string ColumnName => header;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => legend;
    public bool IsAvailable(Summary summary) => summary.BenchmarksCases.Any(c => SideResults.Read(SideResults.Key(c), name) is not null);
    public bool IsDefault(Summary summary, BenchmarkCase c) => false;
    public string GetValue(Summary summary, BenchmarkCase c) => GetValue(summary, c, SummaryStyle.Default);
    public string GetValue(Summary summary, BenchmarkCase c, SummaryStyle style) =>
        SideResults.Read(SideResults.Key(c), name)?.ToString(format, CultureInfo.InvariantCulture) ?? "-";
}

public sealed class RequestsPerSecond : IColumn
{
    public string Id => "req-per-s";
    public string ColumnName => "Req/s";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 1;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "requests per second, from the mean time per request";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase c) => false;
    public string GetValue(Summary summary, BenchmarkCase c) => GetValue(summary, c, SummaryStyle.Default);
    public string GetValue(Summary summary, BenchmarkCase c, SummaryStyle style)
    {
        var report = summary[c];
        if (c.Descriptor.Type != typeof(HttpServerBenchmarks) && c.Descriptor.Type != typeof(MinimalApiBenchmarks) || report?.ResultStatistics is not { } s) return "-";
        return (1e9 / s.Mean).ToString("N0", CultureInfo.InvariantCulture);
    }
}
