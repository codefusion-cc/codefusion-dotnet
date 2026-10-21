using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CodeFusion.Benchmarks.Reference;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;
using Conduit.Data;

namespace CodeFusion.Benchmarks;

/// <summary>
/// The compiled LINQ queries (the Conduit context's, CodeFusion.Data) against the contest rewrite's hand-built
/// commands and row handlers for the same queries (Reference/Handwritten.cs, PR #8's code), on one Postgres source,
/// over PgBenchmarks' small seed in BENCH_DB. Both write the same JSON into the same kind of response. Each run ends
/// with the client's CPU per operation ("client CPU"): the process's CPU time over the operations, warm-up included.
/// </summary>
[MemoryDiagnoser]
public class DataBenchmarks
{
    public enum Way { Linq, Handwritten }

    [Params(Way.Linq, Way.Handwritten)] public Way Use;

    /// <summary>A connection: its response and its context, reused for each request.</summary>
    sealed class Caller(PgDataSource source)
    {
        public readonly BenchResponse Response = new();
        public readonly ConduitDb Db = source.CreateContext<ConduitDb>();

        public void Next()
        {
            Response.Next();
            Db.Bind(Response);
        }
    }

    PgDataSource _source = null!;
    Caller[] _callers = null!;
    readonly byte[][] _slugs = new byte[64][];
    int _next;
    long _ops;
    TimeSpan _cpu;

    [GlobalSetup]
    public async Task Setup()
    {
        await PgBenchmarks.Seed();
        var settings = PgSettings.Parse(PgBenchmarks.Url).Set("search_path", PgBenchmarks.Schema);
        // the contest server's session settings, the same for both ways
        _source = new PostgresDatabase(settings).Open(PgBenchmarks.Readers, PgBenchmarks.Writers);
        _callers = new Caller[64];
        for (int i = 0; i < _callers.Length; i++) _callers[i] = new Caller(_source);
        for (int i = 0; i < 64; i++) _slugs[i] = Encoding.UTF8.GetBytes($"article-{PgBenchmarks.ArticleCount - i * 7}");
        // every statement prepared on every connection, and both ways checked to write the same JSON
        for (int i = 0; i < 10; i++) await Concurrent64();
        await Same();
        _ops = 0;
        _cpu = Environment.CpuUsage.TotalTime;
    }

    async Task Same()
    {
        var c = _callers[0];
        foreach (Way way in new[] { Way.Linq, Way.Handwritten })
        {
            var was = Use;
            Use = way;
            _next = 0; // the same articles for both
            string a = await Json(c, () => BySlug(c, 3));
            string b = await Json(c, () => Page(c, 3));
            string d = await Json(c, () => CommentsOf(c, 3));
            Use = was;
            _same[(int)way] = a + b + d;
        }
        if (_same[0] != _same[1]) throw new InvalidOperationException($"the two ways write different JSON:\n{_same[0]}\n{_same[1]}");
    }

    readonly string[] _same = new string[2];

    static async Task<string> Json(Caller c, Func<ValueTask> run)
    {
        await run();
        return Encoding.UTF8.GetString(c.Response.Written);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        var cpu = Environment.CpuUsage.TotalTime - _cpu;
        Console.WriteLine($"// client CPU {Use}: {cpu.TotalMicroseconds / Math.Max(1, _ops):F2} µs/op over {_ops} operations");
        _source.Stop();
    }

    int Slug() => Interlocked.Increment(ref _next) & 63;

    static long Viewer(int i) => 1 + i % PgBenchmarks.Users;

    Utf8 SlugText(int s) => new(_slugs[s], 0, _slugs[s].Length);

    // ---------- operations ----------

    ValueTask BySlug(Caller c, int i)
    {
        int s = Slug();
        c.Next();
        return Use == Way.Linq ? c.Db.ArticleBySlug(SlugText(s), Viewer(i)).WriteAsync(c.Response)
            : Handwritten.Get(_source.Reader, c.Response, _slugs[s], Viewer(i));
    }

