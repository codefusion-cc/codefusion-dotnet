using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;
using static CodeFusion.Data.Tests.GeneratorHarness;

namespace CodeFusion.Data.Tests;

/// <summary>
/// The SQL the generator writes for the query shapes the Conduit port needed (text functions, an insert of a list's
/// texts, a write from the rows a query found, the newest rows per group), and the variants it refuses, each with a
/// build error on the construct and what to write instead.
/// </summary>
public sealed class ShapeGeneratorTests
{
    static Diagnostic Single(Result r, string id)
    {
        var d = Assert.Single(r.Generator, x => x.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        return d;
    }

    static string At(Diagnostic d, string source) => source.Substring(d.Location.SourceSpan.Start - Usings.Length, d.Location.SourceSpan.Length);

    static string Message(Diagnostic d) => d.GetMessage(CultureInfo.InvariantCulture);

    static void Clean(Result r)
    {
        Assert.Empty(r.Generator);
        Assert.Empty(r.Errors);
    }

    // ---------- text functions ----------

    [Fact]
    public void TextFunctionsAreTheirSql()
    {
        string source = Context("""
                public Many<Name> Slugs() => People.Select(p => new Name(p.Id, p.Name.ReplaceMatches("[^A-Za-z0-9]+", "-").ToLowerAscii().Trim('-').Concat("-"u8).Concat(p.Id))).Many();
                public Many<Name> Others(Utf8 old) => People.Select(p => new Name(p.Id, p.Name.ToUpperAscii().TrimStart('é').TrimEnd('\'').Replace(old, "x"u8))).Many();
                public Many<Name> OfParameters(Utf8 title, long id) => People.Select(p => new Name(p.Id, title.ToLowerAscii().Concat(id))).Many();
                public Many<Name> Nested() => People.Select(p => new Name(p.Id, p.Name.Concat(p.Name.Concat("-"u8)))).Many();
                public Write Guarded(Utf8 name, Utf8 other) => Insert(People, new Person { Name = name }).ThenIf(name.ToLowerAscii() == other, Sql($"UPDATE people SET age = 1"));
                public Write GuardedNullable(Utf8? name, Utf8 other) => Insert(People, new Person { Name = other }).ThenIf(name.Value.ToUpperAscii() == other, Sql($"UPDATE people SET age = 1"));
            """);
        var r = Run(source);
        Clean(r);
        Assert.Equal("SELECT p.id, (trim(both '-' from lower(regexp_replace(p.name, '[^A-Za-z0-9]+', '-', 'g') COLLATE \"C\")) || '-' || p.id) FROM people p", r.Sql("Slugs"));
        Assert.Equal("SELECT p.id, replace(trim(trailing '''' from trim(leading 'é' from upper(p.name COLLATE \"C\"))), $1, 'x') FROM people p", r.Sql("Others"));
        // over parameters it is still SQL: the call never runs, so nothing is allocated
        Assert.Equal("SELECT p.id, (lower($1 COLLATE \"C\") || $2) FROM people p", r.Sql("OfParameters"));
        Assert.DoesNotContain("Utf8Operators.ToLowerAscii(title)", r.Generated);
        // a concatenation on the right keeps its parentheses
        Assert.Equal("SELECT p.id, (p.name || (p.name || '-')) FROM people p", r.Sql("Nested"));
        // where C# decides, the method itself runs, a nullable's .Value as written
        Assert.Contains("(global::CodeFusion.Data.Utf8Operators.ToLowerAscii(name) == other)", r.Generated);
        Assert.Contains("(global::CodeFusion.Data.Utf8Operators.ToUpperAscii((name).Value) == other)", r.Generated);
    }

    [Theory]
    [InlineData("""p.Name.ReplaceMatches("[^a]", "-")""", "\"[^a]\"", "without '+'")]
    [InlineData("""p.Name.ReplaceMatches("[^a]+[^b]+", "-")""", "\"[^a]+[^b]+\"", "a second [^...] class")]
    [InlineData("""p.Name.ReplaceMatches(@"\d+", "-")""", "@\"\\d+\"", "'\\d'")]
    [InlineData("""p.Name.ReplaceMatches("a.b", "-")""", "\"a.b\"", "'.'")]
    [InlineData("""p.Name.ReplaceMatches("a*", "-")""", "\"a*\"", "'*'")]
    [InlineData("""p.Name.ReplaceMatches("a$", "-")""", "\"a$\"", "'$'")]
    [InlineData("""p.Name.ReplaceMatches("é", "-")""", "\"é\"", "'é'")]
    [InlineData("""p.Name.ReplaceMatches("(a)", "-")""", "\"(a)\"", "'('")]
    [InlineData("""p.Name.ReplaceMatches("[z-a]", "-")""", "\"[z-a]\"", "the range 'z-a'")]
    [InlineData("""p.Name.ReplaceMatches("[a-z", "-")""", "\"[a-z\"", "without its ']'")]
    [InlineData("""p.Name.ReplaceMatches("a", "$0")""", "\"$0\"", "'$' in the replacement")]
    [InlineData("""p.Name.ReplaceMatches("a", @"\1")""", "@\"\\1\"", "'\\' in the replacement")]
    [InlineData("""p.Name.ReplaceMatches(pattern, "-")""", "pattern", "pattern that isn't a constant string")]
    [InlineData("""p.Name.Trim(c)""", "c", "character that isn't a constant")]
    [InlineData("""p.Name.Trim('\uD83D')""", "'\\uD83D'", "half a character")]
    public void ATextFunctionSqlWouldReadOtherwiseIsRefused(string call, string at, string why)
    {
        string source = Context($$"""
                public Many<Name> Q(string pattern, char c) => People.Select(p => new Name(p.Id, {{call}})).Many();
            """);
        var d = Single(Run(source), "CFDATA001");
        Assert.Equal(at, At(d, source));
        Assert.Contains(why, Message(d), System.StringComparison.Ordinal);
        Assert.Contains("write this query with Sql<T>", Message(d), System.StringComparison.Ordinal);
    }

    // ---------- an insert of a list's texts ----------

    [Fact]
    public void AListsTextsAreTheRowsOfAnInsert()
    {
        string source = Context("""
                public Write AddNames(Utf8List names) => Insert(People, names.AsRows().Distinct().OrderBy(n => n).Select(n => new Person { Name = n })).OrIgnore();
                public Write AddOthers(Utf8List names, int age) =>
                    Insert(People, names.AsRows().Where(n => n != ""u8).OrderByDescending(n => n.ToUpperAscii()).Select(n => new Person { Name = n, Nick = n.ToLowerAscii(), Age = age }));
                public Write<Name> InAUnit(Utf8 name, Utf8List pets) =>
                    Insert(People, new Person { Name = name })
                        .ThenIf(!pets.IsEmpty, p => Insert(Pets, pets.AsRows().Distinct().Select(x => new Pet { Name = x, OwnerId = p.Id })))
                        .ThenSelect(p => new Name(p.Id, p.Name));
            """);
        var r = Run(source);
        Clean(r);
        Assert.Equal("INSERT INTO people (name) SELECT DISTINCT n.v FROM unnest($1) n(v) ORDER BY n.v ON CONFLICT DO NOTHING", r.Sql("AddNames"));
        Assert.Equal("INSERT INTO people (name, nick, age) SELECT n.v, lower(n.v COLLATE \"C\"), $2 FROM unnest($1) n(v) WHERE n.v <> '' ORDER BY upper(n.v COLLATE \"C\") DESC", r.Sql("AddOthers"));
        Assert.Equal("INSERT INTO pets (name, owner_id) SELECT DISTINCT x.v, currval(pg_get_serial_sequence('people', 'id')) FROM unnest($1) x(v)", r.Sql("InAUnit_2"));
        Assert.Contains("AddNamesParameters => [global::CodeFusion.Data.DataType.TextArray]", r.Generated);
    }

    [Theory]
    [InlineData("""Insert(People, names.AsRows().Distinct().OrderBy(n => n.ToUpperAscii()).Select(n => new Person { Name = n }))""", "names.AsRows().Distinct().OrderBy(n => n.ToUpperAscii())", "an order by something other than the text itself")]
    [InlineData("""Insert(People, names.AsRows().Distinct().Select(n => new Person { Name = n.ToLowerAscii() }))""", "names.AsRows().Distinct().Select(n => new Person { Name = n.ToLowerAscii() })", "sets no column to the text itself")]
    [InlineData("""Insert(People, names.AsRows().Take(2).Select(n => new Person { Name = n }))""", "names.AsRows().Take(2)", "'Take' in the rows of an insert from a list")]
    [InlineData("""Insert(People, People.Where(p => p.Age > 1).Distinct().Select(p => new Person { Name = p.Name }))""", "People.Where(p => p.Age > 1).Distinct().Select(p => new Person { Name = p.Name })", "something other than Table.Where(...).Select")]
    public void AnInsertOfAListSqlWouldReadOtherwiseIsRefused(string insert, string at, string why)
    {
        string source = Context($$"""
                public Write Q(Utf8List names) => {{insert}};
            """);
        var d = Single(Run(source), "CFDATA001");
        Assert.Equal(at, At(d, source));
        Assert.Contains(why, Message(d), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AListsRowsOnTheirOwnAndDistinctInAReadAreRefused()
    {
        string source = Context("""
                public Many<Name> Rows(Utf8List names) => names.AsRows().Select(n => new Name(1, n)).Many();
                public Many<Utf8> Names() => People.Select(p => p.Name).Distinct().Many();
            """);
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA001").Select(Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, m => m.Contains("a list's rows on their own", System.StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("Distinct in a read", System.StringComparison.Ordinal));
    }

    // ---------- a write from the rows a query found ----------

    /// <summary>A provider whose dialect lacks what the shapes need, as SQLite's will.</summary>
    const string Lite = """
        [SqlDialect("lite", WritableCtes = false, Lateral = false, RegexReplace = "")]
        public readonly struct Lite : IDataProvider<Lite, PgStatement, PgCommand>
        {
            public static PgStatement Statement(string sql, params ReadOnlySpan<DataType> parameters) => throw null!;
            public static PgCommand Read(DataContext context, PgStatement statement) => throw null!;
            public static PgCommand Write(DataContext context, PgStatement statement) => throw null!;
            public static System.Threading.Tasks.ValueTask<DataResult> Run<TReader>(in PendingCommand command, object? state, System.Threading.CancellationToken aborted) where TReader : IRowReader => throw null!;
            public static void Discard(in PendingCommand command) => throw null!;
        }
        public sealed partial class LiteDb : DataContext<Lite>
        {
            public Table<Person> People => Table<Person>();
            public Table<Pet> Pets => Table<Pet>();
            public Write<Name> Then(Utf8 name) => People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { OwnerId = p.Id, Name = name })).Select(p => new Name(p.Id, p.Name));
            public Many<Name> Matches() => People.Select(p => new Name(p.Id, p.Name.ReplaceMatches("a+", "b"))).Many();
            public Page<Name> Groups(Paging page) => People.SelectMany(p => p.Pets, (p, x) => x).OrderByDescending(x => x.Id).Page(page, 10).Select(x => new Name(x.Id, x.Name));
        }
        """;

    [Fact]
    public void ADialectWithoutWhatAShapeNeedsRefusesIt()
    {
        var r = Run(Entities + Lite);
        var errors = r.Generator.Where(d => d.Id == "CFDATA001").Select(Message).ToList();
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, m => m.Contains("Then in lite, which has no CTE that writes", System.StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("ReplaceMatches in lite", System.StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("a page of each row's children in lite, which has no LATERAL join", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""People.OrderBy(p => p.Id).Then(p => Insert(Pets, new Pet { OwnerId = p.Id })).Select(p => new Name(p.Id, p.Name))""",
        "People.OrderBy(p => p.Id).Then(p => Insert(Pets, new Pet { OwnerId = p.Id }))", "operators before Then other than Where")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => People.Where(x => x.Id == p.Id).Delete()).Select(p => new Name(p.Id, p.Name))""",
        "People.Where(x => x.Id == p.Id).Delete()", "the table the rows are found in")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Pets.Where(x => x.OwnerId == p.Id).Update(x => new Pet { Name = p.Name })).Select(p => new Name(p.Id, p.Name))""",
        "Pets.Where(x => x.OwnerId == p.Id).Update(x => new Pet { Name = p.Name })", "a write in Then other than an insert")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Pets.Where(x => x.OwnerId == p.Id).DeleteIf(x => x.Id > 1)).Select(p => new Name(p.Id, p.Name))""",
        "Pets.Where(x => x.OwnerId == p.Id).DeleteIf(x => x.Id > 1)", "a write in Then other than an insert")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Sql($"DELETE FROM pets")).Select(p => new Name(p.Id, p.Name))""",
        "Sql($\"DELETE FROM pets\")", "a write in Then other than an insert")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, Pets.Where(x => x.Id == 1).Select(x => new Pet { OwnerId = p.Id }))).Select(p => new Name(p.Id, p.Name))""",
        "Pets.Where(x => x.Id == 1).Select(x => new Pet { OwnerId = p.Id })", "an insert in Then that isn't 'new Pet { ... }'")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, new Pet { OwnerId = p.Id }).Then(Sql($"DELETE FROM pets"))).Select(p => new Name(p.Id, p.Name))""",
        "Insert(Pets, new Pet { OwnerId = p.Id }).Then(Sql($\"DELETE FROM pets\"))", "a unit of writes in Then")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, new Pet { OwnerId = p.Id })).Select(p => new Name(p.Pets.OrderBy(x => x.Id).Take(1).Count(), p.Name))""",
        "p.Pets.OrderBy(x => x.Id).Take(1).Count()", "Count after Select or Take of pets")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, new Pet { Id = 5, OwnerId = p.Id }).OrIgnore()).Select(p => new Name(p.Pets.Count(), p.Name))""",
        "p.Pets.Count()", "or that isn't of its key")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, new Pet { OwnerId = p.Id }).OrIgnore()).Select(p => new Name(p.Pets.Count(), p.Name))""",
        "p.Pets.Count()", "an ignored insert that doesn't set its whole key")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, new Pet { Id = 5 })).Select(p => new Name(p.Pets.Count(), p.Name))""",
        "p.Pets.Count()", "a condition on a column the insert before the Select doesn't set")]
    [InlineData("""Pets.Where(x => x.Id > 1).Then(x => Insert(People, new Person { Name = x.Name })).Select(x => new Name(x.Id, x.Owner.Name))""",
        "x.Owner", "a read of people in the Select after Then")]
    [InlineData("""People.Where(p => p.Age > 1).Then(p => Insert(Pets, new Pet { OwnerId = p.Id })).Select(p => new Name(p.Id, Pets.Single(x => x.Id == 1).Name))""",
        "Pets.Single(x => x.Id == 1).Name", "a read of pets in the Select after Then")]
    public void AWriteFromTheRowsFoundSqlWouldReadOtherwiseIsRefused(string query, string at, string why)
    {
        string source = Context($$"""
                public Write<Name> Q() => {{query}};
            """);
        var d = Single(Run(source), "CFDATA001");
        Assert.Equal(at, At(d, source));
        Assert.Contains(why, Message(d), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteFromTheRowsFoundIsOneStatement()
    {
        string source = Context("""
                public Write<Name> Give(Utf8 name, Utf8 pet) =>
                    People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { OwnerId = p.Id, Name = pet })).Select(p => new Name(p.Id, p.Name));
                public Write Take(int age) => People.Where(p => p.Age > age).Then(p => Pets.Where(x => x.OwnerId == p.Id).Delete());
                public Write TakeYounger(int age) => People.Where(p => p.Age > age).Then(p => Pets.Where(x => x.OwnerId == p.Id && x.Id < p.Age).Delete());
                public Write All() => People.Then(p => Pets.Delete());
                public Write<Name> InAUnitIsRefused(Utf8 name) =>
                    Insert(People, new Person { Name = name }).Then(People.Where(p => p.Name == name).Then(p => Pets.Delete())).ThenSelect(p => new Name(p.Id, p.Name));
                public Write<Name> WithoutSelect(Utf8 name) => People.Where(p => p.Name == name).Then(p => Pets.Delete());
            """);
        var r = Run(source);
        Assert.Equal("WITH t AS (SELECT p.id, p.name FROM people p WHERE p.name = $1), w AS (INSERT INTO pets (owner_id, name) SELECT t.id, $2 FROM t) SELECT t.id, t.name FROM t", r.Sql("Give"));
        Assert.Equal("WITH t AS (SELECT p.id FROM people p WHERE p.age > $1) DELETE FROM pets x WHERE x.owner_id = ANY(ARRAY(SELECT t.id FROM t))", r.Sql("Take"));
        // the rows found used otherwise than one key: joined
        Assert.Equal("WITH t AS (SELECT p.id, p.age FROM people p WHERE p.age > $1) DELETE FROM pets x USING t WHERE x.owner_id = t.id AND x.id < t.age", r.Sql("TakeYounger"));
        // a write that uses nothing of the row still runs once for each row found: none when none is
        Assert.Equal("WITH t AS (SELECT 1 FROM people p) DELETE FROM pets p2 USING t", r.Sql("All"));
        var errors = r.Generator.Where(d => d.Id == "CFDATA001").Select(Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, m => m.Contains("Then from the rows a query found inside a unit", System.StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("Then without a Select in a method that returns rows", System.StringComparison.Ordinal));
    }

    // ---------- a page of each row's children ----------

    [Fact]
    public void APageOfEachRowsChildrenTakesEachGroupsFirstInALateralJoin()
    {
        string source = Context("""
                public Page<Name> Newest(int age, Paging page) =>
                    People.Where(p => p.Age > age).SelectMany(p => p.Pets, (p, x) => x).OrderByDescending(x => x.Id).Page(page, 10).Select(x => new Name(x.Id, x.Owner.Name));
            """);
        var r = Run(source);
        Clean(r);
        string sql = r.Sql("Newest");
        // the page's ids: each person's newest pets, as many as the page reaches (offset and rows, computed in C#)
        Assert.Contains("SELECT x2.id AS id FROM people p5 CROSS JOIN LATERAL (SELECT p6.id AS id FROM pets p6 WHERE p6.owner_id = p5.id ORDER BY p6.id DESC LIMIT $2) x2 " +
            "WHERE p5.age > $1 ORDER BY x2.id DESC LIMIT 100 OFFSET $3", sql, System.StringComparison.Ordinal);
        // the count: every child, capped, with no order
        Assert.Contains("SELECT count(*) AS n FROM (SELECT 1 FROM people p3 CROSS JOIN LATERAL (SELECT p4.id AS id FROM pets p4 WHERE p4.owner_id = p3.id) x WHERE p3.age > $1 LIMIT 10) s", sql, System.StringComparison.Ordinal);
        Assert.Contains("> global::System.Int64.MaxValue - global::System.Math.Min(", r.Generated, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""People.SelectMany(p => p.Pets, (p, x) => p).OrderByDescending(p => p.Id)""", "(p, x) => p", "isn't the child itself")]
    [InlineData("""People.SelectMany(p => p.Pets, (p, x) => x).OrderByDescending(x => x.OwnerId)""", "x => x.OwnerId", "ordered by something other than their key")]
    [InlineData("""People.SelectMany(p => p.Pets.OrderBy(x => x.Name).Take(2), (p, x) => x).OrderByDescending(x => x.Id)""",
        "People.SelectMany(p => p.Pets.OrderBy(x => x.Name).Take(2), (p, x) => x)", "in an order other than the page's")]
    [InlineData("""People.SelectMany(p => p.Pets.OrderByDescending(x => x.Id).Take(2), (p, x) => x).Where(x => x.Name != "a"u8).OrderByDescending(x => x.Id)""",
        "People.SelectMany(p => p.Pets.OrderByDescending(x => x.Id).Take(2), (p, x) => x).Where(x => x.Name != \"a\"u8)", "Where after SelectMany of children taken with Take")]
    [InlineData("""People.SelectMany(p => p.Pets.Select(x => x), (p, x) => x).OrderByDescending(x => x.Id)""",
        "People.SelectMany(p => p.Pets.Select(x => x), (p, x) => x)", "a Select inside SelectMany's children")]
    [InlineData("""People.OrderBy(p => p.Id).SelectMany(p => p.Pets, (p, x) => x).OrderByDescending(x => x.Id)""",
        "People.OrderBy(p => p.Id).SelectMany(p => p.Pets, (p, x) => x)", "operators before SelectMany other than Where")]
    public void APageOfEachRowsChildrenSqlWouldReadOtherwiseIsRefused(string rows, string at, string why)
    {
        string source = Context($$"""
                public Page<Name> Q(Paging page) => {{rows}}.Page(page, 10).Select(x => new Name(x.Id, x.Name));
            """);
        var d = Single(Run(source), "CFDATA001");
        Assert.Equal(at, At(d, source));
        Assert.Contains(why, Message(d), System.StringComparison.Ordinal);
    }
}
