using System;
using CodeFusion.Data.Sqlite;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// Migrations: each step past the file's user_version runs in its own transaction with the version moved inside it, so
/// a failing step leaves the file at the step before and the next open finishes; a file newer than the app is refused.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    SqliteDatabase At(params string[] steps) => new(_db.File()) { Synchronous = SqliteSync.Normal, Readers = 1, Migrations = steps };

    static long Version(SqliteDataSource s)
    {
        var c = s.Read(new SqliteStatement("SELECT user_version FROM pragma_user_version()"));
        return Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default)).Value;
    }

    const string One = "CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID";
    const string Two = "INSERT INTO kv VALUES ('seeded', 2)";
    const string Three = "INSERT INTO kv VALUES ('three', 3)";

    [Fact]
    public void AFreshFileRunsEveryStepAndAReopenRunsNone()
    {
        var s = _db.Open(At(One, Two));
        Assert.Equal(2, Version(s));
        Assert.Equal(2, Kv.Read(s, "seeded").Value);
        SqliteChecks.StopOne(s);
        // again: Two would fail on its key if it ran twice
        var again = _db.Open(At(One, Two));
        Assert.Equal(2, Version(again));
        Assert.Equal(1, Kv.Count(again));
    }

    [Fact]
    public void ANewStepRunsOnTheNextOpen()
    {
        SqliteChecks.StopOne(_db.Open(At(One)));
        var s = _db.Open(At(One, Two, Three));
        Assert.Equal(3, Version(s));
        Assert.Equal(2, Kv.Count(s));
    }

    [Fact]
    public void AFailingStepLeavesTheFileAtTheStepBeforeAndTheNextOpenFinishes()
    {
        var e = Assert.Throws<SqliteException>(() => At(One, Two, "INSERT INTO nowhere VALUES (1)").OpenSource());
        Assert.Contains("no such table", e.Message, StringComparison.Ordinal);
        var s = _db.Open(At(One, Two, Three));
        Assert.Equal(3, Version(s));
        Assert.Equal(2, Kv.Count(s));
    }

    [Fact]
    public void AFileNewerThanTheAppIsRefused()
    {
        SqliteChecks.StopOne(_db.Open(At(One, Two)));
        var e = Assert.Throws<InvalidOperationException>(() => At(One).OpenSource());
        Assert.Contains("schema version 2, newer than this app's 1", e.Message, StringComparison.Ordinal);
    }
}
