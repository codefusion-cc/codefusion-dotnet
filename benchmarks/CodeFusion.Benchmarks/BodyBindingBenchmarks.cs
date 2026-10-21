using System.Text;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using CodeFusion.Http;

namespace CodeFusion.Benchmarks;

/// <summary>
/// The generated body binders alone, over a request buffer: a small body (Login), a larger one with escapes and a
/// list (Article), each valid, with a bad field, and malformed (cut short). Nothing but the binder and the arena it
/// unescapes into is timed. It uses only what every version of the binder had, so the same file benchmarks each.
/// </summary>
public class BodyBindingBenchmarks
{
    [Params("valid", "bad field", "malformed")]
    public string Kind = "valid";

    static readonly ErrorReply Invalid = new(422, """{"error":"invalid body"}"""u8);
    byte[] _login = [], _article = [];
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
        _login = Framed(login);
        _article = Framed(article);
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

    /// <summary>Never called: the Map calls make the generator write the two binders.</summary>
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/bench/login", static (BoundLogin body) => Reply.NoContent());
        api.MapPost("/bench/article", static (BoundArticle body) => Reply.NoContent());
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
