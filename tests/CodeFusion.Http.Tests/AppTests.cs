using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>The builder and the app: settings, registrations, startup checks, error bodies and stopping.</summary>
public class AppTests
{
    internal sealed class Scoped : IRequestScoped
    {
        public void Bind(IResponse response) { }
    }

    internal sealed class Other : IRequestScoped
    {
        public void Bind(IResponse response) { }
    }

    static HttpAppBuilder Builder()
    {
        var b = HttpApp.CreateBuilder([]);
        b.Options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        TestTransport.Apply(b.Options);
        b.Errors.Log = null;
        return b;
    }

    [Fact]
    public void SettingsComeFromTheEnvironment()
    {
        string? host = Environment.GetEnvironmentVariable("HOST"), port = Environment.GetEnvironmentVariable("PORT");
        try
        {
            Environment.SetEnvironmentVariable("HOST", "localhost");
            Environment.SetEnvironmentVariable("PORT", "4321");
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, 4321), HttpApp.CreateBuilder([]).Options.EndPoint);
            Environment.SetEnvironmentVariable("HOST", "0.0.0.0");
            Assert.Equal(new IPEndPoint(IPAddress.Any, 4321), HttpApp.CreateBuilder([]).Options.EndPoint);
            Environment.SetEnvironmentVariable("HOST", "example.internal");
            Assert.Equal(new IPEndPoint(IPAddress.IPv6Any, 4321), HttpApp.CreateBuilder([]).Options.EndPoint);
            Environment.SetEnvironmentVariable("HOST", null);
            Environment.SetEnvironmentVariable("PORT", "");
            Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 3000), HttpApp.CreateBuilder([]).Options.EndPoint);
            Environment.SetEnvironmentVariable("PORT", "70000");
            Assert.Throws<ArgumentException>(() => HttpApp.CreateBuilder([]));
            Environment.SetEnvironmentVariable("PORT", "x");
            Assert.Throws<ArgumentException>(() => HttpApp.CreateBuilder([]));
            Assert.Null(HttpAppBuilder.Setting("PORT_THAT_IS_NOT_SET_ANYWHERE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOST", host);
            Environment.SetEnvironmentVariable("PORT", port);
        }
    }

    [Fact]
    public void RegistrationsAreCheckedAsTheyAreMade()
    {
        HttpAppBuilder b = Builder();
        b.AddSingleton(new object());
        Assert.Throws<InvalidOperationException>(() => b.AddSingleton(new object()));
        b.AddPerConnection(() => new Scoped());
        Assert.Throws<InvalidOperationException>(() => b.AddPerConnection(() => new Scoped()));
        Assert.Throws<ArgumentNullException>(() => b.AddSingleton<object>(null!));
        b.AddJwtAuth(JwtTests.Secret);
        Assert.Throws<InvalidOperationException>(() => b.AddJwtAuth(JwtTests.Secret));
        Assert.Throws<ArgumentException>(() => Builder().AddJwtAuth(""));
        b.Build();
        Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Throws<InvalidOperationException>(() => b.AddSingleton("late"));
        Assert.Throws<InvalidOperationException>(() => b.OnStopping(() => { }));
    }

    [Fact]
    public void WhatAHandlerNeedsIsCheckedWhenItIsMapped()
    {
        HttpApp app = Builder().Build();
        var e1 = Assert.Throws<InvalidOperationException>(() => app.MapGet("/a", static (Viewer v) => Reply.NoContent()));
        Assert.Contains("AddJwtAuth", e1.Message, StringComparison.Ordinal);
        var e2 = Assert.Throws<InvalidOperationException>(() => app.MapGet("/b/{x}", static (Utf8 y) => Reply.NoContent()));
        Assert.Contains("GET /b/{x}: the handler has no {y}", e2.Message, StringComparison.Ordinal);
        var e3 = Assert.Throws<InvalidOperationException>(() => app.MapGet("/c", static (Uri u) => Reply.NoContent()));
        Assert.Contains("System.Uri, which isn't registered", e3.Message, StringComparison.Ordinal);
        var e4 = Assert.Throws<InvalidOperationException>(() => app.MapGet("/d", static (Other o) => Reply.NoContent()));
        Assert.Contains("isn't registered", e4.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => app.MapGet("/e", static () => Reply.NoContent()).AuthRequired());
        // a route value from the group's prefix
        app.MapGroup("/profiles/{name}").MapGet("/follow", static (Utf8 name) => Reply.NoContent());
        app.MapGet("/f", static () => Reply.NoContent());
        Assert.Throws<InvalidOperationException>(() => app.MapGet("/f", static () => Reply.NoContent()));
    }

    [Fact]
    public async Task RoutesAreFixedOnceStarted()
    {
        await using HttpApp app = Builder().Build();
        await using var check = new AppCheck(app);
        app.Start();
        Assert.Throws<InvalidOperationException>(() => app.MapGet("/late", static () => Reply.NoContent()));
        Assert.Throws<InvalidOperationException>(() => app.Start());
    }

    [Fact]
    public async Task StoppingRunsTheCallbacksInReverseOnce()
    {
        HttpAppBuilder b = Builder();
        var order = new List<int>();
        b.OnStopping(() => order.Add(1));
        b.OnStopping(() => order.Add(2));
        HttpApp app = b.Build();
        app.Start();
        await app.StopAsync();
        await new AppCheck(app).DisposeAsync();
        await app.DisposeAsync();
        Assert.Equal([2, 1], order);
    }

    [Fact]
    public async Task AFailingCallbackDoesntStopTheOthers()
    {
        HttpAppBuilder b = Builder();
        bool ran = false;
        b.OnStopping(() => ran = true);
        b.OnStopping(() => throw new InvalidOperationException("x"));
        HttpApp app = b.Build();
        await Assert.ThrowsAsync<AggregateException>(app.StopAsync);
        Assert.True(ran);
    }

    [Theory]
    [InlineData(413, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 99999999\r\n\r\n", """{"e":"too large"}""")]
    [InlineData(414, "GET /{long} HTTP/1.1\r\nHost: x\r\n\r\n", """{"e":"too long"}""")]
    [InlineData(431, "GET / HTTP/1.1\r\nHost: x\r\nX: {long}\r\n\r\n", """{"e":"headers"}""")]
    [InlineData(400, "GET / HTTP/1.1\r\nHost: x\r\nContent-Length: x\r\n\r\n", """{"e":"bad"}""")]
    public async Task TheServersOwnRefusalsCarryTheConfiguredBodies(int status, string request, string body)
    {
        HttpAppBuilder b = Builder();
        b.Options.MaxRequestBodySize = 1000;
        b.Options.MaxRequestLineSize = 200;
        b.Options.MaxRequestHeadersTotalSize = 200;
        b.Errors.BodyTooLarge = new ErrorReply(413, """{"e":"too large"}"""u8);
        b.Errors.UriTooLong = new ErrorReply(414, """{"e":"too long"}"""u8);
        b.Errors.HeadersTooLarge = new ErrorReply(431, """{"e":"headers"}"""u8);
        b.Errors.BadRequest = new ErrorReply(400, """{"e":"bad"}"""u8);
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync(request.Replace("{long}", new string('a', 300), StringComparison.Ordinal));
        Response r = await c.ReadAsync();
        Assert.Equal((status, body, "application/json"), (r.Status, r.Text, r.Header("Content-Type")));
    }

    [Fact]
    public async Task TheFallbackAnd401AreConfigurable()
    {
        HttpAppBuilder b = Builder();
        b.AddJwtAuth(JwtTests.Secret);
        b.Errors.NotFound = new ErrorReply(404, """{"e":"nothing"}"""u8);
        b.Errors.Unauthorized = new ErrorReply(401, """{"e":"who"}"""u8);
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/me", static (Viewer v) => Reply.NoContent()).AuthRequired();
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync("GET /nope HTTP/1.1\r\nHost: x\r\n\r\nGET /me HTTP/1.1\r\nHost: x\r\n\r\n");
        Response a = await c.ReadAsync(), m = await c.ReadAsync();
        Assert.Equal((404, """{"e":"nothing"}"""), (a.Status, a.Text));
        Assert.Equal((401, """{"e":"who"}"""), (m.Status, m.Text));
    }

    [Fact]
    public async Task AFailingHandlerIsLoggedWithItsRequest()
    {
        HttpAppBuilder b = Builder();
        var logged = new List<string>();
        b.Errors.Log = (x, e) => { lock (logged) logged.Add($"{Encoding.ASCII.GetString(x.MethodBytes)} {e.Message}"); };
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapPost("/boom", static () => Boom());
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync("POST /boom HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n");
        Response r = await c.ReadAsync();
        Assert.Equal((500, """{"error":"internal error"}"""), (r.Status, r.Text));
        lock (logged) Assert.Equal(["POST boom"], logged);
    }

    static Reply Boom() => throw new InvalidOperationException("boom");
}
