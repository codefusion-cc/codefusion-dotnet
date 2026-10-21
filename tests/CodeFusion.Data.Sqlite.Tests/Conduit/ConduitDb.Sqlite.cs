using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Sqlite;

namespace Conduit.Data;

/// <summary>
/// SQLite's part of the contest server's queries: the provider, and the five written in SQL. Without writable CTEs a
/// toggle is two statements in one SQL text, which the provider runs in order in one unit: the write, then the row read
/// back (it sees the write, so no count needs correcting). The feed takes the newest articles of the followees from the
/// author index.
/// </summary>
public sealed partial class ConduitDb : DataContext<Sqlite>
{
    public Write<ProfileView> Follow(long viewer, Utf8 username) => Sql<ProfileView>(
        $"INSERT INTO follows (follower_id, followee_id) SELECT {viewer}, id FROM users WHERE username = {username} ON CONFLICT DO NOTHING; " +
        $"SELECT username, bio, image, true FROM users WHERE username = {username}").Write();

    public Write<ProfileView> Unfollow(long viewer, Utf8 username) => Sql<ProfileView>(
        $"DELETE FROM follows WHERE follower_id = {viewer} AND followee_id = (SELECT id FROM users WHERE username = {username}); " +
        $"SELECT username, bio, image, false FROM users WHERE username = {username}").Write();

    /// <summary>The newest articles of the authors the viewer follows, from the author index.</summary>
    public Page<ArticleListView> Feed(long viewer, Paging page) => Sql<ArticleListView>(
        $"WITH c AS (SELECT count(*) AS n FROM (SELECT 1 FROM articles a WHERE a.author_id IN (SELECT fo.followee_id FROM follows fo WHERE fo.follower_id = {viewer}) LIMIT 1000) s) " +
        $"SELECT p.*, c.n FROM c LEFT JOIN (" +
        $"SELECT a.slug, a.title, a.description, a.created_at, a.updated_at, {TagList()}, {FavoritesCount()}, " +
        $"({viewer} <> 0 AND EXISTS (SELECT 1 FROM favorites f WHERE f.article_id = a.id AND f.user_id = {viewer})), u.username, u.bio, u.image, true, a.id " +
        $"FROM articles a JOIN users u ON u.id = a.author_id WHERE a.author_id IN (SELECT fo.followee_id FROM follows fo WHERE fo.follower_id = {viewer}) " +
        $"ORDER BY a.id DESC LIMIT max(0, min(100, {page.Limit})) OFFSET max(0, {page.Offset})) p ON true ORDER BY p.id DESC").Page();

    // SQL shared by the statements above
    [SqlTemplate("(SELECT json_group_array(v) FROM (SELECT (SELECT t.name FROM tags t WHERE t.id = at.tag_id) AS v FROM article_tags at WHERE at.article_id = a.id ORDER BY 1))")]
    static RawJson TagList() => throw new System.NotSupportedException();

    [SqlTemplate("(SELECT count(*) FROM favorites f WHERE f.article_id = a.id)")]
    static long FavoritesCount() => throw new System.NotSupportedException();

    /// <summary>New names, from the list bound as one JSON text.</summary>
    public Write AddTags(Utf8List names) =>
        Sql($"INSERT INTO tags (name) SELECT DISTINCT value FROM json_each({names}) WHERE true ORDER BY 1 ON CONFLICT (name) DO NOTHING");

    public Write<ArticleView> Favorite(long viewer, Utf8 slug) => Sql<ArticleView>(
        $"INSERT INTO favorites (user_id, article_id) SELECT {viewer}, id FROM articles WHERE slug = {slug} ON CONFLICT DO NOTHING; " +
        $"SELECT a.slug, a.title, a.description, a.body, a.created_at, a.updated_at, {TagList()}, {FavoritesCount()}, true, " +
        $"u.username, u.bio, u.image, ({viewer} <> 0 AND EXISTS (SELECT 1 FROM follows fo WHERE fo.follower_id = {viewer} AND fo.followee_id = a.author_id)) " +
        $"FROM articles a JOIN users u ON u.id = a.author_id WHERE a.slug = {slug}").Write();

    public Write<ArticleView> Unfavorite(long viewer, Utf8 slug) => Sql<ArticleView>(
        $"DELETE FROM favorites WHERE user_id = {viewer} AND article_id = (SELECT id FROM articles WHERE slug = {slug}); " +
        $"SELECT a.slug, a.title, a.description, a.body, a.created_at, a.updated_at, {TagList()}, {FavoritesCount()}, false, " +
        $"u.username, u.bio, u.image, ({viewer} <> 0 AND EXISTS (SELECT 1 FROM follows fo WHERE fo.follower_id = {viewer} AND fo.followee_id = a.author_id)) " +
        $"FROM articles a JOIN users u ON u.id = a.author_id WHERE a.slug = {slug}").Write();
}
