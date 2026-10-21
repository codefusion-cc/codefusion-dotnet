using System;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// The SQLite build the provider loads is checked once, before the first connection: a build too old, single-threaded
/// or without what the generated SQL uses is refused with why. The package's own build passes, and its memory
/// statistics are off (on Apple's arm64 through the variadic call that needs its own ABI).
/// </summary>
public sealed class BuildCheckTests
{
    const int Current = 3_050_004;

    [Fact]
    public void ASingleThreadedBuildIsRefused()
    {
        string? why = SqliteConn.Unfit(Current, 0, _ => false);
        Assert.NotNull(why);
        Assert.Contains("SQLITE_THREADSAFE=0", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AThreadSafeBuildOfEitherModePasses(int mode) => Assert.Null(SqliteConn.Unfit(Current, mode, _ => false));

    [Theory]
    [InlineData("OMIT_JSON")]
    [InlineData("OMIT_CTE")]
    [InlineData("OMIT_UPSERT")]
    [InlineData("OMIT_WAL")]
    [InlineData("OMIT_SUBQUERY")]
    public void ABuildWithoutWhatTheSqlUsesIsRefused(string omitted)
    {
        string? why = SqliteConn.Unfit(Current, 2, o => o == omitted);
        Assert.NotNull(why);
        Assert.Contains("SQLITE_" + omitted, why, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherOptionsDontMatter() => Assert.Null(SqliteConn.Unfit(Current, 2, o => o is "OMIT_DEPRECATED" or "OMIT_LOAD_EXTENSION"));

    [Theory]
    [InlineData(Sqlite3.MinVersion - 1, false)]
    [InlineData(Sqlite3.MinVersion, true)]
    public void TheOldestVersionIsTheOneThatOrdersInsideAggregates(int version, bool fit) =>
        Assert.Equal(fit, SqliteConn.Unfit(version, 2, _ => false) is null);

    [Fact]
    public void ThePackagesBuildIsFit()
    {
        SqliteConn.InitializeOnce();
        Assert.InRange(Sqlite3.ThreadSafe(), 1, 2);
        Assert.Equal(0, Sqlite3.CompileOptionUsed("OMIT_JSON"));
        Assert.Equal(1, Sqlite3.CompileOptionUsed("THREADSAFE=" + Sqlite3.ThreadSafe()));
        Assert.Null(SqliteConn.Unfit(Sqlite3.LibVersionNumber(), Sqlite3.ThreadSafe(), o => Sqlite3.CompileOptionUsed(o) != 0));
    }

    [Fact]
    public async System.Threading.Tasks.Task MemoryStatisticsAreOff()
    {
        using var db = new TempDb();
        var s = db.Open(new SqliteDatabase(db.File()) { Readers = 1, Migrations = ["CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID"] });
        Assert.True((await Kv.Write(s, "a", 1)).IsOk);
        Assert.Equal(1, Kv.Read(s, "a").Rows);
        Assert.True(SqliteConn.MemoryStatusOff);
        // statistics on would count what the connections hold; off, SQLite counts nothing
        Assert.Equal(0, Sqlite3.MemoryUsed());
    }
}
