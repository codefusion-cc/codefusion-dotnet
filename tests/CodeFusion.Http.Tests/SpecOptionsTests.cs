using System;
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

    internal static async Task<Response> Send(HttpApp app, string method, string target, string? auth = null, string? body = null)
    {
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        byte[] b = body is null ? [] : Encoding.UTF8.GetBytes(body);
        await c.SendAsync($"{method} {target} HTTP/1.1\r\nHost: x\r\n"
            + (auth is null ? "" : $"Authorization: {auth}\r\n")
            + (body is null ? "" : $"Content-Length: {b.Length}\r\n")
            + "\r\n" + (body ?? ""));
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
