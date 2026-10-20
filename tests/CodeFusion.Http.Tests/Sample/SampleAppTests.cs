using System;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Http.Tests.Sample;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>The sample app over loopback: every parameter and reply kind, answered byte for byte.</summary>
public sealed class SampleAppTests : IAsyncLifetime
{
    HttpApp _app = null!;

    public ValueTask InitializeAsync()
    {
        _app = SampleApp.Build(b => b.Errors.Internal = new ErrorReply(500, """{"errors":{"body":["internal"]}}"""u8));
        _app.Start();
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await new AppCheck(_app).DisposeAsync();
        await _app.DisposeAsync();
    }

    static readonly Jwt Tokens = new(SampleApp.Secret);

    internal static string Token(long user)
    {
        Span<byte> b = stackalloc byte[Jwt.MaxIssuedBytes];
        return Encoding.ASCII.GetString(b[..Tokens.Issue(user, b)]);
    }

    async Task<Response> Send(string method, string target, string? body = null, string? auth = null, string extra = "")
    {
        using Client c = await Client.ConnectAsync(_app.LocalEndPoint);
        string head = $"{method} {target} HTTP/1.1\r\nHost: x\r\n{(auth is null ? "" : $"Authorization: {auth}\r\n")}{extra}";
        byte[] b = body is null ? [] : Encoding.UTF8.GetBytes(body);
        await c.SendAsync(Encoding.UTF8.GetBytes(head + $"Content-Length: {b.Length}\r\n\r\n"));
        if (b.Length > 0) await c.SendAsync(b);
        return await c.ReadAsync(head: method == "HEAD");
    }

    [Fact]
    public async Task ARouteValueTheQueryAViewerAndASingleton()
    {
        string token = Token(5);
        Response r = await Send("GET", "/api/articles/how-to-%C3%A9?tag=dragons&by=jake+o%27&limit=7&offset=2", auth: "Token " + token);
        Assert.Equal(200, r.Status);
        Assert.Equal("application/json", r.Header("Content-Type"));
        Assert.Equal(
            """{"article":{"slug":"how-to-é","tag":"dragons","author":"jake o'","limit":7,"offset":2,"createdAt":"2023-11-14T22:13:20.123Z","favorited":true,"score":1.5,"author2":{"username":"how-to-é","bio":null,"following":false},"editor":{"username":""" + "\""
            + token + "\"" + ""","bio":"how-to-é","following":true},"extra":{"x":[1,2]},"ranks":[3,1],"kind":1,"viewer_id":5}}""",
            r.Text);
    }

    [Fact]
    public async Task AnonymousAndDefaults()
    {
        Response r = await Send("GET", "/api/articles/a");
        Assert.Equal(
            """{"article":{"slug":"a","tag":null,"author":null,"limit":20,"offset":0,"createdAt":"2023-11-14T22:13:20.123Z","favorited":false,"score":1.5,"author2":{"username":"a","bio":null,"following":false},"editor":null,"extra":{"x":[1,2]},"ranks":null,"kind":1,"viewer_id":0}}""",
            r.Text);
    }

    [Theory]
    [InlineData("/api/articles/a?limit=0", 422, """{"errors":{"body":["bad page"]}}""")]
    [InlineData("/api/articles/a?limit=101", 422, """{"errors":{"body":["bad page"]}}""")]
    [InlineData("/api/articles/a?offset=-1", 422, """{"errors":{"body":["bad page"]}}""")]
    [InlineData("/api/articles/a?offset=2147483648", 422, """{"errors":{"body":["bad page"]}}""")]
    [InlineData("/api/articles/missing", 404, """{"errors":{"body":["article not found"]}}""")]
    [InlineData("/api/articles", 404, """{"error":"not found"}""")]
    [InlineData("/api/articles/a/", 404, """{"error":"not found"}""")]
    [InlineData("/api/nothing", 404, """{"error":"not found"}""")]
    public async Task QueryErrorsAndTheFallback(string target, int status, string body)
    {
        Response r = await Send("GET", target);
        Assert.Equal((status, body), (r.Status, r.Text));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("", 401)]
    [InlineData("Token x.y.z", 401)]
    [InlineData("Bearer {token}", 401)]
    [InlineData(" Token {token}\t", 201)]
    public async Task AuthRequired(string? header, int status)
    {
        string body = """{"article":{"title":"t","tagList":["a","b"]}}""";
        Response r = await Send("POST", "/api/articles", body, header?.Replace("{token}", Token(9), StringComparison.Ordinal));
        Assert.Equal(status, r.Status);
        if (status == 401) Assert.Equal("""{"error":"unauthorized"}""", r.Text);
    }

