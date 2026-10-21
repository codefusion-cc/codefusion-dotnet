using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CodeFusion.Data.Postgres;
using CodeFusion.Benchmarks.Reference;
using Npgsql;

namespace CodeFusion.Benchmarks;

/// <summary>
/// The own client (CodeFusion.Data.Postgres) against Npgsql as the contest server used it at 21abce6 (OldReader and
/// OldWriter, set up as OldDb.Init set them up), on the contest's real statements (Reference/Sql.cs) over a small seed
/// of schema.sql in schema pgbench_small of BENCH_DB's database. Both read every column the way the endpoints do: text and bytea as bytes, no strings.
/// Allocated counts the benchmark's own async methods and arrays too (about 180 B per suspended async method, the
/// same for both clients): the client itself allocates nothing (PgAllocationTests), so Npgsql's extra over
/// the Pg row is what Npgsql allocates. Each run ends with the client's CPU per operation on the console ("client CPU"): the process's CPU time over
/// the operations it ran, warm-up included. Postgres's CPU is its own process's, so it isn't in it.
/// </summary>
[MemoryDiagnoser]
public class PgBenchmarks
{
    public enum Client { Pg, Npgsql }

    [Params(Client.Pg, Client.Npgsql)] public Client Use;

    internal const string Schema = "pgbench_small";
    internal const int Readers = 16, Writers = 2, Users = 200, ArticleCount = 2000;
    internal static readonly string Url = Environment.GetEnvironmentVariable("BENCH_DB") ?? "Host=127.0.0.1;Username=conduit;Password=conduit;Database=conduit";

    /// <summary>The contest server's writer lock timeout (its Db.WriterLockTimeout), the same for both clients.</summary>
    static readonly TimeSpan WriterLockTimeout = TimeSpan.FromSeconds(2);

    static readonly PgStatement ArticleBySlug = new(Sql.ArticleBySlug, PgType.Int8, PgType.Text);
    static readonly PgStatement GlobalList = new(Sql.List[(int)Sql.ListKind.Global], PgType.Int8, PgType.Int8, PgType.Int8);
    static readonly PgStatement Favorite = new(Sql.Favorite, PgType.Int8, PgType.Text);
    static readonly PgStatement Unfavorite = new(Sql.Unfavorite, PgType.Int8, PgType.Text);

    readonly byte[][] _slugs = new byte[64][];
    readonly string[] _slugStrings = new string[64];
    PgReader _pgReader = null!;
    PgWriter _pgWriter = null!;
    OldReader _npgReader = null!;
    OldWriter _npgWriter = null!;
    int _next;
    bool _favorite;
    long _ops;
    TimeSpan _cpu;

    [GlobalSetup]
    public async Task Setup()
    {
        await Seed();
        var csb = new NpgsqlConnectionStringBuilder(Url) { SearchPath = Schema };
        // the reader and writer exactly as OldDb.Init made them
        csb.Multiplexing = false;
        csb.MaxPoolSize = Readers;
        csb.NoResetOnClose = true;
        csb.Enlist = false;
        OldDb.Plan(csb);
        _npgReader = new OldReader(NpgsqlDataSource.Create(csb.ConnectionString));
        _npgWriter = new OldWriter(OldDb.WriterSource(csb, Writers, WriterLockTimeout));
        var settings = PgSettings.Parse(Url).Set("search_path", Schema).Set("plan_cache_mode", "force_generic_plan").Set("jit", "off")
            .Set("max_parallel_workers_per_gather", "0").Set("statement_timeout", "30s");
        _pgReader = new PgReader(settings);
        _pgWriter = new PgWriter(settings.Clone().Set("lock_timeout", $"{(long)WriterLockTimeout.TotalMilliseconds}ms"));
        if (Use == Client.Pg)
        {
            _pgReader.Start(Readers);
            _pgWriter.Start(Writers);
        }
        else
        {
            _npgReader.Start(Readers);
            _npgWriter.Start(Writers);
        }
        for (int i = 0; i < 64; i++)
        {
            _slugStrings[i] = $"article-{ArticleCount - i * 7}";
            _slugs[i] = System.Text.Encoding.UTF8.GetBytes(_slugStrings[i]);
        }
        // every statement prepared on every connection before anything is measured
        for (int i = 0; i < 20; i++) await Concurrent64();
        _ops = 0;
        _cpu = Environment.CpuUsage.TotalTime;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        var cpu = Environment.CpuUsage.TotalTime - _cpu;
        Console.WriteLine($"// client CPU {Use}: {cpu.TotalMicroseconds / Math.Max(1, _ops):F2} µs/op over {_ops} operations");
        if (Use == Client.Pg)
        {
            Console.WriteLine($"// groups {Use}: reads {_pgReader.Counters.Grouped}/{_pgReader.Counters.Groups}, writes {_pgWriter.Counters.Grouped}/{_pgWriter.Counters.Groups}");
        }
        _pgReader.Complete();
        _pgWriter.Complete();
        _npgReader.Complete();
        _npgWriter.Complete();
    }

