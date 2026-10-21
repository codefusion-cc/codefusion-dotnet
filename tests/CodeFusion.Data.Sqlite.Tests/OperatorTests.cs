using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Tests;
using Ops.Data;
using Xunit;
using Q = Ops.Data.OpsDb.QuerySql;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>The operator tests' database in SQLite: <see cref="Seed"/> twice, for the tests that read and those that write.</summary>
public sealed class OpsFixture : IAsyncLifetime
{
    /// <summary>The schema of CodeFusion.Data.Tests' operator tests: AUTOINCREMENT where a key is drawn or read back
    /// (sqlite_sequence holds it), booleans as 0 and 1, instants as integer microseconds.</summary>
    public const string Schema = """
        CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE people (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, nick TEXT, age INTEGER NOT NULL,
          score REAL, active INTEGER NOT NULL, born INTEGER NOT NULL, team_id INTEGER REFERENCES teams (id));
        CREATE TABLE pets (id INTEGER PRIMARY KEY, owner_id INTEGER NOT NULL REFERENCES people (id) ON DELETE CASCADE, name TEXT NOT NULL, legs INTEGER NOT NULL);
        CREATE TABLE labels (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE person_labels (person_id INTEGER NOT NULL REFERENCES people (id) ON DELETE CASCADE, label_id INTEGER NOT NULL REFERENCES labels (id),
          PRIMARY KEY (person_id, label_id)) WITHOUT ROWID;
        CREATE TABLE "Gadgets" (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
        CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL)
        """;

    /// <summary>CodeFusion.Data.Tests' seed, row for row.</summary>
    public static readonly string Seed = $"""
        INSERT INTO teams VALUES (1, 'red'), (2, 'blue');
        INSERT INTO people (id, name, nick, age, score, active, born, team_id) VALUES
          (1, 'ann', 'A', 30, 1.5, true, {ConduitFixture.Us("2000-01-01T00:00:00Z")}, 1),
          (2, 'ben', 'Benny', 25, NULL, false, {ConduitFixture.Us("1990-06-15T08:30:00Z")}, 2),
          (3, 'cid', 'c', 40, 3.25, true, {ConduitFixture.Us("1985-03-03T00:00:00Z")}, NULL),
          (4, 'dee', NULL, 30, 0, true, {ConduitFixture.Us("2001-12-31T23:59:59.999999Z")}, 1);
        UPDATE sqlite_sequence SET seq = 100 WHERE name = 'people';
        INSERT INTO pets VALUES (1, 1, 'rex', 4), (2, 1, 'tweety', 2), (3, 3, 'nemo', 0);
        INSERT INTO labels VALUES (1, 'admin'), (2, 'beta'), (3, 'gamma');
        INSERT INTO person_labels VALUES (1, 2), (1, 1), (3, 3), (4, 1);
        INSERT INTO items VALUES (1, 'public')
        """;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "cf-sqlite-ops-" + Guid.NewGuid().ToString("N")[..8]);

    public SqliteDatabase ReadsDatabase { get; private set; } = null!;
    public SqliteDatabase WritesDatabase { get; private set; } = null!;
    public SqliteDataSource Reads { get; private set; } = null!;
    public SqliteDataSource Writes { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        ReadsDatabase = Database("reads.db");
        WritesDatabase = Database("writes.db");
        Reads = ReadsDatabase.OpenSource();
        Writes = WritesDatabase.OpenSource();
        return default;
    }

    public SqliteDatabase Database(string name, int readers = 4) =>
        new(Path.Combine(_dir, name)) { Synchronous = SqliteSync.Normal, Readers = readers, Migrations = [Schema, Seed] };

    public ValueTask DisposeAsync()
    {
        SqliteChecks.Stop(Reads, Writes);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        return default;
    }
}

[CollectionDefinition(nameof(OpsTests), DisableParallelization = true)]
public sealed class OpsTests : ICollectionFixture<OpsFixture>;

