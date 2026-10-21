using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>A response's Content-Type, set per response and back to JSON for the next, and the Server header.</summary>
public class ContentTypeTests
{
    static readonly ContentReply Hello = new(MediaType.PlainText, "Hello, World!"u8);
    static readonly ContentReply Page = new(MediaType.Html, "<p>ä</p>"u8);

    static HttpAppBuilder Builder(string? server = null)
    {
        var b = HttpApp.CreateBuilder([]);
        b.Options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        b.Options.ServerName = server;
        b.Errors.Log = null;
        return b;
    }

    [Fact]
    public async Task EachResponseHasItsOwnTypeAndTheNextIsJsonAgain()
    {
        await using HttpApp app = Builder().Build();
        await using var check = new AppCheck(app);
        app.MapGet("/text", static () => Hello);
        app.MapGet("/json", static () => Reply.Ok(new ContentTypeOk(true)));
        app.MapGet("/html", static () => Page);
        app.MapGet("/none", static (IResponse r) =>
        {
            r.ContentType = MediaType.Html;
            return Reply.NoContent();
        });
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        // pipelined on one connection: each response's type is its own
        await c.SendAsync("GET /text HTTP/1.1\r\nHost: x\r\n\r\nGET /json HTTP/1.1\r\nHost: x\r\n\r\nGET /html HTTP/1.1\r\nHost: x\r\n\r\n" +
            "GET /json HTTP/1.1\r\nHost: x\r\n\r\nGET /none HTTP/1.1\r\nHost: x\r\n\r\nGET /json HTTP/1.1\r\nHost: x\r\n\r\n");
        Response text = await c.ReadAsync(), json = await c.ReadAsync(), html = await c.ReadAsync(), json2 = await c.ReadAsync(),
            none = await c.ReadAsync(), json3 = await c.ReadAsync();
        Assert.Equal((200, "text/plain", "13", "Hello, World!"), (text.Status, text.Header("Content-Type"), text.Header("Content-Length"), text.Text));
        Assert.Equal((200, "application/json", """{"done":true}"""), (json.Status, json.Header("Content-Type"), json.Text));
        Assert.Equal((200, "text/html; charset=utf-8", "<p>ä</p>"), (html.Status, html.Header("Content-Type"), html.Text));
        Assert.Equal("application/json", json2.Header("Content-Type"));
        Assert.Equal((204, null, ""), (none.Status, none.Header("Content-Type"), none.Text));
        Assert.Equal("application/json", json3.Header("Content-Type"));
        Assert.Null(text.Header("Server"));
    }

    [Fact]
    public async Task AHeadRequestGetsTheTypeWithoutTheBody()
    {
        await using HttpApp app = Builder().Build();
        await using var check = new AppCheck(app);
        app.MapGet("/text", static () => Hello);
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync("HEAD /text HTTP/1.1\r\nHost: x\r\n\r\n");
        Response r = await c.ReadAsync(head: true);
        Assert.Equal((200, "text/plain", "13", ""), (r.Status, r.Header("Content-Type"), r.Header("Content-Length"), r.Text));
    }

    [Fact]
    public async Task AFailedHandlersTypeIsDroppedWithItsBody()
    {
        await using HttpApp app = Builder().Build();
        await using var check = new AppCheck(app);
        app.MapGet("/boom", static (IResponse r) => Boom(r));
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync("GET /boom HTTP/1.1\r\nHost: x\r\n\r\n");
        Response r = await c.ReadAsync();
        Assert.Equal((500, "application/json", """{"error":"internal error"}"""), (r.Status, r.Header("Content-Type"), r.Text));
    }

