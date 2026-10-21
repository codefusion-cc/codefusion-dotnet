using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodeFusion.Benchmarks.Reference;
using Conduit.Data;
using Npgsql;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// Each compiled Conduit query has the plan the contest server's hand-written statement (Reference/Sql.cs) has:
/// the generic plan, as the server's sessions use (plan_cache_mode = force_generic_plan), over a seed big enough
/// that Postgres picks indexes the way it does under load. Plans are compared node by node: node types, join
/// types, tables, indexes, scan directions, where each subplan hangs and which conditions a node has; not aliases,
/// costs, or the conditions' text.
/// </summary>
[Collection(nameof(ConduitCollection))]
public sealed class PlanTests
{
    const string Schema = "cfdata_plan";

    /// <summary>The seed the plans are made over, about the contest's dev seed in shape.</summary>
    const string Seed = """
        INSERT INTO users (username, email, password_hash, bio) SELECT 'user' || g, 'user' || g || '@example.com', 'x', CASE WHEN g % 2 = 0 THEN 'bio of ' || g END FROM generate_series(1, 5000) g;
        INSERT INTO tags (name) SELECT 'tag' || g FROM generate_series(1, 200) g;
        INSERT INTO articles (slug, author_id, title, description, body) SELECT 'article-' || g, 1 + g % 5000, 'Title ' || g, 'About ' || g, repeat('Body text ' || g || '. ', 30) FROM generate_series(1, 50000) g;
        INSERT INTO article_tags SELECT a.id, 1 + (a.id * k) % 200 FROM articles a, generate_series(1, 3) k ON CONFLICT DO NOTHING;
        INSERT INTO favorites (user_id, article_id) SELECT 1 + g * 7 % 5000, 1 + g * 13 % 50000 FROM generate_series(1, 100000) g ON CONFLICT DO NOTHING;
        INSERT INTO follows (follower_id, followee_id) SELECT 1 + g % 5000, 1 + g * 31 % 5000 FROM generate_series(1, 50000) g WHERE g % 5000 <> g * 31 % 5000 ON CONFLICT DO NOTHING;
        INSERT INTO comments (article_id, author_id, body) SELECT 1 + g % 50000, 1 + g % 5000, 'comment ' || g FROM generate_series(1, 100000) g;
        CREATE TABLE plan_seed_v1 ()
        """;

    /// <summary>Each compiled statement, the contest's statement it stands for, and that one's parameter types
    /// (the contest's Data/Db.cs).</summary>
    public static readonly TheoryData<string> Statements = new(Pairs.Keys);

