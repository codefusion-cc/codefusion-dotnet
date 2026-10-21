using System;
using System.Text.Json.Serialization;
using CodeFusion;
using CodeFusion.Data;

namespace Conduit.Data;

// The replies' shapes, in the order the RealWorld spec writes their properties. Each is a row's columns in the same
// order; the static Of methods are projections the query methods inline.

[JsonEnvelope("profile")]
public readonly record struct ProfileView(Utf8 Username, Utf8? Bio, Utf8? Image, bool Following)
{
    /// <summary>An anonymous viewer (0) follows nobody, and the check isn't run for them.</summary>
    public static ProfileView Of(User u, long viewer) =>
        new(u.Username, u.Bio, u.Image, viewer != 0 && u.Followers.Any(f => f.FollowerId == viewer));
}

[JsonEnvelope("article")]
public readonly record struct ArticleView(
    Utf8 Slug, Utf8 Title, Utf8 Description, Utf8 Body, Timestamp CreatedAt, Timestamp UpdatedAt,
    RawJson TagList, long FavoritesCount, bool Favorited, ProfileView Author)
{
    public static ArticleView Of(Article a, long viewer) => new(
        a.Slug, a.Title, a.Description, a.Body, a.CreatedAt, a.UpdatedAt,
        a.Tags.OrderBy(t => t.Name).Select(t => t.Name).AsJson(),
        a.Favorites.Count(),
        viewer != 0 && a.Favorites.Any(f => f.UserId == viewer),
        ProfileView.Of(a.Author, viewer));
}

[JsonList("articles", CountName = "articlesCount")]
public readonly record struct ArticleListView(
    Utf8 Slug, Utf8 Title, Utf8 Description, Timestamp CreatedAt, Timestamp UpdatedAt,
    RawJson TagList, long FavoritesCount, bool Favorited, ProfileView Author)
{
    public static ArticleListView Of(Article a, long viewer) => new(
        a.Slug, a.Title, a.Description, a.CreatedAt, a.UpdatedAt,
        a.Tags.OrderBy(t => t.Name).Select(t => t.Name).AsJson(),
        a.Favorites.Count(),
        viewer != 0 && a.Favorites.Any(f => f.UserId == viewer),
        ProfileView.Of(a.Author, viewer));

    /// <summary>An article of the viewer's feed, whose author the viewer follows: the feed found it through the follow.</summary>
    public static ArticleListView InFeed(Article a, long viewer) => Of(a, viewer) with { Author = new ProfileView(a.Author.Username, a.Author.Bio, a.Author.Image, true) };
}

[JsonEnvelope("comment")]
[JsonList("comments")]
public readonly record struct CommentView(long Id, Timestamp CreatedAt, Timestamp UpdatedAt, Utf8 Body, ProfileView Author)
{
    public static CommentView Of(Comment c, long viewer) => new(c.Id, c.CreatedAt, c.UpdatedAt, c.Body, ProfileView.Of(c.Author, viewer));
}

/// <summary>A login's row: the user, and the hash to check, which is never written into JSON.</summary>
public readonly record struct UserLogin(long Id, Utf8 Username, Utf8 Email, Utf8? Bio, Utf8? Image, [property: JsonIgnore] Utf8 PasswordHash);

/// <summary>A new user's row: its id, for the token, and the rest.</summary>
public readonly record struct Registered(long Id, Utf8 Username, Utf8 Email, Utf8? Bio, Utf8? Image);

/// <summary>The current user's row, before the token is added.</summary>
public readonly record struct UserRow(Utf8 Username, Utf8 Email, Utf8? Bio, Utf8? Image)
{
    public static UserRow Of(User u) => new(u.Username, u.Email, u.Bio, u.Image);
}

/// <summary>The spec's slug rule, in the text functions of Utf8Operators: SQL in a query, and C# anywhere else.</summary>
public static class Slugs
{
    /// <summary>The title as a slug, then "-" and the article's id: unique, as the id is (the spec's SLUGIFY). SQLite
    /// has no regexp_replace: there spaces become dashes, which the tests' titles need.</summary>
    [SqlTemplate("lower(replace(trim({0}), ' ', '-')) || '-' || {1}", Dialect = "sqlite")]
    public static Utf8 WithId(Utf8 title, long id) => title.ReplaceMatches("[^A-Za-z0-9]+", "-").ToLowerAscii().Trim('-').Concat("-"u8).Concat(id);
}
