using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Conduit.Data;
using Xunit;
using static CodeFusion.Data.Tests.SeedModel;

namespace CodeFusion.Data.Tests;

/// <summary>
/// Each Conduit query run against the seed (<see cref="ConduitFixture.Seed"/>) through its reply, as a handler's
/// return value is written, and awaited, as a handler that needs the row reads it. The expected JSON comes from
/// <see cref="SeedModel"/>, the seed written out as the spec's replies.
/// </summary>
[Collection(nameof(ConduitCollection))]
public sealed class ExecutionTests(ConduitFixture fixture)
{
    readonly FakeResponse _response = new();

    ConduitDb Db() => fixture.Context(_response);

    ConduitDb Writer() => fixture.Writer(_response);

    async Task<string> Reply<TReply>(TReply reply) where TReply : IReply
    {
        _response.Next();
        await reply.WriteAsync(_response);
        return _response.Text;
    }

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];

    static readonly ErrorReply Taken = new(422, """{"errors":{"body":["username or email is already taken"]}}"""u8);
    static readonly ErrorReply ArticleNotFound = new(404, """{"errors":{"body":["article not found"]}}"""u8);
    static readonly ErrorReply NotTheAuthor = new(403, """{"errors":{"body":["not the author"]}}"""u8);

    // ---------- single articles ----------

    [Fact]
    public async Task ArticleBySlugWritesTheArticleInItsEnvelope()
    {
        string json = await Reply(Db().ArticleBySlug(U("how-to-train-your-dragon"), 1));
        Assert.Equal(200, _response.Status);
        // written out once by hand; the other tests use the seed model
        Assert.Equal("""
            {"article":{"slug":"how-to-train-your-dragon","title":"How to train your dragon","description":"Ever wonder how?","body":"You have to believe","createdAt":"2025-04-01T12:00:00.000Z","updatedAt":"2025-04-01T12:00:00.000Z","tagList":["dragons","training"],"favoritesCount":2,"favorited":true,"author":{"username":"bob","bio":"I write \"quotes\" and naïve UTF-8 ✓","image":null,"following":true}}}
            """, json);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 4)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(5, 99)]
    public async Task ArticleBySlugForEachViewer(long id, long viewer)
    {
        var a = Articles.Single(x => x.Id == id);
        Assert.Equal("{\"article\":" + Article(id, viewer, body: true) + "}", await Reply(Db().ArticleBySlug(U(a.Slug), viewer)));
    }

    [Fact]
    public async Task AMissingArticleIsTheDefaultNotFound()
    {
        string json = await Reply(Db().ArticleBySlug(U("no-such-article"), 1));
        Assert.Equal(404, _response.Status);
        Assert.Equal("""{"errors":{"body":["not found"]}}""", json);
    }

    [Fact]
    public async Task OrNotFoundRepliesItsOwnError()
    {
        string json = await Reply(Db().ArticleBySlug(U("no-such-article"), 1).OrNotFound(ArticleNotFound));
        Assert.Equal(404, _response.Status);
        Assert.Equal("""{"errors":{"body":["article not found"]}}""", json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x' OR '1'='1")]
    [InlineData("how-to-train-your-dragon ")]
    [InlineData("HOW-TO-TRAIN-YOUR-DRAGON")]
    public async Task OnlyTheExactSlugFindsTheArticle(string slug)
    {
        await Reply(Db().ArticleBySlug(U(slug), 1));
        Assert.Equal(404, _response.Status);
    }

    // ---------- awaited rows ----------

    [Fact]
    public async Task AnAwaitedRowLivesInTheRequestsArena()
    {
        _response.Next();
        var row = await Db().UserByEmail(U("bob@example.com"));
        Assert.Equal(DataStatus.Ok, row.Status);
        Assert.True(row.Found);
        var user = row.Value;
        Assert.Equal(2, user.Id);
        Assert.Equal("bob", user.Username.ToString());
        Assert.Equal("bob@example.com", user.Email.ToString());
        Assert.Equal("I write \"quotes\" and naïve UTF-8 ✓", user.Bio?.ToString());
        Assert.Null(user.Image);
        Assert.Equal("hash-b", user.PasswordHash.ToString());
        // one arena allocation for all of the row's text
        Assert.Equal(1, _response.Arena.Allocations);
        Assert.Equal("bob".Length + "bob@example.com".Length + Encoding.UTF8.GetByteCount(user.Bio!.Value.ToString()) + "hash-b".Length, _response.Arena.Used);
        // the values point into the arena: overwriting it changes them
        _response.Arena.Scribble();
        Assert.Equal("###", user.Username.ToString());
    }

    [Fact]
    public async Task AnAwaitedMissingRowIsNotFound()
    {
        _response.Next();
        var row = await Db().UserByEmail(U("nobody@example.com"));
        Assert.Equal(DataStatus.Ok, row.Status);
        Assert.False(row.Found);
        Assert.False(row.Failed);
        Assert.Null(row.OrNull);
        Assert.Null((UserLogin?)row);
        Assert.Null(row.Error);
        Assert.Throws<InvalidOperationException>(() => row.Value);
        Assert.Equal(0, _response.Arena.Allocations);
    }

    [Fact]
    public async Task AwaitedRowsOfTheSameTypeAtOnceKeepTheirOwnValues()
    {
        _response.Next();
        var db = Db();
        // each starts its command before the first awaits
        var a = Run(db.UserByEmail(U("alice@example.com")));
        var b = Run(db.UserByEmail(U("carol@example.com")));
        var c = Run(db.CurrentUser(4));
        Assert.Equal("dave", (await c).Value.Bio?.ToString());
        Assert.Equal("carol", (await b).Value.Username.ToString());
        Assert.Equal("alice", (await a).Value.Username.ToString());
        Assert.Equal(3, _response.Arena.Allocations);

        static async Task<Row<T>> Run<T>(One<T> one) where T : struct => await one;
    }

    [Fact]
    public async Task UserByEmailAsAReplyLeavesTheHashOut()
    {
        Assert.Equal("""{"id":3,"username":"carol","email":"carol@example.com","bio":null,"image":"https://img/c.png"}""",
            await Reply(Db().UserByEmail(U("carol@example.com"))));
    }

    // ---------- users ----------

    [Fact]
    public async Task CurrentUser()
    {
        Assert.Equal("""{"username":"dave","email":"dave@example.com","bio":"dave","image":null}""", await Reply(Db().CurrentUser(4)));
        await Reply(Db().CurrentUser(12345));
        Assert.Equal(404, _response.Status);
    }

    [Fact]
    public async Task RegisterGivesBackTheNewUserAndRefusesATakenName()
    {
        var db = Writer();
        string name = Unique("u");
        string json = await Reply(db.Register(U(name), U(name + "@example.com"), U("h")).WithStatus(201));
        Assert.Equal(201, _response.Status);
        Assert.Matches("^\\{\"id\":[0-9]+,\"username\":\"" + name + "\",\"email\":\"" + name + "@example.com\",\"bio\":null,\"image\":null\\}$", json);

        // the same name again: a unique violation, the default 409, or the reply the query names
        await Reply(db.Register(U(name), U(name + "2@example.com"), U("h")));
        Assert.Equal(409, _response.Status);
        Assert.Equal("""{"errors":{"body":["already exists"]}}""", _response.Text);
        Assert.Equal("""{"errors":{"body":["username or email is already taken"]}}""", await Reply(db.Register(U(name), U(name + "3@example.com"), U("h")).Conflict(Taken)));
        Assert.Equal(422, _response.Status);

        // awaited, the violation is a status the handler matches
        _response.Next();
        var again = await db.Register(U(Unique("v")), U(name + "@example.com"), U("h"));
        Assert.Equal(DataStatus.UniqueViolation, again.Status);
        Assert.True(again.Failed);
        Assert.False(again.Found);
        Assert.Equal(409, again.Error!.Status);
    }

    [Fact]
    public async Task AnAwaitedRegistrationHasTheNewId()
    {
        var db = Writer();
        string name = Unique("w");
        _response.Next();
        var row = await db.Register(U(name), U(name + "@x"), U("hash"));
        Assert.True(row.Found);
        Assert.True(row.Value.Id > 100);
        Assert.Equal(name, row.Value.Username.ToString());
        _response.Next();
        var login = await db.UserByEmail(U(name + "@x"));
        Assert.Equal(row.Value.Id, login.Value.Id);
        Assert.Equal("hash", login.Value.PasswordHash.ToString());
    }

    [Fact]
    public async Task UpdateUserChangesWhatIsSentAndKeepsTheRest()
    {
        var db = Writer();
        string name = Unique("x");
        _response.Next();
        long id = (await db.Register(U(name), U(name + "@x"), U("h1"))).Value.Id;
        Assert.Equal($$"""{"username":"{{name}}","email":"{{name}}@new","bio":"","image":null}""",
            await Reply(db.UpdateUser(id, U(name + "@new"), null, null, U(""), null)));
        Assert.Equal($$"""{"username":"{{name}}2","email":"{{name}}@new","bio":"","image":"i.png"}""",
            await Reply(db.UpdateUser(id, null, U(name + "2"), U("h2"), null, U("i.png"))));
        _response.Next();
        Assert.Equal("h2", (await db.UserByEmail(U(name + "@new"))).Value.PasswordHash.ToString());
        // a user that is gone: no row
        await Reply(db.UpdateUser(999_999, U("a"), null, null, null, null));
        Assert.Equal(404, _response.Status);
    }

    // ---------- profiles ----------

    [Theory]
    [InlineData("bob", 1)]
    [InlineData("bob", 0)]
    [InlineData("carol", 1)]
    [InlineData("alice", 2)]
    [InlineData("dave", 4)]
    public async Task Profile(string username, long viewer)
    {
        long id = Users.Single(u => u.Username == username).Id;
        Assert.Equal("{\"profile\":" + SeedModel.Profile(id, viewer) + "}", await Reply(Db().Profile(U(username), viewer)));
    }

    [Fact]
    public async Task FollowAndUnfollow()
    {
        var db = Writer();
        Assert.Equal("{\"profile\":" + SeedModel.Profile(4, 2, following: true) + "}", await Reply(db.Follow(2, U("dave"))));
        Assert.Equal("{\"profile\":" + SeedModel.Profile(4, 2, following: true) + "}", await Reply(db.Profile(U("dave"), 2)));
        // following again changes nothing
        Assert.Equal("{\"profile\":" + SeedModel.Profile(4, 2, following: true) + "}", await Reply(db.Follow(2, U("dave"))));
        Assert.Equal("{\"profile\":" + SeedModel.Profile(4, 2, following: false) + "}", await Reply(db.Unfollow(2, U("dave"))));
        Assert.Equal("{\"profile\":" + SeedModel.Profile(4, 2, following: false) + "}", await Reply(db.Profile(U("dave"), 2)));
        await Reply(db.Follow(2, U("nobody")));
        Assert.Equal(404, _response.Status);
    }

    // ---------- lists ----------

    [Theory]
    [InlineData(20, 0, new long[] { 5, 4, 3, 2, 1 })]
    [InlineData(2, 0, new long[] { 5, 4 })]
    [InlineData(2, 3, new long[] { 2, 1 })]
    [InlineData(1, 4, new long[] { 1 })]
    [InlineData(5, 5, new long[0])]
    [InlineData(20, 1000, new long[0])]
    public async Task AllArticles(long limit, long offset, long[] ids)
    {
        Assert.Equal(Page(ids, 1, 5), await Reply(Db().AllArticles(new Paging(limit, offset), 1)));
        Assert.Equal(200, _response.Status);
    }

    [Fact]
    public async Task ANegativeLimitOrOffsetIsAnEmptyOrTheFirstPage()
    {
        Assert.Equal(Page([], 1, 5), await Reply(Db().AllArticles(new Paging(-5, 0), 1)));
        Assert.Equal(Page([5, 4], 1, 5), await Reply(Db().AllArticles(new Paging(2, -3), 1)));
        Assert.Equal(200, _response.Status);
    }

    [Fact]
    public async Task APageNeverHoldsMoreThanItsMaxLimit()
    {
        string json = await Reply(Db().AllArticles(new Paging(1000, 0), 0));
        Assert.Equal(Page([5, 4, 3, 2, 1], 0, 5), json);
    }

    [Fact]
    public async Task ArticlesByTag()
    {
        Assert.Equal(Page([2, 1], 1, 2), await Reply(Db().ArticlesByTag(U("dragons"), new Paging(20, 0), 1)));
        Assert.Equal(Page([1], 4, 1), await Reply(Db().ArticlesByTag(U("training"), new Paging(20, 0), 4)));
        Assert.Equal(Page([], 1, 0), await Reply(Db().ArticlesByTag(U("nope"), new Paging(20, 0), 1)));
    }

    [Fact]
    public async Task ArticlesByAuthorAndFavorited()
    {
        Assert.Equal(Page([4, 3], 1, 2), await Reply(Db().ArticlesBy(U("carol"), new Paging(20, 0), 1)));
        Assert.Equal(Page([3], 1, 2), await Reply(Db().ArticlesBy(U("carol"), new Paging(1, 1), 1)));
        Assert.Equal(Page([], 1, 0), await Reply(Db().ArticlesBy(U("dave"), new Paging(20, 0), 1)));
        Assert.Equal(Page([3, 1], 2, 2), await Reply(Db().FavoritedBy(U("alice"), new Paging(20, 0), 2)));
        Assert.Equal(Page([1], 0, 1), await Reply(Db().FavoritedBy(U("dave"), new Paging(20, 0), 0)));
        Assert.Equal(Page([], 0, 0), await Reply(Db().FavoritedBy(U("nobody"), new Paging(20, 0), 0)));
    }

    [Fact]
    public async Task Feed()
    {
        Assert.Equal(Page([4, 3, 2, 1], 1, 4), await Reply(Db().Feed(1, new Paging(20, 0))));
        Assert.Equal(Page([3, 2], 1, 4), await Reply(Db().Feed(1, new Paging(2, 1))));
        Assert.Equal(Page([], 4, 0), await Reply(Db().Feed(4, new Paging(20, 0))));
        // an offset near long.MaxValue doesn't overflow limit + offset
        Assert.Equal(Page([], 1, 4), await Reply(Db().Feed(1, new Paging(20, long.MaxValue - 5))));
    }

    // ---------- comments ----------

    [Fact]
    public async Task CommentsNewestFirstAndAnEmptyListAndAMissingArticle()
    {
        string carol = SeedModel.Profile(3, 1), alice = SeedModel.Profile(1, 1);
        Assert.Equal(
            "{\"comments\":[{\"id\":2,\"createdAt\":\"2025-04-02T11:00:00.000Z\",\"updatedAt\":\"2025-04-02T11:00:00.000Z\",\"body\":\"Second\",\"author\":" + carol + "}," +
            "{\"id\":1,\"createdAt\":\"2025-04-02T10:00:00.000Z\",\"updatedAt\":\"2025-04-02T10:00:00.000Z\",\"body\":\"First!\",\"author\":" + alice + "}]}",
            await Reply(Db().CommentsOf(U("how-to-train-your-dragon"), 1)));
        Assert.Equal("{\"comments\":[]}", await Reply(Db().CommentsOf(U("dragons-2"), 1)));
        Assert.Equal(200, _response.Status);
        Assert.Equal("""{"errors":{"body":["article not found"]}}""", await Reply(Db().CommentsOf(U("missing"), 1).OrNotFound(ArticleNotFound)));
        Assert.Equal(404, _response.Status);
        Assert.Equal("""{"errors":{"body":["not found"]}}""", await Reply(Db().CommentsOf(U("missing"), 1)));
    }

    [Fact]
    public async Task CreateAndDeleteComments()
    {
        var db = Writer();
        string json = await Reply(db.CreateComment(4, U("Nice \"one\" ✓"), U("carols-two")).WithStatus(201));
        Assert.Equal(201, _response.Status);
        Assert.Matches("^\\{\"comment\":\\{\"id\":([0-9]+),\"createdAt\":\"20[0-9-]+T[0-9:.]+Z\",\"updatedAt\":\"[^\"]+\",\"body\":\"Nice \\\\\"one\\\\\" ✓\",\"author\":" +
            System.Text.RegularExpressions.Regex.Escape(SeedModel.Profile(4, 4, following: false)) + "\\}\\}$", json);
        long id = long.Parse(System.Text.RegularExpressions.Regex.Match(json, "\"id\":([0-9]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        await Reply(db.CreateComment(4, U("x"), U("no-such-article")));
        Assert.Equal(404, _response.Status);

        // someone else's: found, refused, not deleted
        _response.Next();
        var other = await db.DeleteComment(1, id, U("carols-two"));
        Assert.True(other.Refused);
        Assert.False(other.Found);
        // the wrong article: not found
        _response.Next();
        var wrong = await db.DeleteComment(4, id, U("carols-one"));
        Assert.False(wrong.Found);
        Assert.False(wrong.Refused);
        // the author's own: deleted, and gone after
        _response.Next();
        Assert.Equal(4, (await db.DeleteComment(4, id, U("carols-two"))).Value);
        _response.Next();
        Assert.False((await db.DeleteComment(4, id, U("carols-two"))).Found);
    }

    // ---------- writes of articles ----------

    [Fact]
    public async Task CreateArticleWithNewAndKnownTags()
    {
        var db = Writer();
        string title = "Tagged " + Unique("t");
        string newTag = Unique("tag");
        string json = await Reply(db.CreateArticle(3, U(title), U("D"), U("B"), Texts.List(newTag, "dragons", newTag)).WithStatus(201));
        Assert.Equal(201, _response.Status);
        var tags = new[] { "dragons", newTag }.OrderBy(t => t, StringComparer.Ordinal);
        Assert.Matches("^\\{\"article\":\\{\"slug\":\"tagged-t[0-9a-f]{12}-[0-9]+\",\"title\":\"" + title + "\",\"description\":\"D\",\"body\":\"B\"," +
            "\"createdAt\":\"[^\"]+\",\"updatedAt\":\"[^\"]+\",\"tagList\":\\[" + string.Join(",", tags.Select(t => "\"" + t + "\"")) + "\\],\"favoritesCount\":0,\"favorited\":false," +
            "\"author\":" + System.Text.RegularExpressions.Regex.Escape(SeedModel.Profile(3, 3)) + "\\}\\}$", json);
    }

    [Fact]
    public async Task CreateArticleWithoutTagsAndAwaited()
    {
        var db = Writer();
        string title = "Plain " + Unique("p");
        _response.Next();
        var row = await db.CreateArticle(1, U(title), U("D"), U("B"), Utf8List.Empty);
        Assert.True(row.Found);
        Assert.Equal(title, row.Value.Title.ToString());
        Assert.Equal("[]", row.Value.TagList.ToString());
        Assert.EndsWith("-" + row.Value.Slug.ToString().Split('-')[^1], row.Value.Slug.ToString());
        Assert.Equal("alice", row.Value.Author.Username.ToString());
        Assert.False(row.Value.Author.Following);
    }

    [Fact]
    public async Task UpdateArticleOwnOthersAndMissing()
    {
        var db = Writer();
        _response.Next();
        var made = (await db.CreateArticle(2, U("Old title"), U("D"), U("B"), Utf8List.Empty)).Value;
        // someone else's: refused
        Assert.Equal("""{"errors":{"body":["not the author"]}}""", await Reply(db.UpdateArticle(3, made.Slug, U("New"), null, null).OrForbidden(NotTheAuthor)));
        Assert.Equal(403, _response.Status);
        await Reply(db.UpdateArticle(3, made.Slug, U("New"), null, null));
        Assert.Equal(403, _response.Status);
        Assert.Equal("""{"errors":{"body":["forbidden"]}}""", _response.Text);
        // missing
        await Reply(db.UpdateArticle(2, U("no-such"), U("New"), null, null).OrNotFound(ArticleNotFound));
        Assert.Equal(404, _response.Status);
        // the author's own: a new title gives a new slug; no title keeps it
        string id = made.Slug.ToString().Split('-')[^1];
        string json = await Reply(db.UpdateArticle(2, U("old-title-" + id), U("New title"), null, U("New body")));
        Assert.Equal(200, _response.Status);
        Assert.StartsWith("{\"article\":{\"slug\":\"new-title-" + id + "\",\"title\":\"New title\",\"description\":\"D\",\"body\":\"New body\",", json);
        json = await Reply(db.UpdateArticle(2, U("new-title-" + id), null, U("New D"), null));
        Assert.StartsWith("{\"article\":{\"slug\":\"new-title-" + id + "\",\"title\":\"New title\",\"description\":\"New D\",\"body\":\"New body\",", json);
        // awaited: refused is its own state
        _response.Next();
        var refused = await db.UpdateArticle(3, U("new-title-" + id), U("x"), null, null);
        Assert.Equal(DataStatus.Ok, refused.Status);
        Assert.True(refused.Refused);
        Assert.False(refused.Found);
        _response.Next();
        var updated = await db.UpdateArticle(2, U("new-title-" + id), null, null, U("Body 3"));
        Assert.True(updated.Found);
        Assert.Equal("Body 3", updated.Value.Body.ToString());
    }

    [Fact]
    public async Task DeleteArticleTellsMissingOthersAndOwnApart()
    {
        var db = Writer();
        _response.Next();
        var made = (await db.CreateArticle(4, U("To delete"), U("D"), U("B"), Texts.List("dragons"))).Value;
        _response.Next();
        var missing = await db.DeleteArticle(U("no-such"), 4);
        Assert.False(missing.Found);
        _response.Next();
        var others = await db.DeleteArticle(made.Slug, 1);
        Assert.True(others.Refused); // someone else's: 403
        Assert.False(others.Found);
        Assert.False(missing.Refused);
        await Reply(db.ArticleBySlug(made.Slug, 0));
        Assert.Equal(200, _response.Status);
        _response.Next();
        Assert.Equal(4, (await db.DeleteArticle(made.Slug, 4)).Value);
        await Reply(db.ArticleBySlug(made.Slug, 0));
        Assert.Equal(404, _response.Status);
    }

    [Fact]
    public async Task FavoriteAndUnfavoriteCountRight()
    {
        var db = Writer();
        _response.Next();
        var made = (await db.CreateArticle(2, U("Fav"), U("D"), U("B"), Utf8List.Empty)).Value;
        string F(int count, bool favorited) => $"\"favoritesCount\":{count},\"favorited\":{(favorited ? "true" : "false")}";
        Assert.Contains(F(1, true), await Reply(db.Favorite(1, made.Slug)));
        Assert.Contains(F(1, true), await Reply(db.Favorite(1, made.Slug))); // again: no change
        Assert.Contains(F(2, true), await Reply(db.Favorite(4, made.Slug)));
        Assert.Contains(F(2, false), await Reply(db.ArticleBySlug(made.Slug, 3)));
        Assert.Contains(F(1, false), await Reply(db.Unfavorite(1, made.Slug)));
        Assert.Contains(F(1, false), await Reply(db.Unfavorite(1, made.Slug))); // again: no change
        await Reply(db.Favorite(1, U("no-such")));
        Assert.Equal(404, _response.Status);
    }

    [Fact]
    public async Task AWriteWithoutRowsIsNoContent()
    {
        var db = Writer();
        await Reply(db.AddTags(Texts.List(Unique("n"), Unique("m"))));
        Assert.Equal(204, _response.Status);
        Assert.Equal("", _response.Text);
        _response.Next();
        var r = await db.AddTags(Texts.List("dragons"));
        Assert.Equal(DataStatus.Ok, r.Status);
    }

    // ---------- tags ----------

    [Fact]
    public async Task AllTagsAsOneJsonArray() =>
        Assert.Equal("""{"tags":["dragons","training","zebra"]}""", await Reply(Db().AllTags()));
}