    static Reply Boom(IResponse r)
    {
        r.ContentType = MediaType.Html;
        r.Body.Write("<p>half"u8);
        throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task EveryResponseCarriesTheServerHeaderTheServersOwnRefusalsToo()
    {
        HttpAppBuilder b = Builder("CodeFusion");
        b.Options.MaxRequestBodySize = 10;
        await using HttpApp app = b.Build();
        await using var check = new AppCheck(app);
        app.MapGet("/text", static () => Hello);
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync("GET /text HTTP/1.1\r\nHost: x\r\n\r\nGET /missing HTTP/1.1\r\nHost: x\r\n\r\nPOST /text HTTP/1.1\r\nHost: x\r\nContent-Length: 99\r\n\r\n");
        Response ok = await c.ReadAsync(), missing = await c.ReadAsync(), large = await c.ReadAsync();
        Assert.Equal((200, "CodeFusion"), (ok.Status, ok.Header("Server")));
        Assert.Equal((404, "CodeFusion"), (missing.Status, missing.Header("Server")));
        Assert.Equal((413, "CodeFusion"), (large.Status, large.Header("Server")));
        Assert.NotNull(ok.Header("Date"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" CodeFusion")]
    [InlineData("CodeFusion ")]
    [InlineData("Code\r\nX-Injected: 1")]
    [InlineData("Codé")]
    [InlineData("1234567890123456789012345678901234567890123456789")]
    public void AServerNameThatWouldBreakTheHeadIsRefused(string name)
    {
        var options = new HttpServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), ServerName = name };
        Assert.Throws<ArgumentException>(() => new HttpServer(options, TestServer.Echo));
    }

    // every optional header at its longest, the longest reason phrase and an unknown status, and each connection
    // header: the longest real head is HTTP/1.0's with keep-alive, five bytes past the one with close
    [Theory]
    [InlineData(431, true, false)]
    [InlineData(431, false, true)]
    [InlineData(599, false, true)]
    [InlineData(599, true, false)]
    public void TheLongestHeadFitsItsReservedRoom(int status, bool close, bool keepAlive)
    {
        var type = new MediaType(new string('t', MediaType.MaxLength));
        byte[] server = Encoding.ASCII.GetBytes("Server: " + new string('s', HttpServerOptions.MaxServerNameLength) + "\r\n");
        var d = new byte[ResponseHead.Reserve];
        int n = ResponseHead.Write(d, status, int.MaxValue, type.HeaderLine, server, int.MaxValue, close, keepAlive);
        Assert.InRange(n, 1, ResponseHead.Reserve);
        string head = Encoding.ASCII.GetString(d, 0, n);
        Assert.Contains("Content-Type: " + type.Value + "\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Server: " + new string('s', HttpServerOptions.MaxServerNameLength) + "\r\n", head, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingSetTheHeadIsByteForByteWhatItWas()
    {
        // the head as the server wrote it before media types: JSON's type for a body, none without one
        await using var server = TestServer.Start(static x =>
        {
            if (x.Path.SequenceEqual("/empty"u8)) x.Status = 204;
            else TestServer.Write(x, "{}");
            return default;
        });
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        await socket.ConnectAsync(server.EndPoint, TestContext.Current.CancellationToken);
        await socket.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\nGET /empty HTTP/1.1\r\nHost: x\r\n\r\nGET / HTTP/1.0\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var got = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(), TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (n == 0) break;
            got.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        string date = System.Text.RegularExpressions.Regex.Match(got.ToString(), "Date: ([^\r]+)\r\n").Groups[1].Value;
        Assert.Equal(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nDate: {date}\r\n\r\n{{}}" +
            $"HTTP/1.1 204 No Content\r\nDate: {date}\r\n\r\n" +
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\nDate: {date}\r\n\r\n{{}}",
            System.Text.RegularExpressions.Regex.Replace(got.ToString(), "Date: [^\r]+", "Date: " + date));
    }

    [Fact]
    public async Task TheRawServersHandlerSetsTheTypeOnTheExchange()
    {
        await using var server = TestServer.Start(static x =>
        {
            x.ContentType = MediaType.PlainText;
            TestServer.Write(x, "raw");
            return default;
        }, o => o.ServerName = "Raw");
        using Client c = await server.ConnectAsync();
        await c.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\nGET / HTTP/1.1\r\nHost: x\r\n\r\n");
        Response a = await c.ReadAsync(), b = await c.ReadAsync();
        Assert.Equal(("text/plain", "Raw", "raw"), (a.Header("Content-Type"), a.Header("Server"), a.Text));
        Assert.Equal(("text/plain", "Raw"), (b.Header("Content-Type"), b.Header("Server")));
    }
}

public readonly record struct ContentTypeOk(bool Done);
