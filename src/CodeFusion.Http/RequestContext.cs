using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Http;

/// <summary>
/// The <see cref="IResponse"/> of an <see cref="HttpApp"/>'s connection: one per connection, kept with it when it is
/// pooled, so nothing here is made per request. A handler that takes an <see cref="IResponse"/> gets it. Its JSON
/// writer starts over for each response and is flushed when the handler is done; its <see cref="Arena"/> is reset
/// once the response is finished.
/// </summary>
// Pooled with its connection for the server's life: its JSON writer writes into the exchange and holds nothing
// that needs disposing.
#pragma warning disable CA1001
public sealed class RequestContext : IResponse, IValueTaskSource
#pragma warning restore CA1001
{
    static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // escapes only what JSON requires; UTF-8 passes through
        SkipValidation = true,
    };

    readonly HttpApp _app;
    readonly HttpExchange _x;
    readonly Utf8JsonWriter _json;
    readonly RequestArena _arena;
    readonly object?[] _scoped;
    readonly int[] _route;
    readonly int[] _claims;
    ArenaBytes _claimPayload;
    int _generation; // bumped when a request is done, so a Viewer kept past it can tell
    bool _jsonInUse;
    long _viewer;

    // A handler that waited: its pending reply, then the reply's own write if that waits too.
    InlineValueTaskSourceCore<bool> _done;
    object? _pendingBox;
    short _pendingToken;
    ValueTask _writing;
    readonly Action _onWritten;

    internal RequestContext(HttpApp app, HttpExchange x)
    {
        _app = app;
        _x = x;
        _json = new Utf8JsonWriter(x, JsonOptions);
        _arena = new RequestArena(app.ArenaChunks);
        _scoped = new object?[app.PerConnectionCount];
        _route = new int[2 * Math.Max(1, app.MaxRouteValues)];
        _claims = new int[2 * (app.Jwt?.ClaimCount ?? 0)];
        _claims.AsSpan().Fill(-1); // absent until a check says otherwise
        _onWritten = OnWritten;
    }

    /// <summary>The connection and its request, for what the generated code doesn't cover. Its
    /// <see cref="HttpExchange.State"/> is this context: leave it as it is.</summary>
    public HttpExchange Exchange => _x;

    /// <inheritdoc/>
    public int Status
    {
        get => _x.Status;
        set => _x.Status = value;
    }

    /// <inheritdoc/>
    public int RetryAfter
    {
        get => _x.RetryAfter;
        set => _x.RetryAfter = value;
    }

    /// <inheritdoc/>
    public MediaType? ContentType
    {
        get => _x.ContentType;
        set => _x.ContentType = value;
    }

    /// <inheritdoc/>
    public Utf8JsonWriter Json
    {
        get
        {
            if (!_jsonInUse)
            {
                _json.Reset(); // a new body: no comma after the last response's root, no bytes it didn't flush
                _jsonInUse = true;
            }
            return _json;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Flushes what the JSON writer holds first, so the two can take turns.</remarks>
    public IBufferWriter<byte> Body
    {
        get
        {
            if (_jsonInUse) _json.Flush();
            return _x;
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_jsonInUse)
        {
            _json.Reset();
            _jsonInUse = false;
        }
        _x.ResetResponse();
    }

    /// <inheritdoc/>
    public IRequestArena Arena => _arena;

    /// <inheritdoc/>
    public CancellationToken Aborted => _x.Aborted;

    // ---- What the generated endpoints call ----

    /// <summary>The app's error replies.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public HttpAppErrors Errors => _app.Errors;

    /// <summary>The request's viewer: the id the dispatcher verified, and the token it presented.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Viewer Viewer => _viewer > 0 ? new Viewer(_viewer, Token(), this, _generation) : new Viewer(0, default, this, _generation);

    internal long ViewerId
    {
        set => _viewer = value;
    }

    /// <summary>Where the dispatcher's check put each declared claim: start and length in <see cref="ClaimPayload"/>,
    /// start -1 when absent.</summary>
    internal Span<int> ClaimRanges => _claims;

    /// <summary>The token's decoded payload in the arena, which <see cref="ClaimRanges"/> points into.</summary>
    internal ArenaBytes ClaimPayload
    {
        set => _claimPayload = value;
    }

    internal bool TryGetClaim(ReadOnlySpan<byte> name, bool authenticated, int generation, out Utf8 value)
    {
        value = default;
        int i = _app.Jwt?.ClaimIndex(name) ?? -1;
        if (i < 0) throw new ArgumentException($"the claim '{System.Text.Encoding.UTF8.GetString(name)}' isn't declared on JwtOptions.Claims", nameof(name));
        // A seqlock: the place is read between two reads of the generation, and taken only if neither saw the request
        // end, from whatever thread the Viewer is read on. The fence keeps the place's reads before the second check.
        if (Volatile.Read(ref _generation) != generation) throw Stale();
        int start = _claims[2 * i], length = _claims[2 * i + 1];
        ArenaBytes payload = _claimPayload;
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _generation) != generation) throw Stale();
        if (!authenticated || start < 0) return false;
        value = payload.Text(start, length);
        return true;
    }

    static InvalidOperationException Stale() =>
        new("the Viewer's request is over: its claims went with it (copy a claim while the request is handled to keep it)");

    Utf8 Token()
    {
        ReadOnlySpan<byte> header = _x.Authorization;
        _ = _app.Jwt!.TryToken(header, out ReadOnlySpan<byte> token); // the dispatcher verified it
        header.Overlaps(token, out int offset);
        return new Utf8(_x.RequestBuffer, _x.AuthorizationOffset + offset, token.Length);
    }

    internal Span<int> RouteValues => _route;

    /// <summary>The route value the router found at <paramref name="index"/> (in the order of the template's
    /// parameters), as the path's decoded bytes.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Utf8 RouteText(int index) => new(_x.RequestBuffer, _x.PathOffset + _route[2 * index], _route[2 * index + 1]);

    /// <summary>The route value at <paramref name="index"/> as ASCII digits that fit an Int64.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryRouteInt64(int index, out long value) =>
        CodeFusion.Http.Query.TryParseUint(_x.RequestBuffer.AsSpan(_x.PathOffset + _route[2 * index], _route[2 * index + 1]), out value);

    /// <summary>The route value at <paramref name="index"/> as ASCII digits that fit an Int32.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryRouteInt32(int index, out int value)
    {
        bool ok = TryRouteInt64(index, out long v) && v <= int.MaxValue;
        value = ok ? (int)v : 0;
        return ok;
    }

    /// <summary>Answers a long or int route value of <paramref name="endpoint"/> that didn't bind: the endpoint's own
    /// reply, else the app's <see cref="HttpAppErrors.InvalidRouteValue"/>, else its <see cref="HttpAppErrors.NotFound"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ValueTask RejectRouteValue(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        HttpAppErrors e = _app.Errors;
        return (endpoint.InvalidRouteValue ?? e.InvalidRouteValue ?? e.NotFound).WriteAsync(this);
    }

    /// <summary>The query string, for a <c>[FromQuery]</c> binder.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public QuerySource Query => new(_x.RequestBuffer, _x.QueryOffset, _x.QueryLength, _arena);

    /// <summary>The request body, for a body binder.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public BodySource RequestBody
    {
        get
        {
            HttpAppErrors e = _app.Errors;
            return new(_x.RequestBuffer, _x.RequestBodyOffset, _x.RequestBodyLength, _arena, e.InvalidBody, e.MalformedBody, e.MissingBody);
        }
    }

    /// <summary>The connection's <typeparamref name="T"/>, made the first time and bound to this request.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public T Scoped<T>(int slot) where T : class, IRequestScoped
    {
        object? s = _scoped[slot];
        T service = s is null ? Create<T>(slot) : Unsafe.As<T>(s);
        service.Bind(this);
        return service;
    }

    T Create<T>(int slot) where T : class
    {
        var service = (T)_app.CreatePerConnection(slot);
        _scoped[slot] = service;
        return service;
    }

    /// <summary>
    /// Waits for an async handler's reply without a state machine of its own: when <paramref name="pending"/> is
    /// ready, <typeparamref name="TSend"/> writes it (and if that waits too, it is waited for), then the server goes
    /// on with the response. What the handler or the write throws becomes the server's 500.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ValueTask Await<T, TSend>(Pending<T> pending) where TSend : struct, IReplySender<T>
    {
        if (pending.IsCompleted) return TSend.Send(pending.GetResult(), this);
        _done.Reset();
        _pendingBox = pending.Box;
        _pendingToken = pending.Token;
        pending.OnCompleted(Continuation<T, TSend>.Run, this);
        return new ValueTask(this, _done.Version);
    }

    /// <summary>The continuation of one reply type, made once.</summary>
    static class Continuation<T, TSend> where TSend : struct, IReplySender<T>
    {
        public static readonly Action<object?> Run = static s => ((RequestContext)s!).Continue<T, TSend>();
    }

    void Continue<T, TSend>() where TSend : struct, IReplySender<T>
    {
        // the box Await kept is the pending reply's, whose type this continuation was made for
        var box = Unsafe.As<PooledBox<T>>(_pendingBox!);
        _pendingBox = null;
        ValueTask written;
        try
        {
            written = TSend.Send(box.GetResult(_pendingToken), this);
        }
        catch (Exception e)
        {
            _done.SetException(e);
            return;
        }
        Finish(written);
    }

    void Finish(ValueTask written)
    {
        if (!written.IsCompleted)
        {
            _writing = written;
            written.GetAwaiter().UnsafeOnCompleted(_onWritten);
            return;
        }
        try
        {
            written.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _done.SetException(e);
            return;
        }
        _done.SetResult(true);
    }

    void OnWritten()
    {
        ValueTask written = _writing;
        _writing = default;
        try
        {
            written.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _done.SetException(e);
            return;
        }
        _done.SetResult(true);
    }

    void IValueTaskSource.GetResult(short token) => _done.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _done.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _done.OnCompleted(continuation, state, token, flags);

    // ---- The server's hooks ----

    /// <summary>The handler is done: what the JSON writer holds goes into the body, and the arena is let go.</summary>
    internal void HandlerDone()
    {
        // first, and a full fence: a Viewer on another thread that reads a claim's place after this sees it changed
        Interlocked.Increment(ref _generation);
        if (_jsonInUse)
        {
            _json.Flush();
            _jsonInUse = false;
        }
        _arena.Reset();
        _viewer = 0;
        _claimPayload = default;
        _claims.AsSpan().Fill(-1);
        // the connection's services let go of what they lent this request (a data context's lists); one that throws
        // is logged as a handler's failure is, and the others still hear it
        foreach (object? service in _scoped)
            if (service is IRequestScoped scoped)
                try { scoped.Unbind(); }
                catch (Exception e) { UnbindFailed(e); }
    }

    void UnbindFailed(Exception e)
    {
        if (_app.Server is { } server) Interlocked.Increment(ref server.CallbackFailures);
        try { _app.Errors.Log?.Invoke(_x, e); }
        catch (Exception) { } // a failing logger mustn't take the connection down either
    }

    /// <summary>The connection is idle or closed: the arena's kept chunk goes back to the pool.</summary>
    internal void ConnectionIdle() => _arena.Release();

    /// <summary>The handler threw: whatever it wrote is dropped, in the JSON writer too.</summary>
    internal void DiscardJson()
    {
        if (!_jsonInUse) return;
        _json.Reset();
        _jsonInUse = false;
    }
}

/// <summary>Writes one kind of handler reply; the endpoint generator makes one per reply type.</summary>
/// <typeparam name="T">The reply.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IReplySender<T>
{
    /// <summary>Writes <paramref name="reply"/> into <paramref name="context"/>'s response.</summary>
    static abstract ValueTask Send(T reply, RequestContext context);
}
