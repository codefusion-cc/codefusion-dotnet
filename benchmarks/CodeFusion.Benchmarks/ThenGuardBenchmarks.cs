using System;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CodeFusion.Data;
using Conduit.Data;
using Npgsql;
using NpgsqlTypes;

namespace CodeFusion.Benchmarks;

/// <summary>
/// The cost of the check a statement after <c>Query.Then</c> makes of the catalog: Follow (an insert) and Unfollow (a
/// delete) as the Conduit context compiles them, with the check and with the condition of their final WHERE taken
/// out, as prepared statements on one Npgsql connection over PgBenchmarks' small seed in BENCH_DB. The two differ
/// only in the check, so the difference is what it costs Postgres per execution.
/// </summary>
[MemoryDiagnoser]
public class ThenGuardBenchmarks
{
    public enum Check { With, Without }

    [Params(Check.With, Check.Without)] public Check Guard;

    NpgsqlDataSource _db = null!;
    NpgsqlConnection _connection = null!;
    NpgsqlCommand _follow = null!, _unfollow = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await PgBenchmarks.Seed();
        var builder = new NpgsqlDataSourceBuilder(PgBenchmarks.Url);
        builder.ConnectionStringBuilder.SearchPath = PgBenchmarks.Schema;
        _db = builder.Build();
        _connection = await _db.OpenConnectionAsync();
        _follow = await Command(ConduitDb.QuerySql.Follow, ConduitDb.QuerySql.FollowParameters.ToArray());
        _unfollow = await Command(ConduitDb.QuerySql.Unfollow, ConduitDb.QuerySql.UnfollowParameters.ToArray());
    }

    /// <summary>The statement, without its check when <see cref="Guard"/> says so, prepared: user7 followed by user 3.</summary>
    async Task<NpgsqlCommand> Command(string sql, DataType[] types)
    {
        if (Guard == Check.Without)
        {
            int start = sql.IndexOf(" WHERE (SELECT CASE WHEN ", StringComparison.Ordinal), end = sql.LastIndexOf(" ORDER BY ", StringComparison.Ordinal);
            if (start < 0 || end < start) throw new InvalidOperationException("no check in " + sql);
            sql = sql[..start] + sql[end..];
        }
        var cmd = new NpgsqlCommand(sql, _connection);
        foreach (var t in types)
            cmd.Parameters.Add(t switch
            {
                DataType.Int64 => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = 3L },
                DataType.Text => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "user7" },
                _ => throw new InvalidOperationException($"a {t} parameter"),
            });
        await cmd.PrepareAsync();
        return cmd;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _follow.DisposeAsync();
        await _unfollow.DisposeAsync();
        await _connection.DisposeAsync();
        await _db.DisposeAsync();
    }

    /// <summary>The insert: after the first call its key is there, and ON CONFLICT skips it.</summary>
    [Benchmark]
    public async Task<object?> Follow() => await _follow.ExecuteScalarAsync();

    /// <summary>The delete: after the first call there is no row of its key.</summary>
    [Benchmark]
    public async Task<object?> Unfollow() => await _unfollow.ExecuteScalarAsync();
}
