using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Conduit.Data;

/// <summary>
/// Postgres's part of the contest server's queries: the provider, and the five written in SQL, where the hand
/// tuning has no LINQ form: the follow toggles and favorite toggles write one table while reading another and correct
/// the count against the statement's snapshot, and the feed takes each followee's newest articles in a lateral join.
/// The tag insert keeps its DISTINCT and ORDER BY (new tags are inserted in one order, so two creates don't deadlock).
/// </summary>
public sealed partial class ConduitDb : DataContext<Postgres>
{
    public Write<ProfileView> Follow(long viewer, Utf8 username) => Sql<ProfileView>(
        $"WITH t AS (SELECT id, username, bio, image FROM users WHERE username = {username}), " +
        $"w AS (INSERT INTO follows (follower_id, followee_id) SELECT {viewer}, id FROM t ON CONFLICT DO NOTHING) " +
        $"SELECT username, bio, image, true FROM t").Write();

    public Write<ProfileView> Unfollow(long viewer, Utf8 username) => Sql<ProfileView>(
        $"WITH t AS (SELECT id, username, bio, image FROM users WHERE username = {username}), " +
        $"w AS (DELETE FROM follows WHERE follower_id = {viewer} AND followee_id = (SELECT id FROM t)) " +
        $"SELECT username, bio, image, false FROM t").Write();

    /// <summary>The newest articles of the authors the viewer follows: per followee, its newest limit + offset
    /// articles are enough, which a lateral join takes from the author index.</summary>
    public Page<ArticleListView> Feed(long viewer, Paging page) => Sql<ArticleListView>(
        $"WITH c AS (SELECT count(*) AS n FROM (SELECT 1 FROM articles a WHERE a.author_id IN (SELECT fo.followee_id FROM follows fo WHERE fo.follower_id = {viewer}) LIMIT 1000) s) " +
        $"SELECT p.*, c.n FROM c LEFT JOIN LATERAL (" +
        $"SELECT a.slug, a.title, a.description, a.created_at, a.updated_at, {TagList()}, {FavoritesCount()}, " +
        $"({viewer} <> 0 AND EXISTS (SELECT 1 FROM favorites f WHERE f.article_id = a.id AND f.user_id = {viewer})), u.username, u.bio, u.image, true, a.id " +
        $"FROM (SELECT id FROM (SELECT x.id FROM follows fo CROSS JOIN LATERAL (SELECT a2.id FROM articles a2 WHERE a2.author_id = fo.followee_id " +
        $"ORDER BY a2.id DESC LIMIT {PageEnd(page)}) x WHERE fo.follower_id = {viewer} ORDER BY x.id DESC LIMIT 100 OFFSET {page.Offset}) b " +
        $"ORDER BY id DESC LIMIT {page.Limit}) p JOIN articles a ON a.id = p.id JOIN users u ON u.id = a.author_id ORDER BY a.id DESC) p ON true ORDER BY p.id DESC").Page();

    /// <summary>limit + offset, without overflowing.</summary>
    static long PageEnd(Paging page) => page.Offset > long.MaxValue - page.Limit ? long.MaxValue : page.Offset + page.Limit;

    // SQL shared by the statements above: a template's call is its SQL, where a hole is a parameter
    [SqlTemplate("convert_to(array_to_json(ARRAY(SELECT (SELECT t.name FROM tags t WHERE t.id = at.tag_id) FROM article_tags at WHERE at.article_id = a.id ORDER BY 1))::text, 'UTF8')")]
    static RawJson TagList() => throw new System.NotSupportedException();

    [SqlTemplate("(SELECT count(*) FROM favorites f WHERE f.article_id = a.id)")]
    static long FavoritesCount() => throw new System.NotSupportedException();

    /// <summary>New names in sorted order: two creates adding the same new tags then wait on each other in one order
    /// instead of deadlocking.</summary>
    public Write AddTags(Utf8List names) =>
        Sql($"INSERT INTO tags (name) SELECT DISTINCT unnest({names}::text[]) ORDER BY 1 ON CONFLICT (name) DO NOTHING");

    public Write<ArticleView> Favorite(long viewer, Utf8 slug) => Sql<ArticleView>(
        $"WITH t AS (SELECT id FROM articles WHERE slug = {slug}), " +
        $"w AS (INSERT INTO favorites (user_id, article_id) SELECT {viewer}, id FROM t ON CONFLICT DO NOTHING) " +
        $"SELECT a.slug, a.title, a.description, a.body, a.created_at, a.updated_at, {TagList()}, " +
        $"({FavoritesCount()} + (NOT EXISTS (SELECT 1 FROM favorites f WHERE f.article_id = a.id AND f.user_id = {viewer}))::int), true, " +
        $"u.username, u.bio, u.image, ({viewer} <> 0 AND EXISTS (SELECT 1 FROM follows fo WHERE fo.follower_id = {viewer} AND fo.followee_id = a.author_id)) " +
        $"FROM articles a JOIN users u ON u.id = a.author_id WHERE a.id = (SELECT id FROM t)").Write();

    public Write<ArticleView> Unfavorite(long viewer, Utf8 slug) => Sql<ArticleView>(
        $"WITH t AS (SELECT id FROM articles WHERE slug = {slug}), " +
        $"w AS (DELETE FROM favorites WHERE user_id = {viewer} AND article_id = (SELECT id FROM t)) " +
        $"SELECT a.slug, a.title, a.description, a.body, a.created_at, a.updated_at, {TagList()}, " +
        $"({FavoritesCount()} - (EXISTS (SELECT 1 FROM favorites f WHERE f.article_id = a.id AND f.user_id = {viewer}))::int), false, " +
        $"u.username, u.bio, u.image, ({viewer} <> 0 AND EXISTS (SELECT 1 FROM follows fo WHERE fo.follower_id = {viewer} AND fo.followee_id = a.author_id)) " +
        $"FROM articles a JOIN users u ON u.id = a.author_id WHERE a.id = (SELECT id FROM t)").Write();
}
