using System;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// The options an app's own spec sets over HTTP: the Authorization scheme, claims beside sub, a 401 body for each
/// reason, the body's and the route values' error replies. Each default is what the app answered before they existed.
/// </summary>
public class SpecOptionsTests
{
    internal static HttpAppBuilder Builder(JwtOptions? jwt = null)
    {
        var b = HttpApp.CreateBuilder([]);
        b.Options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        b.Errors.Log = null;
        if (jwt is not null) b.AddJwtAuth(JwtTests.Secret, jwt);
        return b;
    }

    internal static Task<Response> Send(HttpApp app, string method, string target, string? auth = null, string? body = null) =>
        Send(app, method, target, auth, body is null ? null : Encoding.UTF8.GetBytes(body));

    /// <summary>One request on a new connection; a null body sends no Content-Length.</summary>
    internal static async Task<Response> Send(HttpApp app, string method, string target, string? auth, byte[]? body)
    {
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        string head = $"{method} {target} HTTP/1.1\r\nHost: x\r\n"
            + (auth is null ? "" : $"Authorization: {auth}\r\n")
            + (body is null ? "" : $"Content-Length: {body.Length}\r\n")
            + "\r\n";
        await c.SendAsync([.. Encoding.UTF8.GetBytes(head), .. body ?? []]);
        return await c.ReadAsync();
    }

