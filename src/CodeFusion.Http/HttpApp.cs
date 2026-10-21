using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace CodeFusion.Http;

/// <summary>
/// An HTTP app in the shape of ASP.NET's minimal APIs, compiled by the endpoint generator into code that allocates
/// nothing per request:
/// <code>
/// var builder = HttpApp.CreateBuilder(args);
/// builder.AddJwtAuth(builder.Setting("JWT_SECRET")!);
/// var app = builder.Build();
/// var api = app.MapGroup("/api");
/// api.MapGet("/profiles/{username}", static (Utf8 username, Viewer viewer) => Reply.Ok(new Profile(username)))
///    .AuthOptional();
/// app.Run();
/// </code>
/// Map every endpoint before <see cref="Start"/> or <see cref="Run"/>; a request no route matches gets
/// <see cref="HttpAppErrors.NotFound"/>.
/// </summary>
public sealed class HttpApp : IEndpointRouteBuilder, IAsyncDisposable
{
    readonly HttpServerOptions _options;
    readonly TimeSpan _shutdownTimeout;
    readonly Dictionary<Type, object> _singletons;
    readonly Dictionary<Type, int> _perConnectionSlots;
    readonly Func<object>[] _perConnection;
    readonly Action[] _onStopping;
    readonly Jwt? _jwt;
    readonly Router _router = new();
    HttpServer? _server;
    bool _stopped;

    internal HttpApp(HttpAppBuilder builder)
    {
        _options = builder.Options;
        _shutdownTimeout = builder.ShutdownTimeout;
        Errors = builder.Errors;
        _singletons = new Dictionary<Type, object>(builder.Singletons);
        _perConnectionSlots = new Dictionary<Type, int>();
        _perConnection = [.. builder.PerConnection.Values];
        int slot = 0;
        foreach (Type t in builder.PerConnection.Keys) _perConnectionSlots[t] = slot++;
        _onStopping = [.. builder.Stopping];
        _jwt = builder.JwtPrototype;
        ArenaChunks = new BufferPool(4096, _options.MaxPooledBuffers);
    }

    /// <summary>A builder with the settings in the environment: HOST (default 127.0.0.1; "localhost" is the loopback
    /// address, and a name that isn't an IP address listens on every address) and PORT (default 3000).</summary>
    public static HttpAppBuilder CreateBuilder(string[] args) => new(args);

    /// <summary>The bodies of the app's own error responses.</summary>
    public HttpAppErrors Errors { get; }

    internal BufferPool ArenaChunks { get; }
    /// <summary>The server, once started: tests check it stopped clean.</summary>
    internal HttpServer? Server => _server;
    internal Router Router => _router;
    internal int PerConnectionCount => _perConnection.Length;
    internal int MaxRouteValues => _router.MaxParameters;
    internal bool HasJwt => _jwt is not null;

    internal bool TryGetSingleton(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? service) =>
        _singletons.TryGetValue(type, out service);

    internal int PerConnectionSlot(Type type) => _perConnectionSlots.TryGetValue(type, out int slot) ? slot : -1;

    internal object CreatePerConnection(int slot) => _perConnection[slot]();

    /// <inheritdoc/>
    public RouteGroup MapGroup(string prefix) => new(this, RouteTemplate.Parse(prefix));

    /// <inheritdoc/>
    public EndpointBuilder Map(RequestMethod method, string pattern, Endpoint endpoint) => Map(method, RouteTemplate.Parse(pattern), endpoint);

    internal EndpointBuilder Map(RequestMethod method, RouteTemplate template, Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ThrowIfStarted();
        int verb = Router.Verb(method);
        endpoint.Initialize(new EndpointSetup(this, method.ToString().ToUpperInvariant(), template, endpoint));
        _router.Add(verb, template, endpoint);
        return new EndpointBuilder(this, endpoint);
    }

    internal void ThrowIfStarted()
    {
        if (_server is not null) throw new InvalidOperationException("map endpoints before the app starts");
    }

    /// <summary>Where it listens, with the real port when the options asked for port 0.</summary>
    public IPEndPoint LocalEndPoint => (_server ?? throw new InvalidOperationException("not started")).LocalEndPoint;

    /// <summary>Starts listening; the routes are fixed from now on.</summary>
    public void Start()
    {
        if (_server is not null) throw new InvalidOperationException("already started");
        _options.RejectBody = Errors.Rejected;
        _options.OnHandlerError = HandlerFailed;
        var server = new HttpServer(_options, Dispatch);
        server.HandlerDone = static x => (x.State as RequestContext)?.HandlerDone();
        server.ConnectionIdle = static x => (x.State as RequestContext)?.ConnectionIdle();
        _server = server;
        server.Start();
    }

