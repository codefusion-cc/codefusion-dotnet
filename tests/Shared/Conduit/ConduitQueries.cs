using CodeFusion;
using CodeFusion.Data;

namespace Conduit.Data;

/// <summary>
/// The contest server's queries (its Data/Sql.cs) that LINQ says, for every provider: each provider's part of the
/// context names the provider and adds the queries written in its SQL (ConduitDb.cs is Postgres's).
/// </summary>
public partial class ConduitDb
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
