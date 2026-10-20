using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Xunit;

namespace CodeFusion.Http.Tests;

// The CS0649s: the binder no longer sets these members, which is what the tests check (and why the project lets
// CFH0109 pass).
#pragma warning disable CS0649

/// <summary>A body with members a client must not reach (over-posting), and numbers with [Range].</summary>
public partial struct Signup
{
    public Utf8 Name;
    internal bool IsAdmin;
    public long Role { get; private set; }
    public long Level { get; internal set; }
    long _id;
    public readonly long Id => _id;
    static ErrorReply Invalid => BinderRuleTests.Bad;
}

public partial struct Rated
{
    [Range(1, 100)] public int Age;
    [Range(0.0, 1.0, MinimumIsExclusive = true)] public double Ratio;
    [Range(1, 5)] public long? Stars;
    [Range(typeof(long), "-10", "10", MaximumIsExclusive = true)] public long Delta;
    static ErrorReply Invalid => BinderRuleTests.Bad;
}

[FromQuery]
public partial struct Search
{
    public Utf8 Tag;
    internal Utf8 Admin;
    [Range(typeof(long), "1", "50")] public long? Limit;
    static ErrorReply Invalid => BinderRuleTests.Bad;
}

#pragma warning restore CS0649

/// <summary>
/// What the generated binders take from a request: only members a public caller could set, and numbers inside their
/// [Range], whichever form the attribute takes.
/// </summary>
public class BinderRuleTests
{
    public static readonly ErrorReply Bad = new(422, "{}"u8);
    static readonly ErrorReply Structural = new(400, "{}"u8);

    /// <summary>The Map calls that make the generator write the binders; also run, to see each register.</summary>
    static HttpApp Routes()
    {
        var builder = HttpApp.CreateBuilder([]);
        builder.Options.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
        HttpApp app = builder.Build();
        IEndpointRouteBuilder? maybe = app;
        app.MapPost("/signup", static (Signup s) => Reply.NoContent());
        app.MapPost("/rated", static (Rated r) => Reply.NoContent());
        maybe?.MapGet("/search", static (Search q) => Reply.NoContent());
        return app;
    }

    [Fact]
    public void AConditionalMapCallRegistersToo()
    {
        HttpApp app = Routes();
        var values = new int[2];
        foreach (string path in new[] { "/signup", "/rated" })
            Assert.NotNull(app.Router.Match(Router.Verb(RequestMethod.Post, "POST"u8), Encoding.UTF8.GetBytes(path), values));
        Assert.NotNull(app.Router.Match(Router.Verb(RequestMethod.Get, "GET"u8), "/search"u8, values));
    }

    static T Body<T>(string json, BodyBinder<T> bind, out ErrorReply? error)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var arena = new RequestArena(new BufferPool(4096, 16));
        try
        {
            error = bind(new BodySource(bytes, 0, bytes.Length, arena, Structural), out T value);
            return value;
        }
        finally
        {
            arena.Reset();
        }
    }

    delegate ErrorReply? BodyBinder<T>(in BodySource source, out T value);

    [Fact]
    public void ABodySetsOnlyPublicMembers()
    {
        Signup s = Body<Signup>("""{"name":"ann","isAdmin":true,"role":3,"level":4,"_id":5,"id":6}""", Signup.__BindBody, out ErrorReply? e);
        Assert.Null(e);
        Assert.Equal("ann", s.Name.ToString());
        Assert.False(s.IsAdmin);
        Assert.Equal(0, s.Role);
        Assert.Equal(0, s.Level);
        Assert.Equal(0, s.Id);
    }

    [Theory]
    [InlineData("""{"age":1,"ratio":0.5,"delta":-10}""", "ok")]
    [InlineData("""{"age":100,"ratio":1.0,"delta":9,"stars":5}""", "ok")]
    [InlineData("""{"age":50,"ratio":1e-300,"delta":0,"stars":null}""", "ok")]
    [InlineData("""{"age":0,"ratio":0.5,"delta":0}""", "bad")]
    [InlineData("""{"age":101,"ratio":0.5,"delta":0}""", "bad")]
    [InlineData("""{"age":-1,"ratio":0.5,"delta":0}""", "bad")]
    [InlineData("""{"age":50,"ratio":0,"delta":0}""", "bad")]
    [InlineData("""{"age":50,"ratio":-0.5,"delta":0}""", "bad")]
    [InlineData("""{"age":50,"ratio":1.0000001,"delta":0}""", "bad")]
    [InlineData("""{"age":50,"ratio":0.5,"delta":0,"stars":0}""", "bad")]
    [InlineData("""{"age":50,"ratio":0.5,"delta":0,"stars":6}""", "bad")]
    [InlineData("""{"age":50,"ratio":0.5,"delta":10}""", "bad")]
    [InlineData("""{"age":50,"ratio":0.5,"delta":-11}""", "bad")]
    public void BodyNumbersStayInTheirRange(string json, string outcome)
    {
        Rated r = Body<Rated>(json, Rated.__BindBody, out ErrorReply? e);
        Assert.Equal(outcome, e is null ? "ok" : ReferenceEquals(e, Bad) ? "bad" : "other");
        if (e is null) Assert.InRange(r.Age, 1, 100);
    }

    static ErrorReply? Query(string q, out Search value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(q);
        var arena = new RequestArena(new BufferPool(4096, 16));
        try
        {
            return Search.__BindQuery(new QuerySource(bytes, 0, bytes.Length, arena), out value);
        }
        finally
        {
            arena.Reset();
        }
    }

    [Fact]
    public void AQuerySetsOnlyPublicMembers()
    {
        Assert.Null(Query("tag=a&admin=1", out Search s));
        Assert.Equal("a", s.Tag.ToString());
        Assert.True(s.Admin.IsEmpty);
    }

    [Theory]
    [InlineData("limit=1", "1")]
    [InlineData("limit=50", "50")]
    [InlineData("", "null")]
    [InlineData("limit=0", "bad")]
    [InlineData("limit=51", "bad")]
    public void ATypedRangeBoundsTheQuery(string q, string outcome)
    {
        ErrorReply? e = Query(q, out Search s);
        Assert.Equal(outcome, e is not null ? (ReferenceEquals(e, Bad) ? "bad" : "other") : s.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");
    }
}
