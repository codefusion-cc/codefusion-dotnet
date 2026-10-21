using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CodeFusion.Data.Generator;
using FsCheck;
using FsCheck.Fluent;
using Npgsql;
using Ops.Data;
using Xunit;
using Q = Ops.Data.OpsDb.QuerySql;

#pragma warning disable xUnit1031 // the properties run synchronously

namespace CodeFusion.Data.Tests;

/// <summary>
/// The text functions of <see cref="Utf8Operators"/>: each query's SQL, and, for any text, the SQL's result the
/// database gives is the C# method's. Run against a database of the C collation and one of ICU's (CI's): a
/// collation's own lower() differs there, translate() of A to Z doesn't.
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class TextFunctionTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    OpsDb Db(bool write = false)
    {
        TestDb.Require();
        var db = (write ? fixture.Writes : fixture.Reads).CreateContext<OpsDb>();
        db.Bind(_response);
        return db;
    }

    static Utf8 U(string s) => Texts.U(s);

    async Task<string> Json<TReply>(TReply reply) where TReply : IReply
    {
        _response.Next();
        await reply.WriteAsync(_response);
        return _response.Text;
    }

    async Task<string> Text(One<Utf8> one)
    {
        _response.Next();
        var row = await one;
        Assert.True(row.Found);
        return row.Value.ToString();
    }

    async Task<(string, string, string)> Trims(string text)
    {
        _response.Next();
        var t = (await Db().TrimmedOf(U(text))).Value;
        return (t.Both.ToString(), t.Start.ToString(), t.End.ToString());
    }

    [Fact]
    public async Task EachFunctionIsItsSqlOverParameters()
    {
        Assert.Equal("SELECT translate($1, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') FROM people p WHERE p.id = 1 LIMIT 1", Q.LowerOf);
        Assert.Equal("SELECT translate($1, 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') FROM people p WHERE p.id = 1 LIMIT 1", Q.UpperOf);
        Assert.Equal("SELECT trim(both '-' from $1), trim(leading 'é' from $1), trim(trailing '''' from $1) FROM people p WHERE p.id = 1 LIMIT 1", Q.TrimmedOf);
        Assert.Equal("SELECT replace($1, $2, $3) FROM people p WHERE p.id = 1 LIMIT 1", Q.ReplacedOf);
        Assert.Equal("SELECT (trim(both '-' from translate(regexp_replace($1, '[^A-Za-z0-9]+', '-', 'g'), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')) || '-' || CAST($2 AS text)) FROM people p WHERE p.id = 1 LIMIT 1", Q.SlugOf);
        Assert.Equal("ÄBC-éZ", await Text(Db().UpperOf(U("Äbc-éz"))));
        Assert.Equal("Äbc-éz", await Text(Db().LowerOf(U("ÄBC-éZ"))));
        Assert.Equal(("a-b", "--a-b--", "--a-b--"), await Trims("--a-b--"));
        Assert.Equal(("éé'x''", "'x''", "éé'x"), await Trims("éé'x''"));
        Assert.Equal("a.b.c", await Text(Db().ReplacedOf(U("a--b--c"), U("--"), U("."))));
        Assert.Equal("a--b", await Text(Db().ReplacedOf(U("a--b"), U(""), U("."))));
        Assert.Equal("aab", await Text(Db().ReplacedOf(U("aaab"), U("aa"), U("a"))));
    }

    [Theory]
    [InlineData("How to train your dragon!", 42, "how-to-train-your-dragon-42")]
    [InlineData("  Ünïcödé — Tîtle ✓ ", 7, "n-c-d-t-tle-7")]
    [InlineData("---", 3, "-3")]
    [InlineData("", 1, "-1")]
    [InlineData("ΣΊΣΥΦΟΣ 😀 x", -5, "x--5")]
    public async Task TheSlugRuleGivesWhatTheSpecsSlugifyGives(string title, long id, string slug)
    {
        Assert.Equal(slug, await Text(Db().SlugOf(U(title), id)));
        Assert.Equal(slug, Slugs.WithId(U(title), id).ToString());
        // the spec's SQL, run by the database itself
        await using var db = TestDb.Admin();
        await using var cmd = db.CreateCommand("SELECT trim(both '-' from lower(regexp_replace($1, '[^A-Za-z0-9]+', '-', 'g'))) || '-' || $2");
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(id);
        Assert.Equal(slug, (string)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task FunctionsOverColumnsAndANewKey()
    {
        Assert.Equal("SELECT p.id, (replace(translate(p.name, 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') || ':' || CAST(p.age AS text), 'N', 'n') || COALESCE(p.nick, $1)) FROM people p ORDER BY p.id", Q.NameTags);
        Assert.Equal("""[{"id":1,"text":"Ann:30A"},{"id":2,"text":"BEn:25Benny"},{"id":3,"text":"CID:40c"},{"id":4,"text":"DEE:30"}]""", await Json(Db().NameTags()));
        Assert.Equal("INSERT INTO people (id, name, age, active, born) SELECT n.id, trim(both '-' from translate(regexp_replace($1, '[^A-Za-z0-9]+', '-', 'g'), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')) || '-' || CAST(n.id AS text), 2, true, now() " +
            "FROM (SELECT nextval(pg_get_serial_sequence('people', 'id')) AS id) n", Q.AddSlugged);
        _response.Next();
        var row = await Db(write: true).AddSlugged(U("Hello, World"));
        Assert.True(row.Found);
        Assert.Equal("hello-world-" + row.Value.Id, row.Value.Text.ToString());
    }

    [Fact]
    public async Task WhereCSharpDecidesTheMethodItselfRuns()
    {
        var db = Db(write: true);
        await Json(db.AddIfLowered(U("Zed"), U("zed")));
        Assert.Equal(204, _response.Status);
        await Json(db.AddIfLowered(U("Yan"), U("nope")));
        await using var admin = TestDb.Admin("cfdata_ops_w");
        Assert.Equal("lowered", await admin.Scalar("SELECT nick FROM people WHERE name = 'Zed'"));
        Assert.Equal(DBNull.Value, await admin.Scalar("SELECT nick FROM people WHERE name = 'Yan'"));
    }

    // ---------- the SQL's result is the C# method's ----------

    /// <summary>Pieces of text where C# and SQL could part: ASCII letters next to the letters only a locale lowers,
    /// the final sigma, the Turkish i's, ß, a character outside the BMP and one combining mark.</summary>
    static readonly string[] Pieces = ["a", "Z", "0", "-", "--", "'", " ", ".", "$", "(", ")", "*", "+", "?", "{", "}", "|", "\\", "\"", "[", "]", "#", "/", "~", "_", "é", "É", "Ä", "Σ", "ς", "İ", "ı", "ß", "ǅ", "😀", "𐐀", "́", "x", "N", "aa"];

    static Gen<string> TextGen => Gen.ArrayOf(Gen.Elements(Pieces)).Select(string.Concat);

    [Fact]
    public void ForAnyTextEachFunctionsSqlGivesTheMethodsResult()
    {
        TestDb.Require();
        var db = Db();
        var arb = (from t in TextGen from o in Gen.Elements("", "-", "a", "é", "😀", "aa") from n in Gen.Elements("", ".", "ß", "--") from id in Gen.Choose(-3, 1000)
                   select (Text: t, Old: o, New: n, Id: (long)id)).ToArbitrary();
        // off xunit's context: the property blocks on each query
        Prop.ForAll(arb, c => Task.Run(() => Same(db, c.Text, c.Old, c.New, c.Id)).GetAwaiter().GetResult()).QuickCheckThrowOnFailure();
    }

    async Task Same(OpsDb db, string text, string oldValue, string newValue, long id)
    {
        var t = U(text);
        Assert.Equal(t.ToLowerAscii().ToString(), await Text(db.LowerOf(t)));
        Assert.Equal(t.ToUpperAscii().ToString(), await Text(db.UpperOf(t)));
        _response.Next();
        var trimmed = (await db.TrimmedOf(t)).Value;
        Assert.Equal(t.Trim('-').ToString(), trimmed.Both.ToString());
        Assert.Equal(t.TrimStart('é').ToString(), trimmed.Start.ToString());
        Assert.Equal(t.TrimEnd('\'').ToString(), trimmed.End.ToString());
        Assert.Equal(t.Replace(U(oldValue), U(newValue)).ToString(), await Text(db.ReplacedOf(t, U(oldValue), U(newValue))));
        Assert.Equal(Slugs.WithId(t, id).ToString(), await Text(db.SlugOf(t, id)));
    }

    [Fact]
    public void AsCSharpAPatternRunsInLinearTimeOnALongText()
    {
        // overlapping + atoms that never match: a backtracking engine tries each split at each start (2.8 s here)
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var text = U(string.Concat(Enumerable.Repeat("a-", 12000)));
        Assert.Equal(text.ToString(), text.ReplaceMatches("[a-z]+-[a-z-]+x", "_").ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.Elapsed}");
    }

    // ---------- the patterns ReplaceMatches takes ----------

    /// <summary>Pieces of patterns, those .NET and SQL read alike (most) and those they may not.</summary>
    static readonly string[] Atoms = ["a", "b", "Z", "0", "-", " ", "\\.", "\\-", "[a-z]", "[^a-z]", "[A-Za-z0-9]", "[^0-9]", "[ab-]", "[^ ]", "[.$()*+?{}|]", "[ _,:;!?@#%&=~'\"/<>`]", "[^.$(]", "\\\\", "\\[", "\\$", "\\\"", "\\(", "\\*", "\\?", "\\{", "\\|", "'", "\"", "#", "/", "~"];

    static readonly string[] Others = [".", "\\d", "\\w", "é", "(a)", "a|b", "$", "^", "\\_", "\\0", "\\'", "\\<"];

    static Gen<string> AtomGen => Gen.Frequency((8, Gen.Elements(Atoms)), (1, Gen.Elements(Others)));

    static Gen<string> QuantifierGen => Gen.Frequency((4, Gen.Elements("", "+")), (1, Gen.Elements("*", "?", "{2}")));

    static Gen<string> PatternGen =>
        Gen.ArrayOf(from a in AtomGen from q in QuantifierGen select a + q).Where(a => a.Length is > 0 and <= 4).Select(string.Concat);

    [Fact]
    public void APatternTakenIsReadAlikeByDotNetAndSql()
    {
        TestDb.Require();
        using var admin = TestDb.Admin();
        using var conn = admin.OpenConnection();
        string sql = "SELECT " + Dialect.Format(new Dialect().RegexReplace, "$1", "$2", "$3");
        int taken = 0, refused = 0;
        var arb = (from p in PatternGen from r in Gen.Elements("-", "", "_x") from texts in Gen.ArrayOf(TextGen, 8) select (Pattern: p, Replacement: r, Texts: texts)).ToArbitrary();
        Prop.ForAll(arb, c =>
        {
            if (Patterns.Problem(c.Pattern) is not null)
            {
                refused++;
                return;
            }
            taken++;
            foreach (var text in c.Texts.Append("a😀b").Append("😀😀a-Z").Append(""))
            {
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(text);
                cmd.Parameters.AddWithValue(c.Pattern);
                cmd.Parameters.AddWithValue(c.Replacement);
                string inSql = (string)cmd.ExecuteScalar()!;
                // and as the generator writes it: the pattern and the replacement as SQL literals
                using var literal = new NpgsqlCommand("SELECT " + Dialect.Format(new Dialect().RegexReplace, "$1", Sql.String(c.Pattern), Sql.String(c.Replacement)), conn);
                literal.Parameters.AddWithValue(text);
                Assert.Equal(inSql, (string)literal.ExecuteScalar()!);
                Assert.True(inSql == U(text).ReplaceMatches(c.Pattern, c.Replacement).ToString(), $"/{c.Pattern}/ on \"{text}\": SQL \"{inSql}\", .NET \"{Regex.Replace(text, c.Pattern, c.Replacement)}\"");
            }
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(400));
        // both kinds came up
        Assert.True(taken > 100 && refused > 40, $"{taken} taken, {refused} refused");
    }

    [Theory]
    [InlineData("[^a]", "😀", "-")]
    [InlineData("[^a]+[^b]+", "😀", "-")]
    [InlineData("a$", "a\n", "-")]
    [InlineData(".", "😀", "-")]
    public void APatternRefusedIsOneTheyReadDifferently(string pattern, string text, string replacement)
    {
        TestDb.Require();
        Assert.NotNull(Patterns.Problem(pattern));
        using var admin = TestDb.Admin();
        using var cmd = admin.CreateCommand("SELECT regexp_replace($1, $2, $3, 'g')");
        cmd.Parameters.AddWithValue(text);
        cmd.Parameters.AddWithValue(pattern);
        cmd.Parameters.AddWithValue(replacement);
        Assert.NotEqual((string)cmd.ExecuteScalar()!, Regex.Replace(text, pattern, replacement));
    }
}
