using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CodeFusion.Http.Generators.Tests;

/// <summary>Inputs at the edges of what the generator accepts: each either compiles cleanly or gets its diagnostic.</summary>
public class EdgeTests
{
    static string Generated(string source, string hint, params MetadataReference[] more)
    {
        var (sources, diagnostics, output) = Harness.Generate(source, more);
        Assert.Empty(diagnostics);
        Assert.Empty(Harness.Problems(output));
        return sources.Single(s => s.HintName == hint).SourceText.ToString();
    }

    [Fact]
    public void ABodyStructAlsoNestedElsewhereKeepsItsEnvelope()
    {
        // the nested use comes later in source order, so its model is the one kept
        string body = Generated("""
            [JsonEnvelope("user")] public partial struct NewUser { public Utf8 Email; }
            public partial struct Batch { public NewUser First; }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapPost("/users", static (NewUser u) => Reply.NoContent());
                    api.MapPost("/batch", static (Batch b) => Reply.NoContent());
                }
            }
            """, "NewUser.Body.g.cs");
        Assert.Contains("SequenceEqual(b.PropertyName(name), \"user\"u8)", body, StringComparison.Ordinal);
    }

    // A well-formed body is read once, as before the syntax check existed: the check runs only where binding has
    // failed without the reader throwing, a root that isn't an object, to tell a malformed rest from another shape.
    [Fact]
    public void ABodysSyntaxIsCheckedOnlyWhereBindingFailed()
    {
        string body = Generated("""
            [JsonEnvelope("user")] public partial struct NewUser { public Utf8 Email; public long Age; }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api) => api.MapPost("/users", static (NewUser u) => Reply.NoContent());
            }
            """, "NewUser.Body.g.cs");
        string[] lines = body.Split('\n');
        int check = Array.FindIndex(lines, l => l.Contains("IsWellFormed()", StringComparison.Ordinal));
        Assert.True(check > 0, body);
        Assert.Single(lines, l => l.Contains("IsWellFormed()", StringComparison.Ordinal));
        Assert.Contains("if (!b.ReadRootObject())", lines[check - 1], StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEnd(); // JSON of another shape", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntQueryFieldsBoundsStayInsideInt()
    {
        string query = Generated("""
            [FromQuery] public partial struct S
            {
                [Range(1, 1e10)] public int Limit;
                [Range(-5, 7)] public long Offset;
                static ErrorReply Invalid => null!;
            }
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapGet("/a", static (S q) => Reply.NoContent()); }
            """, "S.Query.g.cs");
        Assert.Contains("query.TryNumber(\"limit\"u8, 1L, int.MaxValue, out long n0)", query, StringComparison.Ordinal);
        Assert.Contains("query.TryNumber(\"offset\"u8, 0L, 7L, out long n1)", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[DefaultValue(5000000000L)] public int Limit;", "[DefaultValue] isn't a whole number")]
    [InlineData("[DefaultValue(\"x\")] public int Limit;", "[DefaultValue] isn't a whole number")]
    [InlineData("[Range(5, 1)] public long Limit;", "[Range] leaves no long")]
    [InlineData("public long A; [FromQuery(Name = \"a\")] public long B;", "as another member is")]
    [InlineData("[Range(typeof(DateTime), \"2000-01-01\", \"2001-01-01\")] public long Limit;", "[Range] isn't")]
    [InlineData("[Range(typeof(long), \"x\", \"5\")] public long Limit;", "[Range] isn't")]
    [InlineData("[Range(1, 5)] public Utf8 Limit;", "[Range] is for numbers")]
    [InlineData("[Range(5, 5, MinimumIsExclusive = true)] public long Limit;", "[Range] leaves no long")]
    public void QueryNumbersItCantBind(string members, string why)
    {
        var diagnostics = Harness.Generate($$"""
            [FromQuery] public partial struct S { {{members}} static ErrorReply Invalid => null!; }
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapGet("/a", static (S q) => Reply.NoContent()); }
            """).Diagnostics;
        Assert.Single(diagnostics);
        Assert.Equal("CFH0105", diagnostics[0].Id);
        Assert.Contains(why, diagnostics[0].GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void InheritedMembersAreWrittenBaseFirst()
    {
        string endpoints = Generated("""
            public class Base { public long Id { get; init; } public virtual Utf8 Name { get; init; } }
            public sealed class Derived : Base { public bool Flag { get; init; } public override Utf8 Name { get; init; } }
            public record Row(long Id);
            public record Wide(long Id, Utf8 Extra) : Row(Id);
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/d", static () => Reply.Ok(new Derived()));
                    api.MapGet("/w", static () => Reply.Ok(new Wide(1, default)));
                }
            }
            """, "CodeFusion.Http.Endpoints.g.cs");
        string derived = endpoints[endpoints.IndexOf("static void Write_Derived(", StringComparison.Ordinal)..];
        derived = derived[..derived.IndexOf("WriteEndObject", StringComparison.Ordinal)];
        Assert.True(derived.IndexOf("value.Id", StringComparison.Ordinal) < derived.IndexOf("value.Name", StringComparison.Ordinal));
        Assert.True(derived.IndexOf("value.Name", StringComparison.Ordinal) < derived.IndexOf("value.Flag", StringComparison.Ordinal));
        Assert.Equal(1, derived.Split("value.Name").Length - 1); // the override takes the base's place, once
        string wide = endpoints[endpoints.IndexOf("static void Write_Wide(", StringComparison.Ordinal)..];
        wide = wide[..wide.IndexOf("WriteEndObject", StringComparison.Ordinal)];
        Assert.Equal(1, wide.Split("value.Id").Length - 1);
        Assert.Contains("value.Extra", wide, StringComparison.Ordinal);
    }

    [Fact]
    public void KeywordsAsMemberNames()
    {
        string source = """
            [JsonEnvelope("e")] public partial struct Event { public Utf8 @event; public long? @default; [FromQuery(Name = "x")] public bool @class; }
            [FromQuery] public partial struct Q { public long @for; static ErrorReply Invalid => null!; }
            public readonly record struct View(long @default, Utf8 @string);
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api) =>
                    api.MapPost("/a", static (Event e, Q q) => Reply.Ok(new View(e.@default ?? q.@for, e.@event)));
            }
            """;
        var (_, diagnostics, output) = Harness.Generate(source);
        Assert.Empty(diagnostics);
        Assert.Empty(Harness.Problems(output));
    }

    [Fact]
    public void FileLocalTypesAndGenericContainers()
    {
        string fine = """
            file static class Handlers { public static Reply Get(Utf8 x) => Reply.NoContent(); }
            public static class Box<T> { public static Reply Get(long y) => Reply.NoContent(); }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a/{x}", Handlers.Get);
                    api.MapGet("/b/{y}", Box<int>.Get);
                }
            }
            """;
        var (_, diagnostics, output) = Harness.Generate(fine);
        Assert.Empty(diagnostics);
        Assert.Empty(Harness.Problems(output));

        string body = """
            file partial struct Hidden { public Utf8 X; }
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapPost("/a", static (Hidden h) => Reply.NoContent()); }
            """;
        var refused = Harness.Generate(body).Diagnostics;
        Assert.Contains(refused, d => d.Id is "CFH0104" or "CFH0106");
    }

    [Fact]
    public async Task AFileLocalReplyCantBeSent()
    {
        string source = """
            file readonly record struct Hidden(Utf8 X);
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapGet("/a", static () => Reply.Ok(new Hidden())); }
            """;
        var diagnostics = await Harness.Analyze(source);
        Assert.Single(diagnostics);
        Assert.Equal("CFH0003", diagnostics[0].Id);
        Assert.Contains("file-local", diagnostics[0].GetMessage(), StringComparison.Ordinal);
        Assert.Empty(Harness.Generate(source).Diagnostics); // the analyzer's rule: the generator only skips the call
    }

    [Theory]
    [InlineData("public partial struct S { [JsonPropertyName(\"tag\")] public Utf8 A; [JsonPropertyName(\"tag\")] public Utf8 B; }")]
    [InlineData("public partial struct S { public Utf8 Tag; [JsonPropertyName(\"tag\")] public Utf8 Other; }")]
    public void TwoBodyMembersWithOneName(string declaration)
    {
        var diagnostics = Harness.Generate($$"""
            {{declaration}}
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapPost("/a", static (S b) => Reply.NoContent()); }
            """).Diagnostics;
        Assert.Single(diagnostics);
        Assert.Contains("as another member is", diagnostics[0].GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullableServices()
    {
        // the handler may take a service as T?; what it is given is never null
        string endpoints = Generated("""
            public sealed class Db : IRequestScoped { public void Bind(IResponse response) { } }
            public interface IClock { long Now { get; } }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api) =>
                    api.MapGet("/a", static (Db? db, IClock? clock, Db plain) => Reply.NoContent());
            }
            """, "CodeFusion.Http.Endpoints.g.cs");
        Assert.Contains("setup.Service<global::Db>(out _slot0)", endpoints, StringComparison.Ordinal);
        Assert.Contains("setup.Singleton<global::IClock>()", endpoints, StringComparison.Ordinal);
    }

    static string Tokens(int n, string? last = null) =>
        string.Join(", ", Enumerable.Range(1, n).Select(i => $"CancellationToken t{i}{(i == n ? last : null)}"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    public void LambdasOfUpTo16Parameters(int n)
    {
        // up to 8 a Map overload takes the lambda as a Func; above, the Delegate overload does and the endpoint casts
        string endpoints = Generated($$"""
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapGet("/a", static ({{Tokens(n)}}) => Reply.NoContent()); }
            """, "CodeFusion.Http.Endpoints.g.cs");
        Assert.Contains($"_handler({string.Join(", ", Enumerable.Repeat("c.Aborted", n))})", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void SeventeenParameters()
    {
        // a Func takes 16 at most: a lambda of 17 can't be held, a method of 17 is called by name
        string source = $$"""
            public static class Handlers { public static Reply Get({{Tokens(17)}}) => Reply.NoContent(); }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a", static ({{Tokens(17)}}) => Reply.NoContent());
                    api.MapGet("/b", Handlers.Get);
                }
            }
            """;
        var (sources, diagnostics, output) = Harness.Generate(source);
        Assert.Equal(["CFH0107 6:26"], Harness.Brief(diagnostics));
        Assert.Contains("17 parameters", diagnostics[0].GetMessage(), StringComparison.Ordinal);
        Assert.Empty(Harness.Problems(output));
        Assert.Contains("global::Handlers.Get(c.Aborted", sources.Single().SourceText.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""api.MapGet("/a", static (CancellationToken t1, CancellationToken t2, CancellationToken t3, CancellationToken t4, CancellationToken t5, CancellationToken t6, CancellationToken t7, CancellationToken t8, CancellationToken t9 = default) => Reply.NoContent());""", "'t9' has a default value")]
    [InlineData("""api.MapGet("/a/{id}", Get);""", "'id' has a default value")]
    [InlineData("""api.MapGet("/a", Many);""", "'ids' is params")]
    public void DelegatesTheEndpointCantName(string call, string why)
    {
        // such a delegate's type is one the compiler makes up: casting it to a Func would throw at startup
        string source = $$"""
            public sealed class Ids : System.Collections.Generic.List<long> { }
            public static class P
            {
                static Reply Get(long id = 0) => Reply.NoContent();
                static Reply Many(params Ids ids) => Reply.NoContent();
                public static void Map(IEndpointRouteBuilder api)
                {
                    {{call}}
                }
            }
            """;
        var diagnostics = Harness.Generate(source).Diagnostics;
        Assert.Equal(["CFH0107"], diagnostics.Select(d => d.Id).ToArray());
        Assert.Contains(why, diagnostics[0].GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void MethodsItCallsByName()
    {
        // defaults don't matter to a call by name, and a closed generic type can be named
        string endpoints = Generated("""
            public static class Handlers { public static Reply Get(long id = 0) => Reply.NoContent(); public static Reply Of<T>(long id) => Reply.NoContent(); }
            public static class Box<T> { public static Reply Get(long id = 0) => Reply.NoContent(); }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a/{id}", Handlers.Get);
                    api.MapGet("/b/{id}", Box<int>.Get);
                    api.MapGet("/c/{id}", Handlers.Of<string>);
                }
            }
            """, "CodeFusion.Http.Endpoints.g.cs");
        Assert.Contains("global::Handlers.Get(p0)", endpoints, StringComparison.Ordinal);
        Assert.Contains("global::Box<int>.Get(p0)", endpoints, StringComparison.Ordinal);
        Assert.Contains("global::Handlers.Of<string>(p0)", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("_handler", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyImplementingIReplyExplicitly()
    {
        Generated("""
            public readonly struct Raw : IReply { ValueTask IReply.WriteAsync<T>(T response) => default; }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a", static () => new Raw());
                    api.MapGet("/b", static () => Reply.Ok(new Raw()));
                    api.MapGet("/c", static async () => { await Task.Yield(); return new Raw(); });
                }
            }
            """, "CodeFusion.Http.Endpoints.g.cs");
    }

    [Fact]
    public void EveryFormOfAMapCall()
    {
        string endpoints = Generated("""
            public static class P
            {
                public static void Map(IEndpointRouteBuilder? maybe, IEndpointRouteBuilder api)
                {
                    maybe?.MapGet("/a", static () => Reply.NoContent());
                    EndpointRouteBuilderExtensions.MapGet(api, "/b", static () => Reply.NoContent());
                    api.MapGet<Reply>("/d", static () => Reply.NoContent());
                    api.MapGet(handler: static () => Reply.NoContent(), pattern: "/e");
                }
            }
            """, "CodeFusion.Http.Endpoints.g.cs");
        Assert.Equal(4, endpoints.Split("[global::System.Runtime.CompilerServices.InterceptsLocation(").Length - 1);
    }

    [Fact]
    public async Task TypesNestedInAGenericClass()
    {
        string source = """
            public class Module<TDb>
            {
                public readonly record struct View(long Id);
                public sealed class Svc { }
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/x", static () => Reply.Ok(new View(1)));
                    api.MapGet("/y", static (Svc s) => Reply.NoContent());
                }
            }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api) =>
                    api.MapGet("/z", static (Module<int>.Svc s) => Reply.Ok(new Module<int>.View(1)));
            }
            """;
        var (_, diagnostics, output) = Harness.Generate(source);
        Assert.Equal(["CFH0103 8:26"], Harness.Brief(diagnostics));
        Assert.Contains("'TDb'", diagnostics[0].GetMessage(), StringComparison.Ordinal);
        Assert.Empty(Harness.Problems(output));
        var analyzed = await Harness.Analyze(source);
        Assert.Equal(["CFH0003 7:26"], Harness.Brief(analyzed));
        Assert.Contains("open generic", analyzed[0].GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeywordsAsTypeAndMethodNames()
    {
        Generated("""
            namespace App.@event
            {
                public static partial class @static
                {
                    public partial struct @class { public Utf8 X; }
                    [FromQuery] public partial struct @struct { public Utf8 @if; }
                    public readonly record struct @object(long Id);
                    public static Reply @return(@class body, @struct q) => Reply.NoContent();
                }
                public static class Routes
                {
                    public static void Map(IEndpointRouteBuilder api)
                    {
                        api.MapPost("/a", @static.@return);
                        api.MapGet("/b", static () => Reply.Ok(new @static.@object(1)));
                    }
                }
            }
            """, "CodeFusion.Http.Endpoints.g.cs");
    }

    [Fact]
    public void OnlyPublicGettersAreWritten()
    {
        // as System.Text.Json does: a getter the type keeps to itself, its derived types or its assembly isn't written
        MetadataReference lib = Harness.Library("public class Remote { public long Shown { get; init; } public long Hidden { internal get; init; } }");
        string endpoints = Generated("""
            public class View
            {
                public long A { get; init; }
                public long B { private get; init; }
                public long C { protected get; init; }
                public long D { internal get; init; }
            }
            public static class P
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/a", static () => Reply.Ok(new View()));
                    api.MapGet("/b", static () => Reply.Ok(new Remote()));
                }
            }
            """, "CodeFusion.Http.Endpoints.g.cs", lib);
        string view = endpoints[endpoints.IndexOf("static void Write_View(", StringComparison.Ordinal)..];
        view = view[..view.IndexOf("WriteEndObject", StringComparison.Ordinal)];
        Assert.Contains("value.A", view, StringComparison.Ordinal);
        foreach (string hidden in new[] { "value.B", "value.C", "value.D" }) Assert.DoesNotContain(hidden, view, StringComparison.Ordinal);
        string remote = endpoints[endpoints.IndexOf("static void Write_Remote(", StringComparison.Ordinal)..];
        remote = remote[..remote.IndexOf("WriteEndObject", StringComparison.Ordinal)];
        Assert.Contains("value.Shown", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("value.Hidden", remote, StringComparison.Ordinal);
    }

    [Fact]
    public void BindersSetOnlyWhatAPublicCallerCould()
    {
        // a member a client could otherwise set through the body or the query (over-posting): the binder leaves it
        string source = """
            #pragma warning disable CS0169, CS0649
            public partial struct Signup
            {
                public Utf8 Name;
                public long Shown { get; init; }
                internal bool IsAdmin;
                long _id;
                readonly long _fixed;
                long Hidden { get; }
                public long Role { get; private set; }
                public long Level { get; internal set; }
            }
            [FromQuery] public partial struct Filter { public Utf8 Tag; internal Utf8 Admin; Utf8 _secret; }
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapPost("/a", static (Signup s, Filter f) => Reply.NoContent()); }
            """;
        var (sources, diagnostics, output) = Harness.Generate(source);
        // the internal ones are left with a warning at each, since they bound before; the private ones quietly
        Assert.Equal(["CFH0109 6:19 Signup.IsAdmin", "CFH0109 13:75 Filter.Admin"], Unbound(diagnostics));
        Assert.Empty(Harness.Problems(output));
        string body = sources.Single(s => s.HintName == "Signup.Body.g.cs").SourceText.ToString();
        Assert.Contains("\"name\"u8", body, StringComparison.Ordinal);
        Assert.Contains("\"shown\"u8", body, StringComparison.Ordinal);
        foreach (string hidden in new[] { "isAdmin", "_id", "_fixed", "hidden", "role", "level", "IsAdmin", "Role", "Level" })
            Assert.DoesNotContain(hidden, body, StringComparison.Ordinal);
        string query = sources.Single(s => s.HintName == "Filter.Query.g.cs").SourceText.ToString();
        Assert.Contains("\"tag\"u8", query, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", query, StringComparison.Ordinal);
    }

    /// <summary>"ID line:column Type.Member" for each warning, lines counted from the source as given.</summary>
    static string[] Unbound(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics) =>
        Harness.Brief(diagnostics).Zip(diagnostics.OrderBy(d => d.Location.SourceSpan.Start),
            (brief, d) => $"{brief} {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Split('\'')[1]}").ToArray();

    [Fact]
    public void AnInternalMemberABinderLeavesIsAWarningAtTheMember()
    {
        // it bound before binders took only what a public caller could set: now it keeps its default, and says so
        string source = """
            #pragma warning disable CS0169, CS0649
            internal partial struct Signup
            {
                public Utf8 Name;
                internal bool IsAdmin;
                internal long Level { get; set; }
                [JsonIgnore] internal long Quiet;
                long _private;
                internal readonly long Fixed;
                internal long Computed => 1;
                internal long Init { get; }
                internal static long Shared;
                internal const long Constant = 1;
            }
            public static class P { public static void Map(IEndpointRouteBuilder api) { api.MapPost("/a", static (Signup s) => Reply.NoContent()); api.MapPut("/b", static (Signup s) => Reply.NoContent()); } }
            """;
        var (_, diagnostics, output) = Harness.Generate(source);
        // once each, however many Map calls bind the struct
        Assert.Equal(["CFH0109 5:19 Signup.IsAdmin", "CFH0109 6:19 Signup.Level"], Unbound(diagnostics));
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Empty(Harness.Problems(output));
    }

    [Fact]
    public void APragmaAtTheMemberSilencesItsWarning()
    {
        // the warning came with a location outside the compilation's trees, which a pragma can't reach: an app had to
        // turn it off for the whole project
        string source = """
            #pragma warning disable CS0169, CS0649
            internal partial struct Signup
            {
                public Utf8 Name;
            #pragma warning disable CFH0109
                internal bool IsAdmin;
            #pragma warning restore CFH0109
                internal long Level { get; set; }
            }
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapPost("/a", static (Signup s) => Reply.NoContent()); }
            """;
        var (_, diagnostics, _) = Harness.Generate(source);
        Assert.Equal(["CFH0109 6:19 Signup.IsAdmin", "CFH0109 8:19 Signup.Level"], Unbound(diagnostics));
        Assert.All(diagnostics, d => Assert.Equal("Program.cs", d.Location.SourceTree?.FilePath));
        Assert.Equal([true, false], diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Select(d => d.IsSuppressed));
    }

    [Theory]
    [InlineData("internal partial struct S { internal required long N; }", "S.N")]
    [InlineData("internal partial struct S { [JsonIgnore] public required long N; }", "S.N")]
    [InlineData("internal partial struct S { public required long N { get; internal set; } }", "S.N")]
    [InlineData("[FromQuery] internal partial struct S { internal required long N; static ErrorReply Invalid => default; }", "S.N")]
    public void ARequiredMemberABinderWontSetIsRefusedAtTheMapCall(string declaration, string member)
    {
        // the binder creates the struct with an initializer of what it set: a required member left out was CS9035 in
        // generated code, with nothing pointing at the cause
        string source = declaration + """

            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapPost("/a", static (S s) => Reply.NoContent()); }
            """;
        var (_, diagnostics, output) = Harness.Generate(source);
        Diagnostic refused = Assert.Single(diagnostics);
        Assert.Equal("CFH0105", refused.Id);
        Assert.Contains($"'{member}' is required", refused.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain(Harness.Problems(output), p => p.Contains("CS9035", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[Range(1, 5)] public long N;", "1L, 5L")]
    [InlineData("[Range(typeof(long), \"1\", \"50\")] public long N;", "1L, 50L")]
    [InlineData("[Range(typeof(int), \"10\", \"20\", MinimumIsExclusive = true, MaximumIsExclusive = true)] public int N;", "11L, 19L")]
    [InlineData("[Range(typeof(double), \"0.5\", \"9.5\")] public long N;", "1L, 9L")]
    [InlineData("[Range(0, 10, MaximumIsExclusive = true)] public long N;", "0L, 9L")]
    [InlineData("[Range(0.5, 2.0, MaximumIsExclusive = true)] public long N;", "1L, 1L")]
    [InlineData("[Range(1.0, 2.0, MinimumIsExclusive = true)] public long N;", "2L, 2L")]
    [InlineData("[Range(typeof(long), \"-5\", \"9223372036854775807\")] public long N;", "0L, long.MaxValue")]
    public void QueryRanges(string member, string bounds)
    {
        string query = Generated($$"""
            [FromQuery] public partial struct S { {{member}} static ErrorReply Invalid => null!; }
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapGet("/a", static (S q) => Reply.NoContent()); }
            """, "S.Query.g.cs");
        Assert.Contains($"query.TryNumber(\"n\"u8, {bounds}, out long n0)", query, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKnownTypesAreResolvedOncePerCompilation()
    {
        var c = Harness.Compilation("public static class P { }");
        Assert.Same(KnownTypes.From(c), KnownTypes.From(c));
        Assert.NotSame(KnownTypes.From(c), KnownTypes.From(c.WithAssemblyName("Other")));
        Assert.Null(KnownTypes.From(c.WithReferences(c.References.Take(1))));
    }

    [Fact]
    public async Task TwoReplyMembersWithOneName()
    {
        var diagnostics = await Harness.Analyze("""
            public readonly record struct View(long A, [property: JsonPropertyName("a")] long B);
            public static class P { public static void Map(IEndpointRouteBuilder api) => api.MapGet("/a", static () => Reply.Ok(new View())); }
            """);
        Assert.Single(diagnostics);
        Assert.Contains("two members written as 'a'", diagnostics[0].GetMessage(), StringComparison.Ordinal);
    }
}