    /// <summary>Starts if needed and runs until SIGTERM or SIGINT; then stops taking requests, lets those in flight
    /// finish within the builder's ShutdownTimeout, and runs the OnStopping callbacks.</summary>
    public void Run() => RunAsync().GetAwaiter().GetResult();

    /// <summary><see cref="Run"/>, awaitable.</summary>
    public async Task RunAsync()
    {
        if (_server is null) Start();
        await _server!.RunAsync(_shutdownTimeout).ConfigureAwait(false);
        Stopped();
    }

    /// <summary>Stops as a SIGTERM would.</summary>
    public async Task StopAsync()
    {
        if (_server is not null) await _server.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        Stopped();
    }

    void Stopped()
    {
        if (_stopped) return;
        _stopped = true;
        List<Exception>? failed = null;
        for (int i = _onStopping.Length - 1; i >= 0; i--)
        {
            try
            {
                _onStopping[i]();
            }
            catch (Exception e)
            {
                (failed ??= []).Add(e);
            }
        }
        if (failed is not null) throw new AggregateException("an OnStopping callback failed", failed);
    }

    /// <summary>Stops as <see cref="StopAsync"/> does, then lets go of the server.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            // the stop above already ran, so this only frees what the server holds
            if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        }
    }

    ValueTask Dispatch(HttpExchange x)
    {
        // State is the app's (RequestContext.Exchange says so); a type check, not a cast, in case a handler set it
        RequestContext c = x.State as RequestContext ?? NewContext(x);
        Endpoint? e = _router.Match(Router.Verb(x.Method, x.MethodBytes), x.Path, c.RouteValues);
        if (e is null) return Errors.NotFound.WriteAsync(c);
        if (e.Auth != AuthMode.None)
        {
            long viewer = _jwt!.Viewer(x.HasAuthorization, x.Authorization);
            if (viewer < 0 || viewer == 0 && e.Auth == AuthMode.Required) return Errors.Unauthorized.WriteAsync(c);
            c.ViewerId = viewer;
        }
        return e.Invoke(c);
    }

    RequestContext NewContext(HttpExchange x)
    {
        var c = new RequestContext(this, x);
        x.State = c;
        return c;
    }

    void HandlerFailed(HttpExchange x, Exception e)
    {
        if (x.State is RequestContext c)
        {
            // ErrorReply.WriteAsync's steps, without a ValueTask to read back
            c.DiscardJson();
            ErrorReply e500 = Errors.Internal;
            c.Reset();
            c.Status = e500.Status;
            c.RetryAfter = e500.RetryAfter;
            c.Body.Write(e500.Body);
        }
        Errors.Log?.Invoke(x, e);
    }
}

/// <summary>Configures an <see cref="HttpApp"/>: its services, auth, error bodies and server options.</summary>
public sealed class HttpAppBuilder : IServiceRegistry
{
    internal readonly Dictionary<Type, object> Singletons = [];
    internal readonly Dictionary<Type, Func<object>> PerConnection = [];
    internal readonly List<Action> Stopping = [];
    internal Jwt? JwtPrototype;
    bool _built;

