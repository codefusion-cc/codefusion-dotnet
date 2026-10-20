using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Http.Tests.Sample;

/// <summary>
/// An app that uses every kind of parameter and reply the endpoint generator compiles: route values (Utf8, long,
/// int), a [FromQuery] struct, body structs with an envelope, nested objects, lists, numbers and bools, Viewer,
/// singletons, per-connection services, IResponse, HttpExchange, CancellationToken; replies as Reply&lt;T&gt;, Reply,
/// ErrorReply, an IReply struct, a Reply&lt;T&gt; of an IReply, async handlers, and a reply whose write waits.
/// </summary>
public static class SampleApp
{
    public const string Secret = "sample-secret-of-at-least-32-bytes";

    public static readonly byte[] Extra = """{"x":[1,2]}"""u8.ToArray();

    public static HttpApp Build(Action<HttpAppBuilder>? configure = null)
    {
        var builder = HttpApp.CreateBuilder([]);
        builder.Options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        builder.Options.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
        builder.Errors.Log = null;
        builder.AddJwtAuth(Secret);
        builder.AddSingleton(new Catalog());
        builder.AddPerConnection(() => new Ticker());
        configure?.Invoke(builder);
        HttpApp app = builder.Build();

        RouteGroup api = app.MapGroup("/api");
        api.MapGet("/articles/{slug}", static (Utf8 slug, ListQuery q, Viewer viewer, Catalog catalog) =>
            slug == "missing"u8 ? Reply.NotFound(SampleErrors.NotFound) : Reply.Ok(catalog.Article(slug, q, viewer)));

        api.MapPost("/articles", static (NewArticle body, Viewer viewer, Jwt jwt) =>
                Reply.Created(new Created(body.Title, body.Description, body.TagList, body.Meta, body.Count, body.Draft, viewer.Id, jwt.Issue(viewer.Id + 1))))
            .AuthRequired();

        api.MapDelete("/articles/{slug}/comments/{id}", static (Utf8 slug, long id, Viewer viewer) =>
                id == 0 ? Reply.NotFound(SampleErrors.NotFound)
                : id % 2 == 1 ? Reply.Forbidden(SampleErrors.Forbidden)
                : Reply.NoContent())
            .AuthRequired();

        api.MapGet("/user", static (Viewer viewer) => Reply.Ok(new UserView(viewer.Id, viewer.Token))).AuthRequired();

        api.MapPost("/users/login", static (Login body, Jwt jwt) =>
            body.Password == "secret"u8 ? Reply.Ok(new UserView(7, jwt.Issue(7))) : Reply.Unauthorized(SampleErrors.WrongLogin));

        api.MapGet("/slow/{n}", static async (int n, Ticker ticker) =>
        {
            long tick = await ticker.NextAsync(); // completes on a thread-pool thread
            return Reply.Ok(new Count(n, tick, ticker.Binds > 0));
        });

        api.MapPost("/slow", static async (Login body, Ticker ticker, IResponse response) =>
        {
            await ticker.NextAsync();
            // the body's text is still the request's after the wait
            return body.Email.IsEmpty ? Reply.Unprocessable(SampleErrors.Invalid) : Reply.Ok(new UserView(1, body.Email));
        });

        api.MapGet("/text/{word}", static (Utf8 word) => new TextReply(word));
        api.MapGet("/created-text", static () => Reply.Created(new TextReply(default)));
        api.MapGet("/later", static (Ticker ticker) => new LaterReply(ticker, true));
        api.MapGet("/written-later", static (Ticker ticker) => new LaterReply(ticker, false));
        api.MapGet("/wait/{n}", static async (int n, Ticker ticker) =>
        {
            await ticker.NextAsync();
            return Reply.Ok(new Count(n, 0, ticker.Binds > 0));
        });
        api.MapGet("/later-async", static async (Ticker ticker) =>
        {
            await ticker.NextAsync();
            return new LaterReply(ticker, true);
        });
        api.MapPut("/fail", static () => Fail());
        api.MapPut("/fail-later", static async (Ticker ticker) =>
        {
            await ticker.NextAsync();
            return Fail();
        });
        api.MapGet("/gone", SampleHandlers.Gone);
        api.MapGet("/probe/{n}", SampleHandlers.Probe);
        api.MapGet("/busy", static () => Reply.Busy(SampleErrors.Busy, retryAfter: 3));
        api.MapGet("/", static () => Reply.Ok(new Count(0, 0, false)));
        return app;
    }

    static Reply Fail() => throw new InvalidOperationException("the handler failed");
}

public static class SampleErrors
{
    public static readonly ErrorReply NotFound = new(404, """{"errors":{"body":["article not found"]}}"""u8);
    public static readonly ErrorReply Forbidden = new(403, """{"errors":{"body":["not the author"]}}"""u8);
    public static readonly ErrorReply WrongLogin = new(401, """{"errors":{"body":["invalid email or password"]}}"""u8);
    public static readonly ErrorReply Invalid = new(422, """{"errors":{"body":["invalid"]}}"""u8);
    public static readonly ErrorReply BadTags = new(422, """{"errors":{"body":["bad tags"]}}"""u8);
    public static readonly ErrorReply BadPage = new(422, """{"errors":{"body":["bad page"]}}"""u8);
    public static readonly ErrorReply Gone = new(410, """{"errors":{"body":["gone"]}}"""u8);
    public static readonly ErrorReply Busy = new(503, """{"errors":{"body":["busy"]}}"""u8);
}