    [Fact]
    public async Task AnInvalidHeaderOnAnOptionalEndpointIs401()
    {
        Response r = await Send("GET", "/api/articles/a", auth: "Token nope");
        Assert.Equal((401, """{"error":"unauthorized"}"""), (r.Status, r.Text));
    }

    [Fact]
    public async Task ABodyWithEverythingAndATokenFromTheArena()
    {
        string body = """{"article":{"title":"Té\"q\"","description":"d","tagList":["x","y\n"],"meta":{"pinned":true,"score":2.5,"rank":3,"note":"n"},"count":12,"draft":false,"extra":[1]}}""";
        Response r = await Send("POST", "/api/articles", body, "Token " + Token(41));
        Assert.Equal(201, r.Status);
        string issued = Token(42); // issued now: the same second as the handler's, unless the clock ticked over
        string expected = """{"article":{"title":"Té\"q\"","description":"d","tagList":["x","y\n"],"meta":{"pinned":true,"score":2.5,"rank":3,"note":"n"},"count":12,"draft":false,"author":41,"token":""" + "\"";
        Assert.StartsWith(expected, r.Text, StringComparison.Ordinal);
        string token = r.Text[expected.Length..^3];
        Assert.Equal(42, Tokens.Verify(Encoding.ASCII.GetBytes(token)));
        _ = issued;
    }

    [Theory]
    [InlineData("""{"article":{"title":""}}""", """{"errors":{"body":["invalid"]}}""")]
    [InlineData("""{"article":{"title":"t","description":""}}""", """{"errors":{"body":["invalid"]}}""")]
    [InlineData("""{"article":{"title":"t","tagList":["a",""]}}""", """{"errors":{"body":["bad tags"]}}""")]
    [InlineData("""{"article":{"title":"t","meta":{"score":"x"}}}""", """{"errors":{"body":["invalid"]}}""")]
    [InlineData("""{"article":{"title":"t","meta":7}}""", """{"errors":{"body":["invalid"]}}""")]
    [InlineData("""{"article":{"title":"t","count":1.5}}""", """{"errors":{"body":["invalid"]}}""")]
    [InlineData("""{"article":{"title":"t","draft":"no"}}""", """{"errors":{"body":["invalid"]}}""")]
    [InlineData("""{"article":"t"}""", """{"error":"invalid body"}""")]
    [InlineData("""{"article":{"title":"t"}""", """{"error":"invalid body"}""")]
    [InlineData("", """{"error":"invalid body"}""")]
    public async Task BodyErrors(string body, string expected)
    {
        Response r = await Send("POST", "/api/articles", body, "Token " + Token(1));
        Assert.Equal((422, expected), (r.Status, r.Text));
    }

