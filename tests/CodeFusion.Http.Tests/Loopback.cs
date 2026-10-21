using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>A server on a free loopback port, with a fast heartbeat so timeouts can be tested in milliseconds.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    public readonly HttpServer Server;
    public readonly IPEndPoint EndPoint;
    public readonly List<Exception> Errors = [];

    TestServer(HttpServer server)
    {
        Server = server;
        server.Start();
        EndPoint = server.LocalEndPoint;
    }

    public static TestServer Start(HttpHandler handler, Action<HttpServerOptions>? configure = null)
    {
        var o = TestTransport.Apply(new HttpServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
        });
        configure?.Invoke(o);
        TestServer? t = null;
        o.OnHandlerError ??= (_, e) => { lock (t!.Errors) t.Errors.Add(e); };
        t = new TestServer(new HttpServer(o, handler));
        return t;
    }

    public Task<Client> ConnectAsync() => Client.ConnectAsync(EndPoint);

    /// <summary><see cref="Client.ConnectAndSend"/>: for a first request that must beat a short request timeout.</summary>
    public Client ConnectAndSend(string request) => Client.ConnectAndSend(EndPoint, request);

    /// <summary>Waits until the server's open connections reach <paramref name="n"/>.</summary>
    public async Task ConnectionsReach(int n, int timeoutMs = 5000)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Server.Connections != n)
        {
            if (Environment.TickCount64 > until) throw new TimeoutException($"{Server.Connections} connections, expected {n}");
            await Task.Delay(5);
        }
    }

    /// <summary>Waits until <paramref name="done"/>, failing the test with <paramref name="what"/> past the time.</summary>
    public static async Task Until(Func<bool> done, int timeoutMs, Func<string> what)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > until) Assert.Fail(what());
            await Task.Delay(5);
        }
    }

    /// <summary>Stops the server, then fails the test if it didn't stop clean (<see cref="ServerChecks"/>): a
    /// connection loop that threw is only logged ("http connection failed"), so the test would pass, and on a loaded
    /// machine the log line can land in a later allocation test instead and fail that one.</summary>
    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(TimeSpan.FromSeconds(1));
        await ServerChecks.AssertStoppedCleanAsync(Server);
    }

    /// <summary>Answers with what it was asked, as text: method, path, query, Authorization and body.</summary>
    public static ValueTask Echo(HttpExchange x)
    {
        string auth = x.HasAuthorization ? "[" + Encoding.Latin1.GetString(x.Authorization) + "]" : "none";
        Write(x, $"{Encoding.ASCII.GetString(x.MethodBytes)} {Encoding.UTF8.GetString(x.Path)} q={Encoding.ASCII.GetString(x.Query)} auth={auth} body={Encoding.UTF8.GetString(x.Body.Span)}");
        return default;
    }

    public static void Write(HttpExchange x, string text) => Encoding.UTF8.GetBytes(text, x);
}

internal sealed record Response(int Status, string StatusLine, Dictionary<string, List<string>> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? string.Join(",", v) : null;
}

/// <summary>A raw HTTP/1.1 client: sends exactly the bytes given and parses responses by Content-Length.</summary>
internal sealed class Client : IDisposable
{
    public readonly Socket Socket;
    byte[] _buf = new byte[4096];
    int _start, _end;

    Client(Socket s) => Socket = s;

    public static async Task<Client> ConnectAsync(IPEndPoint ep)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await s.ConnectAsync(ep);
        return new Client(s);
    }

    /// <summary>Connects and sends <paramref name="request"/> on this thread, with no wait between. The server's
    /// deadline for a first request starts as it takes the connection; a send after an await waits for the thread pool,
    /// which a loaded runner can keep busy past a short request timeout (CI, 300 ms).</summary>
    public static Client ConnectAndSend(IPEndPoint ep, string request)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            s.Connect(ep);
            byte[] b = Encoding.Latin1.GetBytes(request);
            for (int sent = 0; sent < b.Length;) sent += s.Send(b.AsSpan(sent));
        }
        catch
        {
            s.Dispose();
            throw;
        }
        return new Client(s);
    }

    public Task SendAsync(string s) => SendAsync(Encoding.Latin1.GetBytes(s));

    public async Task SendAsync(byte[] b)
    {
        for (int sent = 0; sent < b.Length;) sent += await Socket.SendAsync(b.AsMemory(sent), SocketFlags.None);
    }

    /// <summary>The next response; HEAD responses carry a Content-Length but no body.</summary>
    public async Task<Response> ReadAsync(bool head = false, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        int headEnd;
        while ((headEnd = _buf.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8)) < 0) await Fill(cts.Token);
        string text = Encoding.Latin1.GetString(_buf, _start, headEnd);
        string[] lines = text.Split("\r\n");
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines[1..])
        {
            int colon = line.IndexOf(':');
            string name = line[..colon];
            if (!headers.TryGetValue(name, out var list)) headers[name] = list = [];
            list.Add(line[(colon + 1)..].Trim());
        }
        // Relative to _start: Fill may move the buffered bytes to the front of a bigger buffer.
        int bodyOffset = headEnd + 4;
        int length = head || !headers.TryGetValue("Content-Length", out var cl) ? 0 : int.Parse(cl[0]);
        while (_end - _start - bodyOffset < length) await Fill(cts.Token);
        byte[] body = _buf[(_start + bodyOffset)..(_start + bodyOffset + length)];
        _start += bodyOffset + length;
        return new Response(int.Parse(lines[0].Split(' ')[1]), lines[0], headers, body);
    }

    /// <summary>True when the server closes the connection (FIN or reset) within the time, with nothing more sent.</summary>
    public async Task<bool> ClosedAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                if (_end == _buf.Length) return false;
                int n = await Socket.ReceiveAsync(_buf.AsMemory(_end), SocketFlags.None, cts.Token);
                if (n == 0) return _end == _start;
                _end += n;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return _end == _start;
        }
    }

    /// <summary>True when the server closes the connection with a FIN, not a reset, within the time, with nothing
    /// more sent.</summary>
    public async Task<bool> EndsWithFinAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await Socket.ReceiveAsync(_buf.AsMemory(_end), SocketFlags.None, cts.Token) == 0 && _end == _start;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Bytes that arrived but weren't read as a response.</summary>
    public int Unread => _end - _start;

    async Task Fill(CancellationToken ct)
    {
        if (_start > 0 && _start == _end) _start = _end = 0;
        if (_end == _buf.Length)
        {
            var bigger = new byte[_buf.Length * 2];
            _buf.AsSpan(_start, _end - _start).CopyTo(bigger);
            _end -= _start;
            _start = 0;
            _buf = bigger;
        }
        int n = await Socket.ReceiveAsync(_buf.AsMemory(_end), SocketFlags.None, ct);
        if (n == 0) throw new EndOfStreamException("the server closed the connection");
        _end += n;
    }

    /// <summary>Closes with a reset instead of a FIN.</summary>
    public void Reset()
    {
        Socket.LingerState = new LingerOption(true, 0);
        Socket.Dispose();
    }

    public void Dispose() => Socket.Dispose();
}
