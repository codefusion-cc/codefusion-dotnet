using System.Linq;
using System.Threading.Tasks;
using CodeFusion.Http.CodeFixes;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CodeFusion.Http.Generators.Tests;

/// <summary>
/// Every rule, at the place it points to: the analyzers' CFH0001 to CFH0005 (with their code fixes) and the
/// generator's CFH01xx for Map calls it can't compile. A call the generator refuses gets no interceptor, so an app
/// that ignored the error would throw at that Map call; the error stops the build first.
/// </summary>
public class DiagnosticTests
{
    static string Map(string body, string members = "") => $$"""
        public static class Errors { public static readonly ErrorReply E = new(404, "{}"u8); }
        public readonly record struct View(Utf8 Name);
        {{members}}
        public static class Program
        {
            public static void Map(IEndpointRouteBuilder api, string route)
            {
        {{body}}
            }
        }
        """;

    static async Task<string[]> Analyzers(string source) => Harness.Brief(await Harness.Analyze(source));

    static string[] Generator(string source) => Harness.Brief(Harness.Generate(source).Diagnostics);

    // ---- CFH0001: static handlers ----

    [Fact]
    public async Task ALambdaThatIsntStatic()
    {
        string source = Map("""        api.MapGet("/a", () => Reply.NoContent());""");
        Assert.Equal(["CFH0001 8:26"], await Analyzers(source));
        string fixedSource = await Harness.Fix(source, new MakeHandlerStatic(), "CFH0001");
        Assert.Contains("""api.MapGet("/a", static () => Reply.NoContent());""", fixedSource, System.StringComparison.Ordinal);
        Assert.Empty(await Analyzers(fixedSource));
    }

    [Fact]
    public async Task AnAsyncLambdaThatIsntStatic()
    {
        string source = Map("""        api.MapGet("/a", async (Utf8 x) => { await Task.Yield(); return Reply.NoContent(); });""");
        Assert.Equal(["CFH0001 8:26"], await Analyzers(source));
        string fixedSource = await Harness.Fix(source, new MakeHandlerStatic(), "CFH0001");
        Assert.Contains("""api.MapGet("/a", static async (Utf8 x) =>""", fixedSource, System.StringComparison.Ordinal);
        Assert.Empty(await Analyzers(fixedSource));
    }

    [Fact]
    public async Task ACapturingLambdaGetsNoFix()
    {
        string source = Map("""        api.MapGet("/a", (Utf8 x) => route.Length > 0 ? Reply.NoContent() : Reply.NotFound(Errors.E));""");
        var diagnostics = await Harness.Analyze(source);
        Assert.Equal(["CFH0001 8:26"], Harness.Brief(diagnostics));
        Assert.Contains("captures 'route'", diagnostics.Single().GetMessage(), System.StringComparison.Ordinal);
        Assert.Equal(source, await Harness.Fix(source, new MakeHandlerStatic(), "CFH0001"));
    }

    [Fact]
    public async Task ALambdaCallingALocalFunctionFromOutsideGetsNoFix()
    {
        // a static lambda can't call a local function from outside it that isn't static, and one that captures
        // (itself or through another) makes the lambda capture
        string source = Map("""
                    Reply Local() => route.Length > 0 ? Reply.NoContent() : Reply.NotFound(Errors.E);
                    Reply Deeper() => Local();
                    Reply Plain() => Reply.NoContent();
                    static Reply Alone() => Reply.NoContent();
                    api.MapGet("/d", () => Alone());
                    api.MapGet("/e", (Utf8 x) => { Reply Inner() => x.IsEmpty ? Reply.NoContent() : Alone(); return Inner(); });
                    api.MapGet("/c", () => Plain());
                    api.MapGet("/a", () => Local());
                    api.MapGet("/b", () => Deeper());
            """);
        var diagnostics = (await Harness.Analyze(source)).OrderBy(d => d.Location.SourceSpan.Start).ToArray();
        Assert.Equal(["CFH0001 12:26", "CFH0001 13:26", "CFH0001 14:26", "CFH0001 15:26", "CFH0001 16:26"], Harness.Brief(diagnostics));
        Assert.Contains("calls the local function 'Plain', which isn't static", diagnostics[2].GetMessage(), System.StringComparison.Ordinal);
        Assert.Contains("captures 'route'", diagnostics[3].GetMessage(), System.StringComparison.Ordinal);
        Assert.Contains("captures 'route'", diagnostics[4].GetMessage(), System.StringComparison.Ordinal);
        string fixedSource = await Harness.Fix(source, new MakeHandlerStatic(), "CFH0001");
        Assert.Equal(["CFH0001 14:26", "CFH0001 15:26", "CFH0001 16:26"], await Analyzers(fixedSource));
        Assert.Contains("""api.MapGet("/d", static () => Alone());""", fixedSource, System.StringComparison.Ordinal);
        Assert.Contains("""api.MapGet("/e", static (Utf8 x) =>""", fixedSource, System.StringComparison.Ordinal);
        Assert.Empty(Harness.Problems(Harness.Generate(fixedSource).Output));
    }