    /// <summary>The small seed both benchmark classes run on (DataBenchmarks too): made once, marked by its version's table.</summary>
    internal static async Task Seed()
    {
        await using var db = NpgsqlDataSource.Create(Url);
        await using (var check = db.CreateCommand($"SELECT to_regclass('{Schema}.seed_v2') IS NOT NULL"))
            if ((bool)(await check.ExecuteScalarAsync())!) return;
        string schema = await File.ReadAllTextAsync(SchemaFile());
        await using var cmd = db.CreateCommand(
            $"DROP SCHEMA IF EXISTS {Schema} CASCADE; CREATE SCHEMA {Schema}; SET search_path = {Schema}; {schema}; " +
            $"INSERT INTO users (username, email, password_hash, bio) SELECT 'user' || g, 'user' || g || '@example.com', 'x', " +
            $"CASE WHEN g % 2 = 0 THEN 'bio of ' || g END FROM generate_series(1, {Users}) g; " +
            "INSERT INTO tags (name) SELECT 'tag' || g FROM generate_series(1, 50) g; " +
            $"INSERT INTO articles (slug, author_id, title, description, body) SELECT 'article-' || g, 1 + g % {Users}, 'Title ' || g, " +
            $"'About ' || g, repeat('Body text of article ' || g || '. ', 30) FROM generate_series(1, {ArticleCount}) g; " +
            "INSERT INTO article_tags SELECT a.id, 1 + (a.id * k) % 50 FROM articles a, generate_series(1, 3) k ON CONFLICT DO NOTHING; " +
            $"INSERT INTO favorites (user_id, article_id) SELECT 1 + g * 7 % {Users}, 1 + g * 13 % {ArticleCount} FROM generate_series(1, 6000) g ON CONFLICT DO NOTHING; " +
            $"INSERT INTO follows (follower_id, followee_id) SELECT 1 + g % {Users}, 1 + g * 31 % {Users} FROM generate_series(1, 2000) g " +
            $"WHERE g % {Users} <> g * 31 % {Users} ON CONFLICT DO NOTHING; " +
            $"INSERT INTO comments (article_id, author_id, body) SELECT 1 + g % {ArticleCount}, 1 + g * 3 % {Users}, 'Comment ' || g FROM generate_series(1, 8000) g; " +
            "CREATE TABLE seed_v2 (); ANALYZE");
        await cmd.ExecuteNonQueryAsync();
    }

