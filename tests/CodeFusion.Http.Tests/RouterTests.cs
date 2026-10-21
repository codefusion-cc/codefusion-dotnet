using System;
using System.Text;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>Templates and the router on their own: parsing, joining groups, precedence and the limits.</summary>
public class RouterTests
{
    sealed class Marker(string name) : Endpoint
    {
        public string Name { get; } = name;
        public override void Initialize(EndpointSetup setup) { }
        public override System.Threading.Tasks.ValueTask Invoke(RequestContext context) => default;
    }

    [Theory]
    [InlineData("articles", "it must start with '/'")]
    [InlineData("/a//b", "a segment is empty")]
    [InlineData("/a/", "a segment is empty")]
    [InlineData("/a/{b", "is not a parameter")]
    [InlineData("/a/{}", "is not a parameter")]
    [InlineData("/a/{1b}", "is not a parameter")]
    [InlineData("/a/{b-c}", "is not a parameter")]
    [InlineData("/a/x{b}", "mixes a literal and a parameter")]
    [InlineData("/a/b}", "mixes a literal and a parameter")]
    [InlineData("/{a}/{A}", "appears twice")]
    public void BadTemplates(string pattern, string why)
    {
        var e = Assert.Throws<ArgumentException>(() => RouteTemplate.Parse(pattern));
        Assert.Contains(why, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesJoinUnderPrefixes()
    {
        RouteTemplate t = RouteTemplate.Parse("/comments/{id}").Under(RouteTemplate.Parse("/articles/{slug}"));
        Assert.Equal("/articles/{slug}/comments/{id}", t.Text);
        Assert.Equal(["slug", "id"], t.Parameters);
        Assert.Equal(1, t.IndexOf("ID"));
        Assert.Equal(-1, t.IndexOf("x"));
        Assert.Same(RouteTemplate.Root, RouteTemplate.Parse(""));
        Assert.Equal("/a", RouteTemplate.Parse("/").Under(RouteTemplate.Parse("/a")).Text);
        Assert.Throws<ArgumentException>(() => RouteTemplate.Parse("/{slug}").Under(RouteTemplate.Parse("/{Slug}")));
    }

    static (Router Router, Marker[] Endpoints) Build(params (int Verb, string Pattern)[] routes)
    {
        var router = new Router();
        var endpoints = new Marker[routes.Length];
        for (int i = 0; i < routes.Length; i++)
            router.Add(routes[i].Verb, RouteTemplate.Parse(routes[i].Pattern), endpoints[i] = new Marker(routes[i].Pattern));
        return (router, endpoints);
    }

    static string Match(Router router, int verb, string path)
    {
        byte[] p = Encoding.UTF8.GetBytes(path);
        var values = new int[2 * Math.Max(1, router.MaxParameters)];
        if (router.Match(verb, p, values) is not Marker m) return "none";
        var sb = new StringBuilder(m.Name);
        for (int i = 0; i < m.Name.Split('{').Length - 1; i++) sb.Append(' ').Append(Encoding.UTF8.GetString(p, values[2 * i], values[2 * i + 1]));
        return sb.ToString();
    }

    [Fact]
    public void LiteralsWinOverParametersAndBacktrack()
    {
        var (r, _) = Build((Router.Get, "/a/{x}/c"), (Router.Get, "/a/b/{y}"), (Router.Put, "/a/b"), (Router.Get, "/a/{x}"));
        Assert.Equal("/a/b/{y} c", Match(r, Router.Get, "/a/b/c"));      // the literal first, though both match
        Assert.Equal("/a/{x}/c q", Match(r, Router.Get, "/a/q/c"));
        Assert.Equal("/a/{x} b", Match(r, Router.Get, "/a/b"));          // the literal /a/b has only PUT
        Assert.Equal("/a/b", Match(r, Router.Put, "/a/b"));
        Assert.Equal("none", Match(r, Router.Post, "/a/b"));
        Assert.Equal("/a/b/{y} d", Match(r, Router.Get, "/a/b/d"));
        Assert.Equal("none", Match(r, Router.Get, "/a/q/d"));
    }

    [Fact]
    public void SegmentsAreExactBytes()
    {
        var (r, _) = Build((Router.Get, "/é/{x}"), (Router.Get, "/Feed"));
        Assert.Equal("/é/{x} %2F", Match(r, Router.Get, "/é/%2F"));
        Assert.Equal("none", Match(r, Router.Get, "/feed"));
        Assert.Equal("none", Match(r, Router.Get, "/É/x"));
        Assert.Equal("none", Match(r, Router.Get, "/é//"));
        Assert.Equal("none", Match(r, Router.Get, "/é/x/"));
        Assert.Equal("none", Match(r, Router.Get, "/é/x/y/z/w/v"));       // deeper than any route
        Assert.Equal("none", Match(r, Router.Get, "é/x"));
        Assert.Equal("none", Match(r, Router.Get, ""));
        Assert.Equal("none", Match(r, -1, "/Feed"));
    }

    [Fact]
    public void TheRoot()
    {
        var (r, _) = Build((Router.Get, "/"));
        Assert.Equal("/", Match(r, Router.Get, "/"));
        Assert.Equal("none", Match(r, Router.Get, "//"));
        Assert.Equal("none", Match(r, Router.Get, "/x"));
        var (empty, _) = Build();
        Assert.Equal("none", Match(empty, Router.Get, "/"));
        Assert.Equal("none", Match(empty, Router.Get, "/x"));
    }

    [Fact]
    public void ARouteMappedTwiceIsRefused()
    {
        var router = new Router();
        router.Add(Router.Get, RouteTemplate.Parse("/a/{x}"), new Marker("1"));
        router.Add(Router.Put, RouteTemplate.Parse("/a/{y}"), new Marker("2"));
        var e = Assert.Throws<InvalidOperationException>(() => router.Add(Router.Get, RouteTemplate.Parse("/a/{y}"), new Marker("3")));
        Assert.Contains("GET /a/{y} is mapped twice", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RequestMethod.Get, "GET", Router.Get)]
    [InlineData(RequestMethod.Other, "get", Router.Get)]
    [InlineData(RequestMethod.Other, "pOsT", Router.Post)]
    [InlineData(RequestMethod.Other, "Put", Router.Put)]
    [InlineData(RequestMethod.Other, "delete", Router.Delete)]
    [InlineData(RequestMethod.Head, "HEAD", Router.Get)] // HEAD runs the GET endpoint; the server sends no body
    [InlineData(RequestMethod.Other, "head", -1)]
    [InlineData(RequestMethod.Patch, "PATCH", -1)]
    [InlineData(RequestMethod.Other, "GETS", -1)]
    [InlineData(RequestMethod.Other, "", -1)]
    public void MethodsIgnoreAsciiCase(RequestMethod parsed, string text, int verb) =>
        Assert.Equal(verb, Router.Verb(parsed, Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void MatchingAllocatesNothing()
    {
        var (r, _) = Build((Router.Get, "/api/articles/{slug}/comments/{id}"), (Router.Get, "/api/articles/feed"), (Router.Get, "/api/articles/{slug}"));
        byte[] path = "/api/articles/how-to-train/comments/12"u8.ToArray();
        var values = new int[4];
        for (int i = 0; i < 100; i++) r.Match(Router.Get, path, values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) r.Match(Router.Get, path, values);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal([14, 12, 36, 2], values);
    }
}
