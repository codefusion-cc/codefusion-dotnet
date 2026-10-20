using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodeFusion.Http;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// The request parser on its own, fed the way the connection feeds it: everything buffered so far, again after
/// each new piece. Cases come from RFC 9112's framing rules and the ways two parsers can disagree on them.
/// </summary>
public class ParserTests
{
    internal static readonly HttpServerOptions Defaults = new();
    static readonly Limits L = new(Defaults);

    /// <summary>What the handler would see, or the error status.</summary>
    internal sealed record Parsed(int Status, RequestMethod Method, string MethodText, string Path, string Query, string? Auth,
        byte[] Body, bool Persistent, bool ExpectContinue, int Total);

    /// <summary>Parses <paramref name="input"/> as the connection does, delivering it in pieces cut at
    /// <paramref name="cuts"/>; null while incomplete.</summary>
    internal static Parsed? Run(byte[] input, Limits limits, params int[] cuts)
    {
        var buf = (byte[])input.Clone();
        var s = new RequestState();
        s.Reset();
        int[] ends = cuts.Where(c => c > 0 && c < buf.Length).Distinct().Order().Append(buf.Length).ToArray();
        foreach (int end in ends)
        {
            Span<byte> req = buf.AsSpan(0, end);
            ParseResult r;
            if (s.HeadEnd == 0)
            {
                r = RequestParser.ParseHead(req, ref s, limits);
                if (r == ParseResult.Error) return Error(s.Status);
                if (r == ParseResult.Incomplete) continue;
                int len = RequestParser.NormalizePath(req.Slice(s.PathStart, s.PathLength));
                if (len < 0) return Error(400);
                s.PathLength = len;
            }
            r = RequestParser.ParseBody(req, ref s, limits);
            if (r == ParseResult.Error) return Error(s.Status);
            if (r == ParseResult.Done)
                return new Parsed(0, s.Method, Encoding.ASCII.GetString(buf, 0, s.MethodLength),
                    Encoding.UTF8.GetString(buf, s.PathStart, s.PathLength),
                    Encoding.ASCII.GetString(buf, s.QueryStart, s.QueryLength),
                    s.AuthStart < 0 ? null : Encoding.Latin1.GetString(buf, s.AuthStart, s.AuthLength),
                    buf[s.HeadEnd..s.BodyEnd], s.Persistent, s.ExpectContinue, s.Total);
        }
        return null;
    }

    static Parsed Error(int status) => new(status, default, "", "", "", null, [], false, false, 0);

    static Parsed? Run(string input, params int[] cuts) => Run(Encoding.Latin1.GetBytes(input), L, cuts);

    static int Status(string input) => Run(input)?.Status ?? -1;

    [Fact]
    public void SimpleGet()
    {
        var p = Run("GET /api/tags?limit=20&offset=0 HTTP/1.1\r\nHost: x\r\nAuthorization:  Token abc \r\n\r\n")!;
        Assert.Equal(0, p.Status);
        Assert.Equal(RequestMethod.Get, p.Method);
        Assert.Equal("/api/tags", p.Path);
        Assert.Equal("limit=20&offset=0", p.Query);
        Assert.Equal("Token abc", p.Auth);
        Assert.Empty(p.Body);
        Assert.True(p.Persistent);
    }

    [Theory]
    [InlineData("Authorization: \r\n", "")]      // present but empty: the app answers 401
    [InlineData("Authorization:\r\n", "")]
    [InlineData("authorization: \t x \t\r\n", "x")]
    [InlineData("X-Other: y\r\n", null)]
    public void AuthorizationPresentAbsentAndEmpty(string header, string? expected) =>
        Assert.Equal(expected, Run($"GET / HTTP/1.1\r\nHost: x\r\n{header}\r\n")!.Auth);

    [Theory]
    [InlineData("HTTP/1.1", "", true)]
    [InlineData("HTTP/1.1", "Connection: close\r\n", false)]
    [InlineData("HTTP/1.1", "Connection: Keep-Alive, CLOSE\r\n", false)]
    [InlineData("HTTP/1.0", "", false)]
    [InlineData("HTTP/1.0", "Connection: keep-alive\r\n", true)]
    [InlineData("HTTP/1.0", "Connection: x, Keep-Alive\r\n", true)]
    public void KeepAliveByVersionAndConnection(string version, string header, bool persistent) =>
        Assert.Equal(persistent, Run($"GET / {version}\r\nHost: x\r\n{header}\r\n")!.Persistent);