    static Dictionary<string, (string Sql, DataType[] Types, string Reference, string[] ReferenceTypes)> Pairs => new()
    {
        ["Register"] = (ConduitDb.QuerySql.Register, ConduitDb.QuerySql.RegisterParameters.ToArray(), Sql.Register, ["text", "text", "text"]),
        ["UserByEmail"] = (ConduitDb.QuerySql.UserByEmail, ConduitDb.QuerySql.UserByEmailParameters.ToArray(), Sql.Login + OneRow, ["text"]),
        ["CurrentUser"] = (ConduitDb.QuerySql.CurrentUser, ConduitDb.QuerySql.CurrentUserParameters.ToArray(), Sql.CurrentUser + OneRow, ["int8"]),
        ["UpdateUser"] = (ConduitDb.QuerySql.UpdateUser, ConduitDb.QuerySql.UpdateUserParameters.ToArray(), Sql.UpdateUser, ["int8", "text", "text", "text", "text", "text"]),
        ["Profile"] = (ConduitDb.QuerySql.Profile, ConduitDb.QuerySql.ProfileParameters.ToArray(), Sql.Profile + OneRow, ["int8", "text"]),
        ["Follow"] = (ConduitDb.QuerySql.Follow, ConduitDb.QuerySql.FollowParameters.ToArray(), Sql.Follow, ["int8", "text"]),
        // the followee one of the ids found, as many as there are, where = (SELECT ...) takes one: an index condition on
        // the whole primary key, where the contest's scans the followee index and filters by follower
        ["Unfollow"] = (ConduitDb.QuerySql.Unfollow, ConduitDb.QuerySql.UnfollowParameters.ToArray(), Fixed(Sql.Unfollow, ("followee_id = (SELECT id FROM t)", "followee_id = ANY(ARRAY(SELECT id FROM t))")), ["int8", "text"]),
        ["AllArticles"] = (ConduitDb.QuerySql.AllArticles, ConduitDb.QuerySql.AllArticlesParameters.ToArray(), Sql.List[(int)Sql.ListKind.Global], ["int8", "int8", "int8"]),
        ["ArticlesByTag"] = (ConduitDb.QuerySql.ArticlesByTag, ConduitDb.QuerySql.ArticlesByTagParameters.ToArray(), Sql.List[(int)Sql.ListKind.Tag], ["int8", "int8", "int8", "text"]),
        ["ArticlesBy"] = (ConduitDb.QuerySql.ArticlesBy, ConduitDb.QuerySql.ArticlesByParameters.ToArray(), Sql.List[(int)Sql.ListKind.Author], ["int8", "int8", "int8", "text"]),
        ["FavoritedBy"] = (ConduitDb.QuerySql.FavoritedBy, ConduitDb.QuerySql.FavoritedByParameters.ToArray(), Sql.List[(int)Sql.ListKind.Favorited], ["int8", "int8", "int8", "text"]),
        ["Feed"] = (ConduitDb.QuerySql.Feed, ConduitDb.QuerySql.FeedParameters.ToArray(), Sql.List[(int)Sql.ListKind.Feed], ["int8", "int8", "int8", "int8"]),
        ["ArticleBySlug"] = (ConduitDb.QuerySql.ArticleBySlug, ConduitDb.QuerySql.ArticleBySlugParameters.ToArray(), Sql.ArticleBySlug + OneRow, ["int8", "text"]),
        ["CreateArticle"] = (ConduitDb.QuerySql.CreateArticle, ConduitDb.QuerySql.CreateArticleParameters.ToArray(), Sql.InsertArticle, ["int8", "text", "text", "text"]),
        // unnest in FROM, where a Where can filter it: a Function Scan for the ProjectSet over a Result, the same one pass
        ["CreateArticle_2"] = (ConduitDb.QuerySql.CreateArticle_2, ConduitDb.QuerySql.CreateArticle_2Parameters.ToArray(), Fixed(Sql.InsertTags,
            ("SELECT DISTINCT unnest($1::text[]) ORDER BY 1", "SELECT DISTINCT n.v FROM unnest($1::text[]) n(v) ORDER BY 1")), ["text[]"]),
        ["CreateArticle_3"] = (ConduitDb.QuerySql.CreateArticle_3, ConduitDb.QuerySql.CreateArticle_3Parameters.ToArray(), Sql.InsertArticleTags, ["text[]"]),
        ["CreateArticle_4"] = (ConduitDb.QuerySql.CreateArticle_4, ConduitDb.QuerySql.CreateArticle_4Parameters.ToArray(), Sql.InsertedArticle, ["int8"]),
        ["UpdateArticle"] = (ConduitDb.QuerySql.UpdateArticle, ConduitDb.QuerySql.UpdateArticleParameters.ToArray(), Fixed(Sql.UpdateArticle,
            ("WHERE slug = $2)", "WHERE slug = $2 FOR UPDATE)"), ("WHERE id = (SELECT id FROM t", "WHERE id IN (SELECT id FROM t"),
            ("FROM a JOIN users u ON u.id = a.author_id)", "FROM a JOIN users u ON u.id = a.author_id WHERE a.id = t.id)"), ("x ON true", "x ON true ORDER BY x.slug IS NULL, t.id")), ["int8", "text", "text", "text", "text"]),
        ["DeleteArticle"] = (ConduitDb.QuerySql.DeleteArticle, ConduitDb.QuerySql.DeleteArticleParameters.ToArray(), Fixed(Sql.DeleteArticle,
            ("WHERE slug = $2)", "WHERE slug = $2 FOR UPDATE)"), ("WHERE id = (SELECT id FROM t WHERE author_id = $1))", "WHERE id IN (SELECT id FROM t WHERE author_id = $1) RETURNING id)"),
            ("SELECT author_id FROM t", "SELECT t.author_id, d.id FROM t LEFT JOIN d ON d.id = t.id ORDER BY d.id IS NULL, t.id")), ["int8", "text"]),
        ["CommentsOf"] = (ConduitDb.QuerySql.CommentsOf, ConduitDb.QuerySql.CommentsOfParameters.ToArray(), Sql.Comments, ["int8", "text"]),
        ["CreateComment"] = (ConduitDb.QuerySql.CreateComment, ConduitDb.QuerySql.CreateCommentParameters.ToArray(), Sql.CreateComment, ["int8", "text", "text"]),
        ["DeleteComment"] = (ConduitDb.QuerySql.DeleteComment, ConduitDb.QuerySql.DeleteCommentParameters.ToArray(), Fixed(Sql.DeleteComment,
            ("AND a.slug = $3)", "AND a.slug = $3 FOR UPDATE OF c)"), ("WHERE id = (SELECT id FROM c WHERE author_id = $1))", "WHERE id IN (SELECT id FROM c WHERE author_id = $1) RETURNING id)"),
            ("SELECT author_id FROM c", "SELECT c.author_id, d.id FROM c LEFT JOIN d ON d.id = c.id ORDER BY d.id IS NULL, c.id")), ["int8", "int8", "text"]),
        ["AllTags"] = (ConduitDb.QuerySql.AllTags, ConduitDb.QuerySql.AllTagsParameters.ToArray(), Sql.Tags, []),
    };