    ValueTask Page(Caller c, int i)
    {
        c.Next();
        return Use == Way.Linq ? c.Db.AllArticles(new Paging(20, 0), Viewer(i)).WriteAsync(c.Response)
            : Handwritten.List(_source.Reader, c.Response, Viewer(i), 20, 0);
    }

    ValueTask CommentsOf(Caller c, int i)
    {
        int s = Slug();
        c.Next();
        return Use == Way.Linq ? c.Db.CommentsOf(SlugText(s), Viewer(i)).WriteAsync(c.Response)
            : Handwritten.CommentsOf(_source.Reader, c.Response, _slugs[s], Viewer(i));
    }

    ValueTask Touch(Caller c, int i)
    {
        c.Next();
        return Use == Way.Linq ? c.Db.UpdateUser(Viewer(i), null, null, null, null, null).WriteAsync(c.Response)
            : Handwritten.Touch(_source.Writer, c.Response, Viewer(i));
    }

    [Benchmark]
    public async Task<int> ArticleBySlugOne()
    {
        _ops++;
        var c = _callers[0];
        await BySlug(c, 0);
        return c.Response.Status;
    }

    [Benchmark]
    public async Task<int> ListPage20()
    {
        _ops++;
        var c = _callers[0];
        await Page(c, 1);
        return c.Response.Status;
    }

    [Benchmark]
    public async Task<int> CommentsOfArticle()
    {
        _ops++;
        var c = _callers[0];
        await CommentsOf(c, 2);
        return c.Response.Status;
    }

    [Benchmark]
    public async Task<int> WriteGroup1()
    {
        _ops++;
        var c = _callers[0];
        await Touch(c, 3);
        return c.Response.Status;
    }

    /// <summary>64 connections, each running 20 requests one after another (an article, then a list page): the
    /// throughput is 1 / the time per operation.</summary>
    [Benchmark(OperationsPerInvoke = 64 * 20)]
    public async Task<long> Concurrent64()
    {
        _ops += 64 * 20;
        var tasks = new Task<long>[64];
        for (int t = 0; t < 64; t++)
        {
            var c = _callers[t];
            int me = t;
            tasks[t] = Task.Run(async () =>
            {
                long ok = 0;
                for (int i = 0; i < 20; i++)
                {
                    await (i % 2 == 0 ? BySlug(c, me) : Page(c, me));
                    if (c.Response.Status == 200) ok++;
                }
                return ok;
            });
        }
        long total = 0;
        foreach (var t in tasks) total += await t;
        return total;
    }
}

/// <summary>A response as the server's connection is one: a body buffer and a JSON writer over it, reused.</summary>
sealed class BenchResponse : IResponse
{
    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };
    readonly ArrayBufferWriter<byte> _body = new(64 << 10);
    readonly Utf8JsonWriter _json;
    readonly BumpArena _arena = new();

    public BenchResponse() => _json = new Utf8JsonWriter(_body, Options);

    public int Status { get; set; } = 200;
    public int RetryAfter { get; set; }
    public Utf8JsonWriter Json => _json;
    public IBufferWriter<byte> Body => _body;
    public IRequestArena Arena => _arena;
    public CancellationToken Aborted => default;

    public ReadOnlySpan<byte> Written
    {
        get
        {
            _json.Flush();
            return _body.WrittenSpan;
        }
    }

    public void Reset()
    {
        _json.Reset();
        _body.ResetWrittenCount();
        Status = 200;
        RetryAfter = 0;
    }

    public void Next()
    {
        Reset();
        _arena.Used = 0;
    }

    sealed class BumpArena : IRequestArena
    {
        readonly byte[] _buffer = new byte[64 << 10];
        public int Used;

        public ArenaBytes Allocate(int length)
        {
            var b = new ArenaBytes(_buffer, Used, length);
            Used += length;
            return b;
        }
    }
}
