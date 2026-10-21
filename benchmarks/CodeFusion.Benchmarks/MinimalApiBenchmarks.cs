using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using CodeFusion.Http;

namespace CodeFusion.Benchmarks;

public enum ApiLayer
{
    /// <summary>The raw HttpServer with a handler written by hand, as the contest server's endpoints are.</summary>
    HandWritten,
    /// <summary>HttpApp with the same endpoints as minimal-API handlers, compiled by the endpoint generator.</summary>
    Generated,
}

public enum ApiEndpoint
{
    /// <summary>GET /api/articles/{slug}?tag=&amp;limit= with a token: a route value, the query, the viewer, JSON.</summary>
    Article,
    /// <summary>POST /api/users/login: a JSON body read over its bytes, a token issued.</summary>
    Login,
    /// <summary>GET /api/wait/{n}: an async handler whose wait completes on a thread-pool thread.</summary>
    Wait,
}

/// <summary>
/// The generated layer against the same endpoints written by hand on the raw server: both answer the same bytes
/// over loopback keep-alive connections, driven by <see cref="HttpLoadClient"/>. One operation is one request. They
/// should match within the noise in time and CPU per request, and both allocate nothing.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(HttpConfig))]
public class MinimalApiBenchmarks
{
    public const int RequestsPerInvoke = 8192;
    const string Secret = "benchmark-secret-of-at-least-32-bytes";

    [Params(ApiLayer.HandWritten, ApiLayer.Generated)]
    public ApiLayer Layer { get; set; }

    [Params(ApiEndpoint.Article, ApiEndpoint.Login, ApiEndpoint.Wait)]
    public ApiEndpoint Endpoint { get; set; }

    [Params(1, 64)]
    public int Connections { get; set; }

    Func<Task>? _stop;
    HttpLoadClient? _client;
    TimeSpan _cpu;
    long _requests;

    [GlobalSetup]
    public void Setup()
    {
        IPEndPoint ep;
        if (Layer == ApiLayer.Generated)
        {
            HttpApp app = GeneratedApi.Build(Secret);
            app.Start();
            ep = app.LocalEndPoint;
            _stop = app.StopAsync;
        }
        else
        {
            var server = HandWrittenApi.Start(Secret);
            ep = server.LocalEndPoint;
            _stop = () => server.StopAsync(TimeSpan.FromSeconds(5));
        }
        _client = new HttpLoadClient(ep, Connections, Request(Endpoint));
        for (int i = 0; i < 5; i++) Requests();
        _cpu = Process.GetCurrentProcess().TotalProcessorTime;
        _requests = _client.Requests;
    }

    [Benchmark(OperationsPerInvoke = RequestsPerInvoke)]
    public void Requests() => _client!.Run(RequestsPerInvoke / Connections);

    [GlobalCleanup]
    public void Cleanup()
    {
        double cpuUs = (Process.GetCurrentProcess().TotalProcessorTime - _cpu).TotalMicroseconds / (_client!.Requests - _requests);
        SideResults.Write($"api-{Layer}-{Endpoint}-{Connections}", ("cpu", cpuUs));
        _client.Dispose();
        _stop!().GetAwaiter().GetResult();
    }

    static string Token(long user)
    {
        Span<byte> b = stackalloc byte[Jwt.MaxIssuedBytes];
        return Encoding.ASCII.GetString(b[..new Jwt(Secret).Issue(user, b)]);
    }

    public static byte[] Request(ApiEndpoint endpoint)
    {
        const string Head = "Host: 127.0.0.1:3000\r\nUser-Agent: k6/0.50\r\nAccept: application/json\r\n";
        string body = """{"user":{"email":"jake@jake.jake","password":"jakejake"}}""";
        return Encoding.ASCII.GetBytes(endpoint switch
        {
            ApiEndpoint.Article => $"GET /api/articles/how-to-train-your-dragon?tag=dragons&limit=7 HTTP/1.1\r\n{Head}Authorization: Token {Token(12345)}\r\n\r\n",
            ApiEndpoint.Login => $"POST /api/users/login HTTP/1.1\r\n{Head}Content-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}",
            _ => $"GET /api/wait/42 HTTP/1.1\r\n{Head}\r\n",
        });
    }
}