    /// <summary>
    /// Compiled statements whose plan is another shape than the contest's, as their SQL is (the favorite toggles read
    /// the article from the CTE of the row found, which the contest's reads again by its key): each reads no index
    /// and no table the contest's doesn't, and no more often.
    /// </summary>
    public static readonly TheoryData<string> Fewer = new(FewerPairs.Keys);

    static Dictionary<string, (string Sql, DataType[] Types, string Reference, string[] ReferenceTypes)> FewerPairs => new()
    {
        ["Favorite"] = (ConduitDb.QuerySql.Favorite, ConduitDb.QuerySql.FavoriteParameters.ToArray(), Sql.Favorite, ["int8", "text"]),
        ["Unfavorite"] = (ConduitDb.QuerySql.Unfavorite, ConduitDb.QuerySql.UnfavoriteParameters.ToArray(), Sql.Unfavorite, ["int8", "text"]),
    };

    // The contest's statements with what the compiled ones do more: One reads one row (LIMIT 1); a conditional write
    // locks the rows it finds (a concurrent write between its read and its write is seen), writes each whose
    // condition holds (IN, where = fails on two), pairs each row with its own written one, and a delete marks the row
    // it deleted (RETURNING its key), so a refused one is told apart; the rows written come first (ORDER BY), so the
    // reply is a written row while there is one.
    const string OneRow = " LIMIT 1";

