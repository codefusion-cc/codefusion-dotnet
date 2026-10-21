using Xunit;

namespace CodeFusion.Http.Generators.Tests;

/// <summary>
/// What the generator writes, checked in under Snapshots/: a change to it shows up as a diff to read. Each case also
/// compiles the generated code with the app and requires no warnings, as a consumer's build with warnings as errors
/// would.
/// </summary>
public class SnapshotTests
{
    static void Check(string name, string source)
    {
        var (sources, diagnostics, output) = Harness.Generate(source);
        Assert.Empty(diagnostics);
        Assert.Empty(Harness.Problems(output));
        Harness.Verify(name, sources);
    }

    /// <summary>Every parameter source and reply kind, sync and async, lambdas and static methods.</summary>
    [Fact]
    public void Endpoints() => Check(nameof(Endpoints), """
        namespace App;

        [JsonEnvelope("article")]
        public readonly record struct ArticleView(Utf8 Slug, long Limit, bool Mine);

        public readonly record struct ProfileView(Utf8 Username, Utf8? Bio);

        [FromQuery]
        public partial struct Page
        {
            [DefaultValue(20), Range(1, 100)] public long Limit;
            public Utf8 Tag;
            static ErrorReply Invalid => Errors.BadPage;
        }

        [JsonEnvelope("user")]
        public partial struct LoginBody
        {
            public Utf8 Email;
            public Utf8 Password;
        }

        public static class Errors
        {
            public static readonly ErrorReply NotFound = new(404, "{}"u8);
            public static readonly ErrorReply BadPage = new(422, "{}"u8);
        }

        public sealed class Clock { public long Now => 0; }
        public sealed class Db : IRequestScoped { public void Bind(IResponse response) { } public ValueTask<long> Next() => default; }

        public readonly struct Raw : IReply
        {
            public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse => default;
        }

        public static class Handlers
        {
            public static Reply<ProfileView> Profile(Utf8 username, Viewer viewer) => Reply.Ok(new ProfileView(username, null));
            internal static ErrorReply Gone() => Errors.NotFound;
            static Raw Hidden() => default;
            public static void Map(IEndpointRouteBuilder api) => api.MapGet("/hidden", Hidden);
        }

        public static class Program
        {
            public static void Main(string[] args)
            {
                var builder = HttpApp.CreateBuilder(args);
                builder.AddJwtAuth("secret");
                builder.AddSingleton(new Clock());
                builder.AddPerConnection(() => new Db());
                var app = builder.Build();
                var api = app.MapGroup("/api");
                api.MapGet("/articles/{slug}", static (Utf8 slug, Page page, Viewer viewer, Clock clock) =>
                    slug.IsEmpty ? Reply.NotFound(Errors.NotFound) : Reply.Ok(new ArticleView(slug, page.Limit, viewer.IsAuthenticated)))
                   .AuthOptional();
                api.MapPost("/users/login", static async (LoginBody body, Db db, CancellationToken ct) =>
                {
                    long id = await db.Next();
                    return id == 0 ? Reply.Unauthorized(Errors.NotFound) : Reply.Created(new ProfileView(body.Email, null));
                });
                api.MapDelete("/articles/{slug}/comments/{id}", static (Utf8 slug, long id, int n) => id > 0 ? Reply.NoContent() : Reply.NotFound(Errors.NotFound))
                   .AuthRequired();
                api.MapGet("/profiles/{username}", Handlers.Profile);
                api.MapPut("/gone", Handlers.Gone);
                api.MapGet("/raw", static (IResponse response, HttpExchange exchange) => new Raw());
                api.MapGet("/raw-created", static () => Reply.Created(new Raw()));
                api.MapGet("/later", static async (Db db) =>
                {
                    await db.Next();
                    return new Raw();
                });
                Handlers.Map(api);
                app.Run();
            }
        }
        """);