    [Theory]
    [InlineData("GET", RequestMethod.Get)]
    [InlineData("HEAD", RequestMethod.Head)]
    [InlineData("POST", RequestMethod.Post)]
    [InlineData("PUT", RequestMethod.Put)]
    [InlineData("DELETE", RequestMethod.Delete)]
    [InlineData("PATCH", RequestMethod.Patch)]
    [InlineData("OPTIONS", RequestMethod.Options)]
    [InlineData("get", RequestMethod.Other)] // methods are case-sensitive
    [InlineData("PROPFIND", RequestMethod.Other)]
    public void Methods(string method, RequestMethod expected)
    {
        var p = Run($"{method} / HTTP/1.1\r\nHost: x\r\n\r\n")!;
        Assert.Equal(expected, p.Method);
        Assert.Equal(method, p.MethodText);
    }

    [Theory]
    [InlineData("/api/profiles/J%C3%BCrgen", "/api/profiles/Jürgen")]
    [InlineData("/a%2Fb", "/a%2Fb")]                // an escaped slash stays escaped
    [InlineData("/a%2fb", "/a%2fb")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/a/b/..", "/a/")]
    [InlineData("/a/b/.", "/a/b/")]
    [InlineData("/..", "/")]
    [InlineData("/../../x", "/x")]
    [InlineData("/%2E%2E/x", "/x")]
    [InlineData("/.hidden/..x", "/.hidden/..x")]
    [InlineData("/a%zz", "/a%zz")]                  // an invalid escape is left as it is
    [InlineData("/a%2", "/a%2")]
    [InlineData("/", "/")]
    [InlineData("//a", "//a")]
    [InlineData("/a//./b", "/a//b")]                // an empty segment is a segment
    public void PathsAreDecodedAndNormalized(string target, string path) =>
        Assert.Equal(path, Run($"GET {target} HTTP/1.1\r\nHost: x\r\n\r\n")!.Path);

    [Theory]
    [InlineData("/a%00b")]  // NUL
    [InlineData("/a%C3")]   // a cut-off UTF-8 sequence
    [InlineData("/a%FF")]
    public void BadDecodedPathsAre400(string target) => Assert.Equal(400, Status($"GET {target} HTTP/1.1\r\nHost: x\r\n\r\n"));

    [Theory]
    [InlineData("/x?", "")]
    [InlineData("/x", "")]
    [InlineData("/x?a=%20&b", "a=%20&b")]  // raw, not decoded
    [InlineData("/x??a", "?a")]
    public void QueryIsRaw(string target, string query) => Assert.Equal(query, Run($"GET {target} HTTP/1.1\r\nHost: x\r\n\r\n")!.Query);

    [Fact]
    public void ContentLengthBody()
    {
        var p = Run("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhelloGET")!;
        Assert.Equal("hello", Encoding.ASCII.GetString(p.Body));
        Assert.Equal(p.Total, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello".Length); // the pipelined rest isn't eaten
    }

    [Fact]
    public void ContentLengthWithLeadingZeros() =>
        Assert.Equal("ab", Encoding.ASCII.GetString(Run("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 00000000000000000000002\r\n\r\nab")!.Body));

    [Fact]
    public void IncompleteBodyWaits() => Assert.Null(Run("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhell"));

    [Fact]
    public void ChunkedBody()
    {
        const string req = "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n"
                           + "5\r\nhello\r\n1;name=value\r\n,\r\nA\r\n 012345678\r\n0\r\nTrailer: t\r\n\r\nNEXT";
        var p = Run(req)!;
        Assert.Equal(0, p.Status);
        Assert.Equal("hello, 012345678", Encoding.ASCII.GetString(p.Body));
        Assert.Equal(req.Length - 4, p.Total);
    }

    [Fact]
    public void ChunkedUpperCaseHexAndCaseInsensitiveCoding() =>
        Assert.Equal(26, Run("POST / HTTP/1.1\r\nHost: x\r\ntransfer-encoding: Chunked\r\n\r\n1A\r\nabcdefghijklmnopqrstuvwxyz\r\n0\r\n\r\n")!.Body.Length);

    [Fact]
    public void ExpectContinueIsSeen() =>
        Assert.True(Run("POST / HTTP/1.1\r\nHost: x\r\nExpect: 100-Continue\r\nContent-Length: 0\r\n\r\n")!.ExpectContinue);

    // ---- Smuggling shapes and malformed input: 400 ----

    [Theory]
    [InlineData("Content-Length: 3\r\nTransfer-Encoding: chunked\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 3\r\n")]
    [InlineData("Content-Length: 3\r\nContent-Length: 4\r\n")]
    [InlineData("Content-Length: 3\r\nContent-Length: 3\r\n")] // even equal repeats
    [InlineData("Content-Length: 3, 3\r\n")]
    [InlineData("Content-Length: +3\r\n")]
    [InlineData("Content-Length: -1\r\n")]
    [InlineData("Content-Length: 3 3\r\n")]
    [InlineData("Content-Length: 0x3\r\n")]
    [InlineData("Content-Length:\r\n")]
    [InlineData("Content-Length: 99999999999999999999\r\n")]
    [InlineData("Transfer-Encoding: gzip, chunked\r\n")]
    [InlineData("Transfer-Encoding: chunked, chunked\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n")]
    [InlineData("Transfer-Encoding: identity\r\n")]
    [InlineData("Transfer-Encoding: \"chunked\"\r\n")]
    [InlineData("Transfer-Encoding : chunked\r\n")] // whitespace before the colon
    [InlineData("Content-Length : 3\r\n")]
    [InlineData("X-A: b\r\n c\r\n")]                // obs-fold
    [InlineData("X-A: b\r\n\tc\r\n")]
    [InlineData(" X-A: b\r\n")]
    [InlineData("X-A: b\nX-B: c\r\n")]              // bare LF
    [InlineData("X-A: b\rc\r\n")]                   // bare CR
    [InlineData("X-A: b\0c\r\n")]                   // NUL
    [InlineData("X-A: b\u007fc\r\n")]
    [InlineData(": b\r\n")]
    [InlineData("X A: b\r\n")]
    [InlineData("X-A\r\n")]
    [InlineData("Host: y\r\n")]                     // a second Host
    [InlineData("Authorization: a\r\nAuthorization: b\r\n")] // a proxy reading the last would see another identity
    [InlineData("Authorization: a\r\nauthorization: a\r\n")] // even an equal repeat
    [InlineData("Authorization:\r\nAuthorization: a\r\n")]
    public void BadHeadersAre400(string headers) =>
        Assert.Equal(400, Status($"POST / HTTP/1.1\r\nHost: x\r\n{headers}\r\nabc"));

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]                     // no Host in 1.1
    [InlineData("GET / HTTP/1.1\nHost: x\r\n\r\n")]            // bare LF after the request line
    [InlineData("GET / HTTP/1.1\r\nHost: x\n\n")]
    [InlineData("GET  / HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /  HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1 \r\nHost: x\r\n\r\n")]
    [InlineData(" GET / HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET\t/ HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / http/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1x\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/11\r\nHost: x\r\n\r\n")]
    [InlineData("GET /\r\nHost: x\r\n\r\n")]                   // HTTP/0.9
    [InlineData("GET http://x/ HTTP/1.1\r\nHost: x\r\n\r\n")]  // absolute-form
    [InlineData("OPTIONS * HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("CONNECT x:443 HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /aé HTTP/1.1\r\nHost: x\r\n\r\n")]   // raw non-ASCII in the target
    [InlineData("GET /a\u007f HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("G(T / HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("\r\nGET / HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("POST / HTTP/1.0\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")] // chunked needs 1.1
    public void BadRequestLinesAre400(string req) => Assert.Equal(400, Status(req));

    [Theory]
    [InlineData("HTTP/1.2")]
    [InlineData("HTTP/2.0")]
    [InlineData("HTTP/0.9")]
    [InlineData("HTTP/3.1")]
    public void OtherVersionsAre505(string version) => Assert.Equal(505, Status($"GET / {version}\r\nHost: x\r\n\r\n"));

    [Theory]
    [InlineData("x\r\n")]                 // not hex
    [InlineData("\r\n")]                  // no size
    [InlineData(";a\r\n")]
    [InlineData("-1\r\n")]
    [InlineData("1\r\nab\r\n")]           // more data than the size
    [InlineData("2\r\na\r\n\r\n")]
    [InlineData("1\nb\r\n")]              // bare LF after the size
    [InlineData("1\r\nb\n")]              // bare LF after the data
    [InlineData("1\r\nbX\r\n")]
    [InlineData("0\r\nX\r\n")]            // a trailer line without a colon
    [InlineData("0\r\n\n")]
    [InlineData("0\r\n \r\n")]
    [InlineData("1;\u0001\r\nb\r\n")]       // a control byte in an extension
    [InlineData("1 0\r\nb\r\n")]           // found by fuzzing: whitespace inside a size, which a lenient reader takes for 0x10
    [InlineData("1 x\r\nb\r\n")]           // after the size, whitespace only before an extension's ';'
    [InlineData("1\t\r\nb\r\n")]
    public void BadChunksAre400(string chunks) =>
        Assert.Equal(400, Status($"POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n{chunks}0\r\n\r\n"));

    [Fact]
    public void AChunkSizeThatWouldOverflowIs413() =>
        Assert.Equal(413, Status("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\nffffffffffffffffffffffff\r\n"));

    // ---- Limits, at the limit and one past it ----

    static readonly Limits Small = new(new HttpServerOptions
    {
        MaxRequestLineSize = 64, MaxRequestHeadersTotalSize = 128, MaxRequestHeaderCount = 4, MaxRequestBodySize = 10,
    });

    static int Small_(string req, params int[] cuts) => Run(Encoding.ASCII.GetBytes(req), Small, cuts)?.Status ?? -1;

    /// <summary>A request line of exactly <paramref name="length"/> bytes with its CRLF.</summary>
    static string Line(int length) => "GET /" + new string('a', length - "GET / HTTP/1.1\r\n".Length) + " HTTP/1.1\r\n";

    [Fact]
    public void RequestLineAtTheLimitPassesAndOnePastIs414()
    {
        Assert.Equal(0, Small_(Line(64) + "Host: x\r\n\r\n"));
        Assert.Equal(414, Small_(Line(65) + "Host: x\r\n\r\n"));
        Assert.Equal(414, Small_(Line(65)[..64]));            // refused before the line ends
        Assert.Equal(-1, Small_(Line(65)[..63]));             // still might fit
    }

    /// <summary>Header lines totalling exactly <paramref name="length"/> bytes with the blank line.</summary>
    static string Headers(int length) => "Host: x\r\nX: " + new string('b', length - "Host: x\r\nX: \r\n\r\n".Length) + "\r\n\r\n";

    [Fact]
    public void HeadersAtTheLimitPassAndOnePastIs431()
    {
        Assert.Equal(0, Small_("GET / HTTP/1.1\r\n" + Headers(128)));
        Assert.Equal(431, Small_("GET / HTTP/1.1\r\n" + Headers(129)));
        Assert.Equal(431, Small_("GET / HTTP/1.1\r\nHost: x\r\nX: " + new string('b', 200)));   // no line end yet
    }

    /// <summary>Found by fuzzing: a line whose LF comes past the limit, a bare one, was refused for the bare LF (400)
    /// when the line arrived whole, and for its length (414, 431) when the limit's worth came first. The limit decides
    /// either way, as it does while the LF hasn't come.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(144)]
    public void ALineOverrunningItsLimitIsRefusedForItsLengthHoweverItArrives(int cut)
    {
        Assert.Equal(414, Small_(Line(70)[..^2] + "\nHost: x\r\n\r\n", cut));
        Assert.Equal(431, Small_("GET / HTTP/1.1\r\nX: " + new string('b', 130) + "\n\r\n", cut));
    }

    [Fact]
    public void HeaderCountAtTheLimitPassesAndOnePastIs431()
    {
        Assert.Equal(0, Small_("GET / HTTP/1.1\r\nHost: x\r\nA: 1\r\nB: 2\r\nC: 3\r\n\r\n"));
        Assert.Equal(431, Small_("GET / HTTP/1.1\r\nHost: x\r\nA: 1\r\nB: 2\r\nC: 3\r\nD: 4\r\n\r\n"));
    }

    [Fact]
    public void ContentLengthAtTheLimitPassesAndOnePastIs413()
    {
        Assert.Equal(0, Small_("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\n0123456789"));
        Assert.Equal(413, Small_("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 11\r\n\r\n"));
    }

    [Fact]
    public void ChunkedAtTheLimitPassesAndOnePastIs413()
    {
        Assert.Equal(0, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n01234\r\n5\r\n56789\r\n0\r\n\r\n"));
        Assert.Equal(413, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n01234\r\n6\r\n"));
        Assert.Equal(413, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\nB\r\n"));
    }

    [Fact]
    public void TrailersCountAgainstTheHeaderLimit() =>
        Assert.Equal(431, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nT: " + new string('t', 130) + "\r\n\r\n"));

    [Fact]
    public void ManyTinyChunksDecodeInPlaceWithinTheBodyLimit()
    {
        var sb = new StringBuilder("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
        for (int i = 0; i < 10; i++) sb.Append("1\r\n").Append((char)('a' + i)).Append("\r\n");
        sb.Append("0\r\n\r\n");
        Assert.Equal("abcdefghij", Encoding.ASCII.GetString(Run(Encoding.ASCII.GetBytes(sb.ToString()), Small)!.Body));
    }

    // ---- Every split, and every byte on its own ----

    const string Chunked = "POST /a/%C3%A9?x=1 HTTP/1.1\r\nHost: h\r\nAuthorization: Token t\r\nTransfer-Encoding: chunked\r\n\r\n"
                           + "3;e=1\r\nabc\r\n2\r\nde\r\n0\r\nT: 1\r\n\r\n";

    [Fact]
    public void SplitAtEveryPositionGivesTheSameRequest()
    {
        var whole = Run(Chunked)!;
        for (int cut = 1; cut < Chunked.Length; cut++)
            Assert.Equivalent(whole, Run(Chunked, cut));
    }

    [Fact]
    public void OneByteAtATimeGivesTheSameRequest()
    {
        var whole = Run(Chunked)!;
        Assert.Equal("abcde", Encoding.ASCII.GetString(whole.Body));
        Assert.Equal("/a/é", whole.Path);
        Assert.Equivalent(whole, Run(Chunked, Enumerable.Range(1, Chunked.Length).ToArray()));
    }

    // ---- Properties ----

    internal sealed record Spec(string Method, string[] Segments, string Query, bool Http11, string? Auth,
        (string Name, string Value)[] Extra, byte[] Body, int[]? Chunks, bool Close)
    {
        public string Path => "/" + string.Join('/', Segments);

        public byte[] Bytes()
        {
            var sb = new StringBuilder();
            sb.Append(Method).Append(' ').Append(Path);
            if (Query.Length > 0) sb.Append('?').Append(Query);
            sb.Append(Http11 ? " HTTP/1.1\r\n" : " HTTP/1.0\r\n").Append("Host: example\r\n");
            if (Auth is not null) sb.Append("Authorization: ").Append(Auth).Append("\r\n");
            foreach (var (n, v) in Extra) sb.Append(n).Append(": ").Append(v).Append("\r\n");
            if (Close) sb.Append("Connection: close\r\n");
            else if (!Http11) sb.Append("Connection: keep-alive\r\n");
            if (Chunks is null) sb.Append("Content-Length: ").Append(Body.Length).Append("\r\n\r\n");
            else sb.Append("Transfer-Encoding: chunked\r\n\r\n");
            var bytes = new List<byte>(Encoding.ASCII.GetBytes(sb.ToString()));
            if (Chunks is null) bytes.AddRange(Body);
            else
            {
                int at = 0;
                foreach (int c in Chunks.Append(Body.Length))
                {
                    int n = Math.Min(c, Body.Length - at);
                    if (n <= 0) continue;
                    bytes.AddRange(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"));
                    bytes.AddRange(Body.AsSpan(at, n).ToArray());
                    bytes.AddRange("\r\n"u8.ToArray());
                    at += n;
                }
                bytes.AddRange("0\r\n\r\n"u8.ToArray());
            }
            return bytes.ToArray();
        }
    }

    static Gen<string> From(string chars, int min, int max) =>
        Gen.Choose(min, max).SelectMany(n => Gen.ArrayOf(Gen.Elements(chars.ToCharArray()), n)).Select(a => new string(a));

    const string Alnum = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    const string TokenChars = Alnum + "!#$%&'*+-.^_`|~";

    internal static Gen<Spec> Specs() =>
        from method in Gen.Elements("GET", "POST", "PUT", "DELETE", "PATCH", "X-CUSTOM")
        from segments in Gen.Choose(0, 4).SelectMany(n => Gen.ArrayOf(From(Alnum + "-_~", 1, 12), n))
        from query in From(Alnum + "=&%+-", 0, 30)
        from http11 in Gen.Elements(true, true, false)
        from auth in Gen.OneOf(Gen.Constant<string?>(null), Gen.Constant<string?>(""), From(Alnum + " .-_", 1, 60).Select(s => (string?)s.Trim()))
        from extra in Gen.Choose(0, 5).SelectMany(n => Gen.ArrayOf(Gen.Zip(From(TokenChars, 1, 10).Where(IsPlainName), From(Alnum + " ;,=\"/", 0, 40).Select(v => v.Trim())), n))
        from body in Gen.Choose(0, 300).SelectMany(n => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), n))
        from chunked in Gen.Elements(false, http11)
        from chunks in Gen.Choose(1, 6).SelectMany(n => Gen.ArrayOf(Gen.Choose(1, 80), n))
        from close in Gen.Elements(false, false, true)
        select new Spec(method, segments, query, http11, auth, extra, body, chunked ? chunks : null, close);

    // Names the generated request already sends, which would change its meaning.
    static bool IsPlainName(string n) => n.ToLowerInvariant() is not ("host" or "authorization" or "content-length"
        or "transfer-encoding" or "connection" or "expect");

    [Property(MaxTest = 500)]
    public Property ValidRequestsRoundTrip() => Prop.ForAll(Specs().ToArbitrary(), spec =>
    {
        byte[] bytes = spec.Bytes();
        var p = Run(bytes, L);
        Assert.NotNull(p);
        Assert.Equal(0, p.Status);
        Assert.Equal(spec.Method, p.MethodText);
        Assert.Equal(spec.Path, p.Path);
        Assert.Equal(spec.Query, p.Query);
        Assert.Equal(spec.Auth, p.Auth);
        Assert.Equal(spec.Body, p.Body);
        Assert.Equal(!spec.Close, p.Persistent);
        Assert.Equal(bytes.Length, p.Total);
    });

    [Property(MaxTest = 300)]
    public Property ValidRequestsSplitAnywhereRoundTrip() => Prop.ForAll(
        Specs().SelectMany(s => Gen.ArrayOf(Gen.Choose(1, Math.Max(1, s.Bytes().Length - 1)), 3).Select(c => (s, c))).ToArbitrary(), t =>
        {
            byte[] bytes = t.s.Bytes();
            Assert.Equivalent(Run(bytes, L), Run(bytes, L, t.c));
        });

    /// <summary>Random bytes, alone or spliced into a valid request: an answer or a wait, never an exception or a
    /// read past the input.</summary>
    [Property(MaxTest = 2000)]
    public Property RandomBytesNeverThrow() => Prop.ForAll(
        Gen.OneOf(
            Gen.Choose(0, 400).SelectMany(n => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), n)),
            Gen.Choose(0, 200).SelectMany(n => Gen.ArrayOf(Gen.Elements("\r\n :;0123456789abcdefABCDEF/?%.HTTP/1.1GETPOSTHostContent-LengthTransfer-Encodingchunked\t\0"u8.ToArray()), n)),
            from spec in Specs()
            from at in Gen.Choose(0, 1000)
            from junk in Gen.Choose(1, 8).SelectMany(n => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), n))
            select Splice(spec.Bytes(), at, junk))
        .SelectMany(b => Gen.ArrayOf(Gen.Choose(0, b.Length), 4).Select(c => (b, c))).ToArbitrary(), t =>
        {
            var p = Run(t.b, Small, t.c);
            if (p is not null && p.Status == 0) Assert.InRange(p.Total, 1, t.b.Length);
            else if (p is not null) Assert.Contains(p.Status, new[] { 400, 413, 414, 431, 505 });
        });

    static byte[] Splice(byte[] b, int at, byte[] junk)
    {
        at %= b.Length + 1;
        return [.. b.AsSpan(0, at), .. junk, .. b.AsSpan(at)];
    }

    // ---- Edges Stryker found untested ----

    const string ChunkedHead = "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n";

    [Theory]
    [InlineData("\nGET / HTTP/1.1\r\nHost: x\r\n\r\n")]          // a bare LF before the request line
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n\nX: 1\r\n\r\n")]  // a bare LF as a header line
    public void ABareLfLineIs400AtOnce(string request) => Assert.Equal(400, Status(request));

    [Theory]
    [InlineData(" / HTTP/1.1\r\nHost: x\r\n\r\n")]   // no method
    [InlineData("GET  HTTP/1.1\r\nHost: x\r\n\r\n")] // no target
    public void AnEmptyMethodOrTargetIs400(string request) => Assert.Equal(400, Status(request));

    [Fact]
    public void UnfinishedHeadersAreRefusedOnceTheyCanNoLongerFit()
    {
        // 128 header bytes without an LF: the line's LF would be the 129th.
        Assert.Equal(431, Small_("GET / HTTP/1.1\r\nX: " + new string('a', 125)));
        Assert.Equal(-1, Small_("GET / HTTP/1.1\r\nX: " + new string('a', 124))); // still incomplete
    }

    [Fact]
    public void AContentLengthUpToTheOverflowGuardIsReadAndPastItIs400()
    {
        Assert.Equal(413, Status("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 9223372036854775799\r\n\r\n"));
        Assert.Equal(400, Status("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 9223372036854775800\r\n\r\n"));
    }

    [Fact]
    public void ChunkedWithAZeroContentLengthIsStill400() =>
        Assert.Equal(400, Status("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nContent-Length: 0\r\n\r\n0\r\n\r\n"));

    [Fact]
    public void AChunkSizeLineOf4096BytesPassesAndOneMoreIs400()
    {
        // The size line's bytes count with its CR: 4095 digits, then with an extension.
        Assert.Equal("a", Encoding.ASCII.GetString(Run(ChunkedHead + new string('0', 4094) + "1\r\na\r\n0\r\n\r\n")!.Body));
        Assert.Equal(400, Status(ChunkedHead + new string('0', 4095) + "1\r\na\r\n0\r\n\r\n"));
        Assert.Equal("a", Encoding.ASCII.GetString(Run(ChunkedHead + "1;" + new string('e', 4093) + "\r\na\r\n0\r\n\r\n")!.Body));
        Assert.Equal(400, Status(ChunkedHead + "1;" + new string('e', 4094) + "\r\na\r\n0\r\n\r\n"));
    }

    [Fact]
    public void AChunkExtensionMayHoldSpaces() =>
        Assert.Equal("a", Encoding.ASCII.GetString(Run(ChunkedHead + "1; name=a b\r\na\r\n0\r\n\r\n")!.Body));

    [Theory]
    [InlineData("1 ;a=b")]
    [InlineData("1\t ;a")]
    public void WhitespaceMayComeBeforeAnExtension(string size) =>
        Assert.Equal("a", Encoding.ASCII.GetString(Run(ChunkedHead + size + "\r\na\r\n0\r\n\r\n")!.Body));

    [Fact]
    public void AnEmptySizeLineAfterAChunkIs400() => Assert.Equal(400, Status(ChunkedHead + "1\r\na\r\n\r\n\r\n"));

    [Fact]
    public void TrailersAtTheHeaderLimitPassAndOneByteMoreIs431()
    {
        // A trailer line counts its bytes but the LF, and the final empty line its CR: "T: " + 123 bytes is 128.
        Assert.Equal(0, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nT: " + new string('v', 123) + "\r\n\r\n"));
        Assert.Equal(431, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nT: " + new string('v', 124) + "\r\n\r\n"));
        // Every byte counts, of short lines and of a long name alike.
        Assert.Equal(431, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n" + string.Concat(Enumerable.Repeat("A:\r\n", 50)) + "\r\n"));
        Assert.Equal(431, Small_("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n" + new string('N', 200) + ": v\r\n\r\n"));
    }

    [Theory]
    [InlineData("a\tb", 0)]
    [InlineData("a\u0001b", 400)]
    [InlineData("a\u007Fb", 400)]
    [InlineData("a\nb", 400)]
    public void ATrailerValueHoldsNoControlBytesButTab(string value, int status) =>
        Assert.Equal(status, Run(ChunkedHead + "0\r\nT: " + value + "\r\n\r\n")?.Status ?? -1);
}
