using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;
using static CodeFusion.Data.Tests.GeneratorHarness;

namespace CodeFusion.Data.Tests;

/// <summary>
/// The generator's build errors: what it refuses, with the error on the construct it can't compile and a message
/// that says what to write instead; and what it takes over.
/// </summary>
public sealed class GeneratorTests
{
    static Diagnostic Single(Result r, string id)
    {
        var d = Assert.Single(r.Generator, x => x.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        return d;
    }

    static string At(Diagnostic d, string source) => source.Substring(d.Location.SourceSpan.Start - Usings.Length, d.Location.SourceSpan.Length);

    [Fact]
    public void AValidContextCompilesAndItsCallsAreTakenOver()
    {
        string source = Context("""
                public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => new Name(p.Id, p.Name)).One();
            """, """
            public static class Handler
            {
                public static One<Name> Get(Db db, Utf8 name) => db.ByName(name);
                public static One<Name> Again(Db db) => db.ByName(Utf8.Empty);
            }
            """);
        var r = Run(source);
        Assert.Empty(r.Generator);
        Assert.Empty(r.Errors);
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.name = $1 LIMIT 1", r.Sql("ByName"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"\[global::System\.Runtime\.CompilerServices\.InterceptsLocationAttribute\(1, "));
        Assert.Contains("// Test.cs:", r.Generated);
    }

    [Fact]
    public void ACallWithNothingTheGeneratorKnowsIsRefusedWhereItIs()
    {
        string source = Context("""
                public Many<Name> Odd() => People.Where(p => p.Name.GetHashCode() == 1).Select(p => new Name(p.Id, p.Name)).Many();
            """);
        var r = Run(source);
        var d = Single(r, "CFDATA001");
        Assert.Equal("p.Name.GetHashCode()", At(d, source));
        Assert.Contains("write this query with Sql<T>", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ALocalIsRefused()
    {
        string source = Context("""
                public Many<Name> Local() { return People.Where(p => p.Age > 1).Select(p => new Name(p.Id, p.Name)).Many(); }
                internal static readonly int Limit = 3;
                public Many<Name> Field() => People.Where(p => p.Age > Limit).Select(p => new Name(p.Id, p.Name)).Many();
            """);
        var r = Run(source);
        // a block with one return is a definition too; an internal static field is a value known when called
        Assert.DoesNotContain(r.Generator, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.age > $1", r.Sql("Field"));
    }

    [Fact]
    public void RowsOfAnEntityAreRefused()
    {
        string source = Context("""
                public Many<Person> All() => People.Many();
            """);
        var d = Single(Run(source), "CFDATA001");
        Assert.Contains("entities are never built", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void APageNeedsOneKey()
    {
        string source = Context("""
                public Page<Name> Unordered(Paging page) => People.Page(page, countUpTo: 10).Select(p => new Name(p.Id, p.Name));
                public Page<Name> TwoKeys(Paging page) => People.OrderBy(p => p.Age).ThenBy(p => p.Id).Page(page, countUpTo: 10).Select(p => new Name(p.Id, p.Name));
                public Page<Name> NotAKey(Paging page) => People.OrderBy(p => p.Age).Page(page, countUpTo: 10).Select(p => new Name(p.Id, p.Name));
            """);
        var r = Run(source);
        Assert.Equal(3, r.Generator.Count(d => d.Id == "CFDATA001"));
        Assert.Contains(r.Generator, d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("neither the row's key nor a foreign key", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AnSqlHoleMustBeKnownWhenCalled()
    {
        string source = Context("""
                public Many<Name> Bad() => Sql<Name>($"SELECT id, name FROM people WHERE {People}").Many();
            """);
        var d = Single(Run(source), "CFDATA004");
        Assert.Equal("{People}", At(d, source));
    }

    [Fact]
    public void AConstantStringWhereSqlExpectsANameIsRefused()
    {
        string source = Context("""
                const string Col = "name", Tab = "people", Ann = "ann";
                public Many<Name> A() => Sql<Name>($"SELECT id, name FROM people ORDER BY {Col}").Many();
                public Many<Name> B() => Sql<Name>($"SELECT id, name FROM {Tab}").Many();
                public Many<Name> C() => Sql<Name>($"SELECT p.id, p.name FROM pets x JOIN {Tab} p ON p.id = x.owner_id").Many();
                public Many<Name> D() => Sql<Name>($"SELECT min(id), name FROM people GROUP BY id, {Col}").Many();
                public Many<Name> E() => Sql<Name>($"SELECT {Tab}.id, name FROM people").Many();
                public Many<Name> F() => Sql<Name>($"SELECT p.id, p.{Col} FROM people p").Many();
                public Write H() => Sql($"INSERT INTO people (age, {Col}) VALUES (1, 'x')");
                public Write I() => Sql($"UPDATE {Tab} SET age = 1");
                public Write J() => Sql($"INSERT INTO {Tab} (name) VALUES ('x')");
                public Many<Name> K() => Sql<Name>($"SELECT id, name FROM people ORDER BY name, {Col} DESC NULLS LAST LIMIT 2").Many();
                public Many<Name> L() => Sql<Name>($"SELECT id, name FROM people WHERE id IN (SELECT id FROM {Tab})").Many();
                public Many<Name> M() => Sql<Name>($"SELECT id, name FROM people ORDER BY {Col} NULLS FIRST").Many();
                public Many<Name> Values() => Sql<Name>($"SELECT id, name FROM people WHERE name = {Ann} OR name IN ({Ann}, 'b') OR coalesce(nick, {Ann}) = name ORDER BY name = {Ann}").Many();
                public Write Inserted() => Sql($"INSERT INTO people (name, age) VALUES ({Ann}, 1)");
                // a value where SQL takes one, whatever keyword is near
                public Many<Name> Listed() => Sql<Name>($"SELECT id, {Ann} FROM people").Many();
                public Write<Name> Returned() => Sql<Name>($"INSERT INTO people (name, age) VALUES ('x', 1) RETURNING id, {Ann}").Write();
                public Write Selected() => Sql($"INSERT INTO people (name, age) SELECT {Ann}, 1 FROM people WHERE id = 1");
                public Many<Name> Substring() => Sql<Name>($"SELECT id, substring(name FROM {Ann}) FROM people").Many();
                public Many<Name> Extract() => Sql<Name>($"SELECT id, name FROM people WHERE extract(epoch FROM {Ann}::timestamptz) > 0").Many();
                public Many<Name> Distinct() => Sql<Name>($"SELECT id, name FROM people WHERE name IS DISTINCT FROM {Ann} OR nick IS NOT DISTINCT FROM {Ann}").Many();
                public Many<Name> Compared() => Sql<Name>($"SELECT id, name FROM people ORDER BY {Ann} = name, name").Many();
                public Many<Name> Commented() => Sql<Name>($"SELECT id, name FROM people WHERE name = -- order by\n{Ann} OR name = /* from */ {Ann}").Many();
                public Many<Name> Quoted() => Sql<Name>($"SELECT id, name FROM people WHERE name IN ('ORDER BY', {Ann})").Many();
            """);
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA010").ToList();
        Assert.All(errors, d => Assert.Contains("[SqlTemplate]", Message(d), System.StringComparison.Ordinal));
        Assert.All(errors, d => Assert.Matches(@"^\{(Col|Tab)\}$", At(d, source)));
        Assert.Equal(12, errors.Count);
        Assert.Equal(12, r.Generator.Length);
    }

    [Fact]
    public void SqlThatWritesMustRunAsAWrite()
    {
        string source = Context("""
                public One<Name> Sneaky(long id) => Sql<Name>($"UPDATE people SET age = 1 WHERE id = {id} RETURNING id, name").One();
                public Write<Name> Fine(long id) => Sql<Name>($"UPDATE people SET age = 1 WHERE id = {id} RETURNING id, name").Write();
                public Many<Name> Reads() => Sql<Name>($"SELECT id, name FROM people WHERE name <> 'updated'").Many();
            """);
        var r = Run(source);
        var d = Single(r, "CFDATA005");
        Assert.Contains("UPDATE", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("UPDATE people SET age = 1 WHERE id = $1 RETURNING id, name", r.Sql("Fine"));
    }

    [Fact]
    public void ADefinitionUsedWhereItWouldRunIsRefused()
    {
        string source = Context("", """
            public static class Handler
            {
                public static bool Get(Db db) => db.People.Where(p => p.Age > 1) is not null;
                public static Name Projection(Person p) => new(p.Id, p.Name);
                public static bool Allowed(Person p) => p.Pets.Any();
            }
            """);
        var r = Run(source);
        var d = Single(r, "CFDATA002");
        Assert.Equal("db.People.Where(p => p.Age > 1)", At(d, source));
        Assert.Contains("move the query into a query method", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AQueryMethodWithStatementsIsRefused()
    {
        string source = Context("""
                public One<Name> Steps(Utf8 name) { var q = People.Where(p => p.Name == name); return q.Select(p => new Name(p.Id, p.Name)).One(); }
            """);
        var d = Single(Run(source), "CFDATA003");
        Assert.Equal("Steps", At(d, source));
    }

    [Fact]
    public void AViewMemberOfAnotherTypeIsRefused()
    {
        string source = Context("""
                public One<Bad> Get() => People.Select(p => new Bad(p.Id, "x")).One();
            """, "public readonly record struct Bad(long Id, string Text);");
        var d = Single(Run(source), "CFDATA006");
        Assert.Contains("'Text' has type 'string'", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AContextMustBePartial()
    {
        string source = Entities + """
            public sealed class Db : DataContext<Postgres>
            {
                public Table<Person> People => Table<Person>();
                public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => new Name(p.Id, p.Name)).One();
            }
            """;
        var d = Single(Run(source), "CFDATA007");
        Assert.Contains("must be partial", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ANavigationWithoutItsKeyIsRefused()
    {
        string source = """
            public sealed class Owner { public long Id { get; init; } public Utf8 Name { get; init; } }
            public sealed class Thing { public long Id { get; init; } public Owner Owner { get; init; } = null!; }
            public readonly record struct Name(long Id, Utf8 Text);
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<Thing> Things => Table<Thing>();
                public Many<Name> All() => Things.Select(t => new Name(t.Id, t.Owner.Name)).Many();
            }
            """;
        var d = Single(Run(source), "CFDATA008");
        Assert.Contains("no foreign key OwnerId", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnAmbiguousManyNavigationNeedsInverseProperty()
    {
        string source = """
            public sealed class User { public long Id { get; init; } public Many<Edge> Edges { get; init; } }
            public sealed class Edge { public long FromId { get; init; } public User From { get; init; } = null!; public long ToId { get; init; } public User To { get; init; } = null!; }
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<User> Users => Table<User>();
                public One<long> Count(long id) => Users.Where(u => u.Id == id).Select(u => u.Edges.Count()).One();
            }
            """;
        var d = Single(Run(source), "CFDATA008");
        Assert.Contains("[InverseProperty]", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void QueryMethodsCallingEachOtherAreInlined()
    {
        string source = Context("""
                public Write Touch(long id) => People.Where(p => p.Id == id).Update(p => new Person { Age = p.Age });
                public Write TouchTwice(long a, long b) => Touch(a).Then(Touch(b));
            """);
        var r = Run(source);
        Assert.Empty(r.Generator);
        Assert.Empty(r.Errors);
        Assert.Equal("UPDATE people SET age = age WHERE id = $1", r.Sql("TouchTwice"));
        Assert.Equal("UPDATE people SET age = age WHERE id = $1", r.Sql("TouchTwice_2"));
        Assert.Equal(["a", "b"], r.Bound("TouchTwice"));
    }

    [Fact]
    public void APagesMaxLimitCountsWithoutItsCountMode()
    {
        string source = Context("""
                public Page<Name> Small(Paging page) => People.OrderBy(p => p.Id).Page(page, countUpTo: 10, maxLimit: 3).Select(p => new Name(p.Id, p.Name));
            """);
        var r = Run(source);
        Assert.Empty(r.Generator);
        Assert.Matches(@"ORDER BY p\d*\.id LIMIT 3 OFFSET \$1\) b", r.Sql("Small"));
        // a negative limit or offset is clamped in C#, not sent to Postgres
        Assert.Contains("global::System.Math.Max(0L, page.Offset)", r.Generated);
    }

    [Fact]
    public void AQueryOfAnAbstractBaseIsCompiledAndItsCallsOnASubclassTakenOver()
    {
        string source = Entities + """
            public abstract partial class BaseDb : DataContext<Postgres>
            {
                public Table<Person> People => Table<Person>();
                public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => new Name(p.Id, p.Name)).One();
            }
            public sealed partial class AppDb : BaseDb { }
            public static class Handler
            {
                public static One<Name> Get(AppDb db, Utf8 name) => db.ByName(name);
            }
            """;
        var r = Run(source);
        Assert.Empty(r.Generator);
        Assert.Empty(r.Errors);
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.name = $1 LIMIT 1", r.Sql("ByName"));
        Assert.Contains("InterceptsLocationAttribute(1, ", r.Generated);
    }

    [Fact]
    public void AValueTheGeneratedCodeCantReachIsRefused()
    {
        string source = Context("""
                static int Limit() => 3;
                static int Hidden = 3;
                public Many<Name> Calls() => People.Where(p => p.Age > Limit() + Hidden).Select(p => new Name(p.Id, p.Name)).Many();
            """);
        var r = Run(source);
        // Limit() is inlined (an expression body), Hidden is a private field the interceptor can't read
        var d = Single(r, "CFDATA001");
        Assert.Equal("Hidden", At(d, source));
        Assert.Contains("private", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void ADeleteGivingBackANavigationReadsItFromACte()
    {
        string source = Context("""
                public One<Name> DeleteOne(long id) => Pets.Where(x => x.Id == id).Delete().Select(x => new Name(x.Id, x.Owner.Name)).One();
            """);
        var r = Run(source);
        Assert.Empty(r.Generator);
        Assert.Equal("WITH w AS (DELETE FROM pets WHERE id = $1 RETURNING id, owner_id) SELECT x.id, p.name FROM w x JOIN people p ON p.id = x.owner_id", r.Sql("DeleteOne"));
    }

    static string Message(Diagnostic d) => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Compiles cleanly: no generator diagnostic, no compiler error in what it wrote.</summary>
    static Result Clean(string source, bool checkedArithmetic = false)
    {
        var r = Run(source, checkedArithmetic);
        Assert.True(r.Generator.IsEmpty && r.Errors.IsEmpty, string.Join("\n", r.Generator.Concat(r.Errors)) + "\n" + r.Generated);
        return r;
    }

    [Fact]
    public void OneAsksForOneRow()
    {
        var r = Clean(Context("""
                public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => new Name(p.Id, p.Name)).One();
                public One<Name> FirstOf(long n) => People.OrderBy(p => p.Id).Take(n).Select(p => new Name(p.Id, p.Name)).One();
                public One<Name> Third() => People.OrderBy(p => p.Id).Skip(2).Select(p => new Name(p.Id, p.Name)).One();
            """));
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.name = $1 LIMIT 1", r.Sql("ByName"));
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id LIMIT LEAST($1, 1)", r.Sql("FirstOf"));
        Assert.Contains("global::System.Math.Max(0L, n)", r.Generated);
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id LIMIT 1 OFFSET 2", r.Sql("Third"));
    }

    [Theory]
    [InlineData("Many<Name>", "People.OrderBy(p => p.Id).Take(n).Where(p => p.Age > 1).Select(p => new Name(p.Id, p.Name)).Many()", "Where after Take")]
    [InlineData("Many<Name>", "People.OrderBy(p => p.Id).Skip(n).Where(p => p.Age > 1).Select(p => new Name(p.Id, p.Name)).Many()", "Where after Skip")]
    [InlineData("Many<Name>", "People.Take(n).OrderBy(p => p.Id).Select(p => new Name(p.Id, p.Name)).Many()", "OrderBy after Take")]
    [InlineData("Many<Name>", "People.OrderBy(p => p.Id).Take(n).Select(p => new Name(p.Id, p.Name)).Where(x => x.Id > 1).Many()", "Where after Take")]
    [InlineData("Many<Flag>", "People.OrderBy(p => p.Id).Select(p => new Flag(p.Id, p.Pets.OrderBy(x => x.Id).Take(n).Any(x => x.Name == p.Name))).Many()", "a condition after Take")]
    [InlineData("Many<Number>", "People.OrderBy(p => p.Id).Select(p => new Number(p.Id, p.Pets.OrderBy(x => x.Id).Take(n).Count(x => x.Id > 1))).Many()", "a condition after Take")]
    [InlineData("Many<Number>", "People.OrderBy(p => p.Id).Select(p => new Number(p.Id, p.Pets.Take(n).Where(x => x.Id > 1).Count())).Many()", "Where after Take")]
    [InlineData("Many<Number>", "People.OrderBy(p => p.Id).Select(p => new Number(p.Id, p.Pets.Take(n).OrderBy(x => x.Id).Count())).Many()", "OrderBy after Take")]
    public void AnOperatorAfterTakeOrSkipThatSqlWouldApplyBeforeItIsRefused(string type, string query, string what)
    {
        var r = Run(Context($"    public {type} Q(long n) => {query};", "public readonly record struct Flag(long Id, bool On);\npublic readonly record struct Number(long Id, long N);"));
        var d = Single(r, "CFDATA001");
        Assert.Contains(what, Message(d));
    }

    [Fact]
    public void AnInsertedKeyThatIsntTheRowsIsRefused()
    {
        string source = Context("""
                public Write<Name> Ignored(Utf8 name) => Insert(People, new Person { Name = name }).OrIgnore().ThenSelect(p => new Name(p.Id, p.Name));
                public Write<Name> FromQuery() => Insert(Pets, Pets.Where(x => x.Id == 1).Select(x => new Pet { OwnerId = x.OwnerId, Name = x.Name })).ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> Nested(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(x => Insert(People, new Person { Name = b }))
                    .Then(x => Insert(Pets, new Pet { OwnerId = x.Id, Name = a }))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> NestedSelect(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(x => Insert(People, new Person { Name = b }))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> OtherTable(Utf8 a) => Insert(People, new Person { Name = a })
                    .Then(x => Insert(Pets, new Pet { OwnerId = x.Id, Name = a }))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> OwnKey(long id, Utf8 a) => Insert(People, new Person { Id = id, Name = a }).OrIgnore()
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> SqlInsert(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(Sql($"INSERT INTO people (name) VALUES ({b})"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> NestedOwnKey(long id, Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(x => Insert(People, new Person { Id = id, Name = b }))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write SqlQuoted(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(Sql($"INSERT INTO \"people\" (name) VALUES ({b})"))
                    .Then(x => Insert(Pets, new Pet { OwnerId = x.Id, Name = a }));
                public Write<Name> SqlSchema(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(Sql($"insert into public . \"people\" AS p (name) VALUES ({b})"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> SqlMerge(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(Sql($"WITH s AS (SELECT {b}::text AS name) MERGE INTO People p USING s ON p.name = s.name WHEN NOT MATCHED THEN INSERT (name) VALUES (s.name)"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> SqlCopy(Utf8 a) => Insert(People, new Person { Name = a })
                    .Then(Sql($"COPY people (name) FROM '/dev/null'"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> SqlOtherTable(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(Sql($"INSERT INTO \"People\" (name) VALUES ({b}); INSERT INTO people_log (name) SELECT name FROM people"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> SqlOnly(Utf8 a, Utf8 b) => Insert(People, new Person { Name = a })
                    .Then(Sql($"MERGE INTO ONLY people p USING (SELECT {b}::text AS name) s ON p.name = s.name WHEN NOT MATCHED THEN INSERT (name) VALUES (s.name)"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
                public Write<Name> SqlCopyOut(Utf8 a) => Insert(People, new Person { Name = a })
                    .Then(Sql($"COPY (SELECT name FROM people) TO STDOUT"))
                    .ThenSelect(x => new Name(x.Id, x.Name));
            """);
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA001").ToList();
        Assert.Equal(10, errors.Count);
        Assert.Contains(errors, d => Message(d).Contains("OrIgnore", System.StringComparison.Ordinal));
        Assert.Contains(errors, d => Message(d).Contains("a query's rows", System.StringComparison.Ordinal));
        // and none for SqlOtherTable (a quoted name of other case is another table, and a table read isn't written)
        // or SqlCopyOut (a COPY of a query's rows out writes none)
        Assert.Equal(8, errors.Count(d => Message(d).Contains("another insert into people", System.StringComparison.Ordinal)));
        // an insert that sets its own key draws none, so the earlier key is still the sequence's
        Assert.Equal("SELECT x.id, x.name FROM people x WHERE x.id = (SELECT currval(pg_get_serial_sequence('people', 'id')))", r.Sql("NestedOwnKey_3"));
        // a key set by the insert is that value; one drawn from the sequence is its current value
        Assert.Equal("SELECT x.id, x.name FROM people x WHERE x.id = $1", r.Sql("OwnKey_2"));
        Assert.Equal("SELECT x.id, x.name FROM people x WHERE x.id = (SELECT currval(pg_get_serial_sequence('people', 'id')))", r.Sql("OtherTable_3"));
    }

    [Fact]
    public void AKeyTheInsertSetsFromACallIsComputedOnce()
    {
        var r = Clean(Context("""
                public Write<Name> Add(Utf8 n) => Insert(People, new Person { Id = Ids.Next(), Name = n })
                    .Then(p => Insert(Pets, new Pet { Id = Ids.Next(), OwnerId = p.Id, Name = n }))
                    .ThenSelect(p => new Name(p.Id, p.Name));
                public Write<Name> AddIf(Utf8 n, bool pet, bool twice) => Insert(People, new Person { Id = Ids.Next(), Name = n })
                    .ThenIf(pet, p => Insert(Pets, new Pet { Id = Ids.Next(), OwnerId = p.Id, Name = n })
                        .ThenIf(twice, x => Insert(Pets, new Pet { Id = x.Id + 1, OwnerId = p.Id, Name = n })))
                    .ThenSelect(p => new Name(p.Id, p.Name));
                public Write Bump(int n) => People.Where(p => p.Id == n && p.Age == n).Update(p => new Person { Active = true });
                public Write Both(int n) => Bump(n).Then(People.Where(p => p.Age == n).Delete());
                public Write BothOf(int x) => Both(x);
                public Write Guarded(Utf8 n) => Insert(People, new Person { Name = n })
                    .ThenIf(Ids.Check(), Insert(Pets, new Pet { Id = 1, OwnerId = 1, Name = n }).Then(Insert(Pets, new Pet { Id = 2, OwnerId = 1, Name = n })));
            """, """
            public static class Ids
            {
                static long n;
                public static long Next() { n++; return n; }
                public static bool Check() { n++; return n % 2 == 0; }
            }
            """));
        // (BothOf compiles: an inlined method's parameter is one value, bound as a long and as an int)
        // a call written once is made once: the person's key and the pet's in Add and in AddIf; a ThenIf's condition
        // once for all its statements
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Next\("));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Check\("));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($2, $1, $3)", r.Sql("Add_2"));
        Assert.Equal("SELECT p.id, p.name FROM people p WHERE p.id = $1", r.Sql("Add_3"));
        // the key's one value is bound wherever it is used (__v0_0 the person's, __v1_1 the pet's), under a ThenIf
        // inside another too, and so is the parameter n (__v0_1), which reads the same each time
        Assert.Equal(["__v0_0", "__v0_1", "__v0_0", "__v1_1", "__v0_1", "__v0_0"], r.Binder("Add"));
        Assert.Equal(["__v0_0", "__v0_1", "__v0_0", "__v1_1", "__v0_1", "__v1_1", "__v0_0", "__v0_1", "__v0_0"], r.Binder("AddIf"));
    }

    const string Calls = """
        public static class Ids
        {
            static long n;
            public static long Next() { n++; return n; }
            public static int Small() { long k = Next(); return (int)k; }
        }
        public sealed class Box
        {
            public long Fixed { get; init; }
            public long Settable { get; set; }
            public long Field;
            public readonly long Readonly;
        }
        """;

    [Fact]
    public void TwoCallsWrittenAlikeInOneStatementAreTwoValues()
    {
        var r = Clean(Context("""
                public Write<Name> Two(Utf8 n) => Insert(Pets, new Pet { Id = Ids.Next(), OwnerId = Ids.Next(), Name = n })
                    .ThenSelect(p => new Name(p.Id, p.Name));
                public Write Same(long id, Utf8 n) => Insert(Pets, new Pet { Id = id, OwnerId = id, Name = n });
                public Write Members(Box b, Utf8 n) => Insert(Pets, new Pet { Id = b.Fixed, OwnerId = b.Fixed, Name = n })
                    .Then(Insert(Pets, new Pet { Id = b.Settable, OwnerId = b.Settable, Name = n }))
                    .Then(Insert(Pets, new Pet { Id = b.Field, OwnerId = b.Field, Name = n }))
                    .Then(Insert(Pets, new Pet { Id = b.Readonly, OwnerId = b.Readonly, Name = n }));
                public Write Keyed(long k, Utf8 n) => Insert(Pets, new Pet { Id = k, OwnerId = k, Name = n });
                public Write KeyedOnce(Utf8 n) => Keyed(Ids.Next(), n);
                public Write Typed(int k, Utf8 n) => Insert(Pets, new Pet { Id = k, OwnerId = 1, Name = n }).Then(Insert(People, new Person { Age = k, Name = n }));
                public Write TypedOf(Utf8 n) => Typed(Ids.Small(), n);
            """, Calls));
        // each call written is made once: two in Two, one in KeyedOnce, whose argument is one value used twice
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Next\("));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $2, $3)", r.Sql("Two"));
        Assert.Equal("SELECT p.id, p.name FROM pets p WHERE p.id = $1", r.Sql("Two_2"));
        // the statement after sees the key, not the other call's value
        var two = r.Binder("Two");
        Assert.Equal(4, two.Count);
        Assert.NotEqual(two[0], two[1]);
        Assert.Equal(two[0], two[3]);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(r.Generated, @"= global::Ids\.Next\(\);"));
        // what can't change between two reads is one parameter; a property with a setter or a field that isn't readonly is two
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $1, $2)", r.Sql("Same"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $1, $2)", r.Sql("Members"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $2, $3)", r.Sql("Members_2"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $2, $3)", r.Sql("Members_3"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $1, $2)", r.Sql("Members_4"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $1, $2)", r.Sql("KeyedOnce"));
        // bound as a long, then as an int: one call, in its own type, each use converting from it
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Small\("));
        Assert.Matches(@"var (__a\d+) = global::Ids\.Small\(\);[\s\S]*long __v0_0 = \1;[\s\S]*int __v1_0 = \1;", r.Generated);
    }

    [Fact]
    public void AnArgumentOfACallUsedInsideAnotherValueIsComputedOnce()
    {
        var r = Clean(Context("""
                public Write Twice(long k, Utf8 n) => Insert(Pets, new Pet { Id = k, OwnerId = k + 1, Name = n });
                public Write TwiceOf(Utf8 n) => Twice(Ids.Next(), n);
                public Write Within(long k, Utf8 n) => Insert(Pets, new Pet { Id = k * 2, OwnerId = k + 1, Name = n });
                public Write WithinOf(Utf8 n) => Within(Ids.Next(), n);
            """, Calls));
        // the argument runs once, at the call, as in C#; the values written over it read its local
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Next\("));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $2, $3)", r.Sql("TwiceOf"));
        Assert.Matches(@"^__a\d+$", r.Bound("TwiceOf")[0]);
        Assert.Matches(@"^\(__a\d+ \+ 1L\)$", r.Bound("TwiceOf")[1]);
        Assert.Matches(@"^\(__a\d+ \* 2L\)$", r.Bound("WithinOf")[0]);
    }

    [Fact]
    public void AValueWrittenIntoTwoOthersIsRefused()
    {
        string source = Context("""
                public Page<Name> Feed(ref Paging page) => People.OrderBy(p => p.Id).Page(page, countUpTo: 10).Select(p => new Name(p.Id, p.Name));
            """);
        // a ref parameter may change between two reads: its limit and its offset, each written over it, would read it twice
        var d = Single(Run(source), "CFDATA001");
        Assert.Contains("would run twice (written into another value as well", Message(d), System.StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplatesArgumentOfTheRowWrittenIsQualifiedByItsTable()
    {
        var r = Clean(Context("""
                public Write Delete() => People.Where(p => !Frag.HasPets(p.Id)).Delete();
                public Write<Name> Touch(long id) => People.Where(p => p.Id == id).Update(p => new Person { Active = Frag.HasPets(p.Id) })
                    .Select(p => new Name(p.Id, Frag.Lower(p.Name)));
                public Write<Name> TouchIf(long id) => People.Where(p => p.Id == id).UpdateIf(p => Frag.HasPets(p.Id), p => new Person { Age = 1 })
                    .Select(p => new Name(p.Id, p.Name));
                public One<Name> DeleteIf(long id) => People.Where(p => p.Id == id).DeleteIf(p => Frag.HasPets(p.Id)).Select(p => new Name(p.Id, p.Name)).One();
            """, """
            public static class Frag
            {
                [SqlTemplate("EXISTS (SELECT 1 FROM pets x WHERE x.owner_id = {0})")]
                public static bool HasPets(long id) => throw new NotSupportedException();
                [SqlTemplate("lower({0})")]
                public static Utf8 Lower(Utf8 s) => throw new NotSupportedException();
            }
            """));
        Assert.Equal("DELETE FROM people WHERE NOT (EXISTS (SELECT 1 FROM pets x WHERE x.owner_id = people.id))", r.Sql("Delete"));
        Assert.Equal("UPDATE people SET active = EXISTS (SELECT 1 FROM pets x WHERE x.owner_id = people.id) WHERE id = $1 RETURNING id, lower(people.name)", r.Sql("Touch"));
        Assert.Contains("WHERE id IN (SELECT id FROM t WHERE EXISTS (SELECT 1 FROM pets x WHERE x.owner_id = t.id))", r.Sql("TouchIf"));
        Assert.Contains("FROM t WHERE EXISTS (SELECT 1 FROM pets x WHERE x.owner_id = t.id))", r.Sql("DeleteIf"));
    }

    [Fact]
    public void ASequenceOfAQuotedOrQualifiedTableIsNamedAsSql()
    {
        var r = Clean(Entities + """
            [Table("Gadgets", Schema = "Tenant A")] public sealed class Gadget { public long Id { get; init; } public Utf8 Name { get; init; } public long Code { get; init; } }
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<Gadget> Gadgets => Table<Gadget>();
                public Write<Name> Add(Utf8 name) => Insert(Gadgets, new Gadget { Name = name }).ThenSelect(g => new Name(g.Id, g.Name));
                public Write<Name> AddOwn(Utf8 name) => Insert(Gadgets, n => new Gadget { Name = name, Code = n.Id }).Select(g => new Name(g.Id, g.Name));
            }
            """);
        Assert.Equal("INSERT INTO \"Tenant A\".\"Gadgets\" (name) VALUES ($1)", r.Sql("Add"));
        Assert.Equal("SELECT g.id, g.name FROM \"Tenant A\".\"Gadgets\" g WHERE g.id = (SELECT currval(pg_get_serial_sequence('\"Tenant A\".\"Gadgets\"', 'id')))", r.Sql("Add_2"));
        Assert.Equal("INSERT INTO \"Tenant A\".\"Gadgets\" (id, name, code) SELECT n.id, $1, n.id FROM (SELECT nextval(pg_get_serial_sequence('\"Tenant A\".\"Gadgets\"', 'id')) AS id) n RETURNING id, name", r.Sql("AddOwn"));
    }

    [Fact]
    public void TablesOfABaseContextAreItsTables()
    {
        var r = Clean(Entities + """
            public abstract partial class BaseDb : DataContext<Postgres>
            {
                public Table<Person> Folks => Table<Person>();
            }
            public sealed partial class AppDb : BaseDb
            {
                public Many<Name> All() => Folks.OrderBy(p => p.Id).Select(p => new Name(p.Id, p.Name)).Many();
            }
            """);
        Assert.Equal("SELECT p.id, p.name FROM folks p ORDER BY p.id", r.Sql("All"));
    }

    [Fact]
    public void AVirtualQueryMethodIsRefused()
    {
        string source = Entities + """
            public abstract partial class BaseDb : DataContext<Postgres>
            {
                public Table<Person> People => Table<Person>();
                public virtual Many<Name> Visible() => People.Select(p => new Name(p.Id, p.Name)).Many();
            }
            public sealed partial class TenantDb : BaseDb
            {
                public override Many<Name> Visible() => People.Where(p => p.TeamId == 1).Select(p => new Name(p.Id, p.Name)).Many();
            }
            public static class Handler
            {
                public static Many<Name> Get(BaseDb db) => db.Visible();
            }
            """;
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA007").ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d => Assert.Contains("virtual", Message(d), System.StringComparison.Ordinal));
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void AQueryMethodImplementingAnInterfaceIsRefused()
    {
        string source = Context("""
                public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => new Name(p.Id, p.Name)).One();
                One<long> ICounts.Count() => People.Count();
                public One<long> Other() => People.Count();
            """, """
            public interface INames { One<Name> ByName(Utf8 name); }
            public interface ICounts { One<long> Count(); }
            public sealed partial class Db : INames, ICounts;
            public static class Handler
            {
                public static One<Name> Get(INames db, Utf8 name) => db.ByName(name);
            }
            """);
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA007").ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d => Assert.Contains("interface", Message(d), System.StringComparison.Ordinal));
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void AGenericContextIsRefused()
    {
        string source = Entities + """
            public sealed partial class Db<TMarker> : DataContext<Postgres>
            {
                public Table<Person> People => Table<Person>();
                public One<long> Count() => People.Count();
            }
            """;
        var r = Run(source);
        var d = Single(r, "CFDATA007");
        Assert.Contains("generic", Message(d));
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void AGenericContextWithoutQueriesIsABaseForOneWithThem()
    {
        var r = Clean(Entities + """
            public sealed class Main;
            public partial class TenantDb<T> : DataContext<Postgres>
            {
                public Table<Person> People => Table<Person>();
            }
            public class Plain : DataContext<Postgres>
            {
                public Table<Pet> Pets => Table<Pet>();
            }
            public sealed partial class AppDb : TenantDb<Main>
            {
                public Many<Name> All() => People.OrderBy(p => p.Id).Select(p => new Name(p.Id, p.Name)).Many();
            }
            """);
        Assert.Equal("SELECT p.id, p.name FROM people p ORDER BY p.id", r.Sql("All"));
    }

    [Fact]
    public void AViewWhoseSettersTheReaderCantReachIsRefused()
    {
        string source = Context("""
                public One<Hidden> Get(long id) => People.Where(p => p.Id == id).Select(p => Hidden.Of(p)).One();
            """, """
            public struct Hidden
            {
                public long Id { get; private set; }
                public Utf8 Name { get; init; }
                public static Hidden Of(Person p) => new() { Id = p.Id, Name = p.Name };
            }
            """);
        var r = Run(source);
        var d = Single(r, "CFDATA006");
        Assert.Contains("'Id'", Message(d));
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void AViewOfAnotherAssemblyWhoseSettersAreInternalToItIsRefused()
    {
        var r = Run(Context("""
                public One<Shared> Get() => Sql<Shared>($"SELECT id, name FROM people").One();
                public One<Built> Build() => Sql<Built>($"SELECT id, name FROM people").One();
                public One<Odd> Odd() => Sql<Odd>($"SELECT id, name FROM people").One();
            """), library: """
            public record struct Shared
            {
                public long Id { get; internal set; }
                public Utf8 Name { get; internal set; }
            }
            public readonly record struct Built
            {
                internal Built(long id, Utf8 name) => (Id, Name) = (id, name);
                public long Id { get; }
                public Utf8 Name { get; }
            }
            public readonly record struct Odd(long Id, string Name);
            """);
        // each reported where it is used, the view's own source being another assembly's
        var errors = r.Generator.Where(d => d.Id == "CFDATA006").ToList();
        Assert.Equal(3, errors.Count);
        Assert.Equal(2, errors.Count(d => Message(d).Contains("'Id'", System.StringComparison.Ordinal)));
        Assert.Single(errors, d => Message(d).Contains("'Name' has type 'string'", System.StringComparison.Ordinal));
        Assert.All(errors, d => Assert.Equal("Test.cs", d.Location.SourceTree?.FilePath));
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void OverloadedQueryMethodsAndAMethodNamedLikeAStatementCompile()
    {
        var r = Clean(Context("""
                public One<Name> ById(long id) => People.Where(p => p.Id == id).Select(p => new Name(p.Id, p.Name)).One();
                public One<Name> ById(int id) => People.Where(p => p.Age == id).Select(p => new Name(p.Id, p.Name)).One();
                public Write TouchOne(long a) => People.Where(p => p.Id == a).Update(p => new Person { Age = p.Age });
                public Write Touch(long a, long b) => TouchOne(a).Then(TouchOne(b));
                public Write Touch_2(long a) => People.Where(p => p.Id == a).Delete();
            """, """
            public static class Handler
            {
                public static One<Name> A(Db db) => db.ById(1L);
                public static One<Name> B(Db db) => db.ById(1);
            }
            """));
        // a method's own name is its SQL's; overloads add their parameters' types, and a statement that would take a
        // method's name another underscore
        Assert.Equal("DELETE FROM people WHERE id = $1", r.Sql("Touch_2"));
        Assert.Equal("UPDATE people SET age = age WHERE id = $1", r.Sql("Touch__2"));
        Assert.Contains("p.age = $1", r.Sql("ById_Int32"));
        Assert.Contains("p.id = $1", r.Sql("ById_Int64"));
    }

    [Fact]
    public void ACastOfAParameterIsBoundAsItsType()
    {
        string members = """
                public Many<Name> ByStatus(Status status) => People.Where(p => p.Age == (int)status).Select(p => new Name(p.Id, p.Name)).Many();
                public Many<Name> ByAge(long age) => People.Where(p => p.Age == (int)age).Select(p => new Name(p.Id, p.Name)).Many();
                public Many<Name> Under(int budget) => People.Where(p => p.Score < budget / 2.0).Select(p => new Name(p.Id, p.Name)).Many();
            """;
        var r = Clean(Context(members, "public enum Status { Off, On }"));
        Assert.Contains("__command.Double(", r.Generated);
        Assert.Contains("2D", r.Generated);
        Clean(Context(members, "public enum Status { Off, On }"), checkedArithmetic: true);
    }

    [Fact]
    public void RefAndInParametersAreBound()
    {
        Clean(Context("""
                public Many<Name> Top(ref long n) => People.OrderBy(p => p.Id).Take(n).Select(p => new Name(p.Id, p.Name)).Many();
                public Many<Name> ByName(in Utf8 name, ref readonly long id) => Sql<Name>($"SELECT id, name FROM people WHERE name = {name} AND id = {id}").Many();
            """, """
            public static class Handler
            {
                public static Many<Name> Get(Db db, long n) => db.Top(ref n);
                public static Many<Name> Get(Db db, Utf8 name, long id) => db.ByName(in name, in id);
            }
            """));
    }

    [Fact]
    public void ANullableValueIntoAMemberThatIsntIsRefused()
    {
        string source = Entities + """
            public sealed class Team { public long Id { get; init; } public Utf8 Name { get; init; } }
            public sealed class Member { public long Id { get; init; } public long? TeamId { get; init; } public Team Team { get; init; } = null!; public Utf8? Nick { get; init; } }
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<Member> Members => Table<Member>();
                public Table<Team> Teams => Table<Team>();
                public Many<Name> TeamNames() => Members.Select(m => new Name(m.Id, m.Team.Name)).Many();
                public Many<Name> Nicks() => Members.Select(m => new Name(m.Id, m.Nick!.Value)).Many();
                public Many<Name> WithDefault() => Members.Select(m => new Name(m.Id, m.Nick ?? m.Team.Name)).Many();
            }
            """;
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA001").ToList();
        // the left join's NULL can't be a Utf8; .Value says it is, as in C#; ?? with a nullable right side may still be NULL
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d => Assert.Contains("may be NULL", Message(d), System.StringComparison.Ordinal));
    }

    [Fact]
    public void ATemplateReturningATypeWithoutNullIsNeverNullUnlessGivenANullItDoesntTake()
    {
        string source = Entities + """
            public sealed class Team { public long Id { get; init; } public Utf8 Name { get; init; } }
            public sealed class Member { public long Id { get; init; } public long? TeamId { get; init; } public Team Team { get; init; } = null!; public Utf8? Nick { get; init; } }
            public static class Frag
            {
                [SqlTemplate("coalesce({0}, '')")]
                public static Utf8 OrEmpty(Utf8? s) => throw new NotSupportedException();
                [SqlTemplate("lower({0})")]
                public static Utf8 Lower(Utf8 s) => throw new NotSupportedException();
            }
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<Member> Members => Table<Member>();
                public Table<Team> Teams => Table<Team>();
                public Many<Name> Nicks() => Members.Select(m => new Name(m.Id, Frag.OrEmpty(m.Nick))).Many();
                public Many<Name> TeamNicks() => Members.Select(m => new Name(m.Id, Frag.OrEmpty(m.Team.Name))).Many();
                public Many<Name> TeamNames() => Members.Select(m => new Name(m.Id, Frag.Lower(m.Team.Name))).Many();
            }
            """;
        var r = Run(source);
        // the template takes a NULL and gives none back; lower() is given the left join's NULL, which its parameter
        // doesn't take, and gives it back
        var d = Single(r, "CFDATA001");
        Assert.Contains("may be NULL", Message(d), System.StringComparison.Ordinal);
        Assert.Contains("TeamNames", (Usings + source).Split('\n')[d.Location.GetLineSpan().StartLinePosition.Line], System.StringComparison.Ordinal);
    }

    [Fact]
    public void AMisuseMovedInAnEditIsReportedWhereItIsNow()
    {
        string before = Context("", """
            public static class Handler
            {
                public static bool Get(Db db) => db.People.Where(p => p.Age > 1) is not null;
            }
            """);
        string after = before.Replace("public static class Handler", "\npublic static class Handler", System.StringComparison.Ordinal);
        var (first, second) = RunTwice(before, after);
        var d1 = Assert.Single(first, x => x.Id == "CFDATA002");
        var d2 = Assert.Single(second, x => x.Id == "CFDATA002");
        Assert.Equal(d1.Location.GetLineSpan().StartLinePosition.Line + 1, d2.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal("db.People.Where(p => p.Age > 1)", (Usings + after).Substring(d2.Location.SourceSpan.Start, d2.Location.SourceSpan.Length));
    }

    [Fact]
    public void InAndRefReadonlyParametersReadTheSameEachTime()
    {
        var r = Clean(Context("""
                public Page<Name> Feed(in Paging page) => People.OrderBy(p => p.Id).Page(page, countUpTo: 10).Select(p => new Name(p.Id, p.Name));
                public Page<Name> FeedOf(ref readonly Paging page) => People.OrderBy(p => p.Id).Page(page, countUpTo: 10).Select(p => new Name(p.Id, p.Name));
                public Write Same(in long id, Utf8 n) => Insert(Pets, new Pet { Id = id, OwnerId = id, Name = n });
            """, """
            public static class Handler
            {
                public static Page<Name> Get(Db db, Paging page) => db.Feed(in page);
            }
            """));
        // the generated code reads a member of the parameter; nothing runs
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"global::System\.Math\.Max\(0L, page\.Offset\)"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $1, $2)", r.Sql("Same"));
    }

    [Fact]
    public void APropertyAnOverrideOrAnImplementationMayComputeIsAValuePerPlace()
    {
        var r = Clean(Context("""
                public Write Seq(ISeq seq, Utf8 n) => Insert(Pets, new Pet { Id = seq.Next, OwnerId = seq.Next, Name = n });
                public Write Abstract(Counter c, Utf8 n) => Insert(Pets, new Pet { Id = c.Next, OwnerId = c.Next, Name = n });
                public Write Virtual(Counter c, Utf8 n) => Insert(Pets, new Pet { Id = c.Soft, OwnerId = c.Soft, Name = n });
                public Write Override(Impl i, Utf8 n) => Insert(Pets, new Pet { Id = i.Soft, OwnerId = i.Soft, Name = n });
                public Write Auto(Fixed f, Utf8 n) => Insert(Pets, new Pet { Id = f.Id, OwnerId = f.Id, Name = n });
            """, """
            public interface ISeq { long Next { get; } }
            public abstract class Counter { public abstract long Next { get; } public virtual long Soft { get; } }
            public sealed class Impl : Counter { long _n; public override long Next => ++_n; public override long Soft { get; } }
            public sealed class Fixed { public long Id { get; init; } }
            """));
        // an interface's, abstract, virtual or overriding property is read where it is written; a sealed class's
        // auto-property reads the same each time
        foreach (var name in new[] { "Seq", "Abstract", "Virtual", "Override" })
            Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $2, $3)", r.Sql(name));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"= seq\.Next;"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"= c\.Next;"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES ($1, $1, $2)", r.Sql("Auto"));
    }

    [Fact]
    public void AValueBoundAsADoubleAndAsALongKeepsEveryDigitOfTheLong()
    {
        var r = Clean(Entities + """
            public sealed class Score { public long Id { get; init; } public double Points { get; init; } }
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<Score> Scores => Table<Score>();
                public Table<Pet> Pets => Table<Pet>();
                public Write Scored(long n, Utf8 name) => Insert(Scores, new Score { Id = 1, Points = n }).Then(Insert(Pets, new Pet { Id = n, OwnerId = 1, Name = name }));
                public Write ScoredOf(Utf8 name) => Scored(Ids.Next(), name);
                public Write Both(long n) => Insert(Scores, new Score { Points = n, Id = n });
                public Write BothOf() => Both(Ids.Next());
            }
            """ + Calls);
        // the call's value is kept as its long, and each use converts from it: no long cut back from a double
        // (2^53 + 1 would come back as 2^53)
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Next\("));
        Assert.DoesNotMatch(@"\(long\)__", r.Generated);
        Assert.DoesNotMatch(@"\(global::System\.Int64\)__", r.Generated);
        // one statement binds the value as a double and as a long: two parameters, the long's exact
        Assert.Equal("INSERT INTO scores (points, id) VALUES ($1, $2)", r.Sql("Both"));
        Assert.Equal("INSERT INTO scores (points, id) VALUES ($1, $2)", r.Sql("BothOf"));
        var bound = r.Binder("BothOf");
        Assert.Equal(2, bound.Count);
        Assert.NotEqual(bound[0], bound[1]);
    }

    [Fact]
    public void AnOverloadsStatementsAreDocumentedByTheirNumber()
    {
        var r = Clean(Context("""
                public Write Touch(long id) => Insert(Pets, new Pet { Id = id, OwnerId = 1, Name = Utf8.Empty }).Then(Insert(People, new Person { Age = 1 }));
                public Write Touch(Utf8 n) => Insert(People, new Person { Name = n }).Then(Insert(People, new Person { Nick = n }));
            """));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"/// <summary>Touch\.</summary>"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(r.Generated, @"/// <summary>Touch, statement 2\.</summary>"));
        Assert.DoesNotContain("statement Int64", r.Generated, System.StringComparison.Ordinal);
        Assert.DoesNotContain("statement Utf8", r.Generated, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AQueryMethodOfABaseImplementingAnInterfaceTheSubclassAddsIsRefused()
    {
        string source = Entities + """
            public interface INames { One<Name> ByName(Utf8 name); }
            public abstract partial class BaseDb : DataContext<Postgres>
            {
                public Table<Person> People => Table<Person>();
                public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => new Name(p.Id, p.Name)).One();
            }
            public sealed partial class AppDb : BaseDb, INames;
            public sealed partial class OwnDb : BaseDb, INames
            {
                public One<long> Count() => People.Count();
            }
            public static class Handler
            {
                public static One<Name> Get(INames db, Utf8 name) => db.ByName(name);
            }
            """;
        var r = Run(source);
        var errors = r.Generator.Where(d => d.Id == "CFDATA007").ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d => Assert.Contains("'ByName' implements an interface", Message(d), System.StringComparison.Ordinal));
        Assert.Equal(["AppDb", "OwnDb"], errors.Select(d => At(d, source)).Order());
        Assert.Empty(r.Errors.Where(e => !e.Id.StartsWith("CFDATA", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void AnInlinedMethodsArgumentIsComputedAtTheCallLikeEveryArgumentInCSharp()
    {
        var r = Clean(Context("""
                public Write Log(long audit, Utf8 n) => Insert(People, new Person { Name = n });
                public Write LogOf(Utf8 n) => Log(Ids.Next(), n);
                public Write Maybe(bool b, long k, Utf8 n) => Insert(People, new Person { Name = n }).ThenIf(b, Insert(Pets, new Pet { Id = k, OwnerId = 1, Name = n }));
                public Write MaybeOf(bool flag, Utf8 n) => Maybe(flag, Ids.Next(), n);
                static long Second(long a, long b) => b;
                public Write Static(Utf8 n) => Insert(Pets, new Pet { Id = Second(Ids.Next(), 1), OwnerId = 1, Name = n });
            """, Calls));
        // one call each, made whether the body uses the argument or not, and not only when the ThenIf holds
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Next\("));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(r.Generated, @"\n\s*var __a\d+ = global::Ids\.Next\(\);"));
        Assert.Equal("INSERT INTO pets (id, owner_id, name) VALUES (1, 1, $1)", r.Sql("Static"));
    }

    [Fact]
    public void ACallInAMethodInlinedTwiceIsMadeOncePerInlining()
    {
        var r = Clean(Context("""
                public Write W(Utf8 n) => Insert(People, new Person { Name = n }).Then(Insert(Pets, new Pet { Id = Ids.Next(), OwnerId = 1, Name = n }));
                public Write Twice(Utf8 n) => W(n).Then(W(n));
                public Write Touch(long id) => People.Where(p => p.Id == id && p.Age < Ids.Next()).Update(p => new Person { Active = true });
                public Write TouchTwice(long a) => Touch(a).Then(Touch(a));
            """, Calls));
        // each inlining's call is its own, though written once: in the later statements of a body too
        Assert.Equal(2, Made(r, "Twice", "Ids.Next("));
        Assert.Equal(2, Made(r, "TouchTwice", "Ids.Next("));
    }

    /// <summary>How often the interceptor of <paramref name="method"/> writes <paramref name="call"/>.</summary>
    static int Made(Result r, string method, string call) => System.Text.RegularExpressions.Regex.Count(
        System.Text.RegularExpressions.Regex.Match(r.Generated, $@" {method}\(this [\s\S]*?return new").Value, System.Text.RegularExpressions.Regex.Escape(call));

    [Fact]
    public void AValueUnderAnInnerThenIfAndThenUnderItsOuterOneIsComputedOnce()
    {
        var r = Clean(Context("""
                public Write Tagged(bool a, bool b, long k, Utf8 n) => Insert(People, new Person { Name = n })
                    .ThenIf(a, Insert(People, new Person { Nick = n })
                        .ThenIf(b, Insert(Pets, new Pet { Id = k, OwnerId = 1, Name = n }))
                        .Then(Insert(Pets, new Pet { Id = k, OwnerId = 2, Name = n })));
                public Write TaggedOf(bool a, bool b, Utf8 n) => Tagged(a, b, Ids.Next(), n);
            """, Calls));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(r.Generated, @"Ids\.Next\("));
        // the pet under b and the one under a only: each binds the one value
        var bound = r.Bound("TaggedOf");
        Assert.Equal(6, bound.Count);
        Assert.Equal(bound[2], bound[4]);
        Assert.Matches(@"^__a\d+$", bound[2]);
    }

    [Fact]
    public void AnSqlInsertMovesTheKeyOfARowOfItsSchemaOnly()
    {
        string source = Entities + """
            [Table("gadgets", Schema = "tenant")] public sealed class Gadget { public long Id { get; init; } public Utf8 Name { get; init; } }
            public sealed partial class Db : DataContext<Postgres>
            {
                public Table<Gadget> Gadgets => Table<Gadget>();
                public Write<Name> Other(Utf8 a) => Insert(Gadgets, new Gadget { Name = a }).Then(Sql($"INSERT INTO other.gadgets (name) VALUES ('x')")).ThenSelect(g => new Name(g.Id, g.Name));
                public Write<Name> OtherCase(Utf8 a) => Insert(Gadgets, new Gadget { Name = a }).Then(Sql($"INSERT INTO \"Tenant\".gadgets (name) VALUES ('x')")).ThenSelect(g => new Name(g.Id, g.Name));
                public Write<Name> Same(Utf8 a) => Insert(Gadgets, new Gadget { Name = a }).Then(Sql($"INSERT INTO Tenant.Gadgets (name) VALUES ('x')")).ThenSelect(g => new Name(g.Id, g.Name));
                public Write<Name> Unqualified(Utf8 a) => Insert(Gadgets, new Gadget { Name = a }).Then(Sql($"INSERT INTO gadgets (name) VALUES ('x')")).ThenSelect(g => new Name(g.Id, g.Name));
            }
            """;
        var r = Run(source);
        // another schema's table of that name is another table; with no schema it may be the search path's
        var errors = r.Generator.Where(d => d.Id == "CFDATA001").ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, d => Assert.Contains("another insert into gadgets", Message(d), System.StringComparison.Ordinal));
        Assert.Equal("SELECT g.id, g.name FROM tenant.gadgets g WHERE g.id = (SELECT currval(pg_get_serial_sequence('tenant.gadgets', 'id')))", r.Sql("Other_3"));
        Assert.Equal("SELECT g.id, g.name FROM tenant.gadgets g WHERE g.id = (SELECT currval(pg_get_serial_sequence('tenant.gadgets', 'id')))", r.Sql("OtherCase_3"));
    }
}