    /// <summary>A body with every field kind, nested structs, names from attributes and errors per field.</summary>
    [Fact]
    public void Bodies() => Check(nameof(Bodies), """
        namespace App.Models
        {
            [JsonEnvelope("article")]
            public partial struct NewArticle
            {
                public Utf8 Title;
                [MinLength(1)] public Utf8? Description;
                public Utf8? Body { get; set; }
                [BodyError(nameof(BadTags))] public Utf8List TagList;
                public Utf8List? Labels;
                public long Count;
                public int? Rank { get; init; }
                public double Score;
                public bool? Draft;
                [JsonPropertyName("author_info")] public Author Author;
                public Author? Editor;
                [JsonIgnore] public long Ignored;
                public bool Computed => Count > 0;
                const int Max = 3;
                static ErrorReply Invalid => Errors.Invalid;
                static readonly ErrorReply BadTags = Errors.Invalid;
                static ErrorReply InvalidBody { get; } = Errors.Invalid;
            }

            public partial struct Author
            {
                public Utf8 Name;
                [BodyError(nameof(BadAge))] public int Age;
                static ErrorReply BadAge => Errors.Invalid;
            }

            public static class Errors
            {
                public static readonly ErrorReply Invalid = new(422, "{}"u8);
            }
        }

        namespace App
        {
            public static partial class Outer
            {
                public partial struct Plain
                {
                    public bool Flag;
                }
            }

            public static class Program
            {
                public static void Map(IEndpointRouteBuilder api)
                {
                    api.MapPost("/articles", static (App.Models.NewArticle body) => Reply.NoContent());
                    api.MapPost("/plain", static (Outer.Plain body) => Reply.NoContent());
                }
            }
        }
        """);

    /// <summary>A query struct with text, numbers, defaults, ranges and names.</summary>
    [Fact]
    public void Queries() => Check(nameof(Queries), """
        [FromQuery]
        public partial struct Search
        {
            public Utf8 Tag;
            [FromQuery(Name = "by")] public Utf8? Author;
            [DefaultValue(20), Range(1, 100)] public long Limit;
            public long? Offset;
            [Range(0.5, 9.5)] public int Stars { get; set; }
            [DefaultValue(3)] public int? Page;
            static readonly ErrorReply Invalid = new(422, "{}"u8);
        }

        public static class Program
        {
            public static void Map(IEndpointRouteBuilder api) => api.MapGet("/search", static (Search q) => Reply.NoContent());
        }
        """);

    /// <summary>The JSON rules: names, envelope, every value kind, nesting, sequences, classes and nulls.</summary>
    [Fact]
    public void Json() => Check(nameof(Json), """
        using System.Collections.Generic;
        using System.Collections.Immutable;

        public enum Kind : byte { A, B }
        public enum Big : long { X = long.MaxValue }

        [JsonEnvelope("item")]
        public readonly record struct Item(
            Utf8 Text, Utf8? MaybeText, RawJson Raw, RawJson? MaybeRaw, Timestamp At, Timestamp? MaybeAt, Utf8List Tags,
            string? Label, bool Flag, bool? MaybeFlag, byte B, short S, int I, uint U, long L, ulong UL, float F, double D,
            decimal M, int? MaybeI, Kind Kind, Big Big, Kind? MaybeKind, Inner Inner, Inner? MaybeInner, Node? Node,
            int[] Numbers, List<Inner> List, ImmutableArray<Utf8> Array, Utf8[][] Nested,
            [property: JsonPropertyName("renamed")] long Original, [property: JsonIgnore] long Skipped)
        {
            public static int Static => 0;
            public int this[int i] => i;
        }

        public struct Inner
        {
            public long Id;
            public Utf8 Name { get; set; }
            internal long Hidden { get; set; }
        }

        public sealed class Node
        {
            public Utf8 Name { get; init; }
            public Node? Next { get; init; }
        }

        public static class Program
        {
            public static void Map(IEndpointRouteBuilder api)
            {
                api.MapGet("/item", static () => Reply.Ok(new Item()));
                api.MapGet("/number", static () => Reply.Ok(42L));
                api.MapGet("/inner", static () => Reply.Created(new Inner()));
                api.MapGet("/node", static () => Reply.Ok(new Node()));
            }
        }
        """);

    /// <summary>Route values bounded by [Range] in each of its forms, and the app's reply when one doesn't bind.</summary>
    [Fact]
    public void RouteValues() => Check(nameof(RouteValues), """
        public readonly record struct View(long Id);

        public static class Program
        {
            public static void Map(IEndpointRouteBuilder api)
            {
                api.MapGet("/posts/{id}", static ([Range(1, long.MaxValue)] long id) => Reply.Ok(new View(id)));
                api.MapGet("/pages/{n}", static ([Range(1, 100)] int n) => Reply.Ok(new View(n)));
                api.MapGet("/open/{n}", static ([Range(0.5, 10.0, MaximumIsExclusive = true)] int n) => Reply.Ok(new View(n)));
                api.MapGet("/text/{n}", static ([Range(typeof(long), "-5", "9")] long n) => Reply.Ok(new View(n)));
                api.MapGet("/any/{id}/{n}/{slug}", static (long id, int n, Utf8 slug) => Reply.Ok(new View(id + n)));
            }
        }
        """);
}
