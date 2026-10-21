using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace CodeFusion.Http;

/// <summary>Whether an endpoint reads the Authorization header, and whether it must hold a valid token.</summary>
public enum AuthMode : byte
{
    /// <summary>The header isn't read.</summary>
    None,
    /// <summary>No header is an anonymous viewer; a header that isn't a valid token is a 401.</summary>
    Optional,
    /// <summary>Anything but a valid token is a 401.</summary>
    Required,
}

/// <summary>
/// One mapped handler, as the endpoint generator compiles it: binding its parameters, calling it and sending its
/// reply, with nothing looked up per request. Made once per Map call.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class Endpoint
{
    /// <summary>How the dispatcher checks the Authorization header before <see cref="Invoke"/>.</summary>
    public AuthMode Auth { get; internal set; }

    /// <summary>Called once when the endpoint is mapped: resolves its route values, services and auth.</summary>
    public abstract void Initialize(EndpointSetup setup);

    /// <summary>Handles a request the router matched to this endpoint.</summary>
    public abstract ValueTask Invoke(RequestContext context);
}

/// <summary>What an endpoint resolves once, when it is mapped. Anything missing throws then, not per request.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EndpointSetup
{
    readonly HttpApp _app;
    readonly RouteTemplate _template;
    readonly Endpoint _endpoint;
    readonly string _method;

    internal EndpointSetup(HttpApp app, string method, RouteTemplate template, Endpoint endpoint)
    {
        _app = app;
        _method = method;
        _template = template;
        _endpoint = endpoint;
    }

    /// <summary>The index of the route value <c>{<paramref name="name"/>}</c> (ignoring case).</summary>
    public int RouteValue(string name)
    {
        int i = _template.IndexOf(name);
        if (i < 0) throw Fail($"has no {{{name}}} for the handler's parameter '{name}'");
        return i;
    }

    /// <summary>The singleton registered as <typeparamref name="T"/>.</summary>
    public T Singleton<T>() where T : class =>
        _app.TryGetSingleton(typeof(T), out object? s) ? (T)s
        : throw Fail($"needs a {typeof(T)}, which isn't registered: AddSingleton or AddPerConnection it on the builder");

    /// <summary>The singleton registered as <typeparamref name="T"/>, or (with <paramref name="slot"/> at least 0)
    /// the per-connection slot of one.</summary>
    public T? Service<T>(out int slot) where T : class, IRequestScoped
    {
        if (_app.TryGetSingleton(typeof(T), out object? s))
        {
            slot = -1;
            return (T)s;
        }
        slot = _app.PerConnectionSlot(typeof(T));
        if (slot < 0) throw Fail($"needs a {typeof(T)}, which isn't registered: AddSingleton or AddPerConnection it on the builder");
        return null;
    }

    /// <summary>The handler takes a <see cref="Viewer"/>: the header is read (optional unless marked required).</summary>
    public void UsesViewer()
    {
        if (!_app.HasJwt) throw Fail("takes a Viewer: call builder.AddJwtAuth(secret) first");
        if (_endpoint.Auth == AuthMode.None) _endpoint.Auth = AuthMode.Optional;
    }

    InvalidOperationException Fail(string what) => new($"{_method} {_template}: the handler {what}");
}

/// <summary>A mapped endpoint, to set its auth: <c>app.MapGet(...).AuthRequired()</c>.</summary>
public sealed class EndpointBuilder
{
    readonly Endpoint _endpoint;
    readonly HttpApp _app;

    internal EndpointBuilder(HttpApp app, Endpoint endpoint)
    {
        _app = app;
        _endpoint = endpoint;
    }

    internal Endpoint Endpoint => _endpoint;

    /// <summary>Requests without a valid token (<c>Authorization: Token &lt;jwt&gt;</c>, or the scheme the app set) get a
    /// 401.</summary>
    public EndpointBuilder AuthRequired() => Set(AuthMode.Required);

    /// <summary>A request without the header is anonymous (its <see cref="Viewer"/> is 0); one with a header that
    /// isn't a valid token gets a 401.</summary>
    public EndpointBuilder AuthOptional() => Set(AuthMode.Optional);

    EndpointBuilder Set(AuthMode mode)
    {
        if (!_app.HasJwt) throw new InvalidOperationException("call builder.AddJwtAuth(secret) before marking endpoints' auth");
        _app.ThrowIfStarted();
        _endpoint.Auth = mode;
        return this;
    }
}

/// <summary>Where endpoints are mapped: the app, or a group of it under a prefix.</summary>
public interface IEndpointRouteBuilder
{
    /// <summary>A group whose routes start with <paramref name="prefix"/>, such as <c>"/api"</c>.</summary>
    RouteGroup MapGroup(string prefix);

    /// <summary>Maps a compiled endpoint; the generated Map interceptors call it.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    EndpointBuilder Map(RequestMethod method, string pattern, Endpoint endpoint);
}

/// <summary>Routes under a prefix: <c>app.MapGroup("/api").MapGet("/tags", ...)</c> maps GET /api/tags.</summary>
public sealed class RouteGroup : IEndpointRouteBuilder
{
    readonly HttpApp _app;
    readonly RouteTemplate _prefix;

    internal RouteGroup(HttpApp app, RouteTemplate prefix)
    {
        _app = app;
        _prefix = prefix;
    }

    /// <summary>The prefix, as the templates it joins.</summary>
    public string Prefix => _prefix.Text;

    /// <inheritdoc/>
    public RouteGroup MapGroup(string prefix) => new(_app, RouteTemplate.Parse(prefix).Under(_prefix));

    /// <inheritdoc/>
    public EndpointBuilder Map(RequestMethod method, string pattern, Endpoint endpoint) =>
        _app.Map(method, RouteTemplate.Parse(pattern).Under(_prefix), endpoint);
}