/// <summary>
/// CodeFusion.Data.Tests' operator tests on SQLite: one query per operator the translator knows, its SQL in SQLite's
/// dialect exactly, and the same results on the same seed (worked out by hand from C#'s semantics: a C# comparison with
/// a null is false, == of two nulls true). Left out: a table of another schema (an attached database in SQLite).
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class OperatorTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    OpsDb Db(bool write = false)
    {
        var db = (write ? fixture.Writes : fixture.Reads).CreateContext<OpsDb>();
        db.Bind(_response);
        return db;
    }

    async Task<string> Json<TReply>(TReply reply) where TReply : IReply
    {
        _response.Next();
        await reply.WriteAsync(_response);
        return _response.Text;
    }

    /// <summary>The ids of a list of Name.</summary>
    async Task<string> Ids(Many<Name> names)
    {
        string json = await Json(names);
        Assert.Equal(200, _response.Status);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return string.Join(",", doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()));
    }

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    // ---------- Where ----------

    [Fact]
    public async Task Equality()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.name = ?1 LIMIT 1", Q.ByName);
        Assert.Equal("""{"id":1,"text":"ann"}""", await Json(Db().ByName(U("ann"))));
    }

    [Fact]
    public async Task ComparisonsAndLogic()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.age >= ?1 AND p.id <= ?2 AND (p.active OR NOT (p.age < 30)) AND p.name <> 'ben' ORDER BY p.id", Q.Comparisons);
        Assert.Equal("1,3,4", await Ids(Db().Comparisons(25, 4)));
        Assert.Equal("1", await Ids(Db().Comparisons(25, 2)));
        Assert.Equal("3", await Ids(Db().Comparisons(31, 4)));
    }

    [Fact]
    public async Task EqualityOfTwoNullablesHoldsForTwoNulls()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.nick IS ?1 ORDER BY p.id", Q.NickIs);
        Assert.Equal("4", await Ids(Db().NickIs(null)));
        Assert.Equal("1", await Ids(Db().NickIs(U("A"))));
        Assert.Equal("", await Ids(Db().NickIs(U("a"))));
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE NOT (p.nick IS ?1) ORDER BY p.id", Q.NickIsNot);
        Assert.Equal("1,2,3", await Ids(Db().NickIsNot(null)));
        // dee's NULL nick isn't "A": C# says true, where SQL's <> would say NULL
        Assert.Equal("2,3,4", await Ids(Db().NickIsNot(U("A"))));
    }

    [Fact]
    public async Task NullChecks()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE (p.nick IS NULL OR p.score IS NULL OR NOT (p.score IS NOT NULL)) ORDER BY p.id", Q.Nulls);
        Assert.Equal("2,4", await Ids(Db().Nulls()));
    }

    [Fact]
    public async Task AComparisonWithANullIsFalseAlsoUnderNot()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.score > ?1 ORDER BY p.id", Q.ScoreAbove);
        Assert.Equal("1,3", await Ids(Db().ScoreAbove(1.0)));
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE NOT COALESCE(p.score > ?1, false) ORDER BY p.id", Q.NotScoreAbove);
        Assert.Equal("2,4", await Ids(Db().NotScoreAbove(1.0)));
    }

    [Fact]
    public async Task ContainsIsAJsonArrayParameter()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.name IN (SELECT value FROM json_each(?1)) ORDER BY p.id", Q.InNames);
        Assert.Equal("1,3", await Ids(Db().InNames([U("ann"), U("cid"), U("zed")])));
        Assert.Equal("", await Ids(Db().InNames([])));
        Assert.Equal(Q.InNamesParameters.ToArray(), [DataType.TextArray]);
    }

    [Fact]
    public async Task AUtf8ListIsAJsonArrayParameterToo()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.name IN (SELECT value FROM json_each(?1)) ORDER BY p.id", Q.InList);
        Assert.Equal("2,4", await Ids(Db().InList(Texts.List("dee", "ben", "nobody"))));
        Assert.Equal("", await Ids(Db().InList(Utf8List.Empty)));
    }

    [Fact]
    public async Task NamesThatJsonMustEscapeAreFoundAsWritten()
    {
        var db = Db(write: true);
        string[] names = ["q\"uote", "back\\slash", "tab\tnew\nline", "naïve ✓", "😀", "ctl\u0001", "'single'"];
        foreach (var n in names) Assert.True((await Json(db.AddPerson(U(n), 70))).Length > 0, n);
        string json = await Json(Db(write: true).InNames([.. names.Select(U), U("q")]));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(names.Order(StringComparer.Ordinal), doc.RootElement.EnumerateArray().Select(e => e.GetProperty("text").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SingleIsAScalarSubqueryComparedPlainly()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.team_id = (SELECT t.id FROM teams t WHERE t.name = ?1) ORDER BY p.id", Q.OnTeam);
        Assert.Equal("1,4", await Ids(Db().OnTeam(U("red"))));
        Assert.Equal("", await Ids(Db().OnTeam(U("green"))));
    }

    [Fact]
    public async Task FunctionsAndArithmeticOnParameters()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.born < cf_now() ORDER BY p.id", Q.BornBeforeNow);
        Assert.Equal("1,2,3,4", await Ids(Db().BornBeforeNow()));
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.age > ?1 ORDER BY p.id", Q.OlderThanPlus);
        Assert.Equal(Q.OlderThanPlusParameters.ToArray(), [DataType.Int32]);
        Assert.Equal("1,3,4", await Ids(Db().OlderThanPlus(25, 4)));
    }

    // ---------- Select ----------

    [Fact]
    public async Task EveryScalarTypeAsJson()
    {
        Assert.Equal("SELECT p.id, p.name, p.nick, p.age, p.score, p.active, p.born FROM people p ORDER BY p.id", Q.Cards);
        Assert.Equal("""
            {"people":[{"id":1,"name":"ann","nick":"A","age":30,"score":1.5,"active":true,"born":"2000-01-01T00:00:00.000Z"},{"id":2,"name":"ben","nick":"Benny","age":25,"score":null,"active":false,"born":"1990-06-15T08:30:00.000Z"},{"id":3,"name":"cid","nick":"c","age":40,"score":3.25,"active":true,"born":"1985-03-03T00:00:00.000Z"},{"id":4,"name":"dee","nick":null,"age":30,"score":0,"active":true,"born":"2001-12-31T23:59:59.999Z"}]}
            """, await Json(Db().Cards()));
    }

    [Fact]
    public async Task CoalesceConditionalAndTemplates()
    {
        Assert.Equal("SELECT p.id, COALESCE(p.nick, p.name) FROM people p ORDER BY p.id", Q.NickOrName);
        Assert.Equal("""[{"id":1,"text":"A"},{"id":2,"text":"Benny"},{"id":3,"text":"c"},{"id":4,"text":"dee"}]""", await Json(Db().NickOrName()));
        Assert.Equal("SELECT p.id, (CASE WHEN p.active THEN p.name ELSE lower(COALESCE(p.nick, p.name)) END) FROM people p ORDER BY p.id", Q.Conditional);
        Assert.Equal("""[{"id":1,"text":"ann"},{"id":2,"text":"benny"},{"id":3,"text":"cid"},{"id":4,"text":"dee"}]""", await Json(Db().Conditional()));
        Assert.Equal("SELECT p.id, lower(p.name) FROM people p ORDER BY p.id", Q.WithOverride);
    }

    [Fact]
    public async Task ArithmeticInSql()
    {
        Assert.Equal("SELECT p.id, (((p.age * 2) + ?1) - (p.id % 2)) FROM people p ORDER BY p.id", Q.Arithmetic);
        Assert.Equal("""[{"id":1,"value":69},{"id":2,"value":60},{"id":3,"value":89},{"id":4,"value":70}]""", await Json(Db().Arithmetic(10)));
    }

    [Fact]
    public async Task ConditionsAsValues()
    {
        Assert.Equal("SELECT p.id, (p.age >= 30 AND p.active), (p.nick IS NOT NULL), (p.score IS NOT NULL) FROM people p ORDER BY p.id", Q.Conditions);
        Assert.Equal("""
            [{"id":1,"senior":true,"hasNick":true,"hasScore":true},{"id":2,"senior":false,"hasNick":true,"hasScore":false},{"id":3,"senior":true,"hasNick":true,"hasScore":true},{"id":4,"senior":true,"hasNick":false,"hasScore":true}]
            """, await Json(Db().Conditions()));
    }

    [Fact]
    public async Task InitializersAndOperatorsAfterSelect()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id", Q.Initializer);
        Assert.Equal("""[{"id":1,"name":"ann"},{"id":2,"name":"ben"},{"id":3,"name":"cid"},{"id":4,"name":"dee"}]""", await Json(Db().Initializer()));
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.id >= ?1 ORDER BY p.name DESC", Q.AfterSelect);
        Assert.Equal("4,3,2", await Ids(Db().AfterSelect(2)));
    }

    // ---------- order and paging ----------

    [Fact]
    public async Task OrderSkipTake()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.age, p.name DESC LIMIT ?2 OFFSET ?1", Q.Ordered);
        Assert.Equal("2,4,1,3", await Ids(Db().Ordered(0, 10)));
        Assert.Equal("4,1", await Ids(Db().Ordered(1, 2)));
        Assert.Equal("", await Ids(Db().Ordered(4, 2)));
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id DESC LIMIT ?1", Q.Top);
        Assert.Equal("4,3", await Ids(Db().Top(2)));
        Assert.Equal("", await Ids(Db().Top(0)));
    }

    // ---------- scalars ----------

    [Fact]
    public async Task CountAnyAndAsJson()
    {
        Assert.Equal("SELECT count(*) FROM people p WHERE p.active", Q.CountActive);
        Assert.Equal("3", await Json(Db().CountActive()));
        Assert.Equal("SELECT count(*) FROM (SELECT 1 FROM people p ORDER BY p.id LIMIT ?1) s", Q.CountFirst);
        Assert.Equal("2", await Json(Db().CountFirst(2)));
        Assert.Equal("4", await Json(Db().CountFirst(10)));
        Assert.Equal("SELECT EXISTS (SELECT 1 FROM people p WHERE p.age > ?1)", Q.AnyOlder);
        Assert.Equal("true", await Json(Db().AnyOlder(39)));
        Assert.Equal("false", await Json(Db().AnyOlder(40)));
        Assert.Equal("SELECT CASE WHEN count(*) = 0 THEN NULL ELSE json_group_array(p.name ORDER BY p.name) END FROM people p", Q.NamesAsJson);
        Assert.Equal("""["ann","ben","cid","dee"]""", await Json(Db().NamesAsJson()));
    }

    // An aggregate over no rows is NULL in SQL; RawJson reads a NULL as [], so the reply and the row both say an
    // empty list, never null.
    [Fact]
    public async Task AsJsonOfNoRowsIsAnEmptyList()
    {
        Assert.Equal("""["cid"]""", await Json(Db().NamesOverAgeAsJson(39)));
        Assert.Equal("[]", await Json(Db().NamesOverAgeAsJson(40)));
        Assert.Equal(200, _response.Status);
        Assert.Equal("[]", (await Db().NamesOverAgeAsJson(int.MaxValue)).Value.ToString());
    }

    [Fact]
    public async Task ScalarRowsReplyAndAwait()
    {
        Assert.Equal("40", await Json(Db().AgeOf(3)));
        Assert.Equal("\"Benny\"", await Json(Db().NickOf(2)));
        Assert.Equal("-1", await Json(Db().ScoreOf(2)));
        Assert.Equal("3.25", await Json(Db().ScoreOf(3)));
        Assert.Equal("\"2001-12-31T23:59:59.999Z\"", await Json(Db().BornOf(4)));
        Assert.Equal("SELECT length(p.name) FROM people p WHERE p.id = ?1 LIMIT 1", Q.NameLength);
        Assert.Equal("3", await Json(Db().NameLength(1)));
        await Json(Db().AgeOf(99));
        Assert.Equal(404, _response.Status);

        _response.Next();
        var db = Db();
        Assert.Equal(30, (await db.AgeOf(1)).Value);
        Assert.Equal("dee", (await db.NickOf(4)).Value.ToString());
        Assert.Equal(new Timestamp(1009843199999999), (await db.BornOf(4)).Value);
        Assert.Equal(3.25, (await db.ScoreOf(3)).Value);
        Assert.Null((await db.AgeOf(99)).OrNull);
        Assert.Equal("""["ann","ben","cid","dee"]""", (await db.NamesAsJson()).Value.ToString());
    }

    // ---------- navigations ----------

    [Fact]
    public async Task OneNavigationsJoin()
    {
        Assert.Equal("SELECT x.name, x.owner_id, p.name FROM pets x JOIN people p ON p.id = x.owner_id ORDER BY x.id", Q.Owners);
        Assert.Equal("""[{"pet":"rex","owner":{"id":1,"text":"ann"}},{"pet":"tweety","owner":{"id":1,"text":"ann"}},{"pet":"nemo","owner":{"id":3,"text":"cid"}}]""",
            await Json(Db().Owners()));
        Assert.Equal("SELECT p.name, t.name FROM people p LEFT JOIN teams t ON t.id = p.team_id ORDER BY p.id", Q.Teamed);
        Assert.Equal("""[{"name":"ann","team":"red"},{"name":"ben","team":"blue"},{"name":"cid","team":null},{"name":"dee","team":"red"}]""", await Json(Db().Teamed()));
    }

    [Fact]
    public async Task ManyNavigationsAreCorrelatedSubqueries()
    {
        Assert.Equal("SELECT p.id, (SELECT count(*) FROM pets p2 WHERE p2.owner_id = p.id), (EXISTS (SELECT 1 FROM pets p3 WHERE p3.owner_id = p.id AND p3.legs > 2)), " +
            "(SELECT json_group_array(v) FROM (SELECT x.name AS v FROM pets x WHERE x.owner_id = p.id AND x.legs > 0 ORDER BY 1 LIMIT 5)), " +
            "(SELECT json_group_array(v2) FROM (SELECT (SELECT l.name FROM labels l WHERE l.id = pl.label_id) AS v2 FROM person_labels pl WHERE pl.person_id = p.id ORDER BY 1)), " +
            "(EXISTS (SELECT 1 FROM person_labels pl2 WHERE pl2.person_id = p.id AND (SELECT l2.name FROM labels l2 WHERE l2.id = pl2.label_id) = ?1)) FROM people p ORDER BY p.id", Q.Stats);
        Assert.Equal("""
            [{"id":1,"pets":2,"hasQuadruped":true,"named":["rex","tweety"],"labels":["admin","beta"],"isAdmin":true},{"id":2,"pets":0,"hasQuadruped":false,"named":[],"labels":[],"isAdmin":false},{"id":3,"pets":1,"hasQuadruped":false,"named":[],"labels":["gamma"],"isAdmin":false},{"id":4,"pets":0,"hasQuadruped":false,"named":[],"labels":["admin"],"isAdmin":true}]
            """, await Json(Db().Stats(U("admin"))));
        Assert.Equal("SELECT t.id, (SELECT count(*) FROM people p WHERE p.team_id = t.id AND p.age >= ?1) FROM teams t ORDER BY t.id", Q.MemberCounts);
        Assert.Equal("""[{"id":1,"value":2},{"id":2,"value":0}]""", await Json(Db().MemberCounts(30)));
    }

    // ---------- pages ----------

    [Fact]
    public async Task APageOfTheTablesOwnKeyWithALimitedCount()
    {
        // no LATERAL: the page's rows are a plain subquery, joined on true
        Assert.Equal("WITH c AS (SELECT count(*) AS n FROM (SELECT 1 FROM people p3 WHERE p3.active LIMIT 2) s) SELECT p.*, c.n FROM c LEFT JOIN (" +
            "SELECT p5.id AS c0, p5.name AS c1, p5.nick AS c2, p5.age AS c3, p5.score AS c4, p5.active AS c5, p5.born AS c6, p5.id AS k FROM (SELECT id FROM (" +
            "SELECT p4.id AS id FROM people p4 WHERE p4.active ORDER BY p4.id DESC LIMIT 100 OFFSET ?1) b ORDER BY id DESC LIMIT ?2) p2 JOIN people p5 ON p5.id = p2.id " +
            "ORDER BY p5.id DESC) p ON true ORDER BY p.k DESC", Q.CardPage);
        string json = await Json(Db().CardPage(new Paging(2, 0)));
        Assert.StartsWith("""{"people":[{"id":4,""", json);
        Assert.Contains("""{"id":3,""", json);
        Assert.EndsWith("""}],"total":2}""", json); // three active, counted up to two
        Assert.Equal("""{"people":[],"total":2}""", await Json(Db().CardPage(new Paging(2, 3))));
    }

    [Fact]
    public async Task APageOfALinkTableWithAProbedCount()
    {
        Assert.Equal("WITH c AS (SELECT CASE WHEN EXISTS (SELECT 1 FROM person_labels pl WHERE pl.label_id = (SELECT l.id FROM labels l WHERE l.name = ?1) ORDER BY pl.person_id LIMIT 1 OFFSET 9) " +
            "THEN 10 ELSE (SELECT count(*) FROM person_labels pl2 WHERE pl2.label_id = (SELECT l2.id FROM labels l2 WHERE l2.name = ?1)) END AS n) " +
            "SELECT p.*, c.n FROM c LEFT JOIN (SELECT p3.id AS c0, p3.name AS c1, p3.id AS k FROM (SELECT id FROM (SELECT pl3.person_id AS id FROM person_labels pl3 " +
            "WHERE pl3.label_id = (SELECT l3.id FROM labels l3 WHERE l3.name = ?1) ORDER BY pl3.person_id LIMIT 3 OFFSET ?2) b ORDER BY id LIMIT ?3) p2 " +
            "JOIN people p3 ON p3.id = p2.id ORDER BY p3.id) p ON true ORDER BY p.k", Q.LabelPage);
        Assert.Equal("""{"items":[{"id":1,"text":"ann"},{"id":4,"text":"dee"}],"count":2}""", await Json(Db().LabelPage(U("admin"), new Paging(5, 0))));
        Assert.Equal("""{"items":[{"id":4,"text":"dee"}],"count":2}""", await Json(Db().LabelPage(U("admin"), new Paging(1, 1))));
        Assert.Equal("""{"items":[],"count":0}""", await Json(Db().LabelPage(U("nope"), new Paging(50, 0))));
    }

    [Fact]
    public async Task ChildrenOfARowTellAMissingRowFromNone()
    {
        // no LATERAL: the children are the rows whose key is in the correlated subquery's
        Assert.Equal("SELECT x2.name, p.id, p.name, x2.id FROM people p LEFT JOIN pets x2 ON x2.id IN (SELECT x.id FROM pets x WHERE x.owner_id = p.id ORDER BY x.name) " +
            "WHERE p.name = ?1 ORDER BY x2.name", Q.PetsOf);
        Assert.Equal("""[{"pet":"rex","owner":{"id":1,"text":"ann"}},{"pet":"tweety","owner":{"id":1,"text":"ann"}}]""", await Json(Db().PetsOf(U("ann"))));
        Assert.Equal("[]", await Json(Db().PetsOf(U("ben"))));
        Assert.Equal(200, _response.Status);
        await Json(Db().PetsOf(U("zed")));
        Assert.Equal(404, _response.Status);
    }

    // ---------- writes ----------

    [Fact]
    public async Task InsertsAndTheirReturns()
    {
        var db = Db(write: true);
        Assert.Equal("INSERT INTO people (name, age, active, born) VALUES (?1, ?2, true, cf_now()) RETURNING id, name", Q.AddPerson);
        Assert.Matches("""^\{"id":[0-9]{3,},"text":"eve"\}$""", await Json(db.AddPerson(U("eve"), 22)));
        await Json(db.AddPerson(U("eve"), 23));
        Assert.Equal(409, _response.Status);

        // no writable CTE: a navigation in RETURNING is a subquery of the row written
        Assert.Equal("INSERT INTO pets (id, owner_id, name, legs) VALUES (?1, ?2, ?3, 4) RETURNING name, pets.owner_id, (SELECT p.name FROM people p WHERE p.id = pets.owner_id)", Q.AddPet);
        Assert.Equal("""{"pet":"spot","owner":{"id":4,"text":"dee"}}""", await Json(db.AddPet(50, 4, U("spot"))));

        Assert.Equal("INSERT INTO labels (id, name) VALUES (?1, ?2) ON CONFLICT DO NOTHING", Q.AddLabelOnce);
        await Json(db.AddLabelOnce(9, U("delta")));
        Assert.Equal(204, _response.Status);
        await Json(db.AddLabelOnce(9, U("delta")));
        Assert.Equal(204, _response.Status);
    }

    [Fact]
    public async Task AUnitOfWritesWithItsNewKey()
    {
        // the new key is the next of sqlite_sequence's: one writer, so nothing draws it in between
        Assert.Equal("INSERT INTO people (id, name, nick, age, active, born, team_id) SELECT n.id, ?1, lower(?1), 1, false, cf_now(), (n.id % 2) + 1 " +
            "FROM (SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'people'), 0) + 1 AS id) n", Q.AddNicknamed);
        Assert.Equal("INSERT INTO pets (id, owner_id, name, legs) SELECT (x.id + 1000) + (SELECT seq FROM sqlite_sequence WHERE name = 'people'), " +
            "(SELECT seq FROM sqlite_sequence WHERE name = 'people'), x.name, x.legs FROM pets x WHERE x.id = 1", Q.AddNicknamed_2);
        Assert.Equal("UPDATE people SET score = ?1 WHERE name = ?2", Q.AddNicknamed_3);
        Assert.Equal("SELECT n.id, COALESCE(n.nick, n.name) FROM people n WHERE n.id = (SELECT (SELECT seq FROM sqlite_sequence WHERE name = 'people'))", Q.AddNicknamed_4);
        var db = Db(write: true);
        _response.Next();
        var zelda = (await db.AddNicknamed(U("Zelda"))).Value;
        Assert.Equal("zelda", zelda.Text.ToString());
        _response.Next();
        var bo = (await db.AddNicknamed(U("Bo"))).Value;
        Assert.Equal(zelda.Id + 1, bo.Id);
        // the ThenIf ran for the long name only; each got its copy of pet 1 and a team by its id
        Assert.Equal("5.0", Scalar($"SELECT score FROM people WHERE id = {zelda.Id}"));
        Assert.Null(Scalar($"SELECT score FROM people WHERE id = {bo.Id}"));
        Assert.Equal("rex", Scalar($"SELECT name FROM pets WHERE owner_id = {zelda.Id} AND id = {1001 + zelda.Id}"));
        Assert.Equal($"{zelda.Id % 2 + 1}", Scalar($"SELECT team_id FROM people WHERE id = {zelda.Id}"));
    }

    [Fact]
    public async Task UpdatesConditionalAndNot()
    {
        var db = Db(write: true);
        // rows of this test's own, keyed in a range of its own (10000s, 20000s, ... per test): in SQLite a key written
        // moves an AUTOINCREMENT table's sequence, so the keys drawn after it follow it, where a later test's would be
        await Execute("INSERT INTO people (id, name, age, active, born, team_id) VALUES (10201, 'u201', 50, true, 0, 2), (10202, 'u202', 50, false, 0, NULL), (10203, 'u203', 50, true, 0, NULL)");
        Assert.Equal("UPDATE people SET name = ?1, age = age + 1 WHERE id = ?2 RETURNING id, name", Q.Rename);
        Assert.Equal("""{"id":10202,"text":"renamed"}""", await Json(db.Rename(10202, U("renamed"))));
        Assert.Equal("51", Scalar("SELECT age FROM people WHERE id = 10202"));
        await Json(db.Rename(999, U("x")));
        Assert.Equal(404, _response.Status);

        // the write with its rows, then the rows found but refused: one writer, so nothing changes them in between
        Assert.Equal("UPDATE people SET name = ?1 WHERE id IN (SELECT p.id FROM people p WHERE p.id = ?2 AND p.active) RETURNING name, (SELECT t.name FROM teams t WHERE t.id = people.team_id), id",
            Q.RenameIfActive);
        Assert.Equal("SELECT NULL, NULL, NULL FROM people p WHERE p.id = ?1 AND NOT COALESCE(p.active, false) ORDER BY p.id", Q.RenameIfActive_2);
        Assert.Equal("""{"name":"u201b","team":"blue"}""", await Json(db.RenameIfActive(10201, U("u201b"))));
        Assert.Equal("""{"name":"u203b","team":null}""", await Json(db.RenameIfActive(10203, U("u203b"))));
        await Json(db.RenameIfActive(10202, U("refused")));
        Assert.Equal(403, _response.Status);
        Assert.Equal("renamed", Scalar("SELECT name FROM people WHERE id = 10202"));
        await Json(db.RenameIfActive(999, U("missing")));
        Assert.Equal(404, _response.Status);

        Assert.Equal("UPDATE people SET active = false WHERE age > ?1", Q.Deactivate);
        await Json(db.Deactivate(49));
        Assert.Equal(204, _response.Status);
        Assert.Equal("0", Scalar("SELECT count(*) FROM people WHERE age > 49 AND active"));
    }

    [Fact]
    public async Task Deletes()
    {
        var db = Db(write: true);
        await Json(db.AddPet(60, 3, U("goldie")));
        Assert.Equal("DELETE FROM pets WHERE id IN (SELECT x.id FROM pets x WHERE x.id = ?1 AND x.legs = ?2) RETURNING owner_id, id", Q.DeletePetIf);
        Assert.Equal("SELECT x.owner_id, NULL FROM pets x WHERE x.id = ?1 AND NOT COALESCE((x.legs = ?2), false) ORDER BY x.id", Q.DeletePetIf_2);
        _response.Next();
        var kept = await db.DeletePetIf(60, 2);
        Assert.False(kept.Found);
        Assert.True(kept.Refused);
        _response.Next();
        Assert.Equal(3, (await db.DeletePetIf(60, 4)).Value);
        _response.Next();
        Assert.False((await db.DeletePetIf(60, 4)).Found);
        Assert.Equal("DELETE FROM pets WHERE owner_id = ?1", Q.DeletePets);
        await Json(db.DeletePets(424242));
        Assert.Equal(204, _response.Status);
    }

    // ---------- SQL ----------

    [Fact]
    public async Task SqlHolesAreParametersAndConstantsAreSql()
    {
        Assert.Equal("SELECT id, name FROM people WHERE age >= ?1 AND name <> ?2 AND age < 100 ORDER BY id", Q.Raw);
        Assert.Equal("1,3", await Ids(Db().Raw(26, U("dee"))));
        Assert.Equal("UPDATE people SET age = age WHERE id = ?1 RETURNING id, name", Q.RawWrite);
        Assert.Equal("""{"id":3,"text":"cid"}""", await Json(Db(write: true).RawWrite(3)));
        await Json(Db(write: true).RawWrite(999));
        Assert.Equal(404, _response.Status);
    }

    [Fact]
    public async Task AConstantStringHoleIsAParameterToo()
    {
        Assert.Equal("SELECT id FROM people WHERE name = ?1", Q.IdOfAnn);
        Assert.Equal("1", await Json(Db().IdOfAnn()));
    }

    [Fact]
    public async Task JsonTextIsJsonAsAReplyAndAwaited()
    {
        Assert.Equal("""{"a":1}""", await Json(Db().Jsonb()));
        Assert.Equal("""{"id":1,"meta":[1,"x"],"plain":[2]}""", await Json(Db().Docs()));
        _response.Next();
        var db = Db();
        Assert.Equal("""{"a":1}""", (await db.Jsonb()).Value.ToString());
        var docs = (await db.Docs()).Value;
        Assert.Equal("""[1,"x"]""", docs.Meta.ToString());
        Assert.Equal("[2]", docs.Plain.ToString());
    }

    // ---------- operators that don't commute ----------

    [Fact]
    public async Task SkipAndTakeComposeAsInCSharp()
    {
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id LIMIT max(?1 - ?2, 0) OFFSET ?2", Q.TakeThenSkip);
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id LIMIT min(max(?2 - ?3, 0), ?4) OFFSET ?1 + ?3", Q.Windows);
        Assert.Equal("2,3", await Ids(Db().TakeThenSkip(3, 1)));
        Assert.Equal("", await Ids(Db().TakeThenSkip(2, 5)));
        Assert.Equal("1,2,3,4", await Ids(Db().TakeThenSkip(4, 0)));
        Assert.Equal("3,4", await Ids(Db().Windows(1, 3, 1, 5)));
        Assert.Equal("2", await Ids(Db().Windows(0, 2, 1, 5)));
        Assert.Equal("1", await Ids(Db().Windows(0, 10, 0, 1)));
        Assert.Equal("", await Ids(Db().Windows(5, 10, 0, 10)));
    }

    [Fact]
    public async Task ANegativeTakeOrSkipIsNoneOrAll()
    {
        // SQLite reads a negative LIMIT as no limit at all: C#'s none must not become all
        Assert.Equal("", await Ids(Db().Top(-1)));
        Assert.Equal("", await Ids(Db().Top(long.MinValue)));
        Assert.Equal("2,4", await Ids(Db().Ordered(-1, 2)));
        Assert.Equal("", await Ids(Db().Ordered(1, -5)));
        Assert.Equal("", await Ids(Db().TakeThenSkip(-3, 1)));
        Assert.Equal("1,2", await Ids(Db().TakeThenSkip(2, -1)));
        Assert.Equal("", await Ids(Db().Windows(0, -1, 0, 5)));
        Assert.Equal("", await Ids(Db().Windows(0, 5, 0, -1)));
    }

    [Fact]
    public async Task ALaterOrderByOrdersFirstAndKeepsTheEarlierOrderForTies()
    {
        Assert.Equal("4,3,2,1", await Ids(Db().ReorderedByName()));
        Assert.Equal("2,4,1,3", await Ids(Db().ReorderedByAge()));
        Assert.Equal("""
            [{"id":1,"first":["rex"],"byName":["rex","tweety"]},{"id":2,"first":[],"byName":[]},{"id":3,"first":["nemo"],"byName":["nemo"]},{"id":4,"first":[],"byName":[]}]
            """, await Json(Db().PetOrders()));
    }

    // ---------- comparisons of conditions, keywords ----------

    [Fact]
    public async Task ConditionsComparedWithEachOther()
    {
        Assert.Equal("2,3", await Ids(Db().SameSeniority()));
        Assert.Equal("1,3", await Ids(Db().MixedFlags()));
    }

    [Fact]
    public async Task ALambdaNamedAfterAKeyword()
    {
        Assert.Equal("1,3,4", await Ids(Db().KeywordAliases(26)));
    }

    // ---------- numbers ----------

    [Fact]
    public async Task NumericConversionsAreSqlsToo()
    {
        Assert.Equal("12.5", await Json(Db().HalfAge(2)));
        Assert.Equal("37.5", await Json(Db().AgeAndAHalf(2)));
        Assert.Equal("25", await Json(Db().AgeAsDouble(2)));
        // a count into a double, and a double into an int as C# casts it: toward zero
        Assert.Equal("""[{"id":1,"pets":2,"whole":1},{"id":2,"pets":0,"whole":0},{"id":3,"pets":1,"whole":3},{"id":4,"pets":0,"whole":0}]""", await Json(Db().Ratios()));
        // C#'s 61 / 2.0 is 30.5, not 30
        Assert.Equal("3", await Ids(Db().OlderThanHalf(61)));
        Assert.Equal("1,3,4", await Ids(Db().OlderThanHalf(60)));
        _response.Next();
        var db = Db();
        Assert.Equal(12.5, (await db.HalfAge(2)).Value);
        Assert.Equal(37.5, (await db.AgeAndAHalf(2)).Value);
        Assert.Equal(25.0, (await db.AgeAsDouble(2)).Value);
    }

    // ---------- writes: keys of earlier inserts, several rows, navigations ----------

    [Fact]
    public async Task AnInsertedKeyOfATableNeedingQuotes()
    {
        // sqlite_sequence holds the table's name as it is, without the quotes
        Assert.Equal("SELECT g.id, g.name FROM \"Gadgets\" g WHERE g.id = (SELECT (SELECT seq FROM sqlite_sequence WHERE name = 'Gadgets'))", Q.AddGadget_2);
        var db = Db(write: true);
        Assert.Matches("""^\{"id":[0-9]+,"text":"g1"\}$""", await Json(db.AddGadget(U("g1"))));
        Assert.Equal(200, _response.Status);
        Assert.Matches("""^\{"id":[0-9]+,"text":"g2"\}$""", await Json(db.AddGadget(U("g2"))));
    }

    [Fact]
    public async Task AKeyDrawnInCSharpIsDrawnOnceAndUsedByEachLaterStatement()
    {
        var db = Db(write: true);
        long before = Keys.Calls;
        string json = await Json(db.AddKeyed(U("keyed")));
        Assert.Equal(200, _response.Status);
        Assert.Equal(2, Keys.Calls - before);
        long person = 7000 + before + 1;
        Assert.Equal($$"""{"id":{{person}},"text":"keyed"}""", json);
        Assert.Equal($"{person}", Scalar($"SELECT owner_id FROM pets WHERE id = {person + 1}"));
    }

    [Fact]
    public async Task TwoCallsWrittenAlikeInOneInsertAreTwoValues()
    {
        var db = Db(write: true);
        long before = Keys.Calls;
        string json = await Json(db.AddLabelled());
        Assert.Equal(200, _response.Status);
        Assert.Equal(2, Keys.Calls - before);
        string name = $"k{7000 + before + 1}", nick = $"k{7000 + before + 2}";
        Assert.Matches($$"""^\{"id":\d+,"text":"{{name}}"\}$""", json);
        Assert.Equal(nick, Scalar($"SELECT nick FROM people WHERE name = '{name}'"));
    }

    [Fact]
    public async Task AKeyPast2To53ComparedAsADoubleIsWrittenWithEveryDigit()
    {
        var db = Db(write: true);
        long key = (1L << 53) + 2 * (Keys.HugeCalls + 1) - 1;
        Assert.NotEqual(key, (long)(double)key);
        await Json(db.AddHugeOf(U("huge")));
        Assert.Equal(204, _response.Status);
        Assert.Equal($"{key}", Scalar("SELECT p.id FROM pets p JOIN people o ON o.id = p.owner_id WHERE o.name = 'huge' AND o.active"));
    }

    [Fact]
    public async Task AnInlinedMethodsArgumentRunsOnceWhetherItsBodyUsesItOrNot()
    {
        var db = Db(write: true);
        long before = Keys.Calls;
        await Json(db.AuditedOf(U("audited")));
        Assert.Equal(204, _response.Status);
        Assert.Equal(1, Keys.Calls - before);
        // used only under a ThenIf that doesn't hold: made at the call all the same
        before = Keys.Calls;
        await Json(db.MaybePetOf(false, U("petless")));
        Assert.Equal(204, _response.Status);
        Assert.Equal(1, Keys.Calls - before);
        Assert.Equal("0", Scalar("SELECT count(*) FROM pets p JOIN people o ON o.id = p.owner_id WHERE o.name = 'petless'"));
        // and when it holds, the pet's key is that call's
        before = Keys.Calls;
        await Json(db.MaybePetOf(true, U("petted")));
        Assert.Equal(204, _response.Status);
        Assert.Equal(1, Keys.Calls - before);
        Assert.Equal($"{7000 + before + 1}", Scalar("SELECT p.id FROM pets p JOIN people o ON o.id = p.owner_id WHERE o.name = 'petted'"));
    }

    [Fact]
    public async Task AnIgnoredInsertsRowIsTheOneItsKeyHolds()
    {
        var db = Db(write: true);
        Assert.Equal("""{"id":7,"text":"seven"}""", await Json(db.LabelOnce(7, U("seven"))));
        Assert.Equal("""{"id":7,"text":"seven"}""", await Json(db.LabelOnce(7, U("other"))));
    }

    [Fact]
    public async Task AConditionalWriteOfSeveralRowsWritesEachWhoseConditionHolds()
    {
        var db = Db(write: true);
        await Execute("INSERT INTO teams VALUES (3, 'green'); INSERT INTO people (id, name, age, active, born, team_id) VALUES " +
            "(20311, 'dt311', 50, true, 0, 3), (20312, 'dt312', 50, true, 0, 3), (20313, 'dt313', 50, false, 0, 3), (20321, 'dp321', 50, true, 0, NULL); " +
            "INSERT INTO pets VALUES (3211, 20321, 'a', 4), (3212, 20321, 'b', 4), (3213, 20321, 'c', 2)");
        await Json(db.DeactivateTeam(3));
        Assert.Equal(204, _response.Status);
        Assert.Equal("0", Scalar("SELECT count(*) FROM people WHERE team_id = 3 AND active"));
        await Json(db.DeletePetsIf(20321, 4));
        Assert.Equal(204, _response.Status);
        Assert.Equal("3213", Column("SELECT id FROM pets WHERE owner_id = 20321 ORDER BY id"));
    }

    [Fact]
    public async Task AConditionalWriteOfSeveralRowsGivesBackTheFirstWrittenOrRefusedWhenNoneWas()
    {
        var db = Db(write: true);
        await Execute("INSERT INTO teams VALUES (4, 'gray'); INSERT INTO people (id, name, age, active, born, team_id) VALUES " +
            "(30391, 'gt391', 50, false, 0, 4), (30393, 'gt393', 50, true, 0, 4), (30392, 'gt392', 50, true, 0, 4), (30381, 'po381', 50, true, 0, NULL); " +
            "INSERT INTO pets VALUES (3811, 30381, 'two', 2), (3813, 30381, 'four', 4), (3812, 30381, 'four', 4)");
        _response.Next();
        var deleted = await db.DeleteAPetIf(30381, 4);
        Assert.False(deleted.Refused);
        Assert.Equal(3812, deleted.Value);
        Assert.Equal("3811", Column("SELECT id FROM pets WHERE owner_id = 30381 ORDER BY id"));
        _response.Next();
        var refused = await db.DeleteAPetIf(30381, 4);
        Assert.True(refused.Refused);
        Assert.Equal("""{"id":30392,"text":"gt392"}""", await Json(db.NickTeamIfActive(4, U("n"))));
        Assert.Equal(200, _response.Status);
        Assert.Equal("2", Scalar("SELECT count(*) FROM people WHERE team_id = 4 AND nick = 'n'"));
        await Json(db.NickTeamIfActive(2, U("n")));
        Assert.Equal(403, _response.Status);
    }

    [Fact]
    public async Task AConditionalDeleteSeesTheRowAsAnotherConnectionsWriteLeftIt()
    {
        var db = Db(write: true);
        await Execute("INSERT INTO pets VALUES (3311, 3, 'locked', 4)");
        // another connection (another process, say) holds the file's write lock with a change not yet committed
        using var other = SqliteConn.Open(fixture.WritesDatabase.Path, fixture.WritesDatabase, writer: true);
        Assert.Equal(0, other.Once(new SqliteStatement("BEGIN IMMEDIATE")));
        Assert.Equal(0, other.Once(new SqliteStatement("UPDATE pets SET legs = 2 WHERE id = 3311")));
        // the delete waits for the lock, then sees its legs: 2, so the condition fails
        _response.Next();
        var pending = Awaited(db.DeletePetIf(3311, 4));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, other.Once(new SqliteStatement("COMMIT")));
        var r = await pending;
        Assert.True(r.Refused);
        Assert.Equal("2", Scalar("SELECT legs FROM pets WHERE id = 3311"));
    }

    static async Task<Row<long>> Awaited(One<long> one) => await one;

    static readonly ErrorReply NotYours = new(403, """{"errors":{"body":["not yours"]}}"""u8);

    [Fact]
    public async Task ARefusedDeleteIsForbidden()
    {
        var db = Db(write: true);
        await Execute("INSERT INTO pets VALUES (3611, 3, 'kept', 4)");
        Assert.Equal("""{"errors":{"body":["not yours"]}}""", await Json(db.DeletePetIf(3611, 2).OrForbidden(NotYours)));
        Assert.Equal(403, _response.Status);
        _response.Next();
        var refused = await db.DeletePetIf(3611, 2);
        Assert.True(refused.Refused);
        Assert.False(refused.Found);
        Assert.Equal("1", Scalar("SELECT count(*) FROM pets WHERE id = 3611"));
        _response.Next();
        var deleted = await db.DeletePetIf(3611, 4);
        Assert.False(deleted.Refused);
        Assert.Equal(3, deleted.Value);
        Assert.Equal("0", Scalar("SELECT count(*) FROM pets WHERE id = 3611"));
    }

    [Fact]
    public async Task ANavigationInAnUpdateOrDeleteIsOfTheRowWritten()
    {
        var db = Db(write: true);
        await Execute("INSERT INTO people (id, name, age, active, born) VALUES (40341, 'np341', 77, true, 0), (40342, 'np342', 77, true, 0), " +
            "(40351, 'cp351', 78, false, 0), (40352, 'cp352', 78, false, 0); INSERT INTO pets VALUES (3411, 40341, 'x', 4), (3511, 40351, 'y', 4), (3512, 40351, 'z', 4)");
        Assert.Equal("DELETE FROM people WHERE age = ?1 AND NOT (EXISTS (SELECT 1 FROM pets p WHERE p.owner_id = people.id))", Q.DeletePetless);
        await Json(db.DeletePetless(77));
        Assert.Equal("40341", Column("SELECT id FROM people WHERE age = 77 ORDER BY id"));
        Assert.Equal("UPDATE people SET score = CAST((SELECT count(*) FROM pets p WHERE p.owner_id = people.id) AS double precision) WHERE age = ?1", Q.CountPetsIntoScore);
        await Json(db.CountPetsIntoScore(78));
        Assert.Equal("2.0", Scalar("SELECT score FROM people WHERE id = 40351"));
        Assert.Equal("0.0", Scalar("SELECT score FROM people WHERE id = 40352"));
        Assert.Equal("""{"id":40351,"value":2}""", await Json(db.TouchWithPets(40351)));
        _response.Next();
        Assert.True((await db.DeleteIfPetless(40351)).Refused);
        Assert.Equal("1", Scalar("SELECT count(*) FROM people WHERE id = 40351"));
        _response.Next();
        Assert.Equal(40352, (await db.DeleteIfPetless(40352)).Value);
        Assert.Equal("0", Scalar("SELECT count(*) FROM people WHERE id = 40352"));
    }

    [Fact]
    public async Task ATemplatesSubqueryInAnUpdateOrDeleteSeesTheRowWritten()
    {
        var db = Db(write: true);
        await Execute("INSERT INTO people (id, name, age, active, born) VALUES (50371, 'tp371', 79, true, 0), (50372, 'tp372', 79, true, 0), " +
            "(50373, 'tp373', 80, false, 0), (50374, 'tp374', 80, true, 0); INSERT INTO pets VALUES (3721, 50372, 'p', 4), (3731, 50373, 'q', 4)");
        await Json(db.DeletePetlessByTemplate(79));
        Assert.Equal(204, _response.Status);
        Assert.Equal("50372", Column("SELECT id FROM people WHERE age = 79 ORDER BY id"));
        await Json(db.ActiveIfPetOwner(80));
        Assert.Equal("50373", Column("SELECT id FROM people WHERE age = 80 AND active ORDER BY id"));
    }

    [Fact]
    public async Task AValueThatThrowsWhenCalledRentsNothing()
    {
        var source = fixture.Database("reads.db", readers: 1).OpenSource();
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            Assert.Throws<ArgumentOutOfRangeException>(() => db.Checked(-1));
            Assert.Equal("""{"id":1,"text":"ann"}""", await Json(db.Checked(1)));
        }
        finally
        {
            // in Debug builds: every job rented is back in the pool
            SqliteChecks.StopOne(source);
        }
    }

    [Fact]
    public async Task AListThatThrowsWhileItIsReadGivesItsCommandBack()
    {
        var source = fixture.Database("reads.db", readers: 1).OpenSource();
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            // the job given back here is the one the query rents next (this thread's cached one), and the one after it, if
            // the query gave it back
            var probe = source.Read(Probe).Hold();
            var job = (SqliteJob)probe.Job!;
            int generation = job.Generation;
            Sqlite.Discard(probe);
            Assert.Throws<FormatException>(() => db.InNamesOf(Parsed("ann", "")));
            var next = source.Read(Probe).Hold();
            Assert.Same(job, next.Job);
            Assert.Equal(generation + 2, job.Generation);
            Sqlite.Discard(next);
            Assert.Equal("1,3", await Ids(db.InNamesOf(Parsed("ann", "cid"))));
        }
        finally
        {
            SqliteChecks.StopOne(source);
        }
    }

    static readonly SqliteStatement Probe = new("SELECT 1");

    static IEnumerable<Utf8> Parsed(params string[] names)
    {
        foreach (var n in names) yield return n.Length > 0 ? U(n) : throw new FormatException("an empty name");
    }

    // ---------- the write file, read and written directly ----------

    static readonly Dictionary<string, SqliteStatement> Statements = [];

    static SqliteStatement Statement(string sql)
    {
        lock (Statements)
        {
            if (!Statements.TryGetValue(sql, out var s)) Statements[sql] = s = new SqliteStatement(sql);
            return s;
        }
    }

    async Task Execute(string sql)
    {
        var r = await Sqlite.Run<Value>(fixture.Writes.Write(Statement(sql)).Hold(), null, default);
        Assert.True(r.IsOk, r.ToString());
    }

    /// <summary>The rows of <paramref name="sql"/> on the write file, every column as text.</summary>
    List<string?[]> Rows(string sql)
    {
        var rows = new List<string?[]>();
        var r = Kv.Done(Sqlite.Run<Dump>(fixture.Writes.Read(Statement(sql)).Hold(), rows, default));
        Assert.True(r.IsOk, r.ToString());
        return rows;
    }

    string? Scalar(string sql) => Assert.Single(Rows(sql))[0];

    string Column(string sql) => string.Join(",", Rows(sql).Select(r => r[0]));
}