[JsonEnvelope("article")]
public readonly record struct BenchArticle(Utf8 Slug, Utf8 Tag, long Limit, bool Favorited, long Viewer);

[JsonEnvelope("user")]
public readonly record struct BenchUser(Utf8 Email, Utf8 Token);

public readonly record struct BenchCount(int N);

[FromQuery]
public partial struct BenchQuery
{
    public Utf8 Tag;
    [DefaultValue(20), Range(1, 100)] public long Limit;
    static readonly ErrorReply Invalid = new(422, """{"errors":{"body":["bad page"]}}"""u8);
}

[JsonEnvelope("user")]
public partial struct BenchLogin
{
    public Utf8 Email;
    public Utf8 Password;
}

/// <summary>A dependency that answers from a thread-pool thread, as a database loop does, allocating nothing: a
/// pooled async method whose wait goes on through a work item made once.</summary>
public sealed class BenchTicker : IRequestScoped, IThreadPoolWorkItem
{
    Action? _next;
    long _ticks;

    public void Bind(IResponse response) { }

    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    public async ValueTask<long> NextAsync()
    {
        await new Hop(this);
        return ++_ticks;
    }

    void IThreadPoolWorkItem.Execute()
    {
        Action next = _next!;
        _next = null;
        next();
    }

    readonly struct Hop(BenchTicker ticker) : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        public Hop GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            ticker._next = continuation;
            ThreadPool.UnsafeQueueUserWorkItem(ticker, preferLocal: false);
        }
    }
}

/// <summary>The three endpoints as minimal-API handlers.</summary>
public static class GeneratedApi
{
    static readonly ErrorReply WrongLogin = new(401, """{"errors":{"body":["invalid email or password"]}}"""u8);

    public static HttpApp Build(string secret)
    {
        var builder = HttpApp.CreateBuilder([]);
        builder.Options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        builder.Errors.Log = null;
        builder.AddJwtAuth(secret);
        builder.AddPerConnection(() => new BenchTicker());
        HttpApp app = builder.Build();
        RouteGroup api = app.MapGroup("/api");
        api.MapGet("/articles/{slug}", static (Utf8 slug, BenchQuery q, Viewer viewer) =>
            Reply.Ok(new BenchArticle(slug, q.Tag, q.Limit, viewer.IsAuthenticated, viewer.Id)));
        api.MapPost("/users/login", static (BenchLogin body, Jwt jwt) =>
            body.Password == "jakejake"u8 ? Reply.Ok(new BenchUser(body.Email, jwt.Issue(7))) : Reply.Unauthorized(WrongLogin));
        api.MapGet("/wait/{n}", static async (int n, BenchTicker ticker) =>
        {
            await ticker.NextAsync();
            return Reply.Ok(new BenchCount(n));
        });
        return app;
    }
}

