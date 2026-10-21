using System;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Http.Tests.Sample;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>The feed spec's error table and auth rules, row by row, against <see cref="FeedApp"/>.</summary>
public sealed class FeedAppTests : IAsyncLifetime
{
    HttpApp _app = null!;
    AppCheck _check = null!;

    public ValueTask InitializeAsync()
    {
        _app = FeedApp.Build();
        _check = new AppCheck(_app);
        _app.Start();
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await _check.DisposeAsync();
        await _app.DisposeAsync();
    }

    static readonly Jwt Tokens = new(FeedApp.Secret, Options());

    static JwtOptions Options()
    {
        var o = new JwtOptions { Scheme = "Bearer", AllowShortSecret = true };
        o.Claims.Add(new JwtClaim("username"));
        return o;
    }

    /// <summary>A token as seed/make-seed.sh signs them: sub, username, iat, exp.</summary>
    internal static string Token(long user = 42, string username = "jake", long expIn = 3600)
    {
        byte[] name = Encoding.UTF8.GetBytes(username);
        Utf8[] claims = [new Utf8(name, 0, name.Length)];
        var b = new byte[Tokens.MaxIssuedBytesWith(claims)];
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Encoding.ASCII.GetString(b, 0, Tokens.Issue(user, now + expIn - 30L * 24 * 3600, claims, b));
    }

    static string Signed(string payload) => JwtClaimTests.Signed(Encoding.UTF8.GetBytes(payload), secret: FeedApp.Secret);

    Task<Response> Send(string method, string path, string? auth = null, string? body = null) =>
        SpecOptionsTests.Send(_app, method, path, auth, body);

    static void Is(Response r, int status, string body) => Assert.Equal((status, body), (r.Status, r.Text));

    [Theory]
    [InlineData("GET", "/nothing/here")]
    [InlineData("GET", "/posts")]
    [InlineData("GET", "/posts/1/likes")]
    public async Task AnUnknownPathIsNotFound(string method, string path) =>
        Is(await Send(method, path), 404, """{"error":"not found"}""");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public async Task AnIdThatIsntAPositiveIntegerIsInvalid(string id)
    {
        Is(await Send("GET", "/posts/" + id), 400, """{"error":"invalid post id"}""");
        Is(await Send("POST", "/posts/" + id + "/like", "Bearer " + Token(), ""), 400, """{"error":"invalid post id"}""");
    }

    [Fact]
    public async Task APostThatDoesntExistIsNotFound()
    {
        Is(await Send("GET", "/posts/101"), 404, """{"error":"post not found"}""");
        Is(await Send("POST", "/posts/101/like", "Bearer " + Token(), ""), 404, """{"error":"post not found"}""");
    }

    [Fact]
    public async Task APostIsWrittenInTheSpecsKeyOrder() =>
        Is(await Send("GET", "/posts/3"), 200, """{"post":{"id":3,"body":"hello","created_at":"2025-12-31T23:59:19.409Z","author":"jake","like_count":0}}""");

    public static TheoryData<string, string?, string> Refusals()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string good = Token();
        return new()
        {
            { "no header", null, "missing bearer token" },
            { "another scheme", "Token " + good, "missing bearer token" },
            { "the scheme in lower case", "bearer " + good, "missing bearer token" },
            { "the token alone", good, "missing bearer token" },
            { "a bad signature", "Bearer " + good[..^3] + (good[^3] == 'a' ? "b" : "a") + good[^2..], "invalid or expired token" },
            { "another secret", "Bearer " + JwtClaimTests.Signed($$"""{"sub":"1","username":"a","exp":{{now + 60}}}"""), "invalid or expired token" },
            { "expired", "Bearer " + Token(expIn: -1), "invalid or expired token" },
            { "not HS256", "Bearer " + JwtClaimTests.Signed(Encoding.UTF8.GetBytes($$"""{"sub":"1","username":"a","exp":{{now + 60}}}"""), header: "eyJhbGciOiJIUzM4NCJ9", secret: FeedApp.Secret), "invalid or expired token" },
            { "malformed", "Bearer a.b.c", "invalid or expired token" },
            { "sub 0", "Bearer " + Signed($$"""{"sub":"0","username":"a","exp":{{now + 60}}}"""), "invalid token payload" },
            { "sub a number", "Bearer " + Signed($$"""{"sub":1,"username":"a","exp":{{now + 60}}}"""), "invalid token payload" },
            { "sub not digits", "Bearer " + Signed($$"""{"sub":"x1","username":"a","exp":{{now + 60}}}"""), "invalid token payload" },
            { "no username", "Bearer " + Signed($$"""{"sub":"1","exp":{{now + 60}}}"""), "invalid token payload" },
            { "username a number", "Bearer " + Signed($$"""{"sub":"1","username":7,"exp":{{now + 60}}}"""), "invalid token payload" },
            { "username null", "Bearer " + Signed($$"""{"sub":"1","username":null,"exp":{{now + 60}}}"""), "invalid token payload" },
        };
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachAuthFailureHasTheSpecsMessage(string name, string? auth, string message)
    {
        Is(await Send("POST", "/posts", auth, """{"body":"hi"}"""), 401, $$"""{"error":"{{message}}"}""");
        Is(await Send("POST", "/posts/abc/like", auth, ""), 401, $$"""{"error":"{{message}}"}"""); // auth first, then the id
        Assert.NotNull(name);
    }

    [Theory]
    [InlineData("{", "malformed JSON body")]
    [InlineData("""{"body":"hi""", "malformed JSON body")]
    [InlineData("""{"body":"hi"} x""", "malformed JSON body")]
    [InlineData("", "malformed JSON body")]
    [InlineData("{}", "body is required")]
    [InlineData("[]", "body is required")]
    [InlineData("""{"body":5}""", "body is required")]
    [InlineData("""{"body":null}""", "body is required")]
    [InlineData("""{"body":""}""", "body is required")]
    [InlineData("""{"body":" \t\n "}""", "body is required")]
    public async Task EachBodyFailureHasTheSpecsMessage(string body, string message) =>
        Is(await Send("POST", "/posts", "Bearer " + Token(), body), 400, $$"""{"error":"{{message}}"}""");

    [Fact]
    public async Task APostIsTrimmedAndAtMost500Characters()
    {
        string auth = "Bearer " + Token(username: "jé\"ke");
        Response r = await Send("POST", "/posts", auth, """{"body":"  hi there \n"}""");
        Assert.Equal(201, r.Status);
        Assert.Matches("""^\{"post":\{"id":\d+,"body":"hi there","created_at":"2025-12-31T23:59:19\.409Z","author":"jé\\"ke","like_count":0\}\}$""", r.Text);
        string max = new('é', 500);
        Assert.Equal(201, (await Send("POST", "/posts", auth, $$"""{"body":" {{max}} "}""")).Status);
        Is(await Send("POST", "/posts", auth, $$"""{"body":"{{max}}x"}"""), 400, """{"error":"body must be at most 500 characters"}""");
    }

    [Fact]
    public async Task ALikeIsCreatedOnceAndThenAlreadyThere()
    {
        string auth = "Bearer " + Token(user: 7);
        Is(await Send("POST", "/posts/9/like", auth, ""), 201, """{"liked":true,"already_liked":false,"post_id":9}""");
        Is(await Send("POST", "/posts/9/like", auth, ""), 200, """{"liked":true,"already_liked":true,"post_id":9}""");
        Is(await Send("GET", "/posts/9"), 200, """{"post":{"id":9,"body":"hello","created_at":"2025-12-31T23:59:19.409Z","author":"jake","like_count":1}}""");
    }
}
