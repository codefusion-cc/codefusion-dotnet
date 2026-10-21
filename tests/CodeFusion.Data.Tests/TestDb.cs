using System;
using System.IO;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Conduit.Data;
using Npgsql;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// The real Postgres the execution, plan and allocation tests run against (CODEFUSION_TEST_DB, an Npgsql-style
/// connection string; without it they skip). Each kind of test has a schema of its own holding the contest's
/// schema.sql, so they never see each other's rows.
/// </summary>
static class TestDb
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CODEFUSION_TEST_DB");

    public static void Require() => Assert.SkipWhen(ConnectionString is null, "CODEFUSION_TEST_DB is not set");

    /// <summary>Whether CODEFUSION_TEST_DB is set: a property test skips on it through its attribute, since a skip thrown inside a
    /// property counts as its failure.</summary>
    public static bool Set => ConnectionString is not null;

    public static NpgsqlDataSource Admin(string? schema = null)
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionString!);
        if (schema is not null) b.SearchPath = schema;
        return NpgsqlDataSource.Create(b.ConnectionString);
    }

    public static async Task<object?> Scalar(this NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    public static async Task Execute(this NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public static string Schema()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, "schema.sql");
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException("schema.sql not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Drops and makes <paramref name="schema"/> with the contest's tables, then runs <paramref name="seed"/> in it.</summary>
    public static async Task Create(string schema, string seed)
    {
        await using var db = Admin();
        await db.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {Schema()}; {seed}; ANALYZE");
    }

    /// <summary>A database on <paramref name="schema"/> with the given loops.</summary>
    public static PgDataSource Open(string schema, int readers = 2, int writers = 1, TimeSpan? lockTimeout = null)
    {
        var settings = PgSettings.Parse(ConnectionString!).Set("search_path", schema);
        var db = new PostgresDatabase(settings) { WriterLockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2) };
        return db.Open(readers, writers);
    }
}

/// <summary>
/// The execution tests' database: a small seed whose every row the tests know. Tests that write make rows of their
/// own (unique names), so the seed stays as it is for the tests that read it.
/// </summary>
public sealed class ConduitFixture : IAsyncLifetime
{
    public const string SchemaName = "cfdata_exec", WriteSchemaName = "cfdata_write";

    /// <summary>The seed, never written to.</summary>
    public PgDataSource Source { get; private set; } = null!;

    /// <summary>The same seed, for the tests that write: each makes rows of its own.</summary>
    public PgDataSource Writes { get; private set; } = null!;

    /// <summary>
    /// Users 1 to 4 (alice, bob, carol, dave; bob and dave have a bio, carol an image), alice follows bob and carol.
    /// Articles 1 to 5 by bob (1, 2), carol (3, 4) and alice (5), with created_at a minute apart from
    /// 2025-04-01T12:00:00Z; tags: 1 has dragons and training, 2 has dragons, 5 has zebra. alice favorited 1 and 3,
    /// dave favorited 1. Article 1 has two comments (by alice, then carol), article 2 none.
    /// </summary>
    public const string Seed = """
        INSERT INTO users (id, username, email, password_hash, bio, image) VALUES
          (1, 'alice', 'alice@example.com', 'hash-a', NULL, NULL),
          (2, 'bob', 'bob@example.com', 'hash-b', 'I write "quotes" and naïve UTF-8 ✓', NULL),
          (3, 'carol', 'carol@example.com', 'hash-c', NULL, 'https://img/c.png'),
          (4, 'dave', 'dave@example.com', 'hash-d', 'dave', NULL);
        SELECT setval(pg_get_serial_sequence('users', 'id'), 100);
        INSERT INTO follows (follower_id, followee_id) VALUES (1, 2), (1, 3);
        INSERT INTO articles (id, slug, author_id, title, description, body, created_at, updated_at) VALUES
          (1, 'how-to-train-your-dragon', 2, 'How to train your dragon', 'Ever wonder how?', 'You have to believe', '2025-04-01T12:00:00Z', '2025-04-01T12:00:00Z'),
          (2, 'dragons-2', 2, 'Dragons 2', 'More', 'Body 2', '2025-04-01T12:01:00Z', '2025-04-01T12:01:30.123456Z'),
          (3, 'carols-one', 3, 'Carol one', 'C1', 'Body 3', '2025-04-01T12:02:00Z', '2025-04-01T12:02:00Z'),
          (4, 'carols-two', 3, 'Carol two', 'C2', 'Body 4', '2025-04-01T12:03:00Z', '2025-04-01T12:03:00Z'),
          (5, 'alices', 1, 'Alice''s', 'A', 'Body 5', '2025-04-01T12:04:00Z', '2025-04-01T12:04:00Z');
        SELECT setval(pg_get_serial_sequence('articles', 'id'), 100);
        INSERT INTO tags (id, name) VALUES (1, 'dragons'), (2, 'training'), (3, 'zebra');
        SELECT setval(pg_get_serial_sequence('tags', 'id'), 100);
        INSERT INTO article_tags (article_id, tag_id) VALUES (1, 2), (1, 1), (2, 1), (5, 3);
        INSERT INTO favorites (user_id, article_id) VALUES (1, 1), (1, 3), (4, 1);
        INSERT INTO comments (id, article_id, author_id, body, created_at, updated_at) VALUES
          (1, 1, 1, 'First!', '2025-04-02T10:00:00Z', '2025-04-02T10:00:00Z'),
          (2, 1, 3, 'Second', '2025-04-02T11:00:00Z', '2025-04-02T11:00:00Z');
        SELECT setval(pg_get_serial_sequence('comments', 'id'), 100)
        """;

    public async ValueTask InitializeAsync()
    {
        if (TestDb.ConnectionString is null) return;
        await TestDb.Create(SchemaName, Seed);
        await TestDb.Create(WriteSchemaName, Seed);
        Source = TestDb.Open(SchemaName);
        Writes = TestDb.Open(WriteSchemaName);
    }

    public ValueTask DisposeAsync()
    {
        LoopChecks.Stop(Source, Writes);
        return default;
    }

    /// <summary>A context on the database the write tests share, bound to <paramref name="response"/>.</summary>
    public ConduitDb Writer(FakeResponse response)
    {
        TestDb.Require();
        var db = Writes.CreateContext<ConduitDb>();
        db.Bind(response);
        return db;
    }

    /// <summary>A context on the fixture's database, bound to <paramref name="response"/>.</summary>
    public ConduitDb Context(FakeResponse response)
    {
        TestDb.Require();
        var db = Source.CreateContext<ConduitDb>();
        db.Bind(response);
        return db;
    }
}

[CollectionDefinition(nameof(ConduitCollection), DisableParallelization = true)]
public sealed class ConduitCollection : ICollectionFixture<ConduitFixture>;
