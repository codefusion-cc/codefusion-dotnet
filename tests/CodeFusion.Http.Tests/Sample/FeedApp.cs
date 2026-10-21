using System;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;

namespace CodeFusion.Http.Tests.Sample;

/// <summary>
/// The $12 contest's feed API (its SPEC.md) on the library's options alone, with an in-memory store: Bearer tokens
/// with a username claim, a 401 body for each reason, a malformed body apart from a missing field, and post ids that
/// must be positive, checked after auth. Nothing here reads the Authorization header or parses an id itself.
/// </summary>
public static class FeedApp
{
    public const string Secret = "twelve-dollar-challenge"; // the spec's: 23 bytes

    public static HttpApp Build(Action<HttpAppBuilder>? configure = null)
    {
        var builder = HttpApp.CreateBuilder([]);
        builder.Options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        builder.Options.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
        builder.Errors.Log = null;

        var auth = new JwtOptions { Scheme = "Bearer", AllowShortSecret = true };
        auth.Claims.Add(new JwtClaim("username"));
        builder.AddJwtAuth(Secret, auth);
        builder.Errors.NotFound = FeedErrors.NotFound;
        builder.Errors.Internal = FeedErrors.Internal;
        builder.Errors.MissingToken = FeedErrors.MissingToken;
        builder.Errors.InvalidToken = FeedErrors.InvalidToken;
        builder.Errors.InvalidTokenPayload = FeedErrors.InvalidPayload;
        builder.Errors.MalformedBody = FeedErrors.Malformed;
        builder.Errors.InvalidRouteValue = FeedErrors.InvalidId;
        builder.AddSingleton(new Posts());
        configure?.Invoke(builder);
        HttpApp app = builder.Build();

        app.MapGet("/posts/{id}", static ([Range(1, long.MaxValue)] long id, Posts posts) =>
            posts.Find(id) is { } p ? Reply.Ok(p) : Reply.NotFound(FeedErrors.PostNotFound));

        app.MapPost("/posts", static (NewFeedPost body, Viewer viewer, Posts posts) =>
            {
                Utf8 text = body.Body.Trim(); // a view: nothing copied
                if (text.IsEmpty) return Reply.Failed(FeedErrors.BodyRequired);
                if (Characters(text) > 500) return Reply.Failed(FeedErrors.BodyTooLong);
                return Reply.Created(new PostView(posts.Next(), text, Posts.CreatedAt, viewer.Claim("username"u8), 0));
            })
           .AuthRequired();

        app.MapPost("/posts/{id}/like", static ([Range(1, long.MaxValue)] long id, Viewer viewer, Posts posts) =>
                posts.Find(id) is null ? Reply.NotFound(FeedErrors.PostNotFound)
                : posts.Like(viewer.Id, id) ? Reply.Created(new LikeView(true, false, id))
                : Reply.Ok(new LikeView(true, true, id)))
           .AuthRequired();

        return app;
    }

    static int Characters(ReadOnlySpan<byte> utf8)
    {
        int n = 0;
        foreach (byte b in utf8)
            if ((b & 0xC0) != 0x80) n++;
        return n;
    }
}

/// <summary>Posts 1 to 100 exist; likes are a bit per (user mod 64, post).</summary>
public sealed class Posts
{
    static readonly byte[] Text = "hello"u8.ToArray();
    static readonly byte[] Author = "jake"u8.ToArray();
    static readonly byte[] Created = "2025-12-31T23:59:19.409Z"u8.ToArray();
    readonly long[] _likes = new long[101];
    long _next;

    public static Utf8 CreatedAt => new(Created, 0, Created.Length);

    public PostView? Find(long id) => id is >= 1 and <= 100
        ? new PostView(id, new Utf8(Text, 0, Text.Length), CreatedAt, new Utf8(Author, 0, Author.Length), long.PopCount(Volatile.Read(ref _likes[id])))
        : null;

    /// <summary>A new post's id: six digits, so every reply to a new post has the same length (the allocation tests read by length).</summary>
    public long Next() => 100_000 + Interlocked.Increment(ref _next) % 900_000;

    /// <summary>True the first time.</summary>
    public bool Like(long user, long post)
    {
        long bit = 1L << (int)(user % 64);
        return (Interlocked.Or(ref _likes[post], bit) & bit) == 0;
    }
}

[JsonEnvelope("post")]
public readonly record struct PostView(long Id, Utf8 Body, [property: JsonPropertyName("created_at")] Utf8 CreatedAt, Utf8 Author,
    [property: JsonPropertyName("like_count")] long LikeCount);

public readonly record struct LikeView(bool Liked, [property: JsonPropertyName("already_liked")] bool AlreadyLiked, [property: JsonPropertyName("post_id")] long PostId);

public partial struct NewFeedPost
{
    [BodyError(nameof(Required))] public Utf8 Body;

    static ErrorReply Required => FeedErrors.BodyRequired;
    static ErrorReply InvalidBody => FeedErrors.BodyRequired; // JSON, but not an object: no body field either
}

public static class FeedErrors
{
    public static readonly ErrorReply NotFound = new(404, """{"error":"not found"}"""u8);
    public static readonly ErrorReply InvalidId = new(400, """{"error":"invalid post id"}"""u8);
    public static readonly ErrorReply PostNotFound = new(404, """{"error":"post not found"}"""u8);
    public static readonly ErrorReply MissingToken = new(401, """{"error":"missing bearer token"}"""u8);
    public static readonly ErrorReply InvalidToken = new(401, """{"error":"invalid or expired token"}"""u8);
    public static readonly ErrorReply InvalidPayload = new(401, """{"error":"invalid token payload"}"""u8);
    public static readonly ErrorReply Malformed = new(400, """{"error":"malformed JSON body"}"""u8);
    public static readonly ErrorReply BodyRequired = new(400, """{"error":"body is required"}"""u8);
    public static readonly ErrorReply BodyTooLong = new(400, """{"error":"body must be at most 500 characters"}"""u8);
    public static readonly ErrorReply Internal = new(500, """{"error":"internal server error"}"""u8);
}
