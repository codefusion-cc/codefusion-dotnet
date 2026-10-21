using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Conduit.Data;

/// <summary>
/// The contest server's queries (its Data/Sql.cs) as query methods. Most are LINQ; five are SQL, where the hand
/// tuning has no LINQ form: the follow toggles and favorite toggles write one table while reading another and correct
/// the count against the statement's snapshot, and the feed takes each followee's newest articles in a lateral join.
/// The tag insert keeps its DISTINCT and ORDER BY (new tags are inserted in one order, so two creates don't deadlock).
/// </summary>
public sealed partial class ConduitDb : DataContext<Postgres>
{
    public Table<User> Users => Table<User>();
    public Table<Follow> Follows => Table<Follow>();
    public Table<Article> Articles => Table<Article>();
    public Table<Tag> Tags => Table<Tag>();
    public Table<ArticleTag> ArticleTags => Table<ArticleTag>();
    public Table<Favorite> Favorites => Table<Favorite>();
    public Table<Comment> Comments => Table<Comment>();

    // ---------- users ----------

    public Write<Registered> Register(Utf8 username, Utf8 email, Utf8 passwordHash) =>
        Insert(Users, new User { Username = username, Email = email, PasswordHash = passwordHash })
            .Select(u => new Registered(u.Id, u.Username, u.Email, u.Bio, u.Image));

    public One<UserLogin> UserByEmail(Utf8 email) =>
        Users.Where(u => u.Email == email).Select(u => new UserLogin(u.Id, u.Username, u.Email, u.Bio, u.Image, u.PasswordHash)).One();

    public One<UserRow> CurrentUser(long viewer) =>
        Users.Where(u => u.Id == viewer).Select(u => UserRow.Of(u)).One();

    /// <summary>Each value sent replaces the column; null keeps it.</summary>
    public Write<UserRow> UpdateUser(long viewer, Utf8? email, Utf8? username, Utf8? passwordHash, Utf8? bio, Utf8? image) =>
        Users.Where(u => u.Id == viewer)
            .Update(u => new User
            {
                Email = email ?? u.Email,
                Username = username ?? u.Username,
                PasswordHash = passwordHash ?? u.PasswordHash,
                Bio = bio ?? u.Bio,
                Image = image ?? u.Image,
            })
            .Select(u => UserRow.Of(u));

    // ---------- profiles ----------

    public One<ProfileView> Profile(Utf8 username, long viewer) =>
        Users.Where(u => u.Username == username).Select(u => ProfileView.Of(u, viewer)).One();

    public Write<ProfileView> Follow(long viewer, Utf8 username) => Sql<ProfileView>(
        $"WITH t AS (SELECT id, username, bio, image FROM users WHERE username = {username}), " +
        $"w AS (INSERT INTO follows (follower_id, followee_id) SELECT {viewer}, id FROM t ON CONFLICT DO NOTHING) " +
        $"SELECT username, bio, image, true FROM t").Write();

    public Write<ProfileView> Unfollow(long viewer, Utf8 username) => Sql<ProfileView>(
        $"WITH t AS (SELECT id, username, bio, image FROM users WHERE username = {username}), " +
        $"w AS (DELETE FROM follows WHERE follower_id = {viewer} AND followee_id = (SELECT id FROM t)) " +
        $"SELECT username, bio, image, false FROM t").Write();

    // ---------- article lists ----------

    public Page<ArticleListView> AllArticles(Paging page, long viewer) =>
        Articles.OrderByDescending(a => a.Id)
            .Page(page, countUpTo: 1000, PageCount.Probe)
            .Select(a => ArticleListView.Of(a, viewer));

    public Page<ArticleListView> ArticlesByTag(Utf8 tag, Paging page, long viewer) =>
        ArticleTags.Where(at => at.TagId == Tags.Single(t => t.Name == tag).Id)
            .OrderByDescending(at => at.ArticleId)
            .Page(page, countUpTo: 1000, PageCount.Probe)
            .Select(at => ArticleListView.Of(at.Article, viewer));

    public Page<ArticleListView> ArticlesBy(Utf8 author, Paging page, long viewer) =>
        Articles.Where(a => a.AuthorId == Users.Single(u => u.Username == author).Id)
            .OrderByDescending(a => a.Id)
            .Page(page, countUpTo: 1000)
            .Select(a => ArticleListView.Of(a, viewer));