    [Fact]
    public async Task AnAnonymousMethodIsTheOneMadeStatic()
    {
        string source = Map("""        Action register = () => api.MapGet("/a", delegate (Utf8 x) { return Reply.NoContent(); });""");
        Assert.Equal(["CFH0001 8:50"], await Analyzers(source));
        string fixedSource = await Harness.Fix(source, new MakeHandlerStatic(), "CFH0001");
        Assert.Contains("""Action register = () => api.MapGet("/a", static delegate (Utf8 x) {""", fixedSource, System.StringComparison.Ordinal);
        Assert.Empty(await Analyzers(fixedSource));
        Assert.Empty(Harness.Problems(Harness.Generate(fixedSource).Output));
    }

    [Fact]
    public async Task AnInstanceMethod()
    {
        string source = Map("""        api.MapGet("/a", new Handlers().Get);""", "public sealed class Handlers { public Reply Get() => Reply.NoContent(); }");
        Assert.Equal(["CFH0001 8:26"], await Analyzers(source));
    }

    [Fact]
    public async Task StaticHandlersAreFine()
    {
        string source = Map("""
                    api.MapGet("/a", static () => Reply.NoContent());
                    api.MapGet("/b", Handlers.Get);
                    api.MapGet("/c", static (Utf8 x) => { var local = x; Func<int> f = () => local.Length; return Reply.NoContent(); });
            """, "public static class Handlers { public static Reply Get() => Reply.NoContent(); }");
        Assert.Empty(await Analyzers(source));
        Assert.Empty(Generator(source));
    }

    // ---- CFH0002: Utf8, not string ----

    [Fact]
    public async Task AStringParameter()
    {
        string source = Map("""        api.MapGet("/a/{name}", static (string name, long id) => Reply.NoContent());""");
        Assert.Equal(["CFH0002 8:48"], await Analyzers(source));
        Assert.Empty(Generator(source)); // the analyzer's rule: the generator only skips the call
        string fixedSource = await Harness.Fix(source, new TakeUtf8(), "CFH0002");
        Assert.Contains("""static (Utf8 name, long id)""", fixedSource, System.StringComparison.Ordinal);
        Assert.Empty(await Analyzers(fixedSource));
    }

    // ---- CFH0003: replies the generator can send ----

