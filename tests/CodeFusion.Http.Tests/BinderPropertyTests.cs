using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeFusion.Http.Tests.Conduit;
using CodeFusion.Http.Tests.Reference;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// The generated binders and the router, given the contest's routes, query and bodies, must answer exactly as the
/// contest server's hand-written code did (its frozen copy in Reference/): the same route and values, the same 422s
/// in the same order, the same field values.
/// </summary>
public class BinderPropertyTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>A real arena over a real chunk pool, reset after each use as the server resets it.</summary>
    static RequestArena NewArena() => new(new BufferPool(4096, 16));

    /// <summary>The bytes at an offset inside a larger buffer, with junk around them, as a request buffer holds a body.</summary>
    static (byte[] Buffer, int Start) InBuffer(byte[] bytes)
    {
        var buffer = new byte[bytes.Length + 40];
        buffer.AsSpan().Fill((byte)'#');
        bytes.CopyTo(buffer, 17);
        return (buffer, 17);
    }

    static string Show(Utf8 t) => JsonSerializer.Serialize(Strict.GetString(t.Span));
    static string Show(Utf8? t) => t is { } x ? Show(x) : "null";
    static string Show(string? v) => v is null ? "null" : JsonSerializer.Serialize(v);

    // ---------- routes ----------

    static readonly Lazy<(HttpApp App, Dictionary<Endpoint, RouteId> Ids)> Routes = new(ConduitRoutes.Build);

    /// <summary>What RequestParser makes of a method: exact upper-case names only, the rest Other.</summary>
    static RequestMethod Parsed(string m) => m switch
    {
        "GET" => RequestMethod.Get,
        "POST" => RequestMethod.Post,
        "PUT" => RequestMethod.Put,
        "DELETE" => RequestMethod.Delete,
        "HEAD" => RequestMethod.Head,
        "PATCH" => RequestMethod.Patch,
        "OPTIONS" => RequestMethod.Options,
        _ => RequestMethod.Other,
    };

    static string Contest(string method, string path)
    {
        byte[] p = B(path);
        // the contest server had no HEAD; here it runs the GET endpoint
        ContestRoute r = ContestRoute.Match(B(method == "HEAD" ? "GET" : method), p);
        string Text(Range x) => x.Equals(default(Range)) ? "" : Encoding.UTF8.GetString(p[x]);
        return $"{r.Kind} {Text(r.Arg)} {Text(r.Id)}";
    }

    static string Ours(string method, string path)
    {
        var (app, ids) = Routes.Value;
        byte[] p = B(path);
        var values = new int[2 * Math.Max(1, app.Router.MaxParameters)];
        Endpoint? e = app.Router.Match(Router.Verb(Parsed(method), B(method)), p, values);
        if (e is null) return $"{RouteId.NotFound}  ";
        RouteId id = ids[e];
        // the contest's Arg is the first value and Id the second, in the template's order
        int n = id switch
        {
            RouteId.DeleteComment => 2,
            RouteId.ListArticles or RouteId.CreateArticle or RouteId.Feed or RouteId.Tags or RouteId.CurrentUser
                or RouteId.UpdateUser or RouteId.Register or RouteId.Login => 0,
            _ => 1,
        };
        string Value(int i) => i < n ? Encoding.UTF8.GetString(p, values[2 * i], values[2 * i + 1]) : "";
        return $"{id} {Value(0)} {Value(1)}";
    }

    static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE", "get", "Post", "pUt", "delete", "PATCH", "HEAD", "OPTIONS", "", "GETX", "DELET", "GÉT"];

    static readonly string[] Segments =
    [
        "articles", "feed", "comments", "favorite", "tags", "user", "users", "login", "profiles", "follow",
        "x", "slug-1", "123", "", "é", "Articles", "FEED", "a b", "%20", "api",
    ];

    static readonly string[] Templates =
    [
        "articles", "articles/feed", "articles/{}", "articles/{}/comments", "articles/{}/comments/{}", "articles/{}/favorite",
        "tags", "user", "users", "users/login", "profiles/{}", "profiles/{}/follow",
    ];

    /// <summary>Real routes, then often bent: a segment added, dropped, emptied or upper-cased, a trailing slash.</summary>
    static Gen<string> NearRoutes() =>
        from t in Gen.Elements(Templates)
        from args in Gen.Elements("x", "feed", "comments", "follow", "login", "1", "é", "a b").ArrayOf(2)
        from op in Gen.Choose(0, 9)
        from at in Gen.Choose(0, 4)
        from extra in Gen.Elements(Segments)
        select Bend(t.Replace("{}", args[0]).Replace("{}", args[1]).Split('/').ToList(), op, at, extra);

    static string Bend(List<string> segs, int op, int at, string extra)
    {
        int i = Math.Min(at, segs.Count - 1);
        switch (op)
        {
            case 0: segs.Insert(Math.Min(at, segs.Count), extra); break;
            case 1: if (segs.Count > 1) segs.RemoveAt(i); break;
            case 2: segs[i] = ""; break;
            case 3: segs[i] = segs[i].ToUpperInvariant(); break;
            case 4: segs.Add(""); break;
        }
        return "/api/" + string.Join("/", segs);
    }

    static Gen<string> Paths() => Gen.Frequency((1, RandomPaths()), (1, NearRoutes()));

    static Gen<string> RandomPaths() =>
        from prefix in Gen.Frequency((8, Gen.Constant("/api/")), (1, Gen.Elements("/api", "/", "//api/", "/API/", "/ap/", "/apix/", "/api//", "", "api/")))
        from segs in Gen.Elements(Segments).ListOf()
        from trailing in Gen.Frequency((6, Gen.Constant("")), (1, Gen.Constant("/")))
        select prefix + string.Join("/", segs.Take(7)) + trailing;

    [Theory]
    [InlineData("GET", "/api/articles")]
    [InlineData("GET", "/api/articles/feed")]
    [InlineData("PUT", "/api/articles/feed")]       // only GET has the literal: the parameter route takes it
    [InlineData("DELETE", "/api/articles/feed")]
    [InlineData("POST", "/api/articles/feed")]      // neither has POST
    [InlineData("get", "/api/articles/x")]
    [InlineData("HEAD", "/api/articles/x")]
    [InlineData("DELETE", "/api/articles/x/comments/12")]
    [InlineData("GET", "/api/articles/x/comments/12")]
    [InlineData("GET", "/api/articles//comments")]
    [InlineData("GET", "/api/articles/x/")]
    [InlineData("GET", "/api/articles/a/b/c/d/e")]
    [InlineData("POST", "/api/users/login")]
    [InlineData("GET", "/api/users/login")]
    [InlineData("GET", "/api")]
    [InlineData("GET", "/")]
    [InlineData("GET", "")]
    [InlineData("GET", "/API/tags")]
    [InlineData("GET", "/api/profiles/a%2Fb")]
    public void Examples(string method, string path) => Assert.Equal(Contest(method, path), Ours(method, path));

    [Property(MaxTest = 10000)]
    public Property RoutesMatchTheContestRouter() => Prop.ForAll(Gen.Elements(Methods).ToArbitrary(), Paths().ToArbitrary(), (m, p) =>
    {
        Assert.Equal(Contest(m, p), Ours(m, p));
        return true;
    });

    // ---------- the query ----------

    /// <summary>Articles.List: TryPage, then the first non-empty of tag, author and favorited.</summary>
    static string ContestList(string q)
    {
        byte[] query = B(q);
        if (!ContestQuery.TryPage(query, out long limit, out long offset)) return "bad page";
        var filter = new byte[ContestQuery.MaxDecodedLength(query.Length)];
        int n;
        string kind = (n = ContestQuery.Get(query, "tag"u8, filter)) > 0 ? "tag"
            : (n = ContestQuery.Get(query, "author"u8, filter)) > 0 ? "author"
            : (n = ContestQuery.Get(query, "favorited"u8, filter)) > 0 ? "favorited"
            : "global";
        return $"{kind} {(kind == "global" ? "" : Show(Strict.GetString(filter, 0, n)))} {limit} {offset}";
    }

    static string OurList(string q)
    {
        var (buffer, start) = InBuffer(B(q));
        RequestArena arena = NewArena();
        try
        {
            ErrorReply? e = ArticleListQuery.__BindQuery(new QuerySource(buffer, start, buffer.Length - 40, arena), out ArticleListQuery v);
            if (e is not null) return ConduitErrors.Name(e);
            (string kind, Utf8 text) = !v.Tag.IsEmpty ? ("tag", v.Tag) : !v.Author.IsEmpty ? ("author", v.Author) : !v.Favorited.IsEmpty ? ("favorited", v.Favorited) : ("global", default);
            return $"{kind} {(kind == "global" ? "" : Show(text))} {v.Limit} {v.Offset}";
        }
        finally
        {
            arena.Reset();
        }
    }

    static readonly string[] Keys = ["tag", "author", "favorited", "limit", "offset", "t%61g", "%74ag", "ta%67", "TAG", "tag+", "+tag", "t%zzg", "", "x", "lim%69t", "offset+"];

    static readonly string[] ValuePieces =
    [
        "a", "lorem", "5", "007", "100", "101", "0", "-1", "+", "%20", "%2B", "%2b", "%zz", "%2", "%", "%E2%82%AC", "%FF", "%C3",
        "%C3%A9", "%ED%A0%80", "%F0%9F%98%80", "%F0%9F%98", "%35", "%31%30%30", "é", "€", "😀", ";", "=", "%3D", "%26", "9223372036854775808", "9223372036854775807",
    ];

    static Gen<string> Queries()
    {
        var value = Gen.Elements(ValuePieces).ListOf().Select(ps => string.Concat(ps));
        var pair = Gen.Frequency(
            (6, from k in Gen.Elements(Keys) from v in value select k + "=" + v),
            (1, Gen.Elements(Keys)),
            (1, value));
        return pair.ListOf().Select(ps => string.Join("&", ps));
    }

    [Theory]
    [InlineData("")]
    [InlineData("limit=100&offset=0")]
    [InlineData("limit=101")]
    [InlineData("limit=0")]
    [InlineData("limit=")]
    [InlineData("limit")]
    [InlineData("offset=-1")]
    [InlineData("offset=9223372036854775807")]
    [InlineData("offset=9223372036854775808")]
    [InlineData("limit=%31%30")]
    [InlineData("limit=1%zz&limit=5")]           // a pair with a bad escape is skipped
    [InlineData("limit=5;&limit=6")]
    [InlineData("tag=&author=a+b")]              // empty means no filter
    [InlineData("tag=%FF%C3")]                   // bad UTF-8 becomes U+FFFD
    [InlineData("t%61g=x&tag=y")]                // the first, by its decoded key
    [InlineData("favorited=%E2%82%AC")]
    public void QueryExamples(string q) => Assert.Equal(ContestList(q), OurList(q));

    [Property(MaxTest = 5000)]
    public Property TheQueryBindsAsArticlesListDid() => Prop.ForAll(Queries().ToArbitrary(), q =>
    {
        Assert.Equal(ContestList(q), OurList(q));
        return true;
    });

    // ---------- bodies ----------

    /// <summary>What an endpoint of the contest made of a body: Body.TryRead, then its own checks in its own order.</summary>
    static string ContestBodyOutcome(string shape, byte[] body)
    {
        var scratch = new byte[body.Length];
        string Text(in ContestBodyField f) => Show(Strict.GetString(f.Value(body, scratch)));
        string Opt(in ContestBodyField f) => f.IsMissing ? "null" : Text(f);
        switch (shape)
        {
            case "register":
                if (!ContestBody.TryRead(body, ContestBodyShape.Register, scratch, out var r)) return "invalid body";
                if (!r[0].IsRequired | !r[1].IsRequired | !r[2].IsRequired) return "register fields";
                return $"{Text(r[0])} {Text(r[1])} {Text(r[2])}";
            case "login":
                if (!ContestBody.TryRead(body, ContestBodyShape.Login, scratch, out var l)) return "invalid body";
                if (!l[0].IsRequired | !l[1].IsRequired) return "login fields";
                return $"{Text(l[0])} {Text(l[1])}";
            case "update user":
                if (!ContestBody.TryRead(body, ContestBodyShape.UpdateUser, scratch, out var u)) return "invalid body";
                if (!u[0].IsOptional(true) | !u[1].IsOptional(true) | !u[2].IsOptional(true) | !u[3].IsOptional(false) | !u[4].IsOptional(false)) return "user fields";
                return $"{Opt(u[0])} {Opt(u[1])} {Opt(u[2])} {Opt(u[3])} {Opt(u[4])}";
            case "create article":
                if (!ContestBody.TryRead(body, ContestBodyShape.CreateArticle, scratch, out var a)) return "invalid body";
                if (!a[0].IsRequired | !a[1].IsRequired | !a[2].IsRequired) return "article fields";
                if (a[3].Kind == ContestFieldKind.Invalid) return "bad tag list";
                var tags = new List<string>();
                var reader = ContestBody.Tags(body, a[3], scratch);
                while (reader.Next(out var tag)) tags.Add(Show(Strict.GetString(tag)));
                return $"{Text(a[0])} {Text(a[1])} {Text(a[2])} [{string.Join(",", tags)}]";
            case "update article":
                if (!ContestBody.TryRead(body, ContestBodyShape.UpdateArticle, scratch, out var ua)) return "invalid body";
                if (!ua[0].IsOptional(true) | !ua[1].IsOptional(true) | !ua[2].IsOptional(true)) return "article update fields";
                return $"{Opt(ua[0])} {Opt(ua[1])} {Opt(ua[2])}";
            default:
                if (!ContestBody.TryRead(body, ContestBodyShape.CreateComment, scratch, out var c) || !c[0].IsRequired) return "comment fields";
                return Text(c[0]);
        }
    }

    static string OurBodyOutcome(string shape, byte[] body)
    {
        var (buffer, start) = InBuffer(body);
        RequestArena arena = NewArena();
        var source = new BodySource(buffer, start, body.Length, arena, ConduitErrors.InvalidBody);
        try
        {
            switch (shape)
            {
                case "register":
                    return RegisterBody.__BindBody(source, out var r) is { } er ? ConduitErrors.Name(er) : $"{Show(r.Username)} {Show(r.Email)} {Show(r.Password)}";
                case "login":
                    return LoginBody.__BindBody(source, out var l) is { } el ? ConduitErrors.Name(el) : $"{Show(l.Email)} {Show(l.Password)}";
                case "update user":
                    return UpdateUserBody.__BindBody(source, out var u) is { } eu ? ConduitErrors.Name(eu)
                        : $"{Show(u.Email)} {Show(u.Username)} {Show(u.Password)} {Show(u.Bio)} {Show(u.Image)}";
                case "create article":
                    if (CreateArticleBody.__BindBody(source, out var a) is { } ea) return ConduitErrors.Name(ea);
                    var tags = new List<string>();
                    foreach (Utf8 t in a.TagList) tags.Add(Show(t));
                    Assert.Equal(a.TagList.Count, tags.Count);
                    return $"{Show(a.Title)} {Show(a.Description)} {Show(a.Body)} [{string.Join(",", tags)}]";
                case "update article":
                    return UpdateArticleBody.__BindBody(source, out var ua) is { } eua ? ConduitErrors.Name(eua) : $"{Show(ua.Title)} {Show(ua.Description)} {Show(ua.Text)}";
                default:
                    return CommentBody.__BindBody(source, out var c) is { } ec ? ConduitErrors.Name(ec) : Show(c.Body);
            }
        }
        finally
        {
            arena.Reset();
        }
    }

    static readonly string[] Shapes = ["register", "login", "update user", "create article", "update article", "comment"];

    static void AssertSameBody(byte[] body)
    {
        foreach (string s in Shapes) Assert.Equal(ContestBodyOutcome(s, body), OurBodyOutcome(s, body));
    }

    [Theory]
    [InlineData("""{"user":{"username":"jake","email":"j@x.io","password":"pw"}}""")]
    [InlineData("""{"user":{"username":"","email":null,"password":5}}""")]
    [InlineData("""{"user":{"username":"a","username":"b"}}""")]
    [InlineData("""{"user":{"username":"a","username":7}}""")]
    [InlineData("""{"user":{"username":"a"},"user":{"email":"b"}}""")]
    [InlineData("""{"user":{"username":"a"},"user":"x"}""")]
    [InlineData("""{"user":"x","user":{"username":"a","email":"e","password":"p"}}""")]
    [InlineData("""{"user":{"username":"a\nbé😀\/","email":"e","password":"\"pw\""}}""")]
    [InlineData("""{"user":{"username":"\ud800"}}""")]
    [InlineData("""{"user":{"username":"\udc00x"}}""")]
    [InlineData("""{"user":{"username":"a\u0000b","email":"x","password":"y"}}""")]
    [InlineData("""{"user":{"bio":{"a":[1,{"b":null}]},"image":[],"email":true}}""")]
    [InlineData("""{"user":{"\ud800":1,"email":"x"}}""")]
    [InlineData("""{"user":{"email":"x","password":"y"}}""")]
    [InlineData("""{"user":{"email":"","username":"x","bio":"","image":null}}""")]
    [InlineData("""{"user":{}}""")]
    [InlineData("""{"user":null}""")]
    [InlineData("""{"User":{"email":"x"}}""")]
    [InlineData("""{"article":{"title":"t","description":"d","body":"b","tagList":["a","bé","\n"]}}""")]
    [InlineData("""{"article":{"title":"t","description":"d","body":"b","tagList":[]}}""")]
    [InlineData("""{"article":{"title":"t","description":"d","body":"b","tagList":null}}""")]
    [InlineData("""{"article":{"title":"t","description":"d","body":"b","tagList":"a"}}""")]
    [InlineData("""{"article":{"title":"t","description":"d","body":"b","tagList":["a",""]}}""")]
    [InlineData("""{"article":{"title":"","description":"d","body":"b","tagList":"a"}}""")]   // fields first
    [InlineData("""{"article":{"title":"t","description":"d","body":"b","tagList":["a"],"tagList":["b"]}}""")]
    [InlineData("""{"article":{"title":"","body":"x"}}""")]
    [InlineData("""{"comment":{"body":"x"}}  """)]
    [InlineData("""{"comment":{"body":"x"}} x""")]
    [InlineData("""{"comment":{"body":"x"},}""")]
    [InlineData("""{"comment":{"body":"x""")]
    [InlineData("""["comment"]""")]
    [InlineData("")]
    [InlineData("null")]
    public void BodyExamples(string body) => AssertSameBody(B(body));

    public static TheoryData<byte[]> RawBodies() => new()
    {
        Cat(B("{\"user\":{\"email\":\""), [0xFF], B("\",\"password\":\"x\"}}")),
        Cat(B("{\"user\":{\"email\":\""), [0xED, 0xA0, 0x80], B("\"}}")),
        Cat(B("{\"user\":{\"bio\":\""), [0xFF], B("\",\"email\":\"x\"}}")),
        Cat(B("{\"article\":{\"title\":\"t\",\"description\":\"d\",\"body\":\"b\",\"tagList\":[\"a\",\""), [0xFF], B("\"]}}")),
        Cat([0xEF, 0xBB, 0xBF], B("{\"comment\":{\"body\":\"x\\n\"}}")),
        Cat([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF], B("{\"comment\":{\"body\":\"x\"}}")),
        Cat([0xEF, 0xBB, 0xBF], B("{\"article\":{\"title\":\"t\",\"description\":\"d\",\"body\":\"b\",\"tagList\":[\"a\\n\",\"b\"]}}")),
        new byte[] { 0xEF, 0xBB, 0xBF },
    };

    [Theory]
    [MemberData(nameof(RawBodies))]
    public void RawBodyExamples(byte[] body) => AssertSameBody(body);

    [Theory]
    [InlineData(62)]
    [InlineData(63)]
    public void NestingAtJsonDocumentsLimit(int extra) =>
        AssertSameBody(B("""{"comment":{"body":"x","d":""" + new string('[', extra) + new string(']', extra) + "}}"));

    [Fact]
    public void HugeEscapedBodiesComeBackWhole()
    {
        string big = string.Concat(Enumerable.Repeat("abc\\u00e9\\n\\ud83d\\ude00", 40_000));
        AssertSameBody(B("{\"comment\":{\"body\":\"" + big + "\"}}"));
        AssertSameBody(B("{\"article\":{\"title\":\"" + big + "\",\"description\":\"d\",\"body\":\"b\",\"tagList\":[\"" + big + "\"]}}"));
    }

    static readonly string[] BodyKeys =
    [
        "user", "article", "comment", "username", "email", "password", "bio", "image", "title", "description", "body",
        "tagList", "\\u0075ser", "b\\u006fdy", "tag\\u004cist", "x", "User", "", "\\ud800", "em\\u0061il",
    ];

    static readonly string[] FieldKeys =
    [
        "username", "email", "password", "bio", "image", "title", "description", "body", "tagList",
        "b\\u006fdy", "tag\\u004cist", "em\\u0061il", "\\u0074itle",
    ];

    static readonly byte[][] CleanPieces =
    [
        B("a"), B("hi there"), B(" "), B("é"), B("€"), B("😀"), B("\\n"), B("\\\""), B("\\\\"), B("\\/"), B("\\t"),
        B("\\u0041"), B("\\u00e9"), B("\\ud83d\\ude00"), B("\\uD83D\\uDE00"), B("\\u0000"), B("\\b\\f\\r"),
    ];

    static readonly byte[][] BadPieces =
    [
        B("\\ud800"), B("\\udc00"), B("\\ud800\\ud800"), B("\\ud83d"), B("\\ude00x"), B("\\ud83dx"),
        [0xFF], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xE2, 0x82], [0x80], [0xF4, 0x90, 0x80, 0x80], [0x09], [0x00],
    ];

    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static byte[] Seq(string open, IEnumerable<byte[]> items, string close) =>
        Cat([B(open), .. items.SelectMany((x, i) => i == 0 ? [x] : new[] { B(","), x }), B(close)]);

    static Gen<byte[]> JsonString() =>
        Gen.Frequency((1, Gen.Constant(B("\"\""))),
            (6, Gen.Elements(CleanPieces).ListOf().Select(ps => Cat([B("\""), .. ps, B("\"")]))),
            (2, from ps in Gen.Elements(CleanPieces).ListOf()
                from bad in Gen.Elements(BadPieces)
                from at in Gen.Choose(0, ps.Count)
                select Cat([B("\""), .. ps.Take(at), bad, .. ps.Skip(at), B("\"")])));

    static Gen<byte[]> Key() => Gen.Elements(BodyKeys).Select(k => B("\"" + k + "\""));

    static Gen<byte[]> Value(int depth)
    {
        var leaf = Gen.Frequency(
            (6, JsonString()),
            (2, Gen.Constant(B("null"))),
            (1, Gen.Elements("0", "-1.5e3", "12", "true", "false").Select(B)));
        if (depth <= 0) return leaf;
        return Gen.Frequency((6, leaf), (2, Array(depth - 1)), (2, Object(depth - 1)));
    }

    static Gen<byte[]> Array(int depth) =>
        Gen.Frequency((3, JsonString()), (1, Value(depth))).ListOf().Select(vs => Seq("[", vs, "]"));

    static Gen<byte[]> Member(Gen<byte[]> key, Gen<byte[]> value) => from k in key from v in value select Cat(k, B(":"), v);

    static Gen<byte[]> Object(int depth) => Member(Key(), Value(depth)).ListOf().Select(ms => Seq("{", ms, "}"));

    /// <summary>A body: mostly well-formed wrappers with the shapes' fields, then sometimes broken on purpose.</summary>
    static Gen<byte[]> Bodies()
    {
        var fieldKey = Gen.Elements(FieldKeys).Select(k => B("\"" + k + "\""));
        var cleanTag = Gen.Elements(CleanPieces).NonEmptyListOf().Select(ps => Cat([B("\""), .. ps, B("\"")]));
        var tags = Gen.Frequency((12, cleanTag), (1, JsonString()), (1, Value(1))).ListOf().Select(vs => Seq("[", vs, "]"));
        var wrapper = Gen.Frequency(
                (6, Member(fieldKey, JsonString())),
                (2, Member(Gen.Elements("tagList", "tag\\u004cist").Select(k => B("\"" + k + "\"")), tags)),
                (2, Member(Key(), Value(2))))
            .ListOf().Select(ms => Seq("{", ms, "}"));
        var wrapperKey = Gen.Elements("user", "article", "comment", "\\u0075ser").Select(k => B("\"" + k + "\""));
        var member = Gen.Frequency((5, Member(wrapperKey, wrapper)), (1, Member(Key(), Value(2))));
        var root = Gen.Frequency((8, member.ListOf().Select(ms => Seq("{", ms, "}"))), (1, Value(2)));
        return from body in root
               from mutation in Gen.Choose(0, 23)
               from at in Gen.Choose(0, Math.Max(0, body.Length - 1))
               from b in Gen.Choose(0, 255)
               select mutation switch
               {
                   0 => Cat([0xEF, 0xBB, 0xBF], body),
                   1 => Cat(body, B(" \r\n\t")),
                   2 => Cat(body, B(" x")),
                   3 => body[..at],
                   4 => body.Select((x, i) => i == at ? (byte)b : x).ToArray(),
                   5 => body.Length == 0 ? body : Cat(body[..at], body[(at + 1)..]),
                   _ => body,
               };
    }

    [Property(MaxTest = 10000)]
    public Property BodiesBindAsTheContestEndpointsRead() => Prop.ForAll(Bodies().ToArbitrary(), body =>
    {
        AssertSameBody(body);
        return true;
    });

    // ---------- auth ----------

    static readonly Jwt OurJwt = new(ConduitRoutes.Secret);

    static BinderPropertyTests() => ContestJwt.Init(ConduitRoutes.Secret);

    static string Token(long user)
    {
        Span<byte> b = stackalloc byte[Jwt.MaxIssuedBytes];
        return Encoding.ASCII.GetString(b[..ContestJwt.Issue(user, b)]);
    }

    /// <summary>The contest's Auth.Viewer, on the header as Kestrel gave it, and ours on the raw header.</summary>
    static void AssertSameViewer(string? header)
    {
        long contest = ContestAuth.Viewer(header is not null, header is null ? default : B(header));
        long ours = JwtSchemeTests.Dispatched(OurJwt, header); // through the dispatcher's own calls
        Assert.Equal(contest, ours);
        if (contest > 0) Assert.Equal(Encoding.ASCII.GetString(ContestAuth.Token(B(header!))), Encoding.ASCII.GetString(B(header!).AsSpan().Trim(" \t"u8)[6..]));
    }

    [Fact]
    public void TokensIssuedByEitherVerifyInTheOther()
    {
        Span<byte> b = stackalloc byte[Jwt.MaxIssuedBytes];
        int n = OurJwt.Issue(42, 1_700_000_000, b);
        Span<byte> c = stackalloc byte[Jwt.MaxIssuedBytes];
        int m = ContestJwt.Issue(42, 1_700_000_000, c);
        Assert.Equal(c[..m].ToArray(), b[..n].ToArray());
        Assert.Equal(42, ContestJwt.Verify(b[..n], 1_700_000_000_000));
        Assert.Equal(0, OurJwt.Verify(b[..n], (1_700_000_000 + 30L * 24 * 3600) * 1000)); // expired
    }

    [Property(MaxTest = 3000)]
    public Property ViewersMatchTheContestAuth()
    {
        string token = Token(12345);
        var header =
            from pre in Gen.Elements("", " ", "\t", "Token ", "Token", "token ", "Bearer ", "Token  ", "x", "Token\t")
            from body in Gen.Elements(token, token[..^1], token + "x", "", Token(7), "a.b.c", Token(0), Token(-5), token.Replace('.', '_'))
            from post in Gen.Elements("", " ", "\t", " \t", "x", ".")
            from present in Gen.Frequency((9, Gen.Constant(true)), (1, Gen.Constant(false)))
            select present ? pre + body + post : null;
        return Prop.ForAll(header.ToArbitrary(), h =>
        {
            AssertSameViewer(h);
            return true;
        });
    }

    [Property(MaxTest = 1000)]
    public Property ArbitraryHeadersAgreeToo() => Prop.ForAll(ArbMap.Default.ArbFor<string>(), h =>
    {
        AssertSameViewer(h);
        return true;
    });
}
