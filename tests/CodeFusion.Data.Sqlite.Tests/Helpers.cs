using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>A folder of its own per test, deleted after; and sources opened in it, stopped and checked at the end.</summary>
public sealed class TempDb : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cf-sqlite-" + Guid.NewGuid().ToString("N")[..10]);
    readonly List<SqliteDataSource> _sources = [];

    public TempDb() => Directory.CreateDirectory(_dir);

    public string File(string name = "test.db") => Path.Combine(_dir, name);

    /// <summary>Opens <paramref name="database"/> (made with <see cref="Database"/>), stopped and checked at the end.</summary>
    public SqliteDataSource Open(SqliteDatabase database)
    {
        var s = database.OpenSource();
        _sources.Add(s);
        return s;
    }

    /// <summary>A database in this folder with a key-value table (k TEXT PRIMARY KEY, v INTEGER).</summary>
    public SqliteDatabase Database(string name = "test.db", int readers = 2, TimeSpan? timeout = null, int maxQueued = 8192, bool leader = false,
        int checkpointFrames = 1000, bool exclusive = false, bool completeOnWriter = false, bool batches = false, string[]? more = null) =>
        new(File(name))
        {
            Synchronous = SqliteSync.Normal,
            Readers = readers,
            StatementTimeout = timeout ?? TimeSpan.FromSeconds(30),
            MaxQueued = maxQueued,
            LeaderCommit = leader,
            CheckpointFrames = checkpointFrames,
            Exclusive = exclusive,
            CompleteOnWriterThread = completeOnWriter,
            EventLoopBatches = batches,
            Migrations = ["CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID", .. more ?? []],
        };

    public void Dispose()
    {
        foreach (var s in _sources) SqliteChecks.StopOne(s);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }
}

/// <summary>Statements and calls the tests share.</summary>
public static class Kv
{
    public static readonly SqliteStatement Put = new("INSERT INTO kv (k, v) VALUES (?1, ?2)", DataType.Text, DataType.Int64);
    public static readonly SqliteStatement Get = new("SELECT v FROM kv WHERE k = ?1", DataType.Text);
    public static readonly SqliteStatement CountAll = new("SELECT count(*) FROM kv");
    public static readonly SqliteStatement CountLike = new("SELECT count(*) FROM kv WHERE k LIKE ?1", DataType.Text);
    /// <summary>Writes many rows from a recursive CTE: a write that takes a while (?2 rows of key prefix ?1).</summary>
    public static readonly SqliteStatement PutMany = new(
        "INSERT INTO kv (k, v) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?2) SELECT ?1 || i, i FROM n",
        DataType.Text, DataType.Int64);
    /// <summary>Reads for a while: counts a recursive CTE of ?1 rows.</summary>
    public static readonly SqliteStatement Spin = new(
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?1) SELECT count(*) FROM n", DataType.Int64);

    public static ValueTask<DataResult> Write(SqliteDataSource s, string key, long value, CancellationToken aborted = default)
    {
        var c = s.Write(Put);
        c.Text(key);
        c.Int64(value);
        return Sqlite.Run<Value>(c.Hold(), null, aborted);
    }

    public static ValueTask<DataResult> WriteMany(SqliteDataSource s, string prefix, long rows, CancellationToken aborted = default)
    {
        var c = s.Write(PutMany);
        c.Text(prefix);
        c.Int64(rows);
        return Sqlite.Run<Value>(c.Hold(), null, aborted);
    }

    public static DataResult Read(SqliteDataSource s, string key)
    {
        var c = s.Read(Get);
        c.Text(key);
        return Done(Sqlite.Run<Value>(c.Hold(), null, default));
    }

    /// <summary>A read whose task may still be running: one on an event loop whose batch holds writes not committed yet
    /// completes at the batch's end.</summary>
    public static ValueTask<DataResult> ReadLater(SqliteDataSource s, string key)
    {
        var c = s.Read(Get);
        c.Text(key);
        return Sqlite.Run<Value>(c.Hold(), null, default);
    }

    /// <summary>As <see cref="ReadLater"/>, the count of every row.</summary>
    public static ValueTask<DataResult> CountLater(SqliteDataSource s) => Sqlite.Run<Value>(s.Read(CountAll).Hold(), null, default);

    public static long Count(SqliteDataSource s, string? like = null)
    {
        var c = s.Read(like is null ? CountAll : CountLike);
        if (like is not null) c.Text(like);
        var r = Done(Sqlite.Run<Value>(c.Hold(), null, default));
        Assert.True(r.IsOk, r.ToString());
        return r.Value;
    }

    /// <summary>A read's task, which is complete when Run returns.</summary>
    public static DataResult Done(ValueTask<DataResult> t)
    {
        Assert.True(t.IsCompleted, "a read completes before Run returns");
        return t.Result;
    }
}

/// <summary>The first column as an integer.</summary>
public sealed class Value : IRowReader
{
    public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct => row.IsNull(0) ? -1 : row.GetInt64(0);
}

/// <summary>Every column as text, into a list.</summary>
public sealed class Dump : IRowReader
{
    public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct
    {
        var rows = (List<string?[]>)state!;
        var r = new string?[row.FieldCount];
        for (int c = 0; c < r.Length; c++)
        {
            if (row.IsNull(c)) continue;
            try
            {
                r[c] = Encoding.UTF8.GetString(row.GetBytes(c));
            }
            catch (InvalidOperationException)
            {
                r[c] = row.GetInt64(c).ToString(System.Globalization.CultureInfo.InvariantCulture); // a kept row's number
            }
        }
        rows.Add(r);
        return rows.Count;
    }
}