    static string Fixed(string sql, params (string From, string To)[] edits)
    {
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, sql, StringComparison.Ordinal);
            sql = sql.Replace(from, to, StringComparison.Ordinal);
        }
        return sql;
    }

    static readonly System.Threading.SemaphoreSlim SeedLock = new(1, 1);
    static bool _seeded;

    static async Task EnsureSeed()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            await using var db = TestDb.Admin();
            bool exists = (bool)(await db.Scalar($"SELECT to_regclass('{Schema}.plan_seed_v1') IS NOT NULL"))!;
            if (!exists) await TestDb.Create(Schema, Seed);
            _seeded = true;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public async Task TheCompiledQueryHasTheHandWrittenPlan(string name)
    {
        TestDb.Require();
        await EnsureSeed();
        var (sql, types, reference, referenceTypes) = Pairs[name];
        string ours = await Plan(sql, types.Select(TypeName).ToArray());
        string theirs = await Plan(reference, referenceTypes);
        Assert.True(theirs == ours, $"{name}: the contest's plan\n{theirs}\nthe compiled one\n{ours}");
    }

    [Theory]
    [MemberData(nameof(Fewer))]
    public async Task TheCompiledQueryReadsNoMoreThanTheHandWrittenOne(string name)
    {
        TestDb.Require();
        await EnsureSeed();
        var (sql, types, reference, referenceTypes) = FewerPairs[name];
        var ours = Scans(await PlanJson(sql, types.Select(TypeName).ToArray()));
        var theirs = Scans(await PlanJson(reference, referenceTypes));
        string both = $"{name}: the contest's reads\n{string.Join("\n", theirs)}\nthe compiled one's\n{string.Join("\n", ours)}";
        // no index or table read that theirs doesn't read (a delete may take another of their indexes, the primary key
        // where theirs takes the article's and filters), and no more reads
        foreach (var scan in ours) Assert.True(theirs.Any(t => Where(t) == Where(scan)), both);
        Assert.True(ours.Count <= theirs.Count, both);
        TestContext.Current.TestOutputHelper?.WriteLine(both);
    }

    /// <summary>A scan's relation and index, without the kind of scan.</summary>
    static string Where(string scan) => scan.Substring(0, scan.IndexOf(" | ", StringComparison.Ordinal));

    /// <summary>The scans of a plan, each as its relation, index (or none) and kind of scan.</summary>
    static List<string> Scans(JsonElement node)
    {
        var all = new List<string>();
        void Walk(JsonElement n)
        {
            if (n.TryGetProperty("Relation Name", out var rel))
                all.Add($"{rel.GetString()} {(n.TryGetProperty("Index Name", out var ix) ? ix.GetString() : "-")} | {n.GetProperty("Node Type").GetString()}");
            if (n.TryGetProperty("Plans", out var plans))
                foreach (var p in plans.EnumerateArray()) Walk(p);
        }
        Walk(node);
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    /// <summary>A plan's shape.</summary>
    [Fact]
    public async Task ThePlanShapeTellsADifferentPlanApart()
    {
        // the check itself: the same rows by a join instead of a scalar subquery is another plan
        TestDb.Require();
        await EnsureSeed();
        string scalar = await Plan("SELECT a.id FROM articles a WHERE a.author_id = (SELECT u.id FROM users u WHERE u.username = $1) ORDER BY a.id DESC LIMIT 100", ["text"]);
        string join = await Plan("SELECT a.id FROM articles a JOIN users u ON u.id = a.author_id WHERE u.username = $1 ORDER BY a.id DESC LIMIT 100", ["text"]);
        string again = await Plan("SELECT x.id FROM articles x WHERE x.author_id = (SELECT y.id FROM users y WHERE y.username = $1) ORDER BY x.id DESC LIMIT 100", ["text"]);
        Assert.NotEqual(scalar, join);
        Assert.Equal(scalar, again); // other aliases: the same plan
    }

    static string TypeName(DataType t) => t switch
    {
        DataType.Int64 => "int8",
        DataType.Int32 => "int4",
        DataType.Boolean => "bool",
        DataType.Double => "float8",
        DataType.Text => "text",
        DataType.Timestamp => "timestamptz",
        DataType.TextArray => "text[]",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    static string Argument(string type) => type switch
    {
        "text" => "'x'",
        "text[]" => "'{x}'",
        "bool" => "false",
        "timestamptz" => "now()",
        _ => "1",
    };

    /// <summary>The generic plan of <paramref name="sql"/>, as an indented outline of its shape.</summary>
    static async Task<string> Plan(string sql, string[] types)
    {
        var b = new StringBuilder();
        Outline(await PlanJson(sql, types), 0, b);
        return b.ToString();
    }

    /// <summary>The generic plan of <paramref name="sql"/>, as EXPLAIN writes it.</summary>
    static async Task<JsonElement> PlanJson(string sql, string[] types)
    {
        await using var db = TestDb.Admin(Schema);
        await using var conn = await db.OpenConnectionAsync();
        foreach (var set in new[] { "SET plan_cache_mode = force_generic_plan", "SET jit = off", "SET max_parallel_workers_per_gather = 0" })
            await Run(conn, set);
        string name = "p" + Guid.NewGuid().ToString("N");
        await Run(conn, $"PREPARE {name}{(types.Length == 0 ? "" : "(" + string.Join(", ", types) + ")")} AS {sql}");
        await using var explain = new NpgsqlCommand($"EXPLAIN (FORMAT JSON) EXECUTE {name}{(types.Length == 0 ? "" : "(" + string.Join(", ", types.Select(Argument)) + ")")}", conn);
        string json = (string)(await explain.ExecuteScalarAsync())!;
        await Run(conn, $"DEALLOCATE {name}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[0].GetProperty("Plan").Clone();
    }

    static async Task Run(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static readonly string[] Shape = ["Node Type", "Operation", "Strategy", "Join Type", "Relation Name", "Index Name", "Scan Direction", "Parent Relationship"];
    static readonly string[] Conditions = ["Filter", "Index Cond", "Recheck Cond", "Join Filter", "Hash Cond", "Merge Cond", "One-Time Filter"];

    static void Outline(JsonElement node, int depth, StringBuilder b)
    {
        b.Append(' ', depth * 2);
        foreach (var key in Shape)
            if (node.TryGetProperty(key, out var v)) b.Append(v.GetString()).Append(' ');
        // "InitPlan 2", "SubPlan 1", "CTE t": the kind, without its number or the CTE's alias
        if (node.TryGetProperty("Subplan Name", out var sub)) b.Append('[').Append(sub.GetString()!.Split(' ')[0]).Append("] ");
        foreach (var key in Conditions)
            if (node.TryGetProperty(key, out _)) b.Append('{').Append(key).Append("} ");
        if (node.TryGetProperty("Sort Key", out var sort)) b.Append("sort:").Append(sort.GetArrayLength()).Append(' ');
        b.Append('\n');
        if (node.TryGetProperty("Plans", out var plans))
            foreach (var p in plans.EnumerateArray()) Outline(p, depth + 1, b);
    }
}