    internal static string SchemaFile()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, "schema.sql");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("schema.sql not found above " + AppContext.BaseDirectory);
    }

    int Slug() => Interlocked.Increment(ref _next) & 63;
    static long Viewer(int i) => 1 + i % Users;

    // ---------- reading rows as the endpoints do ----------

    /// <summary>An article's columns from <paramref name="c"/>: Sql.Columns's order.</summary>
    static long Article(in PgRow r, int c, bool body)
    {
        long n = r.GetBytes(c).Length + r.GetBytes(c + 1).Length + r.GetBytes(c + 2).Length;
        if (body) n += r.GetBytes(++c + 2).Length;
        n += r.GetDateTime(c + 3).Second + r.GetDateTime(c + 4).Second + r.GetBytes(c + 5).Length + r.GetInt64(c + 6) + (r.GetBoolean(c + 7) ? 1 : 0);
        n += r.GetBytes(c + 8).Length + (r.IsNull(c + 9) ? 0 : r.GetBytes(c + 9).Length) + (r.IsNull(c + 10) ? 0 : r.GetBytes(c + 10).Length);
        return n + (r.GetBoolean(c + 11) ? 1 : 0);
    }

    static readonly PgRowHandler PgArticle = static (in PgRow r, object? _) => Article(r, 0, true);
    static readonly PgRowHandler PgPage = static (in PgRow r, object? _) => (r.IsNull(0) ? 0 : Article(r, 0, false)) + r.GetInt64(r.FieldCount - 1);

    [ThreadStatic] static byte[]? t_buf;

    static long Bytes(NpgsqlDataReader r, int c)
    {
        var buf = t_buf ??= new byte[64 << 10];
        return r.GetBytes(c, 0, buf, 0, buf.Length);
    }

    static long Article(NpgsqlDataReader r, int c, bool body)
    {
        long n = Bytes(r, c) + Bytes(r, c + 1) + Bytes(r, c + 2);
        if (body) n += Bytes(r, ++c + 2);
        n += r.GetFieldValue<DateTime>(c + 3).Second + r.GetFieldValue<DateTime>(c + 4).Second + Bytes(r, c + 5) + r.GetInt64(c + 6) + (r.GetBoolean(c + 7) ? 1 : 0);
        n += Bytes(r, c + 8) + (r.IsDBNull(c + 9) ? 0 : Bytes(r, c + 9)) + (r.IsDBNull(c + 10) ? 0 : Bytes(r, c + 10));
        return n + (r.GetBoolean(c + 11) ? 1 : 0);
    }

    static readonly Func<NpgsqlDataReader, ValueTask<long>> NpgArticle = static async r => await r.ReadAsync() ? Article(r, 0, true) : 0;

    static readonly Func<NpgsqlDataReader, ValueTask<long>> NpgPage = static async r =>
    {
        long n = 0;
        while (await r.ReadAsync()) n += (r.IsDBNull(0) ? 0 : Article(r, 0, false)) + r.GetInt64(r.FieldCount - 1);
        return n;
    };

    static readonly Func<NpgsqlDataReader?, long> NpgWritten = static r => r is null ? 0 : Article(r, 0, true);

    // ---------- operations ----------

    async ValueTask<long> BySlug(int i)
    {
        int s = Slug();
        if (Use == Client.Pg) return (await _pgReader.Command(ArticleBySlug).Int8(Viewer(i)).Text(_slugs[s]).Run(PgArticle, null)).Value;
        return await _npgReader.Run(OldDb.Command(Sql.ArticleBySlug, OldDb.P(Viewer(i)), OldDb.P(_slugStrings[s])), NpgArticle, CancellationToken.None);
    }

    async ValueTask<long> Page(int i)
    {
        if (Use == Client.Pg) return (await _pgReader.Command(GlobalList).Int8(Viewer(i)).Int8(20).Int8(0).Run(PgPage, null)).Value;
        return await _npgReader.Run(OldDb.Command(Sql.List[(int)Sql.ListKind.Global], OldDb.P(Viewer(i)), OldDb.P(20L), OldDb.P(0L)), NpgPage, CancellationToken.None);
    }

    /// <summary>Favorites or unfavorites (alternating per group) one article for viewer <paramref name="i"/>.</summary>
    async ValueTask<long> Toggle(int i, bool favorite)
    {
        int s = i & 63;
        if (Use == Client.Pg)
            return (await _pgWriter.Command(favorite ? Favorite : Unfavorite).Int8(Viewer(i)).Text(_slugs[s]).Run(PgArticle, null)).Value;
        return await _npgWriter.Run(OldDb.Command(favorite ? Sql.Favorite : Sql.Unfavorite, OldDb.P(Viewer(i)), OldDb.P(_slugStrings[s])), NpgWritten);
    }

    [Benchmark]
    public async Task<long> ArticleBySlugOne()
    {
        _ops++;
        return await BySlug(0);
    }

    [Benchmark]
    public async Task<long> ListPage20()
    {
        _ops++;
        return await Page(1);
    }

    /// <summary>32 reads queued together, so the loop that takes the first takes them all as one group.</summary>
    [Benchmark(OperationsPerInvoke = 32)]
    public async Task<long> ReadGroup32()
    {
        _ops += 32;
        var tasks = new ValueTask<long>[32];
        for (int i = 0; i < 32; i++) tasks[i] = i % 2 == 0 ? BySlug(i) : Page(i);
        long n = 0;
        foreach (var t in tasks) n += await t;
        return n;
    }

    [Benchmark]
    public async Task<long> WriteGroup1()
    {
        _ops++;
        _favorite = !_favorite;
        return await Toggle(3, _favorite);
    }

    /// <summary>64 writes queued together: one transaction, one commit.</summary>
    [Benchmark(OperationsPerInvoke = 64)]
    public async Task<long> WriteGroup64()
    {
        _ops += 64;
        _favorite = !_favorite;
        var tasks = new ValueTask<long>[64];
        for (int i = 0; i < 64; i++) tasks[i] = Toggle(i, _favorite);
        long n = 0;
        foreach (var t in tasks) n += await t;
        return n;
    }

    /// <summary>64 callers, each running 20 reads one after another (an article, then a list page): the
    /// throughput is 1 / the time per operation.</summary>
    [Benchmark(OperationsPerInvoke = 64 * 20)]
    public async Task<long> Concurrent64()
    {
        _ops += 64 * 20;
        var callers = new Task<long>[64];
        for (int c = 0; c < 64; c++)
        {
            int me = c;
            callers[c] = Task.Run(async () =>
            {
                long n = 0;
                for (int i = 0; i < 20; i++) n += i % 2 == 0 ? await BySlug(me) : await Page(me);
                return n;
            });
        }
        long total = 0;
        foreach (var t in callers) total += await t;
        return total;
    }
}