public static class SampleHandlers
{
    public static ErrorReply Gone() => SampleErrors.Gone;

    public static Reply<Probe> Probe(int n, HttpExchange exchange, IResponse response, CancellationToken aborted) =>
        Reply.Ok(new Probe(n, aborted.CanBeCanceled, exchange.Path.Length, ReferenceEquals(response.Arena, response.Arena)));
}

public enum Kind
{
    Draft,
    Published,
}

[JsonEnvelope("article")]
public readonly record struct ArticleView(
    Utf8 Slug, Utf8? Tag, Utf8? Author, long Limit, int Offset, Timestamp CreatedAt, bool Favorited, double Score,
    ProfileView Author2, ProfileView? Editor, RawJson Extra, int[]? Ranks, Kind Kind, [property: JsonIgnore] long Hidden,
    [property: JsonPropertyName("viewer_id")] long ViewerId);

public readonly record struct ProfileView(Utf8 Username, Utf8? Bio, bool Following);

[JsonEnvelope("article")]
public readonly record struct Created(Utf8 Title, Utf8? Description, Utf8List TagList, Meta? Meta, long? Count, bool? Draft, long Author, Utf8 Token);

[JsonEnvelope("user")]
public readonly record struct UserView(long Id, Utf8 Token);

public readonly record struct Count(int N, long Tick, bool Bound);

public readonly record struct Probe(int N, bool Cancelable, int PathLength, bool SameArena);

[FromQuery]
public partial struct ListQuery
{
    public Utf8? Tag;
    [FromQuery(Name = "by")] public Utf8? Author;
    [DefaultValue(20), Range(1, 100)] public long Limit;
    public int Offset { get; set; }
    static ErrorReply Invalid => SampleErrors.BadPage;
}

[JsonEnvelope("article")]
public partial struct NewArticle
{
    public Utf8 Title;
    [MinLength(1)] public Utf8? Description;
    [BodyError(nameof(BadTags))] public Utf8List TagList;
    public Meta? Meta;
    public long? Count;
    public bool? Draft;
    static ErrorReply Invalid => SampleErrors.Invalid;
    static ErrorReply BadTags => SampleErrors.BadTags;
}

public partial struct Meta
{
    public bool Pinned { get; set; }
    public double Score { get; set; }
    public int Rank { get; set; }
    [JsonPropertyName("note")] public Utf8? Remark { get; set; }
}

[JsonEnvelope("user")]
public partial struct Login
{
    public Utf8 Email;
    public Utf8 Password;
}

/// <summary>A singleton: builds the article view.</summary>
public sealed class Catalog
{
    readonly double _score = 1.5;

    public ArticleView Article(Utf8 slug, ListQuery q, Viewer viewer) => new(
        slug, q.Tag, q.Author, q.Limit, q.Offset, new Timestamp(1_700_000_000_123_456), viewer.IsAuthenticated, _score,
        new ProfileView(slug, null, false), viewer.IsAuthenticated ? new ProfileView(viewer.Token, slug, true) : null,
        new RawJson(new Utf8(SampleApp.Extra, 0, SampleApp.Extra.Length)), viewer.Id == 0 ? null : Ranks, Kind.Published, 99, viewer.Id);

    static readonly int[] Ranks = [3, 1];
}

/// <summary>
/// A per-connection service whose answers come from another thread, as a database's do: each wait goes on on a
/// thread-pool thread, through a work item made once, in a pooled async method; nothing is allocated for it.
/// </summary>
public sealed class Ticker : IRequestScoped, IThreadPoolWorkItem
{
    Action? _next;
    long _ticks;

    public int Binds { get; private set; }
    public IResponse? Response { get; private set; }

    public void Bind(IResponse response)
    {
        Response = response;
        Binds++;
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
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

    /// <summary>Goes on on a thread-pool thread.</summary>
    readonly struct Hop(Ticker ticker) : ICriticalNotifyCompletion
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

/// <summary>An IReply that writes its own bytes, without the JSON writer.</summary>
public readonly struct TextReply(Utf8 word) : IReply
{
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse
    {
        var body = response.Body;
        Span<byte> s = body.GetSpan(word.Length + 2);
        s[0] = (byte)'"';
        word.Span.CopyTo(s[1..]);
        s[word.Length + 1] = (byte)'"';
        body.Advance(word.Length + 2);
        return default;
    }
}

/// <summary>An IReply whose write waits, as a query result's does: it writes once the ticker has ticked (the tick, or 0
/// for a body of the same length every time).</summary>
public readonly struct LaterReply(Ticker ticker, bool showTick) : IReply
{
    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]
    public async ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse
    {
        long tick = await ticker.NextAsync();
        response.Status = 202;
        var w = response.Json;
        w.WriteStartObject();
        w.WriteNumber("tick"u8, showTick ? tick : 0);
        w.WriteEndObject();
    }
}