    internal HttpAppBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string host = Setting("HOST") ?? "127.0.0.1";
        string port = Setting("PORT") ?? "3000";
        if (!int.TryParse(port, out int p) || p is < 0 or > 65535) throw new ArgumentException($"PORT is not a port: '{port}'");
        IPAddress address = host == "localhost" ? IPAddress.Loopback : IPAddress.TryParse(host, out var ip) ? ip : IPAddress.IPv6Any;
        Options = new HttpServerOptions { EndPoint = new IPEndPoint(address, p) };
    }

    /// <summary>The server's options: where it listens (from HOST and PORT) and its limits.</summary>
    public HttpServerOptions Options { get; }

    /// <summary>The bodies of the app's own error responses.</summary>
    public HttpAppErrors Errors { get; } = new();

    /// <summary>How long requests in flight may take to finish once the app stops.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The environment variable <paramref name="name"/>, or null when it is unset or empty.</summary>
    public static string? Setting(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <inheritdoc/>
    public void AddSingleton<TService>(TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        Add(typeof(TService));
        Singletons[typeof(TService)] = instance;
    }

    /// <inheritdoc/>
    public void AddPerConnection<TService>(Func<TService> factory) where TService : class, IRequestScoped
    {
        ArgumentNullException.ThrowIfNull(factory);
        Add(typeof(TService));
        PerConnection[typeof(TService)] = () => factory() ?? throw new InvalidOperationException($"the factory of {typeof(TService)} returned null");
    }

    /// <inheritdoc/>
    public void OnStopping(Action onStopping)
    {
        ArgumentNullException.ThrowIfNull(onStopping);
        ThrowIfBuilt();
        Stopping.Add(onStopping);
    }

    /// <summary>
    /// Verifies <c>Authorization: Token &lt;jwt&gt;</c> headers signed with <paramref name="secret"/> (HS256) for
    /// handlers that take a <see cref="Viewer"/> and endpoints marked with an auth mode, and registers a
    /// <see cref="Jwt"/> per connection for issuing tokens.
    /// </summary>
    public HttpAppBuilder AddJwtAuth(string secret) => AddJwtAuth(secret, TimeSpan.FromDays(30));

    /// <summary><see cref="AddJwtAuth(string)"/>, with tokens issued for <paramref name="lifetime"/> instead of 30 days.</summary>
    public HttpAppBuilder AddJwtAuth(string secret, TimeSpan lifetime) => AddJwtAuth(secret, lifetime, false);

    /// <summary><see cref="AddJwtAuth(string, TimeSpan)"/>, and with <paramref name="allowShortSecret"/> a secret
    /// shorter than <see cref="Jwt.MinSecretBytes"/> too, for a deployment handed a fixed one: see
    /// <see cref="Jwt.Jwt(string, TimeSpan, bool)"/> for the risk.</summary>
    public HttpAppBuilder AddJwtAuth(string secret, TimeSpan lifetime, bool allowShortSecret)
    {
        if (JwtPrototype is not null) throw new InvalidOperationException("AddJwtAuth was called already");
        var jwt = new Jwt(secret, lifetime, allowShortSecret);
        AddPerConnection(jwt.Copy);
        JwtPrototype = jwt;
        return this;
    }

    /// <summary>The app; map its endpoints next.</summary>
    public HttpApp Build()
    {
        ThrowIfBuilt();
        _built = true;
        return new HttpApp(this);
    }

    void Add(Type type)
    {
        ThrowIfBuilt();
        if (Singletons.ContainsKey(type) || PerConnection.ContainsKey(type)) throw new InvalidOperationException($"{type} is registered already");
    }

    void ThrowIfBuilt()
    {
        if (_built) throw new InvalidOperationException("the app is built: register services before Build()");
    }
}

/// <summary>
/// The bodies of the responses the app makes itself, each pre-encoded once. The defaults are
/// <c>{"error":"..."}</c>; set them to the app's own error shape.
/// </summary>
public sealed class HttpAppErrors
{
    /// <summary>No route matches the method and path. 404 by default.</summary>
    public ErrorReply NotFound { get; set; } = Make(404, "not found");

    /// <summary>A missing or invalid token on an endpoint that needs one, or an invalid one on any endpoint that reads
    /// it. 401 by default.</summary>
    public ErrorReply Unauthorized { get; set; } = Make(401, "unauthorized");

    /// <summary>A body that isn't the JSON its struct describes, or a field that struct doesn't name its own reply
    /// for. 422 by default.</summary>
    public ErrorReply InvalidBody { get; set; } = Make(422, "invalid body");

    /// <summary>A handler threw. 500 by default.</summary>
    public ErrorReply Internal { get; set; } = Make(500, "internal error");

    /// <summary>The body of the server's 400 (malformed framing, and 505 for an HTTP version it doesn't speak); its
    /// status is the server's.</summary>
    public ErrorReply BadRequest { get; set; } = Make(400, "bad request");

    /// <summary>The body of the server's 413: a body over the limit.</summary>
    public ErrorReply BodyTooLarge { get; set; } = Make(413, "request body too large");

    /// <summary>The body of the server's 414: a request line over the limit.</summary>
    public ErrorReply UriTooLong { get; set; } = Make(414, "request line too long");

    /// <summary>The body of the server's 431: headers over the limit.</summary>
    public ErrorReply HeadersTooLarge { get; set; } = Make(431, "request headers too large");

    /// <summary>What a handler threw, with its request: by default a line on stderr. Null logs nothing.</summary>
    public Action<HttpExchange, Exception>? Log { get; set; } = static (x, e) =>
        HttpServerOptions.Stderr($"error: {Encoding.ASCII.GetString(x.MethodBytes)} {Encoding.UTF8.GetString(x.Path)}", e);

    internal ReadOnlySpan<byte> Rejected(int status) => status switch
    {
        413 => BodyTooLarge.Body,
        414 => UriTooLong.Body,
        431 => HeadersTooLarge.Body,
        _ => BadRequest.Body,
    };

    static ErrorReply Make(int status, string message) => new(status, Encoding.UTF8.GetBytes($$"""{"error":"{{message}}"}"""));
}
