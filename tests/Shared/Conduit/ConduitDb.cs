using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Conduit.Data;

/// <summary>
/// Postgres's part of the contest server's queries: the provider, and the six whose LINQ needs what SQLite's SQL lacks.
/// The follow and favorite toggles write one table from a row found in another and give it back as the write left it,
/// in one statement (a writable CTE); the feed takes each followee's newest articles in a lateral join; the tag insert
/// inserts each new name once, in sorted order (so two creates don't deadlock), from the list's rows.
/// </summary>
public sealed partial class ConduitDb : DataContext<Postgres>
{
    /// <summary>The follow written from the user found by name, and the profile as the follow left it, in one statement.</summary>
    public Write<ProfileView> Follow(long viewer, Utf8 username) =>
        Users.Where(u => u.Username == username)
            .Then(u => Insert(Follows, new Follow { FollowerId = viewer, FolloweeId = u.Id }).OrIgnore())
            .Select(u => ProfileView.Of(u, viewer));

    public Write<ProfileView> Unfollow(long viewer, Utf8 username) =>
        Users.Where(u => u.Username == username)
            .Then(u => Follows.Where(f => f.FollowerId == viewer && f.FolloweeId == u.Id).Delete())
            .Select(u => ProfileView.Of(u, viewer));

    /// <summary>The newest articles of the authors the viewer follows: per followee, a lateral join takes its newest
    /// from the author index, as many as the page reaches, and the page is the newest of those.</summary>
    public Page<ArticleListView> Feed(long viewer, Paging page) =>
        Follows.Where(f => f.FollowerId == viewer)
            .SelectMany(f => f.Followee.Articles, (f, a) => a)
            .OrderByDescending(a => a.Id)
            .Page(page, countUpTo: 1000)
            .Select(a => ArticleListView.InFeed(a, viewer));

    /// <summary>Each new name once, in sorted order: two creates adding the same new tags then wait on each other in one
    /// order instead of deadlocking.</summary>
    public Write AddTags(Utf8List names) =>
        Insert(Tags, names.AsRows().Distinct().OrderBy(n => n).Select(n => new Tag { Name = n })).OrIgnore();

    /// <summary>The favorite written from the article found by slug, and the article as the favorite left it, in one
    /// statement: its count is the snapshot's, corrected by the favorite (written now, there before, or by a concurrent
    /// request), and favorited is known.</summary>
    public Write<ArticleView> Favorite(long viewer, Utf8 slug) =>
        Articles.Where(a => a.Slug == slug)
            .Then(a => Insert(Favorites, new Favorite { UserId = viewer, ArticleId = a.Id }).OrIgnore())
            .Select(a => ArticleView.Of(a, viewer));

    public Write<ArticleView> Unfavorite(long viewer, Utf8 slug) =>
        Articles.Where(a => a.Slug == slug)
            .Then(a => Favorites.Where(f => f.UserId == viewer && f.ArticleId == a.Id).Delete())
            .Select(a => ArticleView.Of(a, viewer));
}
