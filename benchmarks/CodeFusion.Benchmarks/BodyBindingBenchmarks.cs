using System;
using System.Text;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using CodeFusion.Http;

namespace CodeFusion.Benchmarks;

/// <summary>
/// The generated body binders alone, over a request buffer: a small body (Login), a larger one with escapes and a
/// list (Article), and a 2 KB order (Order: numbers of three kinds, a bool, a list, two nested objects, unknown keys
/// holding an object and arrays to skip, a 1.6 KB note with escapes). Each valid, with a bad field, malformed at its
/// end (cut short), and malformed at its second byte. Nothing but the binder and the arena it
/// unescapes into is timed. It uses only what every version of the binder had, so the same file benchmarks each.
/// </summary>
public class BodyBindingBenchmarks
{
    [Params("valid", "bad field", "malformed", "malformed early")]
    public string Kind = "valid";

    static readonly ErrorReply Invalid = new(422, """{"error":"invalid body"}"""u8);
    byte[] _login = [], _article = [], _order = [];
    RequestArena _arena = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arena = new RequestArena(new BufferPool(4096, 16));
        (string login, string article) = Kind switch
        {
            "valid" => ("""{"user":{"email":"jake@jake.jake","password":"jakejake"}}""",
                """{"article":{"title":"How to \"train\" your dragon","description":"Ever wonder how?","body":"You have to believe, and then some more text to make it a body","tagList":["dragons","training","é"]}}"""),
            "bad field" => ("""{"user":{"email":"jake@jake.jake","password":5}}""",
                """{"article":{"title":"How to \"train\" your dragon","description":"Ever wonder how?","body":7,"tagList":["dragons","training","é"]}}"""),
            _ => ("""{"user":{"email":"jake@jake.jake","password":"jake""",
                """{"article":{"title":"How to \"train\" your dragon","description":"Ever wonder how?","body":"You have to believe","tagList":["dragons","train"""),
        };
        string order = Order(Kind == "bad field");
        if (Kind == "malformed") order = order[..^3];
        if (Kind == "malformed early")
        {
            login = "{x" + login[2..];
            article = "{x" + article[2..];
            order = "{x" + order[2..];
        }
        _login = Framed(login);
        _article = Framed(article);
        _order = Framed(order);
        // each body must do what its kind says, or the numbers measure something else
        bool binds = Kind == "valid";
        if (Login() != binds || Article() != binds || Order() != binds) throw new InvalidOperationException($"a {Kind} body didn't bind as its kind says");
    }

    // the body after a request head's worth of other bytes, as it lies in a request buffer
    static byte[] Framed(string body) => [.. new byte[64], .. Encoding.UTF8.GetBytes(body)];

    [Benchmark]
    public bool Login()
    {
        bool ok = BoundLogin.__BindBody(new BodySource(_login, 64, _login.Length - 64, _arena, Invalid), out _) is null;
        _arena.Reset();
        return ok;
    }

    [Benchmark]
    public bool Article()
    {
        bool ok = BoundArticle.__BindBody(new BodySource(_article, 64, _article.Length - 64, _arena, Invalid), out _) is null;
        _arena.Reset();
        return ok;
    }


    /// <summary>About 2 KB: every kind of field, nested objects, unknown keys to skip, and a long note.</summary>
    static string Order(bool badPrice)
    {
        var note = new StringBuilder();
        while (note.Length < 1600) note.Append("Leave it with the neighbour at number 12, \\\"the blue door\\\", après 18h. ");
        return "{\"order\":{\"id\":\"A-1001\",\"quantity\":3,\"priority\":2,\"price\":" + (badPrice ? "\"19.99\"" : "19.99")
            + ",\"paid\":true,\"tags\":[\"red\",\"large\",\"gift\"],"
            + "\"address\":{\"street\":\"1 Main St\",\"city\":\"Springfield\",\"zip\":12345},"
            + "\"meta\":{\"source\":\"web\",\"ab\":[1,2,3],\"flags\":{\"x\":true,\"y\":null}},\"seen\":[[1,2],[3]],"
            + "\"billing\":{\"street\":\"2 Elm St\",\"city\":\"Shelbyville\",\"zip\":54321},"
            + "\"note\":\"" + note + "\"}}";
    }

    [Benchmark]
    public bool Order()
    {
        bool ok = BoundOrder.__BindBody(new BodySource(_order, 64, _order.Length - 64, _arena, Invalid), out _) is null;
        _arena.Reset();
        return ok;
    }

    /// <summary>Never called: the Map calls make the generator write the binders.</summary>
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/bench/login", static (BoundLogin body) => Reply.NoContent());
        api.MapPost("/bench/article", static (BoundArticle body) => Reply.NoContent());
        api.MapPost("/bench/order", static (BoundOrder body) => Reply.NoContent());
    }
}

[JsonEnvelope("user")]
public partial struct BoundLogin
{
    public Utf8 Email;
    public Utf8 Password;
}

[JsonEnvelope("article")]
public partial struct BoundArticle
{
    public Utf8 Title;
    public Utf8 Description;
    public Utf8 Body;
    public Utf8List TagList;
}

[JsonEnvelope("order")]
public partial struct BoundOrder
{
    public Utf8 Id;
    public long Quantity;
    public int Priority;
    public double Price;
    public bool Paid;
    public Utf8List Tags;
    public BoundAddress Address;
    public BoundAddress? Billing;
    public Utf8 Note;
}

public partial struct BoundAddress
{
    public Utf8 Street;
    public Utf8 City;
    public long Zip;
}
