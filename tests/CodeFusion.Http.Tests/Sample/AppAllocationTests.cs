using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using CodeFusion.Http.Tests.Sample;
using Xunit;

#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>
/// After warm-up, a keep-alive request to a generated endpoint allocates nothing, whatever it binds and however it
/// replies: GC.GetTotalAllocatedBytes counts every thread in the process, the server's, the thread-pool threads that
/// complete async handlers, and the clients'. As AllocationTests does for the bare server.
/// </summary>
[Collection(Quiet.Name)]
public class AppAllocationTests(ITestOutputHelper output)
{
    static string Token(long user) => SampleAppTests.Token(user);

    static string Request(string method, string target, string? body = null, bool auth = true)
    {
        byte[] b = body is null ? [] : Encoding.UTF8.GetBytes(body);
        return $"{method} {target} HTTP/1.1\r\nHost: localhost:3000\r\nUser-Agent: k6/0.50\r\n"
            + (auth ? $"Authorization: Token {Token(12345)}\r\n" : "")
            + (b.Length > 0 ? $"Content-Type: application/json\r\nContent-Length: {b.Length}\r\n" : "")
            + "\r\n" + (body ?? "");
    }

    public static TheoryData<string, int, HttpTransport> Cases() => new()
    {
        { "route, query, viewer, singleton, JSON", 1, HttpTransport.Sockets },
        { "route, query, viewer, singleton, JSON", 8, HttpTransport.Sockets },
        { "body with escapes and tags, token issued into the arena", 1, HttpTransport.Sockets },
        { "body with escapes and tags, token issued into the arena", 8, HttpTransport.Sockets },
        { "async handler, wait ends on another thread", 1, HttpTransport.Sockets },
        { "async handler, wait ends on another thread", 8, HttpTransport.Sockets },
        { "async handler with a body", 1, HttpTransport.Sockets },
        { "reply whose write waits", 1, HttpTransport.Sockets },
        { "long route value, 204", 1, HttpTransport.Sockets },
        { "error reply", 1, HttpTransport.Sockets },
        { "no route: 404", 1, HttpTransport.Sockets },
        { "invalid token: 401", 1, HttpTransport.Sockets },
        { "route, query, viewer, singleton, JSON", 1, HttpTransport.EventLoop },
        { "route, query, viewer, singleton, JSON", 8, HttpTransport.EventLoop },
        { "body with escapes and tags, token issued into the arena", 8, HttpTransport.EventLoop },
        { "async handler, wait ends on another thread", 1, HttpTransport.EventLoop },
        { "async handler, wait ends on another thread", 8, HttpTransport.EventLoop },
        { "reply whose write waits", 1, HttpTransport.EventLoop },
        { "async handler with a body", 1, HttpTransport.EventLoop },
        { "error reply", 1, HttpTransport.EventLoop },
        { "no route: 404", 1, HttpTransport.EventLoop },
        { "invalid token: 401", 1, HttpTransport.EventLoop },
        { "feed: post with a Bearer token and a claim, 201", 1, HttpTransport.Sockets },
        { "feed: post with a Bearer token and a claim, 201", 8, HttpTransport.Sockets },
        { "feed: like after auth, 200", 1, HttpTransport.Sockets },
        { "feed: no bearer token, 401", 1, HttpTransport.Sockets },
        { "feed: invalid token, 401", 1, HttpTransport.Sockets },
        { "feed: invalid token payload, 401", 1, HttpTransport.Sockets },
        { "feed: malformed body, 400", 1, HttpTransport.Sockets },
        { "feed: no body, 400", 1, HttpTransport.Sockets },
        { "feed: invalid post id after auth, 400", 1, HttpTransport.Sockets },
        { "feed: post id out of range, 400", 1, HttpTransport.Sockets },
        { "feed: post with a Bearer token and a claim, 201", 1, HttpTransport.EventLoop },
        { "feed: post with a Bearer token and a claim, 201", 8, HttpTransport.EventLoop },
        { "feed: like after auth, 200", 1, HttpTransport.EventLoop },
        { "feed: no bearer token, 401", 1, HttpTransport.EventLoop },
        { "feed: invalid token, 401", 1, HttpTransport.EventLoop },
        { "feed: invalid token payload, 401", 1, HttpTransport.EventLoop },
        { "feed: malformed body, 400", 1, HttpTransport.EventLoop },
        { "feed: no body, 400", 1, HttpTransport.EventLoop },
        { "feed: invalid post id after auth, 400", 1, HttpTransport.EventLoop },
        { "feed: post id out of range, 400", 1, HttpTransport.EventLoop },
    };

    static readonly string FeedToken = "Bearer " + FeedAppTests.Token(user: 12345);

