using System;
using System.Collections.Generic;
using CodeFusion.Http.Tests.Reference;

namespace CodeFusion.Http.Tests.Conduit;

/// <summary>
/// The contest server's nineteen routes as minimal-API endpoints. Each handler answers with its route's name in a
/// header-free 200 and the route values it was given, so the router and the generated binding can be compared with
/// the contest's hand-written Route.Match.
/// </summary>
public static class ConduitRoutes
{
    public const string Secret = "conduit-routes-secret-of-32-bytes";

    /// <summary>An app with the routes, not started, and which RouteId each mapped endpoint is.</summary>
    public static (HttpApp App, Dictionary<Endpoint, RouteId> Ids) Build()
    {
        var builder = HttpApp.CreateBuilder([]);
        builder.Options.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
        builder.AddJwtAuth(Secret);
        HttpApp app = builder.Build();
        var ids = new Dictionary<Endpoint, RouteId>();
        void Id(EndpointBuilder e, RouteId id) => ids[e.Endpoint] = id;

        IEndpointRouteBuilder api = app.MapGroup("/api");
        Id(api.MapGet("/articles", static (ArticleListQuery q, Viewer v) => Echo.Of("ListArticles", q.Tag, q.Author)).AuthOptional(), RouteId.ListArticles);
        Id(api.MapPost("/articles", static (CreateArticleBody b, Viewer v) => Echo.Of("CreateArticle", b.Title, default)).AuthRequired(), RouteId.CreateArticle);
        Id(api.MapGet("/articles/feed", static (ArticleListQuery q, Viewer v) => Echo.Of("Feed", default, default)).AuthRequired(), RouteId.Feed);
        Id(api.MapGet("/articles/{slug}", static (Utf8 slug, Viewer v) => Echo.Of("GetArticle", slug, default)).AuthOptional(), RouteId.GetArticle);
        Id(api.MapPut("/articles/{slug}", static (Utf8 slug, UpdateArticleBody b, Viewer v) => Echo.Of("UpdateArticle", slug, default)).AuthRequired(), RouteId.UpdateArticle);
        Id(api.MapDelete("/articles/{slug}", static (Utf8 slug, Viewer v) => Echo.Of("DeleteArticle", slug, default)).AuthRequired(), RouteId.DeleteArticle);
        Id(api.MapGet("/articles/{slug}/comments", static (Utf8 slug, Viewer v) => Echo.Of("ListComments", slug, default)).AuthOptional(), RouteId.ListComments);
        Id(api.MapPost("/articles/{slug}/comments", static (Utf8 slug, CommentBody b, Viewer v) => Echo.Of("CreateComment", slug, b.Body)).AuthRequired(), RouteId.CreateComment);
        Id(api.MapDelete("/articles/{slug}/comments/{id}", static (Utf8 slug, Utf8 id, Viewer v) => Echo.Of("DeleteComment", slug, id)).AuthRequired(), RouteId.DeleteComment);
        Id(api.MapPost("/articles/{slug}/favorite", static (Utf8 slug, Viewer v) => Echo.Of("Favorite", slug, default)).AuthRequired(), RouteId.Favorite);
        Id(api.MapDelete("/articles/{slug}/favorite", static (Utf8 slug, Viewer v) => Echo.Of("Unfavorite", slug, default)).AuthRequired(), RouteId.Unfavorite);
        Id(api.MapGet("/tags", static () => Echo.Of("Tags", default, default)), RouteId.Tags);
        Id(api.MapGet("/user", static (Viewer v) => Echo.Of("CurrentUser", v.Token, default)).AuthRequired(), RouteId.CurrentUser);
        Id(api.MapPut("/user", static (UpdateUserBody b, Viewer v) => Echo.Of("UpdateUser", default, default)).AuthRequired(), RouteId.UpdateUser);
        Id(api.MapGet("/profiles/{username}", static (Utf8 username, Viewer v) => Echo.Of("GetProfile", username, default)).AuthOptional(), RouteId.GetProfile);
        Id(api.MapPost("/profiles/{username}/follow", static (Utf8 username, Viewer v) => Echo.Of("Follow", username, default)).AuthRequired(), RouteId.Follow);
        Id(api.MapDelete("/profiles/{username}/follow", static (Utf8 username, Viewer v) => Echo.Of("Unfollow", username, default)).AuthRequired(), RouteId.Unfollow);
        Id(api.MapPost("/users", static (RegisterBody b) => Echo.Of("Register", b.Username, default)), RouteId.Register);
        Id(api.MapPost("/users/login", static (LoginBody b) => Echo.Of("Login", b.Email, default)), RouteId.Login);
        return (app, ids);
    }
}

/// <summary>A route's name and up to two values, as JSON.</summary>
public readonly record struct Echo(Utf8 Route, Utf8 Arg, Utf8 Id)
{
    static readonly Dictionary<string, byte[]> Names = [];

    public static Reply<Echo> Of(string route, Utf8 arg, Utf8 id)
    {
        byte[] name;
        lock (Names)
            if (!Names.TryGetValue(route, out name!)) Names[route] = name = System.Text.Encoding.UTF8.GetBytes(route);
        return Reply.Ok(new Echo(new Utf8(name, 0, name.Length), arg, id));
    }
}
