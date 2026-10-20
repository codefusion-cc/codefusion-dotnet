using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using Conduit.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// The Conduit schema in SQLite, and the seed of CodeFusion.Data.Tests' fixture (users 1 to 4, articles 1 to 5, their
/// tags, follows, favorites and comments, every sequence at 100), in two files: one never written to, one the write
/// tests share. The linked execution tests run against it as against Postgres.
/// </summary>
public sealed class ConduitFixture : IAsyncLifetime
{
    /// <summary>The schema: AUTOINCREMENT keys (never reused, as a sequence's), instants as integer microseconds.</summary>
    public static readonly string[] Schema =
    [
        """
        CREATE TABLE users (
            id INTEGER PRIMARY KEY AUTOINCREMENT, username TEXT NOT NULL UNIQUE, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL,
            bio TEXT, image TEXT, created_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)));
        CREATE TABLE follows (
            follower_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE, followee_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
            created_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)), PRIMARY KEY (follower_id, followee_id)) WITHOUT ROWID;
        CREATE INDEX follows_followee_idx ON follows (followee_id);
        CREATE TABLE articles (
            id INTEGER PRIMARY KEY AUTOINCREMENT, slug TEXT NOT NULL UNIQUE, author_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
            title TEXT NOT NULL, description TEXT NOT NULL, body TEXT NOT NULL,
            created_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)),
            updated_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)));
        CREATE INDEX articles_author_idx ON articles (author_id, id DESC);
        CREATE TABLE tags (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
        CREATE TABLE article_tags (
            article_id INTEGER NOT NULL REFERENCES articles (id) ON DELETE CASCADE, tag_id INTEGER NOT NULL REFERENCES tags (id) ON DELETE CASCADE,
            PRIMARY KEY (article_id, tag_id)) WITHOUT ROWID;
        CREATE INDEX article_tags_tag_idx ON article_tags (tag_id, article_id);
        CREATE TABLE favorites (
            user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE, article_id INTEGER NOT NULL REFERENCES articles (id) ON DELETE CASCADE,
            created_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)), PRIMARY KEY (user_id, article_id)) WITHOUT ROWID;
        CREATE INDEX favorites_article_idx ON favorites (article_id);
        CREATE TABLE comments (
            id INTEGER PRIMARY KEY AUTOINCREMENT, article_id INTEGER NOT NULL REFERENCES articles (id) ON DELETE CASCADE,
            author_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE, body TEXT NOT NULL,
            created_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)),
            updated_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000000 AS INTEGER)));
        CREATE INDEX comments_article_idx ON comments (article_id, created_at DESC)
        """,
    ];

    /// <summary>CodeFusion.Data.Tests' seed, row for row (its instants in microseconds).</summary>
    public static readonly string Seed = string.Create(CultureInfo.InvariantCulture, $"""
        INSERT INTO users (id, username, email, password_hash, bio, image) VALUES
          (1, 'alice', 'alice@example.com', 'hash-a', NULL, NULL),
          (2, 'bob', 'bob@example.com', 'hash-b', 'I write "quotes" and naïve UTF-8 ✓', NULL),
          (3, 'carol', 'carol@example.com', 'hash-c', NULL, 'https://img/c.png'),
          (4, 'dave', 'dave@example.com', 'hash-d', 'dave', NULL);
        INSERT INTO follows (follower_id, followee_id) VALUES (1, 2), (1, 3);
        INSERT INTO articles (id, slug, author_id, title, description, body, created_at, updated_at) VALUES
          (1, 'how-to-train-your-dragon', 2, 'How to train your dragon', 'Ever wonder how?', 'You have to believe', {Us("2025-04-01T12:00:00Z")}, {Us("2025-04-01T12:00:00Z")}),
          (2, 'dragons-2', 2, 'Dragons 2', 'More', 'Body 2', {Us("2025-04-01T12:01:00Z")}, {Us("2025-04-01T12:01:30.123456Z")}),
          (3, 'carols-one', 3, 'Carol one', 'C1', 'Body 3', {Us("2025-04-01T12:02:00Z")}, {Us("2025-04-01T12:02:00Z")}),
          (4, 'carols-two', 3, 'Carol two', 'C2', 'Body 4', {Us("2025-04-01T12:03:00Z")}, {Us("2025-04-01T12:03:00Z")}),
          (5, 'alices', 1, 'Alice''s', 'A', 'Body 5', {Us("2025-04-01T12:04:00Z")}, {Us("2025-04-01T12:04:00Z")});
        INSERT INTO tags (id, name) VALUES (1, 'dragons'), (2, 'training'), (3, 'zebra');
        INSERT INTO article_tags (article_id, tag_id) VALUES (1, 2), (1, 1), (2, 1), (5, 3);
        INSERT INTO favorites (user_id, article_id) VALUES (1, 1), (1, 3), (4, 1);
        INSERT INTO comments (id, article_id, author_id, body, created_at, updated_at) VALUES
          (1, 1, 1, 'First!', {Us("2025-04-02T10:00:00Z")}, {Us("2025-04-02T10:00:00Z")}),
          (2, 1, 3, 'Second', {Us("2025-04-02T11:00:00Z")}, {Us("2025-04-02T11:00:00Z")});
        UPDATE sqlite_sequence SET seq = 100
        """);

    /// <summary>An instant as the provider stores it: microseconds since 1970.</summary>
    public static long Us(string iso) =>
        Timestamp.FromDateTime(DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)).UnixMicroseconds;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "cf-sqlite-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The seed, never written to.</summary>
    public SqliteDataSource Source { get; private set; } = null!;

    /// <summary>The same seed, for the tests that write: each makes rows of its own.</summary>
    public SqliteDataSource Writes { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        Source = Open(Path.Combine(_dir, "seed.db"));
        Writes = Open(Path.Combine(_dir, "writes.db"));
        return default;
    }

    /// <summary>A file with the schema (a migration) and the seed (a second one).</summary>
    public static SqliteDataSource Open(string path, SqliteSync sync = SqliteSync.Normal) =>
        new SqliteDatabase(path) { Synchronous = sync, Readers = 4, Migrations = [.. Schema, Seed] }.OpenSource();

    public ValueTask DisposeAsync()
    {
        SqliteChecks.Stop(Source, Writes);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        return default;
    }

    /// <summary>A context on the database the write tests share, bound to <paramref name="response"/>.</summary>
    public ConduitDb Writer(FakeResponse response)
    {
        var db = Writes.CreateContext<ConduitDb>();
        db.Bind(response);
        return db;
    }

    /// <summary>A context on the fixture's database, bound to <paramref name="response"/>.</summary>
    public ConduitDb Context(FakeResponse response)
    {
        var db = Source.CreateContext<ConduitDb>();
        db.Bind(response);
        return db;
    }
}

[CollectionDefinition(nameof(ConduitCollection), DisableParallelization = true)]
public sealed class ConduitCollection : ICollectionFixture<ConduitFixture>;

/// <summary>What every test that stops a source checks: the writers stopped, nothing unexpected was thrown on them, and
/// in Debug builds every pooled job is back.</summary>
public static class SqliteChecks
{
    public static void Stop(params SqliteDataSource[] sources)
    {
        foreach (var s in sources)
        {
            s.Stop();
            var c = s.Writer.Counters;
            Assert.True(c.Unexpected == 0, $"the writer caught {c.Unexpected} unexpected exceptions; the first: {c.FirstUnexpected}");
        }
#if DEBUG
        foreach (var s in sources) Assert.Equal(0, System.Threading.Interlocked.Read(ref s.JobsOut));
#endif
    }
}