/// <summary>
/// The same three endpoints written by hand on the raw server, the way the contest server's are: a switch on the
/// path, a JSON writer per connection, the query and the body read over the request's bytes, and a pooled async
/// method for the endpoint that waits.
/// </summary>
public static class HandWrittenApi
{
    static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };
    static readonly BufferPool Chunks = new(4096, 4096);
    static readonly ErrorReply Invalid = new(422, """{"errors":{"body":["invalid body"]}}"""u8);

    static Jwt _jwt = null!;

    sealed class Connection(HttpExchange x)
    {
        public readonly Utf8JsonWriter W = new(x, JsonOptions);
        public readonly RequestArena Arena = new(Chunks);
        public readonly BenchTicker Ticker = new();
    }

    public static HttpServer Start(string secret)
    {
        _jwt = new Jwt(secret);
        var server = new HttpServer(new HttpServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, Handle);
        server.HandlerDone = static x => (x.State as Connection)?.Arena.Reset();
        server.Start();
        return server;
    }

    static Connection Of(HttpExchange x) => x.State as Connection ?? (Connection)(x.State = new Connection(x));

    static void NotFound(HttpExchange x)
    {
        x.Status = 404;
        """{"error":"not found"}"""u8.CopyTo(x.GetSpan(21));
        x.Advance(21);
    }

    static ValueTask Handle(HttpExchange x)
    {
        ReadOnlySpan<byte> path = x.Path;
        if (x.Method == RequestMethod.Get && path.StartsWith("/api/articles/"u8) && path.Length > 14 && path[14..].IndexOf((byte)'/') < 0)
            return Article(x);
        if (x.Method == RequestMethod.Post && path.SequenceEqual("/api/users/login"u8)) return Login(x);
        if (x.Method == RequestMethod.Get && path.StartsWith("/api/wait/"u8)) return Wait(x);
        NotFound(x);
        return default;
    }

    static ValueTask Article(HttpExchange x)
    {
        long viewer = _jwt.Viewer(x.HasAuthorization, x.Authorization);
        if (viewer < 0)
        {
            x.Status = 401;
            return default;
        }
        Connection c = Of(x);
        var q = new QuerySource(x.RequestBuffer, x.QueryOffset, x.QueryLength, c.Arena);
        q.TryText("tag"u8, out Utf8 tag);
        QueryNumber n = q.TryNumber("limit"u8, 1, 100, out long limit);
        if (n == QueryNumber.Invalid)
        {
            x.Status = 422;
            return default;
        }
        if (n == QueryNumber.Absent) limit = 20;
        Utf8JsonWriter w = c.W;
        w.Reset();
        w.WriteStartObject();
        w.WriteStartObject("article"u8);
        w.WriteString("slug"u8, x.Path[14..]);
        w.WriteString("tag"u8, tag.Span);
        w.WriteNumber("limit"u8, limit);
        w.WriteBoolean("favorited"u8, viewer > 0);
        w.WriteNumber("viewer"u8, viewer);
        w.WriteEndObject();
        w.WriteEndObject();
        w.Flush();
        return default;
    }

    static ValueTask Login(HttpExchange x)
    {
        Connection c = Of(x);
        var source = new BodySource(x.RequestBuffer, x.RequestBodyOffset, x.RequestBodyLength, c.Arena, Invalid);
        if (BenchLogin.__BindBody(source, out BenchLogin body) is { } error)
        {
            x.Status = 422;
            error.Body.CopyTo(x.GetSpan(error.Body.Length));
            x.Advance(error.Body.Length);
            return default;
        }
        if (body.Password != "jakejake"u8)
        {
            x.Status = 401;
            return default;
        }
        Span<byte> token = stackalloc byte[Jwt.MaxIssuedBytes];
        int t = _jwt.Issue(7, token);
        Utf8JsonWriter w = c.W;
        w.Reset();
        w.WriteStartObject();
        w.WriteStartObject("user"u8);
        w.WriteString("email"u8, body.Email.Span);
        w.WriteString("token"u8, token[..t]);
        w.WriteEndObject();
        w.WriteEndObject();
        w.Flush();
        return default;
    }

    static ValueTask Wait(HttpExchange x)
    {
        if (!Query.TryParseUint(x.Path[10..], out long n) || n > int.MaxValue)
        {
            NotFound(x);
            return default;
        }
        return WaitAsync(x, Of(x), (int)n);
    }

    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]
    static async ValueTask WaitAsync(HttpExchange x, Connection c, int n)
    {
        await c.Ticker.NextAsync();
        Utf8JsonWriter w = c.W;
        w.Reset();
        w.WriteStartObject();
        w.WriteNumber("n"u8, n);
        w.WriteEndObject();
        w.Flush();
    }
}
