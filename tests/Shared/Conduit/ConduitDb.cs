using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Conduit.Data;

/// <summary>
/// The contest server's queries (its Data/Sql.cs) as query methods, all in LINQ: the follow and favorite toggles write
/// one table from a row found in another and give it back as the write left it, in one statement; the feed takes each
/// followee's newest articles in a lateral join; the tag insert inserts each new name once, in sorted order (so two
/// creates don't deadlock); the slug is the spec's rule in SQL's text functions.
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

    /// <summary>The follow written from the user found by name, and the profile as the follow left it, in one statement.</summary>
    public Write<ProfileView> Follow(long viewer, Utf8 username) =>
        Users.Where(u => u.Username == username)
            .Then(u => Insert(Follows, new Follow { FollowerId = viewer, FolloweeId = u.Id }).OrIgnore())
            .Select(u => ProfileView.Of(u, viewer));

    public Write<ProfileView> Unfollow(long viewer, Utf8 username) =>
        Users.Where(u => u.Username == username)
            .Then(u => Follows.Where(f => f.FollowerId == viewer && f.FolloweeId == u.Id).Delete())
            .Select(u => ProfileView.Of(u, viewer));

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

    /// <summary>The newest articles of the authors the viewer follows: per followee, a lateral join takes its newest
    /// from the author index, as many as the page reaches, and the page is the newest of those.</summary>
    public Page<ArticleListView> Feed(long viewer, Paging page) =>
        Follows.Where(f => f.FollowerId == viewer)
            .SelectMany(f => f.Followee.Articles, (f, a) => a)
            .OrderByDescending(a => a.Id)
            .Page(page, countUpTo: 1000)
            .Select(a => ArticleListView.InFeed(a, viewer));

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
