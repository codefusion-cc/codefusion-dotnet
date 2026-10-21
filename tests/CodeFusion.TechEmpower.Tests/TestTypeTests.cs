using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.TechEmpower.Tests;

/// <summary>
/// Each TFB test type as TFB's requirements and verifier (toolset/test_types) state it: the headers every response
/// needs, the bodies, the queries parameter's clamping, the fortunes page exactly, updates that persist, nothing
/// cached.
/// </summary>
[Collection(nameof(TfbTests))]
public sealed class TestTypeTests(TfbApp app)
{
    async Task<HttpResponseMessage> Get(string path)
    {
        TfbApp.Require();
        return await app.Http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
    }

    static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    /// <summary>What every TFB response needs: Server, a Date that parses, and a length.</summary>
    static void HasTfbHeaders(HttpResponseMessage r)
    {
        Assert.Equal("CodeFusion", string.Join(",", r.Headers.GetValues("Server")));
        Assert.True(r.Headers.Date is { } date && Math.Abs((date - DateTimeOffset.UtcNow).TotalSeconds) < 5, "Date is now");
        Assert.NotNull(r.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task PlaintextIsHelloWorld()
    {
        using var r = await Get("/plaintext");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        HasTfbHeaders(r);
        Assert.Equal("text/plain", r.Content.Headers.ContentType!.ToString());
        Assert.Equal(13, r.Content.Headers.ContentLength);
        Assert.Equal("Hello, World!", await Body(r));
    }

    [Fact]
    public async Task SixteenPipelinedPlaintextRequestsAreAnsweredInOrder()
    {
        TfbApp.Require();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(app.EndPoint);
        // as TFB's pipeline.lua sends them: sixteen requests in one write
        string one = "GET /plaintext HTTP/1.1\r\nHost: tfb-server\r\nAccept: text/plain,text/html;q=0.9,application/xhtml+xml;q=0.9,application/xml;q=0.8,*/*;q=0.7\r\nConnection: keep-alive\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(one, 16))));
        var received = new StringBuilder();
        var buffer = new byte[64 << 10];
        while (Count(received.ToString(), "Hello, World!") < 16)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(0, n);
            received.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        string all = received.ToString();
        Assert.Equal(16, Count(all, "HTTP/1.1 200 OK\r\n"));
        Assert.Equal(16, Count(all, "Server: CodeFusion\r\n"));
        Assert.Equal(16, Count(all, "\r\n\r\nHello, World!"));
    }

    static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public async Task JsonIsTheMessageObject()
    {
        using var r = await Get("/json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        HasTfbHeaders(r);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("""{"message":"Hello, World!"}""", await Body(r));
    }

    /// <summary>A World row's id and randomNumber, and nothing else.</summary>
    static (int Id, int RandomNumber) World(JsonElement e)
    {
        Assert.Equal(JsonValueKind.Object, e.ValueKind);
        Assert.Equal(["id", "randomNumber"], e.EnumerateObject().Select(p => p.Name));
        int id = e.GetProperty("id").GetInt32();
        Assert.InRange(id, 1, 10000);
        return (id, e.GetProperty("randomNumber").GetInt32());
    }

    [Fact]
    public async Task DbIsOneRandomRowAsTheDatabaseHasIt()
    {
        var ids = new HashSet<int>();
        for (int i = 0; i < 40; i++)
        {
            using var r = await Get("/db");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            HasTfbHeaders(r);
            Assert.Equal("application/json", r.Content.Headers.ContentType!.ToString());
            using var doc = JsonDocument.Parse(await Body(r));
            var (id, number) = World(doc.RootElement);
            Assert.Equal(await app.RandomNumberOf(id), number);
            ids.Add(id);
        }
        Assert.True(ids.Count > 30, $"random ids: {ids.Count} of 40 distinct");
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("foo", 1)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("1.5", 1)]
    [InlineData("2", 2)]
    [InlineData("%32%30", 20)]
    [InlineData("20", 20)]
    [InlineData("500", 500)]
    [InlineData("501", 500)]
    [InlineData("99999999999999999999999", 500)]
    public async Task QueriesClampTheCountAndReadEachRow(string? queries, int expected)
    {
        using var r = await Get(queries is null ? "/queries" : "/queries?queries=" + queries);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        HasTfbHeaders(r);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.ToString());
        using var doc = JsonDocument.Parse(await Body(r));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(expected, doc.RootElement.GetArrayLength());
        foreach (var e in doc.RootElement.EnumerateArray().Take(25))
        {
            var (id, number) = World(e);
            Assert.Equal(await app.RandomNumberOf(id), number);
        }
    }

    [Fact]
    public async Task NothingIsCachedAChangedRowIsReadAgain()
    {
        TfbApp.Require();
        foreach (string set in (string[])["id", "10001 - id"])
        {
            await TfbApp.Exec(app.Db, $"UPDATE world SET randomnumber = {set}");
            using var r = await Get("/queries?queries=20");
            using var doc = JsonDocument.Parse(await Body(r));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var (id, number) = World(e);
                Assert.Equal(set == "id" ? id : 10001 - id, number);
            }
            using var one = await Get("/db");
            using var row = JsonDocument.Parse(await Body(one));
            var (oneId, oneNumber) = World(row.RootElement);
            Assert.Equal(set == "id" ? oneId : 10001 - oneId, oneNumber);
        }
    }

    /// <summary>TFB's fortune_html_parser.py's valid_fortune, its line breaks taken out as its parser takes them.</summary>
    const string ValidFortunes =
        "<!doctype html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr>" +
        "<tr><td>11</td><td>&lt;script&gt;alert(&quot;This should not be displayed in a browser alert box.&quot;);&lt;/script&gt;</td></tr>" +
        "<tr><td>4</td><td>A bad random number generator: 1, 1, 1, 1, 1, 4.33e+67, 1, 1, 1</td></tr>" +
        "<tr><td>5</td><td>A computer program does what you tell it to do, not what you want it to do.</td></tr>" +
        "<tr><td>2</td><td>A computer scientist is someone who fixes things that aren&apos;t broken.</td></tr>" +
        "<tr><td>8</td><td>A list is only as strong as its weakest link. — Donald Knuth</td></tr>" +
        "<tr><td>0</td><td>Additional fortune added at request time.</td></tr>" +
        "<tr><td>3</td><td>After enough decimal places, nobody gives a damn.</td></tr>" +
        "<tr><td>7</td><td>Any program that runs right is obsolete.</td></tr>" +
        "<tr><td>10</td><td>Computers make very fast, very accurate mistakes.</td></tr>" +
        "<tr><td>6</td><td>Emacs is a nice operating system, but I prefer UNIX. — Tom Christaensen</td></tr>" +
        "<tr><td>9</td><td>Feature: A bug with seniority.</td></tr>" +
        "<tr><td>1</td><td>fortune: No such file or directory</td></tr>" +
        "<tr><td>12</td><td>フレームワークのベンチマーク</td></tr>" +
        "</table></body></html>";

    /// <summary>What TFB's parser does before it compares: the doctype lowercased, the escapes it accepts as
    /// equivalents made one.</summary>
    static string Normalized(string html) => html
        .Replace("<!DOCTYPE html>", "<!doctype html>", StringComparison.Ordinal)
        .Replace("&#39;", "&apos;", StringComparison.Ordinal).Replace("&#x27;", "&apos;", StringComparison.Ordinal)
        .Replace("&#34;", "&quot;", StringComparison.Ordinal).Replace("&#x22;", "&quot;", StringComparison.Ordinal)
        .Replace("&#43;", "+", StringComparison.Ordinal).Replace("&#x2B;", "+", StringComparison.Ordinal);

    [Fact]
    public async Task FortunesIsTheSortedEscapedPage()
    {
        using var r = await Get("/fortunes");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        HasTfbHeaders(r);
        Assert.Equal("text/html; charset=utf-8", r.Content.Headers.ContentType!.ToString());
        byte[] bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.Equal(ValidFortunes, Normalized(Encoding.UTF8.GetString(bytes)));
        Assert.Equal(bytes.Length, r.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task AFortuneAddedNowIsOnThePageEscapedAndInItsPlace()
    {
        TfbApp.Require();
        // every character HTML gives a meaning, a long one, and one sorting first and one last in code point order
        string tricky = "\"quoted\" & <b>bold</b> 'single'" + new string('x', 2000);
        await TfbApp.Exec(app.Db, $"INSERT INTO fortune VALUES (13, '{tricky.Replace("'", "''", StringComparison.Ordinal)}'), (14, ' leading space'), (15, '\U0001F600 emoji')");
        try
        {
            using var r = await Get("/fortunes");
            string page = await Body(r);
            string escaped = "&quot;quoted&quot; &amp; &lt;b&gt;bold&lt;/b&gt; &#39;single&#39;" + new string('x', 2000);
            Assert.Contains($"<tr><td>13</td><td>{escaped}</td></tr>", page, StringComparison.Ordinal);
            Assert.StartsWith("<!DOCTYPE html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr><tr><td>14</td><td> leading space</td></tr><tr><td>13</td>", page, StringComparison.Ordinal);
            Assert.EndsWith("<tr><td>12</td><td>フレームワークのベンチマーク</td></tr><tr><td>15</td><td>\U0001F600 emoji</td></tr></table></body></html>", page, StringComparison.Ordinal);
        }
        finally
        {
            await TfbApp.Exec(app.Db, "DELETE FROM fortune WHERE id > 12");
        }
        using var again = await Get("/fortunes");
        Assert.Equal(ValidFortunes, Normalized(await Body(again)));
    }

    [Fact]
    public async Task ALongTableIsSortedToo()
    {
        TfbApp.Require();
        // 40 more rows in a scrambled order: past the short-list sort, into the long one
        var messages = Enumerable.Range(0, 40).Select(i => $"m{(i * 17) % 40:D2} {(char)('a' + i % 26)}").ToArray();
        await TfbApp.Exec(app.Db, "INSERT INTO fortune VALUES " + string.Join(", ", messages.Select((m, i) => $"({100 + i}, '{m}')")));
        try
        {
            string page = await Body(await Get("/fortunes"));
            var cells = System.Text.RegularExpressions.Regex.Matches(page, "<tr><td>([0-9]+)</td><td>([^<]*)</td></tr>")
                .Select(m => (Id: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Message: m.Groups[2].Value)).ToArray();
            Assert.Equal(53, cells.Length);
            var sorted = cells.Select(c => c.Message).OrderBy(m => Encoding.UTF8.GetBytes(System.Net.WebUtility.HtmlDecode(m)), new BytesOrder()).ToArray();
            Assert.Equal(sorted, cells.Select(c => c.Message).ToArray());
            Assert.Equal(Enumerable.Range(0, 13).Concat(Enumerable.Range(100, 40)).Order(), cells.Select(c => c.Id).Order());
        }
        finally
        {
            await TfbApp.Exec(app.Db, "DELETE FROM fortune WHERE id > 12");
        }
    }

    sealed class BytesOrder : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("foo", 1)]
    [InlineData("5", 5)]
    [InlineData("20", 20)]
    [InlineData("501", 500)]
    public async Task UpdatesWriteEachRowsNewNumberAndCommitIt(string queries, int expected)
    {
        TfbApp.Require();
        await TfbApp.Exec(app.Db, "UPDATE world SET randomnumber = 0");
        using var r = await Get("/updates?queries=" + queries);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        HasTfbHeaders(r);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.ToString());
        using var doc = JsonDocument.Parse(await Body(r));
        Assert.Equal(expected, doc.RootElement.GetArrayLength());
        var last = new Dictionary<int, int>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var (id, number) = World(e);
            Assert.InRange(number, 1, 10000);
            last[id] = number; // a request may draw an id twice: its last number is the one kept
        }
        foreach (var (id, number) in last) Assert.Equal(number, await app.RandomNumberOf(id));
        // every row the reply names, and no other, was written: the rest still hold the 0 set above
        await using var cmd = app.Db.CreateCommand("SELECT count(*) FROM world WHERE randomnumber <> 0");
        Assert.Equal((long)last.Count, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ManyConcurrentUpdatesOfTheSameRowsAllCommit()
    {
        TfbApp.Require();
        // 64 requests of 20 rows at once, over 10,000 rows: they overlap, in one group commit or several
        var requests = Enumerable.Range(0, 64).Select(_ => Get("/updates?queries=20")).ToArray();
        foreach (var r in await Task.WhenAll(requests))
        {
            using (r)
            {
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                using var doc = JsonDocument.Parse(await Body(r));
                Assert.Equal(20, doc.RootElement.GetArrayLength());
            }
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/nothing")]
    [InlineData("/plaintext/more")]
    public async Task AnUnknownPathIs404WithTheHeaders(string path)
    {
        using var r = await Get(path);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("CodeFusion", string.Join(",", r.Headers.GetValues("Server")));
    }

    [Fact]
    public async Task WithTheDatabaseGoneTheDatabaseTestsFailFastAndTheOthersGoOn()
    {
        TfbApp.Require();
        // a port nothing listens on
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var ep = await app.StartAsync($"postgres://nobody:nothing@127.0.0.1:{port}/none");
        using var http = new HttpClient { BaseAddress = new Uri($"http://{ep}"), Timeout = TimeSpan.FromSeconds(10) };
        using var db = await http.GetAsync(new Uri("/db", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.True((int)db.StatusCode >= 500, $"/db without a database: {(int)db.StatusCode}");
        using var fortunes = await http.GetAsync(new Uri("/fortunes", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.True((int)fortunes.StatusCode >= 500, $"/fortunes without a database: {(int)fortunes.StatusCode}");
        using var text = await http.GetAsync(new Uri("/plaintext", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("Hello, World!", await Body(text));
    }
}
