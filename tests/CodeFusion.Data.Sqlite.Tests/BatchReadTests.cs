using System;
using System.Threading.Tasks;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// A read on an event loop whose batch holds writes not committed yet joins the batch, and waits for its COMMIT, only
/// when the batch has written a table the read reads: what each statement reads and writes is what SQLite reports as it
/// prepares it (triggers, foreign-key actions and views included), so raw SQL is covered as generated SQL is. A read of
/// tables the batch hasn't written sees only committed rows, and answers at once.
/// </summary>
public sealed class BatchReadTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    static readonly SqliteStatement GetOther = new("SELECT v FROM other WHERE k = ?1", DataType.Text);
    static readonly SqliteStatement GetAudit = new("SELECT count(*) FROM audit");
    static readonly SqliteStatement CountChildren = new("SELECT count(*) FROM child");
    static readonly SqliteStatement DeleteParent = new("DELETE FROM parent WHERE id = ?1", DataType.Int64);
    static readonly SqliteStatement RawOther = new("SELECT v FROM other WHERE k = 'o' AND v > 0");
    static readonly SqliteStatement RawKv = new("SELECT count(*) FROM kv WHERE v > 0");

    SqliteDataSource Open(bool foreignKeys = false, params string[] schema) => _db.Open(new SqliteDatabase(_db.File())
    {
        Synchronous = SqliteSync.Normal,
        Exclusive = true,
        EventLoopBatches = true,
        ForeignKeys = foreignKeys,
        Migrations =
        [
            "CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID; " +
            "CREATE TABLE other (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID; INSERT INTO other VALUES ('o', 1)",
            .. schema,
        ],
    });

    static ValueTask<DataResult> Read(SqliteDataSource s, SqliteStatement statement, string? key = null)
    {
        var c = s.Read(statement);
        if (key is not null) c.Text(key);
        return Sqlite.Run<Value>(c.Hold(), null, default);
    }

    static ValueTask<DataResult> Write(SqliteDataSource s, SqliteStatement statement, long id)
    {
        var c = s.Write(statement);
        c.Int64(id);
        return Sqlite.Run<Value>(c.Hold(), null, default);
    }

    [Fact(Timeout = 30_000)]
    public async Task AReadOfATableTheBatchHasntWrittenAnswersAtOnce()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        ValueTask<DataResult> write = default, other = default, kv = default;
        bool otherAtOnce = false, kvAtOnce = true;
        await loop.WakeUp(() =>
        {
            write = Kv.Write(s, "a", 1);
            other = Read(s, GetOther, "o");
            otherAtOnce = other.IsCompleted;
            kv = Kv.ReadLater(s, "a");
            kvAtOnce = kv.IsCompleted;
        });
        Assert.True(otherAtOnce, "a read of a table the batch hadn't written waited for its COMMIT");
        Assert.False(kvAtOnce, "a read of the table the batch wrote answered before its COMMIT");
        Assert.Equal(1, (await other).Value);
        Assert.Equal(1, (await kv).Value);
        Assert.True((await write).IsOk);
    }

    [Fact(Timeout = 30_000)]
    public async Task RawSqlAnswersAtOnceOrWaitsByTheTablesItReads()
    {
        var s = Open();
        using var loop = new TestLoop(s);
        ValueTask<DataResult> write = default, other = default, kv = default;
        bool otherAtOnce = false, kvAtOnce = true;
        await loop.WakeUp(() =>
        {
            write = Kv.Write(s, "a", 1);
            other = Read(s, RawOther);
            otherAtOnce = other.IsCompleted;
            kv = Read(s, RawKv);
            kvAtOnce = kv.IsCompleted;
        });
        Assert.True(otherAtOnce);
        Assert.False(kvAtOnce, "raw SQL reading the written table answered before the COMMIT");
        Assert.Equal(1, (await other).Value);
        Assert.Equal(1, (await kv).Value);
        Assert.True((await write).IsOk);
    }

    [Fact(Timeout = 30_000)]
    public async Task ATriggersTableCountsAsWritten()
    {
        var s = Open(schema: "CREATE TABLE audit (k TEXT NOT NULL); CREATE TRIGGER kv_audit AFTER INSERT ON kv BEGIN INSERT INTO audit VALUES (new.k); END");
        using var loop = new TestLoop(s);
        ValueTask<DataResult> write = default, audit = default;
        bool atOnce = true;
        await loop.WakeUp(() =>
        {
            write = Kv.Write(s, "a", 1);
            audit = Read(s, GetAudit);
            atOnce = audit.IsCompleted;
        });
        Assert.False(atOnce, "a read of the table a trigger wrote answered before the COMMIT");
        Assert.Equal(1, (await audit).Value);
        Assert.True((await write).IsOk);
    }

    [Fact(Timeout = 30_000)]
    public async Task ACascadesTableCountsAsWritten()
    {
        var s = Open(foreignKeys: true,
            "CREATE TABLE parent (id INTEGER PRIMARY KEY); CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent (id) ON DELETE CASCADE); " +
            "INSERT INTO parent VALUES (1); INSERT INTO child VALUES (10, 1), (11, 1)");
        using var loop = new TestLoop(s);
        ValueTask<DataResult> delete = default, children = default;
        bool atOnce = true;
        await loop.WakeUp(() =>
        {
            delete = Write(s, DeleteParent, 1);
            children = Read(s, CountChildren);
            atOnce = children.IsCompleted;
        });
        Assert.False(atOnce, "a read of the table a cascade wrote answered before the COMMIT");
        Assert.Equal(0, (await children).Value);
        Assert.True((await delete).IsOk);
    }

    [Fact(Timeout = 30_000)]
    public async Task AReadOfAnUntouchedTableIsAnsweredThoughTheCommitFails()
    {
        var path = _db.File("deferred.db");
        var s = _db.Open(new SqliteDatabase(path)
        {
            Synchronous = SqliteSync.Normal,
            Exclusive = true,
            EventLoopBatches = true,
            Migrations =
            [
                "CREATE TABLE parent (id INTEGER PRIMARY KEY); INSERT INTO parent VALUES (7); " +
                "CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL REFERENCES parent (id) DEFERRABLE INITIALLY DEFERRED) WITHOUT ROWID",
            ],
        });
        using var loop = new TestLoop(s);
        ValueTask<DataResult> orphan = default, parents = default, kv = default;
        await loop.WakeUp(() =>
        {
            orphan = Kv.Write(s, "orphan", 42); // no parent 42: only the COMMIT finds out
            parents = Read(s, new SqliteStatement("SELECT count(*) FROM parent"));
            kv = Kv.CountLater(s);
        });
        Assert.Equal(DataStatus.Failed, (await orphan).Status);
        var p = await parents;
        Assert.True(p.IsOk, p.ToString()); // it read only committed rows
        Assert.Equal(1, p.Value);
        Assert.Equal(DataStatus.Failed, (await kv).Status);
    }
}
