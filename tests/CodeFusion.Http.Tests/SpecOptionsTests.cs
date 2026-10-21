using System;
using System.Net;
using System.Text;
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
}

internal readonly record struct Me(long Id, Utf8 Token);