    public Page<ArticleListView> FavoritedBy(Utf8 username, Paging page, long viewer) =>
        Favorites.Where(f => f.UserId == Users.Single(u => u.Username == username).Id)
            .OrderByDescending(f => f.ArticleId)
            .Page(page, countUpTo: 1000)
            .Select(f => ArticleListView.Of(f.Article, viewer));

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

    // ---------- single articles ----------

    public One<ArticleView> ArticleBySlug(Utf8 slug, long viewer) =>
        Articles.Where(a => a.Slug == slug).Select(a => ArticleView.Of(a, viewer)).One();

    /// <summary>The insert, its tags and the article read back commit together, in the writer's group. The tags are
    /// the request body's list as CodeFusion.Http binds it, in the request's arena.</summary>
    public Write<ArticleView> CreateArticle(long author, Utf8 title, Utf8 description, Utf8 body, Utf8List tags) =>
        Insert(Articles, n => new Article { Slug = Slugs.WithId(title, n.Id), AuthorId = author, Title = title, Description = description, Body = body })
            .ThenIf(!tags.IsEmpty, AddTags(tags))
            .ThenIf(!tags.IsEmpty, n => Insert(ArticleTags, Tags.Where(t => tags.Contains(t.Name)).Select(t => new ArticleTag { ArticleId = n.Id, TagId = t.Id })))
            .ThenSelect(n => ArticleView.Of(n, author));

    /// <summary>Each new name once, in sorted order: two creates adding the same new tags then wait on each other in one
    /// order instead of deadlocking.</summary>
    public Write AddTags(Utf8List names) =>
        Insert(Tags, names.AsRows().Distinct().OrderBy(n => n).Select(n => new Tag { Name = n })).OrIgnore();

    /// <summary>Updates the viewer's own article: no row is an unknown slug, a refused one someone else's.</summary>
    public Write<ArticleView> UpdateArticle(long viewer, Utf8 slug, Utf8? title, Utf8? description, Utf8? body) =>
        Articles.Where(a => a.Slug == slug)
            .UpdateIf(a => a.AuthorId == viewer, a => new Article
            {
                Title = title ?? a.Title,
                Description = description ?? a.Description,
                Body = body ?? a.Body,
                Slug = title == null ? a.Slug : Slugs.WithId(title.Value, a.Id),
                UpdatedAt = SqlFunctions.Now(),
            })
            .Select(a => ArticleView.Of(a, viewer));

    /// <summary>Deletes the viewer's own article, and gives back its author; someone else's is refused (OrForbidden).</summary>
    public One<long> DeleteArticle(Utf8 slug, long viewer) =>
        Articles.Where(a => a.Slug == slug).DeleteIf(a => a.AuthorId == viewer).Select(a => a.AuthorId).One();

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

    // ---------- comments ----------

    /// <summary>No rows: an unknown article. An article without comments: an empty list.</summary>
    public Many<CommentView> CommentsOf(Utf8 slug, long viewer) =>
        Articles.Where(a => a.Slug == slug)
            .SelectMany(a => a.Comments.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(100), (a, c) => CommentView.Of(c, viewer))
            .Many();

    public Write<CommentView> CreateComment(long viewer, Utf8 body, Utf8 slug) =>
        Insert(Comments, Articles.Where(a => a.Slug == slug).Select(a => new Comment { ArticleId = a.Id, AuthorId = viewer, Body = body }))
            .Select(c => new CommentView(c.Id, c.CreatedAt, c.UpdatedAt, c.Body, new ProfileView(c.Author.Username, c.Author.Bio, c.Author.Image, false)));

    /// <summary>Deletes the viewer's own comment on the article, and gives back its author; someone else's is refused (OrForbidden).</summary>
    public One<long> DeleteComment(long viewer, long id, Utf8 slug) =>
        Comments.Where(c => c.Id == id && c.Article.Slug == slug).DeleteIf(c => c.AuthorId == viewer).Select(c => c.AuthorId).One();

    // ---------- tags ----------

    [JsonList("tags")]
    public One<RawJson> AllTags() => Tags.OrderBy(t => t.Name).Select(t => t.Name).AsJson();
}