    static string Feed(string method, string target, string? auth, string? body)
    {
        byte[] b = body is null ? [] : Encoding.UTF8.GetBytes(body);
        return $"{method} {target} HTTP/1.1\r\nHost: localhost:3000\r\nUser-Agent: k6/0.50\r\n"
            + (auth is null ? "" : $"Authorization: {auth}\r\n")
            + (body is null ? "" : $"Content-Type: application/json\r\nContent-Length: {b.Length}\r\n")
            + "\r\n" + (body ?? "");
    }

    static string RequestFor(string name) => name switch
    {
        "feed: post with a Bearer token and a claim, 201" => Feed("POST", "/posts", FeedToken, """{"body":"  hello \"world\" é "}"""),
        "feed: like after auth, 200" => Feed("POST", "/posts/7/like", FeedToken, ""),
        "feed: no bearer token, 401" => Feed("POST", "/posts", "Token " + FeedToken[7..], """{"body":"x"}"""),
        "feed: invalid token, 401" => Feed("POST", "/posts", FeedToken[..^4] + "AAAA", """{"body":"x"}"""),
        "feed: invalid token payload, 401" => Feed("POST", "/posts", "Bearer " + JwtClaimTests.Signed(System.Text.Encoding.UTF8.GetBytes($$"""{"sub":"0","username":"jake","exp":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600}}}"""), secret: FeedApp.Secret), """{"body":"x"}"""),
        "feed: malformed body, 400" => Feed("POST", "/posts", FeedToken, """{"body":"x"""),
        "feed: no body, 400" => Feed("POST", "/posts", FeedToken, ""),
        "feed: invalid post id after auth, 400" => Feed("POST", "/posts/abc/like", FeedToken, ""),
        "feed: post id out of range, 400" => Feed("GET", "/posts/0", null, null),
        "route, query, viewer, singleton, JSON" => Request("GET", "/api/articles/how-to-train-your-dragon?tag=dragons&by=jake+o%27&limit=7"),
        "body with escapes and tags, token issued into the arena" => Request("POST", "/api/articles",
            """{"article":{"title":"How to \"train\" your dragon","description":"Ever wonder how?","tagList":["dragons","training","é"],"meta":{"pinned":true,"score":2.5,"rank":3},"count":12}}"""),
        "async handler, wait ends on another thread" => Request("GET", "/api/wait/42"),
        "async handler with a body" => Request("POST", "/api/slow", """{"user":{"email":"jake@jake.jake","password":"jakejake"}}""", auth: false),
        "reply whose write waits" => Request("GET", "/api/written-later"),
        "long route value, 204" => Request("DELETE", "/api/articles/some-slug/comments/1234"),
        "error reply" => Request("DELETE", "/api/articles/some-slug/comments/1235"),
        "no route: 404" => Request("GET", "/api/nothing/here"),
        _ => Request("GET", "/api/articles/a").Replace("Token ey", "Token xy", StringComparison.Ordinal),
    };

    [Theory(Timeout = 300_000)] // a loaded machine stretches each connection's 190,000 requests to minutes
    [MemberData(nameof(Cases))]
    public async Task KeepAliveRequestsAllocateNothing(string name, int connections, HttpTransport transport)
    {
        Assert.SkipWhen(AllocationTests.Debugger(), "Debug builds make every async method's state machine a class: run in Release");
        Action<HttpAppBuilder> configure = b =>
        {
            TestTransport.UseWithHook(b.Options, transport);
            // an app's own batch actions are called per wake-up too: with nothing allocated
            if (transport == HttpTransport.EventLoop)
            {
                b.OnEventLoopBatch(static _ => { });
                b.OnEventLoopBatch(static _ => { });
            }
        };
        await using HttpApp app = name.StartsWith("feed:", StringComparison.Ordinal) ? FeedApp.Build(configure) : SampleApp.Build(configure);
        await using var check = new AppCheck(app);
        app.Start();
        byte[] request = Encoding.UTF8.GetBytes(RequestFor(name));
        var sockets = new Socket[connections];
        var responseLength = new int[connections];
        for (int i = 0; i < connections; i++)
        {
            sockets[i] = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            sockets[i].Connect(app.LocalEndPoint);
            if (name.StartsWith("feed: like", StringComparison.Ordinal)) AllocationTests.FirstResponseLength(sockets[i], request); // the first like is a 201, the rest 200s
            responseLength[i] = AllocationTests.FirstResponseLength(sockets[i], request);
        }

        try
        {
            AllocationTests.AssertKeepAliveAllocatesNothing(output, $"{name}, {connections} connection(s), {transport}, ", sockets, request, responseLength);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }
}
