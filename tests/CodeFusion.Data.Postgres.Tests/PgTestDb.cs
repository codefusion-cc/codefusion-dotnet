using System;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Npgsql;
using Xunit;

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// The real Postgres the client tests run against (CODEFUSION_TEST_DB, an Npgsql-style connection string; without it
/// they skip), set up as the server's loops are, and Npgsql for setup and checks, as the reference.
/// </summary>
static class PgTestDb
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CODEFUSION_TEST_DB");

    public static void Require() => Assert.SkipWhen(ConnectionString is null, "CODEFUSION_TEST_DB is not set");

    /// <summary>The reader's settings: what the contest server's Db.Plan set, as startup parameters.</summary>
    public static PgSettings Settings(string? searchPath = null)
    {
        Require();
        var s = PgSettings.Parse(ConnectionString!)
            .Set("plan_cache_mode", "force_generic_plan").Set("jit", "off").Set("max_parallel_workers_per_gather", "0").Set("statement_timeout", "30s");
        if (searchPath is not null) s.Set("search_path", searchPath);
        return s;
    }

    /// <summary>A writer's: the reader's and a lock timeout.</summary>
    public static PgSettings WriterSettings(TimeSpan lockTimeout, string? searchPath = null) =>
        Settings(searchPath).Set("lock_timeout", $"{(long)lockTimeout.TotalMilliseconds}ms");

    public static NpgsqlDataSource Admin() => NpgsqlDataSource.Create(ConnectionString!);

    public static async Task<object?> Scalar(this NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    public static string Text(this in PgRow r, int c) => Encoding.UTF8.GetString(r.GetBytes(c));

    /// <summary>Waits until a loop of <paramref name="loops"/> sleeps, waiting for commands: its connection is then
    /// idle, and the next group checks it before it sends. However slow the machine, no sleep of a guessed length.</summary>
    public static async Task Asleep(PgLoops loops)
    {
        for (long until = Environment.TickCount64 + 10_000; loops.Sleeping == 0; await Task.Delay(1))
            if (Environment.TickCount64 > until) throw new TimeoutException("no loop went to sleep in 10 s");
    }

    /// <summary>A box for what a handler read, as the app's handlers write into their response buffer.</summary>
    public sealed class Box
    {
        public string? Value;
        public long Id;
        public int Calls;
    }
}