    internal static string Token(Jwt jwt, long user = 42)
    {
        var b = new byte[Jwt.MaxIssuedBytes];
        return Encoding.ASCII.GetString(b, 0, jwt.Issue(user, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), b));
    }

    // ---- the scheme ----

    [Fact]
    public async Task ABearerAppReadsBearerHeadersOnly()
    {
        HttpAppBuilder b = Builder(new JwtOptions { Scheme = "Bearer" });
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/me", static (Viewer v) => Reply.Ok(new Me(v.Id, v.Token))).AuthRequired();
        app.Start();
        string t = Token(b.JwtPrototype!);
        Response ok = await Send(app, "GET", "/me", "Bearer " + t);
        Assert.Equal((200, $$"""{"id":42,"token":"{{t}}"}"""), (ok.Status, ok.Text));
        Response trimmed = await Send(app, "GET", "/me", "Bearer " + t + " \t");
        Assert.Equal((200, $$"""{"id":42,"token":"{{t}}"}"""), (trimmed.Status, trimmed.Text));
        foreach (string wrong in new[] { "Token " + t, "bearer " + t, "Bearer  " + t, "Bearer" })
        {
            Response r = await Send(app, "GET", "/me", wrong);
            Assert.Equal((401, """{"error":"unauthorized"}"""), (r.Status, r.Text));
        }
    }

    [Fact]
    public async Task TheDefaultAppStillReadsToken()
    {
        HttpAppBuilder b = Builder();
        b.AddJwtAuth(JwtTests.Secret);
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/me", static (Viewer v) => Reply.Ok(new Me(v.Id, v.Token))).AuthOptional();
        app.Start();
        string t = Token(b.JwtPrototype!);
        Response ok = await Send(app, "GET", "/me", "Token " + t);
        Assert.Equal((200, $$"""{"id":42,"token":"{{t}}"}"""), (ok.Status, ok.Text));
        Response anonymous = await Send(app, "GET", "/me");
        Assert.Equal((200, """{"id":0,"token":""}"""), (anonymous.Status, anonymous.Text));
        Response bearer = await Send(app, "GET", "/me", "Bearer " + t);
        Assert.Equal((401, """{"error":"unauthorized"}"""), (bearer.Status, bearer.Text));
    }

    // ---- claims ----

    internal static JwtOptions Feed(bool required = true)
    {
        var o = new JwtOptions { Scheme = "Bearer" };
        o.Claims.Add(new JwtClaim("username") { Required = required, MaxBytes = 64 });
        return o;
    }

    static readonly byte[] Neo = Encoding.UTF8.GetBytes("né\"o");

    /// <summary>A genuine token for user 42, in time, with no username.</summary>
    internal static string Unclaimed() => JwtClaimTests.Signed($$"""{"sub":"42","exp":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60}}}""");

    [Fact]
    public async Task AHandlerReadsTheTokensClaimAsItWasIssued()
    {
        HttpAppBuilder b = Builder(Feed());
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/me", static (Viewer v) => Reply.Ok(new Named(v.Id, v.Claim("username"u8)))).AuthOptional();
        app.MapGet("/has", static (Viewer v) => Reply.Ok(new Has(v.TryGetClaim("username"u8, out Utf8 n), n))).AuthOptional();
        app.MapGet("/typo", static (Viewer v) => Reply.Ok(new Named(v.Id, v.Claim("usernme"u8)))).AuthOptional();
        app.MapPost("/login", static (Jwt jwt) => Reply.Ok(new Issued(jwt.Issue(7, new Utf8(Neo, 0, Neo.Length)))));
        app.Start();

        Response login = await Send(app, "POST", "/login", body: "");
        string token = JsonDocument.Parse(login.Body).RootElement.GetProperty("token").GetString()!;
        Response me = await Send(app, "GET", "/me", "Bearer " + token);
        Assert.Equal((200, """{"id":7,"username":"né\"o"}"""), (me.Status, me.Text));
        Response has = await Send(app, "GET", "/has", "Bearer " + token);
        Assert.Equal((200, """{"found":true,"username":"né\"o"}"""), (has.Status, has.Text));

        Response anonymous = await Send(app, "GET", "/me");
        Assert.Equal((200, """{"id":0,"username":""}"""), (anonymous.Status, anonymous.Text));
        Response nobody = await Send(app, "GET", "/has");
        Assert.Equal((200, """{"found":false,"username":""}"""), (nobody.Status, nobody.Text));
        // a name nobody declared is a bug in the app, not an empty claim
        Response typo = await Send(app, "GET", "/typo", "Bearer " + token);
        Assert.Equal(500, typo.Status);
        Response typoAnonymous = await Send(app, "GET", "/typo");
        Assert.Equal(500, typoAnonymous.Status);
        // genuine, but without the claim: a 401, today's body unless the app sets another
        Response without = await Send(app, "GET", "/me", "Bearer " + Unclaimed());
        Assert.Equal((401, """{"error":"unauthorized"}"""), (without.Status, without.Text));
    }

    [Fact]
    public async Task AnOptionalClaimThatIsAbsentReadsAsNone()
    {
        HttpAppBuilder b = Builder(Feed(required: false));
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/has", static (Viewer v) => Reply.Ok(new Has(v.TryGetClaim("username"u8, out Utf8 n), n))).AuthRequired();
        app.Start();
        Response r = await Send(app, "GET", "/has", "Bearer " + Unclaimed());
        Assert.Equal((200, """{"found":false,"username":""}"""), (r.Status, r.Text));
    }

    // ---- a 401 for each reason ----

    static readonly ErrorReply Missing = new(401, """{"error":"missing bearer token"}"""u8);
    static readonly ErrorReply Invalid = new(401, """{"error":"invalid or expired token"}"""u8);
    static readonly ErrorReply Payload = new(401, """{"error":"invalid token payload"}"""u8);

    /// <summary>A token signed with the test secret: sub, username (JSON as given), exp seconds from now.</summary>
    internal static string FeedToken(string username = "\"jake\"", string sub = "\"42\"", long expIn = 60)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return JwtClaimTests.Signed($$"""{"sub":{{sub}},"username":{{username}},"iat":{{now}},"exp":{{now + expIn}}}""");
    }

    static string Forged()
    {
        string t = FeedToken();
        return t[..^2] + (t[^2] == 'A' ? 'B' : 'A') + t[^1];
    }

    /// <summary>Each header, and what a required and an optional endpoint answer it with.</summary>
    public static TheoryData<string, string?, string, string> Refusals() => new()
    {
        { "no header", null, "missing", "anonymous" },
        { "an empty header", "", "missing", "missing" },
        { "only spaces", "   ", "missing", "missing" },
        { "another scheme", "Token {t}", "missing", "missing" },
        { "the scheme in lower case", "bearer {t}", "missing", "missing" },
        { "the scheme alone", "Bearer", "missing", "missing" },
        { "two spaces", "Bearer  {t}", "invalid", "invalid" },
        { "not a JWT", "Bearer abc", "invalid", "invalid" },
        { "forged", "Bearer {forged}", "invalid", "invalid" },
        { "expired", "Bearer {expired}", "invalid", "invalid" },
        { "sub 0", "Bearer {sub0}", "payload", "payload" },
        { "sub a number", "Bearer {subNumber}", "payload", "payload" },
        { "no username", "Bearer {noName}", "payload", "payload" },
        { "username a number", "Bearer {nameNumber}", "payload", "payload" },
        { "expired with a bad payload", "Bearer {expiredBad}", "invalid", "invalid" },
        { "valid", "Bearer {t}", "ok", "ok" },
    };

    static string? Header(string? template) => template?
        .Replace("{t}", FeedToken(), StringComparison.Ordinal)
        .Replace("{forged}", Forged(), StringComparison.Ordinal)
        .Replace("{expired}", FeedToken(expIn: -1), StringComparison.Ordinal)
        .Replace("{sub0}", FeedToken(sub: "\"0\""), StringComparison.Ordinal)
        .Replace("{subNumber}", FeedToken(sub: "42"), StringComparison.Ordinal)
        .Replace("{noName}", Unclaimed(), StringComparison.Ordinal)
        .Replace("{nameNumber}", FeedToken(username: "5"), StringComparison.Ordinal)
        .Replace("{expiredBad}", FeedToken(username: "5", sub: "\"0\"", expIn: 0), StringComparison.Ordinal);

    static HttpApp RefusingApp(Action<HttpAppErrors> errors)
    {
        HttpAppBuilder b = Builder(Feed());
        errors(b.Errors);
        HttpApp app = b.Build();
        app.MapGet("/required", static (Viewer v) => Reply.Ok(new Named(v.Id, v.Claim("username"u8)))).AuthRequired();
        app.MapGet("/optional", static (Viewer v) => Reply.Ok(new Named(v.Id, v.Claim("username"u8)))).AuthOptional();
        app.Start();
        return app;
    }

    static (int, string) Expected(string outcome, ErrorReply missing, ErrorReply invalid, ErrorReply payload) => outcome switch
    {
        "ok" => (200, """{"id":42,"username":"jake"}"""),
        "anonymous" => (200, """{"id":0,"username":""}"""),
        "missing" => (401, Encoding.UTF8.GetString(missing.Body)),
        "invalid" => (401, Encoding.UTF8.GetString(invalid.Body)),
        _ => (401, Encoding.UTF8.GetString(payload.Body)),
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachRefusalHasItsOwnBody(string name, string? header, string required, string optional)
    {
        await using HttpApp app = RefusingApp(e =>
        {
            e.MissingToken = Missing;
            e.InvalidToken = Invalid;
            e.InvalidTokenPayload = Payload;
        });
        await using var check = new AppCheck(app);
        string? h = Header(header);
        Response r = await Send(app, "GET", "/required", h);
        Assert.Equal(Expected(required, Missing, Invalid, Payload), (r.Status, r.Text));
        Response o = await Send(app, "GET", "/optional", h);
        Assert.Equal(Expected(optional, Missing, Invalid, Payload), (o.Status, o.Text));
        Assert.NotNull(name);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task UnsetReasonsAnswerWithUnauthorizedAsBefore(string name, string? header, string required, string optional)
    {
        var unauthorized = new ErrorReply(401, """{"errors":{"body":["unauthorized"]}}"""u8);
        await using HttpApp app = RefusingApp(e => e.Unauthorized = unauthorized);
        await using var check = new AppCheck(app);
        string? h = Header(header);
        Response r = await Send(app, "GET", "/required", h);
        Assert.Equal(Expected(required, unauthorized, unauthorized, unauthorized), (r.Status, r.Text));
        Response o = await Send(app, "GET", "/optional", h);
        Assert.Equal(Expected(optional, unauthorized, unauthorized, unauthorized), (o.Status, o.Text));
        Assert.NotNull(name);
    }

    [Fact]
    public async Task AnUnsetPayloadReasonAnswersAsAnInvalidToken()
    {
        await using HttpApp app = RefusingApp(e => e.InvalidToken = Invalid);
        await using var check = new AppCheck(app);
        Response payload = await Send(app, "GET", "/required", "Bearer " + FeedToken(sub: "\"0\""));
        Assert.Equal((401, """{"error":"invalid or expired token"}"""), (payload.Status, payload.Text));
        Response missing = await Send(app, "GET", "/required");
        Assert.Equal((401, """{"error":"unauthorized"}"""), (missing.Status, missing.Text));
        Assert.Null(app.Errors.MissingToken);
        Assert.Null(app.Errors.InvalidTokenPayload);
    }

    /// <summary>The whole response but its Date: what a default app sent for every refusal before reasons existed.</summary>
    static string Raw(Response r) =>
        $"{r.StatusLine}|{r.Header("Content-Type")}|{r.Header("Content-Length")}|{r.Header("Connection")}|{r.Header("Retry-After")}|{r.Text}";

    [Fact]
    public async Task TheDefault401IsTheSameBytesForEveryReason()
    {
        HttpAppBuilder b = Builder();
        b.AddJwtAuth(JwtTests.Secret);
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/required", static (Viewer v) => Reply.NoContent()).AuthRequired();
        app.Start();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[] headers =
        [
            "", "Bearer " + Token(b.JwtPrototype!), "Token abc", "Token " + JwtClaimTests.Signed($$"""{"sub":"42","exp":{{now - 1}}}"""),
            "Token " + JwtClaimTests.Signed($$"""{"sub":"0","exp":{{now + 60}}}"""),
        ];
        const string Expected = """HTTP/1.1 401 Unauthorized|application/json|24|||{"error":"unauthorized"}""";
        Assert.Equal(Expected, Raw(await Send(app, "GET", "/required")));
        foreach (string h in headers) Assert.Equal(Expected, Raw(await Send(app, "GET", "/required", h)));
        Assert.Equal(204, (await Send(app, "GET", "/required", "Token " + Token(b.JwtPrototype!))).Status);
    }

    // ---- the body ----

    /// <summary>Each body sent to POST /posts, and what it is: no body, not JSON, JSON of another shape, a bad field, or fine.</summary>
    public static TheoryData<string, string?, string> Bodies() => new()
    {
        { "no body", null, "missing" },
        { "Content-Length: 0", "", "missing" },
        { "a space", " ", "malformed" },
        { "a BOM alone", "{bom}", "malformed" },
        { "an open brace", "{", "malformed" },
        { "cut short", """{"body":"x" """, "malformed" },
        { "cut in a string", """{"body":"x""", "malformed" },
        { "something after it", """{"body":"x"} x""", "malformed" },
        { "a brace too many", """{"body":"x"}}""", "malformed" },
        { "an array cut short", "[1,", "malformed" },
        { "a word cut short", "nul", "malformed" },
        { "a bad escape", """{"body":"\x"}""", "malformed" },
        { "a raw control character in a string", "{ctl}", "malformed" },
        { "a raw tab in a string", "{tab}", "malformed" },
        { "a raw DEL in a string", "{del}", "ok" },
        { "a leading zero", """{"n":01,"body":"hi"}""", "malformed" },
        { "a bare minus", """{"n":-,"body":"hi"}""", "malformed" },
        { "a point without digits", """{"n":1.,"body":"hi"}""", "malformed" },
        { "an exponent without digits", """{"n":1e,"body":"hi"}""", "malformed" },
        { "a comment", """{"body":"hi"} // x""", "malformed" },
        { "a short unicode escape", """{"body":"\u12"}""", "malformed" },
        { "a lone surrogate escape", """{"x":"\ud800","body":"hi"}""", "ok" },
        { "numbers of every form", """{"n":[0,-0,12,-3.25,1e5,1E+5,2e-3,0.5e10],"body":"hi"}""", "ok" },
        { "literals", """{"n":[true,false,null],"body":"hi"}""", "ok" },
        { "every escape", """{"body":"hi","e":"\"\\\/\b\f\n\r\t\u00e9"}""", "ok" },
        { "whitespace everywhere", " \t\r\n{ \"body\" : \"hi\" , \"n\" : [ 1 , 2 ] } \n", "ok" },
        { "a single quote", "{'body':'x'}", "malformed" },
        { "a trailing comma", """{"body":"x",}""", "malformed" },
        { "past the depth limit", "{deep}", "malformed" },
        { "an array", "[]", "shape" },
        { "a string", "\"text\"", "shape" },
        { "null", "null", "shape" },
        { "a number", "5", "shape" },
        { "an array after a BOM", "{bom}[1]", "shape" },
        { "no field", "{}", "field" },
        { "a number field", """{"body":5}""", "field" },
        { "an empty field", """{"body":""}""", "field" },
        { "a field that isn't UTF-8", "{ff}", "field" },
        { "fine", """{"body":"hi"}""", "ok" },
        { "fine after a BOM", """{bom}{"body":"hi"}""", "ok" },
        { "fine and deep", """{"x":{deep63},"body":"hi"}""", "ok" },
        { "fine at depth 64", """{"x":{deep64},"body":"hi"}""", "ok" },
    };

    static byte[]? BodyBytes(string? template)
    {
        if (template is null) return null;
        string deep63 = new string('[', 62) + new string(']', 62);
        string deep64 = new string('[', 63) + new string(']', 63);
        string text = template.Replace("{deep}", "{\"body\":" + new string('[', 64) + new string(']', 64) + "}", StringComparison.Ordinal)
            .Replace("{deep63}", deep63, StringComparison.Ordinal)
            .Replace("{deep64}", deep64, StringComparison.Ordinal)
            .Replace("{ctl}", "{\"body\":\"a\u0001b\"}", StringComparison.Ordinal)
            .Replace("{tab}", "{\"body\":\"a\tb\"}", StringComparison.Ordinal)
            .Replace("{del}", "{\"body\":\"hi\",\"x\":\"\u007f\"}", StringComparison.Ordinal);
        byte[] b = Encoding.UTF8.GetBytes(text.Replace("{ff}", """{"body":"a?b"}""", StringComparison.Ordinal));
        if (text.Contains("{ff}", StringComparison.Ordinal)) b[Array.IndexOf(b, (byte)'?')] = 0xFF;
        return text.StartsWith("{bom}", StringComparison.Ordinal) ? [0xEF, 0xBB, 0xBF, .. b[5..]] : b;
    }

    static HttpApp BodyApp(Action<HttpAppErrors> errors)
    {
        HttpAppBuilder b = Builder();
        errors(b.Errors);
        HttpApp app = b.Build();
        app.MapPost("/posts", static (NewPost p) => Reply.Created(new PostEcho(p.Body)));
        app.MapPost("/wrapped", static (WrappedPost p) => Reply.Created(new PostEcho(p.Body)));
        app.Start();
        return app;
    }

    const string InvalidBody = """{"error":"invalid body"}""";

    static (int, string) BodyOutcome(string outcome, ErrorReply? missing, ErrorReply? malformed) => outcome switch
    {
        "ok" => (201, """{"body":"hi"}"""),
        "field" => (400, """{"error":"body is required"}"""),
        "shape" => (422, InvalidBody),
        "missing" when missing is not null => (missing.Status, Encoding.UTF8.GetString(missing.Body)),
        "missing" or "malformed" when malformed is not null => (malformed.Status, Encoding.UTF8.GetString(malformed.Body)),
        _ => (422, InvalidBody),
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task EachBodyFailureHasItsOwnReply(string name, string? body, string outcome)
    {
        await using HttpApp app = BodyApp(e =>
        {
            e.MalformedBody = SpecErrors.Malformed;
            e.MissingBody = SpecErrors.NoBody;
        });
        await using var check = new AppCheck(app);
        Response r = await Send(app, "POST", "/posts", null, BodyBytes(body));
        Assert.Equal(BodyOutcome(outcome, SpecErrors.NoBody, SpecErrors.Malformed), (r.Status, r.Text));
        Assert.NotNull(name);
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task UnsetBodyReasonsAnswerWithInvalidBodyAsBefore(string name, string? body, string outcome)
    {
        await using HttpApp app = BodyApp(_ => { });
        await using var check = new AppCheck(app);
        Response r = await Send(app, "POST", "/posts", null, BodyBytes(body));
        Assert.Equal(BodyOutcome(outcome, null, null), (r.Status, r.Text));
        Assert.Null(app.Errors.MalformedBody);
        Assert.Null(app.Errors.MissingBody);
        Assert.NotNull(name);
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task NoBodyIsMalformedUnlessTheAppSaysOtherwise(string name, string? body, string outcome)
    {
        await using HttpApp app = BodyApp(e => e.MalformedBody = SpecErrors.Malformed);
        await using var check = new AppCheck(app);
        Response r = await Send(app, "POST", "/posts", null, BodyBytes(body));
        Assert.Equal(BodyOutcome(outcome, null, SpecErrors.Malformed), (r.Status, r.Text));
        Assert.NotNull(name);
    }

    // A struct's own InvalidBody answers a body of another shape, and a malformed or missing one when the app set no
    // reply of its own for that: the reason the app names wins over the place.
    [Theory]
    [InlineData(false, null, 422, """{"error":"not a post"}""")]
    [InlineData(false, "", 422, """{"error":"not a post"}""")]
    [InlineData(false, """{"post":""", 422, """{"error":"not a post"}""")]
    [InlineData(false, """{"other":1}""", 422, """{"error":"not a post"}""")]
    [InlineData(true, null, 400, """{"error":"no body"}""")]
    [InlineData(true, "", 400, """{"error":"no body"}""")]
    [InlineData(true, """{"post":""", 400, """{"error":"malformed JSON body"}""")]
    [InlineData(true, """{"post":{"body":"x"}""", 400, """{"error":"malformed JSON body"}""")]
    [InlineData(true, """{"other":1}""", 422, """{"error":"not a post"}""")]
    [InlineData(true, """{"post":[]}""", 422, """{"error":"not a post"}""")]
    [InlineData(true, "[]", 422, """{"error":"not a post"}""")]
    [InlineData(true, """{"post":{"body":5}}""", 422, InvalidBody)]
    [InlineData(true, """{"post":{"body":"hi"}}""", 201, """{"body":"hi"}""")]
    public async Task AStructsOwnInvalidBodyGivesWayToTheAppsReasons(bool reasons, string? body, int status, string text)
    {
        await using HttpApp app = BodyApp(e =>
        {
            if (!reasons) return;
            e.MalformedBody = SpecErrors.Malformed;
            e.MissingBody = SpecErrors.NoBody;
        });
        await using var check = new AppCheck(app);
        Response r = await Send(app, "POST", "/wrapped", null, BodyBytes(body));
        Assert.Equal((status, text), (r.Status, r.Text));
    }

    [Fact]
    public void ABodySourceMadeWithoutReasonsHasNone()
    {
        var arena = new JwtClaimTests.CountingArena();
        var s = new BodySource([1, 2], 0, 2, arena, SpecErrors.NoBody);
        Assert.Equal((false, (ErrorReply?)null, (ErrorReply?)null), (s.IsEmpty, s.MalformedBody, s.MissingBody));
        var t = new BodySource([], 0, 0, arena, SpecErrors.NoBody, SpecErrors.Malformed, null);
        Assert.Equal((true, SpecErrors.Malformed, (ErrorReply?)null), (t.IsEmpty, t.MalformedBody, t.MissingBody));
    }

    // ---- route values ----

    static readonly ErrorReply InvalidPostId = new(400, """{"error":"invalid post id"}"""u8);
    static readonly ErrorReply InvalidUserId = new(400, """{"error":"invalid user id"}"""u8);

    static HttpApp RouteApp(Action<HttpAppErrors> errors)
    {
        HttpAppBuilder b = Builder(Feed());
        errors(b.Errors);
        HttpApp app = b.Build();
        app.MapGet("/posts/{id}", static ([Range(1, long.MaxValue)] long id) => Reply.Ok(new Numbered(id)));
        app.MapGet("/any/{id}", static (long id) => Reply.Ok(new Numbered(id)));
        app.MapGet("/pages/{n}", static ([Range(1, 100)] int n) => Reply.Ok(new Numbered(n)));
        app.MapGet("/users/{id}", static ([Range(1, long.MaxValue)] long id) => Reply.Ok(new Numbered(id))).WithInvalidRouteValue(InvalidUserId);
        app.MapPost("/posts/{id}/like", static ([Range(1, long.MaxValue)] long id, Viewer v) => Reply.Created(new Numbered(id))).AuthRequired();
        app.Start();
        return app;
    }

    [Theory]
    [InlineData("/posts/1", 1L)]
    [InlineData("/posts/007", 7L)]
    [InlineData("/posts/%31", 1L)]                       // the path is decoded first
    [InlineData("/posts/9223372036854775807", long.MaxValue)]
    [InlineData("/posts/0", null)]
    [InlineData("/posts/-1", null)]
    [InlineData("/posts/abc", null)]
    [InlineData("/posts/1.5", null)]
    [InlineData("/posts/+1", null)]
    [InlineData("/posts/1e3", null)]
    [InlineData("/posts/0x10", null)]
    [InlineData("/posts/%201", null)]
    [InlineData("/posts/1%20", null)]
    [InlineData("/posts/9223372036854775808", null)]   // past a long
    [InlineData("/posts/99999999999999999999999", null)]
    [InlineData("/posts/%D9%A1", null)]                 // an Arabic-Indic one
    [InlineData("/posts/%EF%BC%91", null)]              // a fullwidth one
    [InlineData("/any/0", 0L)]                           // no [Range]: any digits that fit
    [InlineData("/any/abc", null)]
    [InlineData("/pages/1", 1L)]
    [InlineData("/pages/100", 100L)]
    [InlineData("/pages/0", null)]
    [InlineData("/pages/101", null)]
    [InlineData("/pages/2147483648", null)]
    public async Task ARouteValueThatDoesntBindGetsTheAppsReply(string path, long? id)
    {
        await using HttpApp app = RouteApp(e => e.InvalidRouteValue = InvalidPostId);
        await using var check = new AppCheck(app);
        Response r = await Send(app, "GET", path);
        Assert.Equal(id is { } n ? (200, $$"""{"id":{{n}}}""") : (400, """{"error":"invalid post id"}"""), (r.Status, r.Text));
    }

    [Theory]
    [InlineData("/posts/0")]
    [InlineData("/posts/abc")]
    [InlineData("/any/abc")]
    [InlineData("/pages/101")]
    public async Task UnsetARouteValueThatDoesntBindIsANotFoundAsBefore(string path)
    {
        await using HttpApp app = RouteApp(_ => { });
        await using var check = new AppCheck(app);
        Assert.Null(app.Errors.InvalidRouteValue);
        Response r = await Send(app, "GET", path);
        Assert.Equal((404, """{"error":"not found"}"""), (r.Status, r.Text));
        await using HttpApp own = RouteApp(e => e.NotFound = new ErrorReply(404, """{"e":"nothing"}"""u8));
        await using var check2 = new AppCheck(own);
        Response o = await Send(own, "GET", path);
        Assert.Equal((404, """{"e":"nothing"}"""), (o.Status, o.Text));
    }

    [Theory]
    [InlineData(null, "/posts/abc/like", 401, """{"error":"missing bearer token"}""")]
    [InlineData("Bearer x", "/posts/abc/like", 401, """{"error":"invalid or expired token"}""")]
    [InlineData("{t}", "/posts/abc/like", 400, """{"error":"invalid post id"}""")]
    [InlineData("{t}", "/posts/0/like", 400, """{"error":"invalid post id"}""")]
    [InlineData(null, "/posts/5/like", 401, """{"error":"missing bearer token"}""")]
    [InlineData("{t}", "/posts/5/like", 201, """{"id":5}""")]
    public async Task AuthIsCheckedBeforeTheRouteValue(string? auth, string path, int status, string text)
    {
        await using HttpApp app = RouteApp(e =>
        {
            e.InvalidRouteValue = InvalidPostId;
            e.MissingToken = Missing;
            e.InvalidToken = Invalid;
        });
        await using var check = new AppCheck(app);
        Response r = await Send(app, "POST", path, auth?.Replace("{t}", "Bearer " + FeedToken(), StringComparison.Ordinal), body: "");
        Assert.Equal((status, text), (r.Status, r.Text));
    }

    [Fact]
    public async Task AnEndpointCanNameItsOwnReply()
    {
        await using HttpApp app = RouteApp(e => e.InvalidRouteValue = InvalidPostId);
        await using var check = new AppCheck(app);
        Response user = await Send(app, "GET", "/users/0");
        Assert.Equal((400, """{"error":"invalid user id"}"""), (user.Status, user.Text));
        Response post = await Send(app, "GET", "/posts/0");
        Assert.Equal((400, """{"error":"invalid post id"}"""), (post.Status, post.Text));
        await using HttpApp unset = RouteApp(_ => { });
        await using var check2 = new AppCheck(unset);
        Response alone = await Send(unset, "GET", "/users/x");
        Assert.Equal((400, """{"error":"invalid user id"}"""), (alone.Status, alone.Text));
    }

    [Fact]
    public async Task AnEndpointsReplyIsCheckedWhenItIsSet()
    {
        HttpApp app = Builder().Build();
        EndpointBuilder e = app.MapGet("/a/{id}", static (long id) => Reply.NoContent());
        Assert.Throws<ArgumentNullException>(() => e.WithInvalidRouteValue(null!));
        Assert.Same(e, e.WithInvalidRouteValue(InvalidUserId));
        await using (app)
        {
            await using var check = new AppCheck(app);
            app.Start();
            Assert.Throws<InvalidOperationException>(() => e.WithInvalidRouteValue(InvalidPostId));
        }
    }

    [Fact]
    public void AViewerMadeByHandHasNoClaims()
    {
        var v = new Viewer(5, default);
        Assert.False(v.TryGetClaim("username"u8, out Utf8 value));
        Assert.True(value.IsEmpty);
        Assert.True(v.Claim("anything"u8).IsEmpty);
    }
}

internal readonly record struct Me(long Id, Utf8 Token);

internal readonly record struct Named(long Id, Utf8 Username);

internal readonly record struct Has(bool Found, Utf8 Username);

internal readonly record struct Issued(Utf8 Token);

internal readonly record struct PostEcho(Utf8 Body);

internal readonly record struct Numbered(long Id);

internal static class SpecErrors
{
    public static readonly ErrorReply Malformed = new(400, """{"error":"malformed JSON body"}"""u8);
    public static readonly ErrorReply NoBody = new(400, """{"error":"no body"}"""u8);
    public static readonly ErrorReply BodyRequired = new(400, """{"error":"body is required"}"""u8);
    public static readonly ErrorReply NotAPost = new(422, """{"error":"not a post"}"""u8);
}

internal partial struct NewPost
{
    [BodyError(nameof(Required))] public Utf8 Body;

    static ErrorReply Required => SpecErrors.BodyRequired;
}

[JsonEnvelope("post")]
internal partial struct WrappedPost
{
    public Utf8 Body;

    static ErrorReply InvalidBody => SpecErrors.NotAPost;
}
