using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;
using CodeFusion.TechEmpower;
using Npgsql;

namespace CodeFusion.Benchmarks;

/// <summary>
/// TFB's queries test: 20 reads of World by id together (<c>Each</c>), as one connection asks for them, and as 16 ask
/// at once. TFB's World table in schema pgbench_tfb of BENCH_DB's database. Each run ends with the client's CPU per
/// operation ("client CPU"), where the Each's own costs (its pools' locks among them) show; the time is mostly
/// Postgres's.
/// </summary>
[MemoryDiagnoser]
public class EachBenchmarks
{
    const string Schema = "pgbench_tfb";

    sealed class Caller(PgDataSource source)
    {
        public readonly BenchResponse Response = new();
        public readonly TfbDb Db = source.CreateContext<TfbDb>();

        public async Task<int> Queries()
        {
            Response.Next();
            Db.Bind(Response);
            var rows = await Db.Each(20, static (db, _) => db.WorldById(Random.Shared.Next(1, 10001)));
            Db.Unbind();
            return rows.Count;
        }
    }

    PgDataSource _source = null!;
    Caller[] _callers = null!;
    long _ops;
    TimeSpan _cpu;

    [GlobalSetup]
    public async Task Setup()
    {
        await using (var db = NpgsqlDataSource.Create(PgBenchmarks.Url))
        {
            await using var cmd = db.CreateCommand($"""
                CREATE SCHEMA IF NOT EXISTS {Schema};
                CREATE TABLE IF NOT EXISTS {Schema}.world (id integer NOT NULL, randomnumber integer NOT NULL default 0, PRIMARY KEY (id));
                INSERT INTO {Schema}.world (id, randomnumber) SELECT x.id, least(floor(random() * 10000 + 1), 10000)
                    FROM generate_series(1, 10000) AS x(id) ON CONFLICT DO NOTHING;
                """);
            await cmd.ExecuteNonQueryAsync();
        }
        var settings = PgSettings.Parse(PgBenchmarks.Url).Set("search_path", Schema);
        _source = new PostgresDatabase(settings).Open(PgBenchmarks.Readers, PgBenchmarks.Writers);
        _callers = new Caller[16];
        for (int i = 0; i < _callers.Length; i++) _callers[i] = new Caller(_source);
        // every statement prepared on every connection, and the pools warm
        for (int i = 0; i < 20; i++) await Sixteen();
        _ops = 0;
        _cpu = Environment.CpuUsage.TotalTime;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        var cpu = Environment.CpuUsage.TotalTime - _cpu;
        Console.WriteLine($"// client CPU: {cpu.TotalMicroseconds / Math.Max(1, _ops):F2} µs/op over {_ops} operations");
        _source.Stop();
    }

    [Benchmark]
    public Task<int> One()
    {
        _ops++;
        return _callers[0].Queries();
    }

    /// <summary>16 connections at once, each with its Each of 20.</summary>
    [Benchmark(OperationsPerInvoke = 16)]
    public async Task<int> Sixteen()
    {
        Interlocked.Add(ref _ops, 16);
        var all = new Task<int>[16];
        for (int c = 0; c < all.Length; c++) all[c] = _callers[c].Queries();
        int n = 0;
        foreach (var t in all) n += await t;
        return n;
    }
}