    [Theory]
    [InlineData("""static () => 42""", "struct implementing IReply")]
    [InlineData("""static () => Task.FromResult(Reply.NoContent())""", "async lambda passed to Map")]
    [InlineData("""static () => new ValueTask<Reply>(Reply.NoContent())""", "async lambda passed to Map")]
    [InlineData("""static () => Reply.Ok(DateTime.UtcNow)""", "use Timestamp")]
    [InlineData("""static () => Reply.Ok(new Bad())""", "Bad.Items")]
    [InlineData("""static () => Reply.Ok((object)1)""", "object has no JSON form")]
    public async Task RepliesThatCantBeSent(string handler, string why)
    {
        string source = Map($"""        api.MapGet("/a", {handler});""",
            "public readonly record struct Bad(System.Collections.Generic.IEnumerable<int> Items);");
        var diagnostics = (await Harness.Analyze(source)).Where(d => d.Id == "CFH0003").ToArray();
        Assert.Equal(["CFH0003 8:26"], Harness.Brief(diagnostics));
        Assert.Contains(why, diagnostics[0].GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAsyncLambdaReturningTaskIsRefused()
    {
        // with Task<T> as its return type the lambda can only take the Delegate overload
        string source = Map("""        api.MapGet("/a", Handlers.Get);""",
            "public static class Handlers { public static async Task<Reply> Get() { await Task.Yield(); return Reply.NoContent(); } }");
        var diagnostics = await Harness.Analyze(source);
        Assert.Equal(["CFH0003 8:26"], Harness.Brief(diagnostics));
        Assert.Contains("Pending<T>", diagnostics[0].GetMessage(), System.StringComparison.Ordinal);
    }

    // ---- CFH0004: no Utf8 kept in a class ----

    [Fact]
    public async Task Utf8InAClass()
    {
        string source = """
            public sealed class Cache
            {
                Utf8 _last;
                public static Utf8? Shared;
                public Viewer Who { get; set; }
                public Pair[] Pairs = [];
                public System.Collections.Generic.List<RawJson> Raws = [];
                public Utf8 Computed => default;
                public string Copy = "";
                public void Keep(Utf8 x) => _last = x;
            }
            public struct Pair { public Utf8List Tags; }
            public struct Fine { public Utf8 Name; }
            public record Row(Utf8 Name);
            public struct Last { public static Utf8 Kept; public static Pair Pairs { get; set; } public static Utf8 Computed => default; }
            """;
        // a struct's own fields are held, and checked, where the struct is; its static ones are kept for good
        Assert.Equal(["CFH0004 3:10", "CFH0004 4:25", "CFH0004 5:19", "CFH0004 6:19", "CFH0004 7:53", "CFH0004 14:24", "CFH0004 15:41", "CFH0004 15:66"],
            await Analyzers(source));
    }

    [Fact]
    public async Task AnEntityADataContextDescribesMayHoldAUtf8()
    {
        // CodeFusion.Data's entities describe tables to its query generator and are never built: their Utf8 points
        // into no buffer. The same class without a table is still refused.
        string source = """
            namespace CodeFusion.Data
            {
                public sealed class Table<T> where T : class { }
                public abstract class DataContext { protected static Table<T> Table<T>() where T : class => new(); }
            }
            public sealed class User { public long Id { get; init; } public Utf8 Name { get; init; } }
            public sealed class Loose { public Utf8 Name { get; init; } }
            public sealed class Db : CodeFusion.Data.DataContext { public CodeFusion.Data.Table<User> Users => Table<User>(); }
            public sealed class NotAContext { public CodeFusion.Data.Table<Loose> Looses => new(); }
            """;
        Assert.Equal(["CFH0004 7:41"], await Analyzers(source));
    }

    [Fact]
    public async Task ADelegateOrComparerThatOnlyTakesAUtf8HoldsNone()
    {
        // a type parameter declared 'in' only takes values: Func<Utf8, bool> can't hand one back. One that returns
        // it, or a type with no variance (a List<T>, or IList<T> holding one), may keep it.
        string source = """
            using System.Collections.Generic;
            public sealed class Rules
            {
                public Func<Utf8, bool> Match = static _ => true;
                public Action<Utf8> Log = static _ => { };
                public IEqualityComparer<Utf8> Same = EqualityComparer<Utf8>.Default;
                public IComparer<Utf8>? Order;
                public Predicate<Utf8>? Test;
                public Func<Utf8>? Make;
                public Func<Utf8, Utf8>? Map;
                public IEnumerable<Utf8> All = [];
                public IList<Utf8>? Some;
                public Lazy<Utf8>? Later;
                public Func<Func<Utf8>, bool>? Calls;
                public Action<Action<Utf8>>? Hands;
            }
            """;
        Assert.Equal(["CFH0004 9:24", "CFH0004 10:30", "CFH0004 11:30", "CFH0004 12:25", "CFH0004 13:24", "CFH0004 15:34"],
            await Analyzers(source));
    }

    [Fact]
    public async Task AnEntitysBaseAndATableFromABaseContext()
    {
        // the entity base's members are the entity's columns; a context may get its tables from a base in a library,
        // and an entities-only assembly marks them with [Table]
        MetadataReference lib = Harness.Library("""
            namespace CodeFusion.Data
            {
                public sealed class Table<T> where T : class { }
                public abstract class DataContext { protected static Table<T> Table<T>() where T : class => new(); }
                public class SharedDb<TUser> : DataContext where TUser : class { public Table<TUser> Users => Table<TUser>(); }
            }
            """);
        string source = """
            public abstract class EntityBase { public Utf8 Slug { get; init; } }
            public sealed class AppUser : EntityBase { public Utf8 Name { get; init; } public static Utf8 Last; }
            public sealed class AppDb : CodeFusion.Data.SharedDb<AppUser> { }
            [System.ComponentModel.DataAnnotations.Schema.Table("posts")] public sealed class Post { public Utf8 Title { get; init; } }
            public class Loose { public Utf8 Name { get; init; } }
            public sealed class LooseChild : Loose { }
            public sealed class Cache : EntityBase { }
            public sealed class Session(Utf8 token) : AppUserBase(token) { }
            public abstract class AppUserBase(Utf8 token) { public bool Is(Utf8 t) => t == token; }
            public sealed class Member(Utf8 token) : AppUserBase(token) { }
            public sealed class MemberDb : CodeFusion.Data.SharedDb<Member> { }
            """;
        // an entity's static member is kept for good; the base's members are reported at a class that derives from it
        // without being an entity; [Table] marks an entity only in an assembly with no context
        Assert.Equal(["CFH0004 2:95", "CFH0004 4:102", "CFH0004 5:34", "CFH0004 7:21", "CFH0004 8:21"],
            Harness.Brief(await Harness.Analyze(source, "Program.cs", "", lib)));

        string entitiesOnly = """
            [System.ComponentModel.DataAnnotations.Schema.Table("posts")] public sealed class Post { public Utf8 Title { get; init; } }
            public sealed class Loose { public Utf8 Name { get; init; } }
            """;
        Assert.Equal(["CFH0004 2:41"], Harness.Brief(await Harness.Analyze(entitiesOnly, "Program.cs", "", lib)));
        // without CodeFusion.Data, nothing is an entity
        Assert.Equal(["CFH0004 1:102", "CFH0004 2:41"], Harness.Brief(await Harness.Analyze(entitiesOnly)));
    }

    [Fact]
    public async Task APrimaryConstructorParameterAMemberKeeps()
    {
        // a parameter used by a member is stored in a hidden field; one used only to initialize isn't kept
        string source = """
            public sealed class Session(Utf8 token) { public bool Is(Utf8 t) => t == token; }
            public sealed class Reads(Utf8 token) { public int Length = token.Length; public int Twice { get; } = token.Length * 2; }
            public class Base(Utf8 seen) { public int Seen = seen.Length; }
            public sealed class Passes(Utf8 token) : Base(token) { }
            public sealed class Later(Utf8 token) { public int Size() { return Inner(); int Inner() => token.Length; } }
            public readonly struct Holder(Utf8 x) { public int L => x.Length; }
            public readonly struct Plain(Utf8 x) { public int L { get; } = x.Length; }
            public sealed class Keeps { public Holder H; public Plain P; }
            public record Shown(Utf8 Name) { public int L => Name.Length; }
            public sealed class Cached(Utf8 token, string copy) { public string Copy => copy; public int Token => token.Length; }
            public sealed class Named(Utf8 token) { public string Name => nameof(token); }
            """;
        Assert.Equal(["CFH0004 1:34", "CFH0004 5:32", "CFH0004 8:43", "CFH0004 9:26", "CFH0004 10:33"], await Analyzers(source));
    }

    [Fact]
    public async Task TheGeneratedEndpointsHoldNoView()
    {
        // an endpoint holds its handler, a static lambda, as a Func that returns a view: that keeps none
        string source = Map("""        api.MapGet("/a/{name}", static (Utf8 name) => Reply.Ok(new View(name)));""");
        Assert.Empty(await Harness.Analyze(source));
    }

    [Theory]
    [InlineData("Routes.g.cs", "")]
    [InlineData("Routes.cs", "// <auto-generated/>\n")]
    public async Task FilesThatLookGeneratedAreCheckedToo(string path, string head)
    {
        // the generator leaves these rules to the analyzer and skips the call: unchecked, the endpoint would be lost
        string source = """
            using System.Linq;
            public static class Routes
            {
                static readonly int[] Numbers = [1];
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a", (string name) => Reply.NoContent());
                    api.MapGet("/b", static () => Numbers.Any() ? Reply.NoContent() : Reply.NoContent());
                }
            }
            public sealed class Cache { public Utf8 Last; }
            """;
        var ids = (await Harness.Analyze(source, path, head)).Select(d => d.Id).Order(System.StringComparer.Ordinal).ToArray();
        Assert.Equal(["CFH0001", "CFH0002", "CFH0004", "CFH0005"], ids);
    }

    // ---- CFH0005: no LINQ in handlers ----

    [Fact]
    public async Task LinqInALambdaAndInAMethod()
    {
        string source = """
            using System.Linq;
            public static class Handlers
            {
                static readonly int[] Numbers = [1, 2, 3];
                public static Reply Get() => Numbers.Any(n => n > 2) ? Reply.NoContent() : Reply.WithStatus(200);
                public static Reply NotAHandler() => Numbers.Count() > 0 ? Reply.NoContent() : Reply.NoContent();
            }
            public static class Program
            {
                static readonly int[] Numbers = [1, 2, 3];
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a", static () => (from n in Numbers where n > 1 select n).First() > 0 ? Reply.NoContent() : Reply.NoContent());
                    api.MapGet("/b", Handlers.Get);
                    var total = Numbers.Sum();
                }
            }
            """;
        string[] found = await Analyzers(source);
        // Handlers.Get's Any (found once the whole compilation is seen), the lambda's First, and the where clause
        // (the select is the identity, so the compiler leaves it out)
        Assert.Equal(["CFH0005 5:34", "CFH0005 13:39", "CFH0005 13:58"], found);
    }

    // ---- The generator's own ----

    [Fact]
    public void ARouteThatIsntALiteral()
    {
        Assert.Equal(["CFH0101 8:20"], Generator(Map("""        api.MapGet(route, static () => Reply.NoContent());""")));
        Assert.Equal(["CFH0101 8:20"], Generator(Map("""        api.MapGet("articles/{slug}", static (Utf8 slug) => Reply.NoContent());""")));
        Assert.Equal(["CFH0101 8:20"], Generator(Map("""        api.MapGet("/a//b", static () => Reply.NoContent());""")));
        Assert.Equal(["CFH0101 8:20"], Generator(Map("""        api.MapGet("/a/{x}/{X}", static (Utf8 x) => Reply.NoContent());""")));
        Assert.Equal(["CFH0101 8:20"], Generator(Map("""        api.MapGet("/a/x{y}", static (Utf8 y) => Reply.NoContent());""")));
        Assert.Equal(["CFH0101 8:20"], Generator(Map("""        api.MapGet("/a/{1y}", static (Utf8 y) => Reply.NoContent());""")));
        Assert.Empty(Generator(Map("""
                    const string Route = "/a/{y}";
                    api.MapGet(Route, static (Utf8 y) => Reply.NoContent());
                    api.MapGet("", static () => Reply.NoContent());
            """)));
    }

    [Fact]
    public void AHandlerItCantSee() =>
        Assert.Equal(["CFH0102 8:74"], Generator(Map("""        Func<Reply> h = static () => Reply.NoContent(); api.MapGet("/a", h);""")));

    [Theory]
    [InlineData("bool flag")]
    [InlineData("double x")]
    [InlineData("Guid id")]
    [InlineData("int[] ids")]
    [InlineData("Utf8? maybe")]
    [InlineData("Timestamp at")]
    [InlineData("ref int x")]
    public void ParametersItCantBind(string parameter)
    {
        string source = Map($$"""        api.MapGet("/a", static ({{parameter}}) => Reply.NoContent());""");
        Assert.Equal(["CFH0103"], Generator(source).Select(d => d.Split(' ')[0]).ToArray());
    }

    [Fact]
    public void TwoBodiesOrTwoQueries()
    {
        string members = """
            public partial struct A { public Utf8 X; }
            public partial struct B { public Utf8 Y; }
            [FromQuery] public partial struct Q { public Utf8 X; }
            [FromQuery] public partial struct R { public Utf8 Y; }
            """;
        Assert.Equal(["CFH0103 11:42"], Generator(Map("""        api.MapPost("/a", static (A a, B b) => Reply.NoContent());""", members)));
        Assert.Equal(["CFH0103 11:41"], Generator(Map("""        api.MapGet("/a", static (Q q, R r) => Reply.NoContent());""", members)));
        Assert.Empty(Generator(Map("""        api.MapPost("/a", static (Q q, A a) => Reply.NoContent());""", members)));
    }

    [Theory]
    [InlineData("public struct NotPartial { public Utf8 X; }", "must be a partial struct")]
    [InlineData("public partial struct G<T> { public Utf8 X; }", "is generic")]
    [InlineData("public static class Outer { public partial struct Inner { public Utf8 X; } }", "must be partial too")]
    public void BodyStructsItCantExtend(string declaration, string why)
    {
        string type = declaration.Contains("G<T>", System.StringComparison.Ordinal) ? "G<int>" : declaration.Contains("Outer", System.StringComparison.Ordinal) ? "Outer.Inner" : "NotPartial";
        var diagnostics = Harness.Generate(Map($$"""        api.MapPost("/a", static ({{type}} b) => Reply.NoContent());""", declaration)).Diagnostics;
        Assert.Equal(["CFH0104"], Harness.Brief(diagnostics).Select(d => d.Split(' ')[0]).ToArray());
        Assert.Contains(why, diagnostics[0].GetMessage(), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public partial struct S { public string X; }", "body fields are")]
    [InlineData("public partial struct S { public readonly Utf8 X; }", "is readonly")]
    [InlineData("public partial struct S { public Utf8 X { get; } }", "has no setter")]
    [InlineData("public partial struct S { [BodyError(\"Nope\")] public Utf8 X; }", "names no static ErrorReply")]
    [InlineData("public partial struct S { [JsonPropertyName(\"\")] public Utf8 X; }", "non-empty")]
    [InlineData("public partial struct S { public Other X; } public struct Other { public Utf8 Y; }", "must be a partial struct")]
    [InlineData("public partial struct S { [Range(1, 5)] public Utf8 X; }", "[Range] is for numbers")]
    [InlineData("public partial struct S { [Range(typeof(long), \"1\", \"x\")] public long X; }", "[Range] isn't")]
    [InlineData("public partial struct S { [Range(1, 1, MaximumIsExclusive = true)] public int X; }", "[Range] leaves no int")]
    public void BodyMembersItCantBind(string declaration, string why)
    {
        var diagnostics = Harness.Generate(Map("""        api.MapPost("/a", static (S b) => Reply.NoContent());""", declaration)).Diagnostics;
        Assert.Single(diagnostics);
        Assert.Contains(why, diagnostics[0].GetMessage(), System.StringComparison.Ordinal);
    }

    // A route value is digits: a [Range] it can't read, or one that leaves no value of digits, is a mistake in the app.
    [Theory]
    [InlineData("[Range(1, 5)] Utf8 slug", "/a/{slug}", "is a Utf8 route value: [Range] is for long and int ones")]
    [InlineData("[Range(-5, -1)] long id", "/a/{id}", "'id''s [Range] leaves no long of digits")]
    [InlineData("[Range(1.2, 1.8)] long id", "/a/{id}", "'id''s [Range] leaves no long of digits")]
    [InlineData("[Range(5, 5, MinimumIsExclusive = true)] int n", "/a/{n}", "'n''s [Range] leaves no int of digits")]
    [InlineData("[Range(3000000000.0, 4000000000.0)] int n", "/a/{n}", "'n''s [Range] leaves no int of digits")]
    [InlineData("[Range(typeof(long), \"1\", \"x\")] long id", "/a/{id}", "'id''s [Range] isn't one the generator can read")]
    [InlineData("[Range(typeof(string), \"a\", \"b\")] long id", "/a/{id}", "'id''s [Range] isn't one the generator can read")]
    public void RouteValueRangesItCantUse(string parameter, string route, string why)
    {
        var diagnostics = Harness.Generate(Map($$"""        api.MapGet("{{route}}", static ({{parameter}}) => Reply.NoContent());""")).Diagnostics;
        Assert.Equal(["CFH0103"], Harness.Brief(diagnostics).Select(d => d.Split(' ')[0]).ToArray());
        Assert.Contains(why, diagnostics[0].GetMessage(), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Range(0, 0)] long id")]
    [InlineData("[Range(-5, 5)] int n")]
    [InlineData("[Range(1, long.MaxValue)] long id")]
    [InlineData("[Range(typeof(int), \"1\", \"100\")] int n")]
    public void RouteValueRangesItUses(string parameter)
    {
        string name = parameter.EndsWith(" id", System.StringComparison.Ordinal) ? "id" : "n";
        var (_, diagnostics, output) = Harness.Generate(Map($$"""        api.MapGet("/a/{{{name}}}", static ({{parameter}}) => Reply.NoContent());"""));
        Assert.Empty(diagnostics);
        Assert.Empty(Harness.Problems(output));
    }

    [Theory]
    [InlineData("[FromQuery] public partial struct S { public long N; }", "static ErrorReply Invalid")]
    [InlineData("[FromQuery] public partial struct S { public bool B; static ErrorReply Invalid => null!; }", "query parameters are read as")]
    [InlineData("[FromQuery] public partial struct S { [FromQuery(Name = \"a-name-that-is-longer-than-thirty-two-bytes\")] public Utf8 X; }", "1 to 32 bytes")]
    public void QueryStructsItCantBind(string declaration, string why)
    {
        var diagnostics = Harness.Generate(Map("""        api.MapGet("/a", static (S q) => Reply.NoContent());""", declaration)).Diagnostics;
        Assert.Single(diagnostics);
        Assert.Contains(why, diagnostics[0].GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypesTheGeneratedCodeCantName()
    {
        string source = """
            public static partial class Program
            {
                record struct Secret(Utf8 X);
                partial struct Hidden { public Utf8 X; }
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a", static () => Reply.Ok(new Secret()));
                    api.MapPost("/b", static (Hidden h) => Reply.NoContent());
                }
            }
            """;
        // a parameter type it can't name is the generator's to report; a reply type is a reply it can't send
        Assert.Equal(["CFH0106 8:27"], Generator(source));
        var analyzed = await Harness.Analyze(source);
        Assert.Equal(["CFH0003 7:26"], Harness.Brief(analyzed));
        Assert.Contains("Secret is private, protected, file-local or internal to another assembly", analyzed[0].GetMessage(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratorLeavesOtherMapMethodsAlone()
    {
        string source = """
            public static class Mine
            {
                public static int MapGet(this string s, string pattern, Func<int> f) => f();
            }
            public static class Program
            {
                public static int Use() => "x".MapGet("/a", static () => 1);
            }
            """;
        var (sources, diagnostics, output) = Harness.Generate(source);
        Assert.Empty(sources);
        Assert.Empty(diagnostics);
        Assert.Empty(Harness.Problems(output));
    }
}
