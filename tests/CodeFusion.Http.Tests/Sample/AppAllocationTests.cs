using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    public static TheoryData<string, int> Cases() => new()
    {
        { "route, query, viewer, singleton, JSON", 1 },
        { "route, query, viewer, singleton, JSON", 8 },
        { "body with escapes and tags, token issued into the arena", 1 },
        { "body with escapes and tags, token issued into the arena", 8 },
        { "async handler, wait ends on another thread", 1 },
        { "async handler, wait ends on another thread", 8 },
        { "async handler with a body", 1 },
        { "reply whose write waits", 1 },
        { "long route value, 204", 1 },
        { "error reply", 1 },
        { "no route: 404", 1 },
        { "invalid token: 401", 1 },
    };

    static string RequestFor(string name) => name switch
    {
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

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Cases))]
    public async Task KeepAliveRequestsAllocateNothing(string name, int connections)
    {
        Assert.SkipWhen(AllocationTests.Debugger(), "Debug builds make every async method's state machine a class: run in Release");
        await using HttpApp app = SampleApp.Build();
        await using var check = new AppCheck(app);
        app.Start();
        byte[] request = Encoding.UTF8.GetBytes(RequestFor(name));
        var sockets = new Socket[connections];
        var responseLength = new int[connections];
        for (int i = 0; i < connections; i++)
        {
            sockets[i] = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            sockets[i].Connect(app.LocalEndPoint);
            responseLength[i] = AllocationTests.FirstResponseLength(sockets[i], request);
        }

        const int warm = 20_000, measured = 50_000;
        AllocationTests.RunClients(sockets, request, responseLength, warm).Go();
        Thread.Sleep(200); // let tiered compilation's background work settle
        AllocationTests.RunClients(sockets, request, responseLength, warm).Go();
        // up to three windows, keeping the least: something the requests allocate shows in every window, while a
        // one-time growth of the runtime's own buffers (the socket engine's event array, seen once on CI) shows in one
        double perRequest = double.MaxValue;
        for (int window = 0; window < 3 && perRequest >= 0.5; window++)
        {
            var clients = AllocationTests.RunClients(sockets, request, responseLength, measured);
            GC.Collect();
            using var ticks = new AllocationTests.AllocationTicks();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            clients.Go();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            ticks.Dispose();
            double inWindow = (double)allocated / (measured * connections);
            output.WriteLine($"{name}, {connections} connection(s), window {window + 1}: {allocated} bytes over {measured * connections} requests: {inWindow:F3} B/request");
            foreach (var g in ticks.Types.GroupBy(t => t).OrderByDescending(g => g.Count())) output.WriteLine($"  {g.Count(),4} samples: {g.Key}");
            perRequest = Math.Min(perRequest, inWindow);
        }
        foreach (var s in sockets) s.Dispose();
        Assert.True(perRequest < 0.5, $"{perRequest:F3} bytes per request");
    }
}