    [Fact]
    public async Task OptionalFieldsAreNullWhenAbsent()
    {
        Response r = await Send("POST", "/api/articles", """{"article":{"title":"t"}}""", "Token " + Token(1));
        Assert.StartsWith("""{"article":{"title":"t","description":null,"tagList":[],"meta":null,"count":null,"draft":null,"author":1,"token":""", r.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/articles/s/comments/0", 404, """{"errors":{"body":["article not found"]}}""")]
    [InlineData("/api/articles/s/comments/1", 403, """{"errors":{"body":["not the author"]}}""")]
    [InlineData("/api/articles/s/comments/2", 204, "")]
    [InlineData("/api/articles/s/comments/x", 404, """{"error":"not found"}""")]
    [InlineData("/api/articles/s/comments/-2", 404, """{"error":"not found"}""")]
    [InlineData("/api/articles/s/comments/99999999999999999999", 404, """{"error":"not found"}""")]
    public async Task ALongRouteValueAndRepliesWithoutValues(string target, int status, string body)
    {
        Response r = await Send("DELETE", target, auth: "Token " + Token(3));
        Assert.Equal((status, body), (r.Status, r.Text));
    }

    [Fact]
    public async Task TheViewersTokenPointsIntoTheRequest()
    {
        string token = Token(77);
        Response r = await Send("GET", "/api/user", auth: "  Token " + token + " ");
        Assert.Equal("{\"user\":{\"id\":77,\"token\":\"" + token + "\"}}", r.Text);
    }

    [Fact]
    public async Task AnonymousEndpointsIgnoreTheHeader()
    {
        Response r = await Send("POST", "/api/users/login", """{"user":{"email":"a","password":"secret"}}""", "garbage");
        Assert.Equal(200, r.Status);
        Assert.StartsWith("""{"user":{"id":7,"token":"ey""", r.Text, StringComparison.Ordinal);
        Response wrong = await Send("POST", "/api/users/login", """{"user":{"email":"a","password":"no"}}""");
        Assert.Equal((401, """{"errors":{"body":["invalid email or password"]}}"""), (wrong.Status, wrong.Text));
    }

    [Fact]
    public async Task AnAsyncHandlerWhoseWaitEndsOnAnotherThread()
    {
        using Client c = await Client.ConnectAsync(_app.LocalEndPoint);
        long first = await FirstTick(c);
        for (int i = 1; i <= 3; i++)
        {
            await c.SendAsync($"GET /api/slow/{i} HTTP/1.1\r\nHost: x\r\n\r\n");
            Response r = await c.ReadAsync();
            // the connection's ticker counts on, and was bound before each call
            Assert.Equal("{\"n\":" + i + ",\"tick\":" + (first + i) + ",\"bound\":true}", r.Text);
        }
        await c.SendAsync("GET /api/slow/x HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal(404, (await c.ReadAsync()).Status);
    }

    /// <summary>The tick the connection's ticker is at: a pooled connection keeps its services.</summary>
    static async Task<long> FirstTick(Client c)
    {
        await c.SendAsync("GET /api/slow/0 HTTP/1.1\r\nHost: x\r\n\r\n");
        string text = (await c.ReadAsync()).Text;
        return long.Parse(text[(text.IndexOf("\"tick\":", StringComparison.Ordinal) + 7)..text.IndexOf(",\"bound", StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task RequestBytesOutliveTheWait()
    {
        Response r = await Send("POST", "/api/slow", """{"user":{"email":"aéb","password":"p"}}""");
        Assert.Equal("""{"user":{"id":1,"token":"aéb"}}""", r.Text);
        // a required field that is empty: the struct names no reply of its own, so the app's
        Response e = await Send("POST", "/api/slow", """{"user":{"email":"","password":"p"}}""");
        Assert.Equal((422, """{"error":"invalid body"}"""), (e.Status, e.Text));
    }

    [Fact]
    public async Task PipelinedAsyncRequestsAnswerInOrder()
    {
        using Client c = await Client.ConnectAsync(_app.LocalEndPoint);
        long first = await FirstTick(c);
        var sb = new StringBuilder();
        for (int i = 1; i <= 20; i++) sb.Append($"GET /api/slow/{i} HTTP/1.1\r\nHost: x\r\n\r\nGET /api/text/w{i} HTTP/1.1\r\nHost: x\r\n\r\n");
        await c.SendAsync(sb.ToString());
        for (int i = 1; i <= 20; i++)
        {
            Assert.Equal("{\"n\":" + i + ",\"tick\":" + (first + i) + ",\"bound\":true}", (await c.ReadAsync()).Text);
            Assert.Equal($"\"w{i}\"", (await c.ReadAsync()).Text);
        }
    }

    [Fact]
    public async Task IReplyStructs()
    {
        Assert.Equal((200, "\"hé\""), Pair(await Send("GET", "/api/text/h%C3%A9")));
        Assert.Equal((201, "\"\""), Pair(await Send("GET", "/api/created-text")));
        // the ticker is the connection's (and stays with it when it is pooled): count on from where it is
        using Client c = await Client.ConnectAsync(_app.LocalEndPoint);
        await c.SendAsync("GET /api/later HTTP/1.1\r\nHost: x\r\n\r\n");
        Response later = await c.ReadAsync();
        Assert.Equal(202, later.Status);
        long tick = long.Parse(later.Text[8..^1], System.Globalization.CultureInfo.InvariantCulture);
        await c.SendAsync("GET /api/later-async HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal((202, "{\"tick\":" + (tick + 2) + "}"), Pair(await c.ReadAsync()));
    }

    static (int, string) Pair(Response r) => (r.Status, r.Text);

    [Fact]
    public async Task AThrowingHandlerIsTheConfigured500()
    {
        Assert.Equal((500, """{"errors":{"body":["internal"]}}"""), Pair(await Send("PUT", "/api/fail")));
        Assert.Equal((500, """{"errors":{"body":["internal"]}}"""), Pair(await Send("PUT", "/api/fail-later")));
        // the connection goes on
        using Client c = await Client.ConnectAsync(_app.LocalEndPoint);
        await c.SendAsync("PUT /api/fail HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\nGET /api/text/ok HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Equal(500, (await c.ReadAsync()).Status);
        Assert.Equal("\"ok\"", (await c.ReadAsync()).Text);
    }

    [Fact]
    public async Task AStaticMethodErrorReplyAndBusy()
    {
        Assert.Equal((410, """{"errors":{"body":["gone"]}}"""), Pair(await Send("GET", "/api/gone")));
        Response busy = await Send("GET", "/api/busy");
        Assert.Equal((503, """{"errors":{"body":["busy"]}}""", "3"), (busy.Status, busy.Text, busy.Header("Retry-After")));
    }

    [Fact]
    public async Task TheConnectionsOwnObjects()
    {
        Assert.Equal("""{"n":12,"cancelable":true,"pathLength":13,"sameArena":true}""", (await Send("GET", "/api/probe/12")).Text);
        Assert.Equal("""{"n":0,"tick":0,"bound":false}""", (await Send("GET", "/api")).Text);
        Assert.Equal(404, (await Send("GET", "/api/")).Status); // an empty segment
    }

    [Theory]
    [InlineData("get")]
    [InlineData("Get")]
    public async Task MethodsIgnoreCase(string method) => Assert.Equal(200, (await Send(method, "/api/text/a")).Status);

    [Fact]
    public async Task HeadRunsTheGetEndpointAndSendsOnlyItsHead()
    {
        using Client c = await Client.ConnectAsync(_app.LocalEndPoint);
        await c.SendAsync("HEAD /api/text/abc HTTP/1.1\r\nHost: x\r\n\r\nGET /api/text/abc HTTP/1.1\r\nHost: x\r\n\r\n");
        Response head = await c.ReadAsync(head: true), get = await c.ReadAsync();
        Assert.Equal((200, "\"abc\""), (get.Status, get.Text));
        // the GET's status, type and length, and no body: the GET's response parsed right after it
        Assert.Equal((200, get.Header("Content-Type"), get.Header("Content-Length")), (head.Status, head.Header("Content-Type"), head.Header("Content-Length")));
        Assert.Empty(head.Body);
        Assert.Equal(0, c.Unread);
        Assert.Equal(404, (await Send("HEAD", "/api/nothing")).Status);
        Assert.Equal(404, (await Send("head", "/api/text/abc")).Status); // "head" isn't HEAD: methods are case-sensitive, and only GET, POST, PUT and DELETE ignore case here
    }
}
