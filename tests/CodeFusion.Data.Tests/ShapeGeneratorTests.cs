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
}
