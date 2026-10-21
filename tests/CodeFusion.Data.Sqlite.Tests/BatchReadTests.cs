using System;
using System.Linq;
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

    static int _writes;

    /// <summary>Whether a read made in one wake-up after a write to kv answered before the batch's COMMIT.</summary>
    static async Task<bool> AnsweredAtOnce(SqliteDataSource s, SqliteStatement read, SqliteStatement? write = null)
    {
        using var loop = new TestLoop(s);
        ValueTask<DataResult> w = default, r = default;
        bool atOnce = false;
        await loop.WakeUp(() =>
        {
            if (write is null) w = Kv.Write(s, "w" + System.Threading.Interlocked.Increment(ref _writes), 1);
            else
            {
                var c = s.Write(write);
                w = Sqlite.Run<Value>(c.Hold(), null, default);
            }
            r = Sqlite.Run<Value>(s.Read(read).Hold(), null, default);
            atOnce = r.IsCompleted;
        });
        var written = await w;
        Assert.True(written.IsOk, "the write: " + written);
        var result = await r;
        Assert.True(result.IsOk, "the read: " + result);
        return atOnce;
    }

    [Fact(Timeout = 30_000)]
    public async Task AViewOfAWrittenTableWaits()
    {
        var s = Open(schema: "CREATE VIEW kv_v AS SELECT k, v FROM kv");
        Assert.False(await AnsweredAtOnce(s, new SqliteStatement("SELECT count(*) FROM kv_v")));
    }

    [Fact(Timeout = 30_000)]
    public async Task WhatSqliteDoesntReportWaits()
    {
        var s = Open(schema: "CREATE TABLE seq (id INTEGER PRIMARY KEY AUTOINCREMENT, v INTEGER)");
        // AUTOINCREMENT's sequence, and the connection's own state: SQLite reports no table for them
        var insert = new SqliteStatement("INSERT INTO seq (v) VALUES (1)");
        Assert.False(await AnsweredAtOnce(s, new SqliteStatement("SELECT seq FROM sqlite_sequence WHERE name = 'seq'"), insert));
        Assert.False(await AnsweredAtOnce(s, new SqliteStatement("SELECT last_insert_rowid()")));
        Assert.False(await AnsweredAtOnce(s, new SqliteStatement("SELECT changes()")));
        Assert.False(await AnsweredAtOnce(s, new SqliteStatement("PRAGMA user_version")));
        // and still, a read of a table nothing wrote
        Assert.True(await AnsweredAtOnce(s, GetOtherO));
    }

    static readonly SqliteStatement GetOtherO = new("SELECT v FROM other WHERE k = 'o'");

    [Fact(Timeout = 30_000)]
    public async Task AVirtualTablesWritesCountAsEverything()
    {
        var s = Open(schema: "CREATE VIRTUAL TABLE docs USING fts5(body)");
        // the module writes its shadow tables with SQL of its own, which SQLite doesn't report
        var write = new SqliteStatement("INSERT INTO docs (body) VALUES ('hello world')");
        Assert.False(await AnsweredAtOnce(s, new SqliteStatement("SELECT count(*) FROM docs_data"), write));
        Assert.False(await AnsweredAtOnce(s, GetOtherO, write));
    }

    [Fact(Timeout = 30_000)]
    public async Task AfterTheSchemaChangesEveryReadWaits()
    {
        var s = Open();
        Assert.True(await AnsweredAtOnce(s, GetOtherO));
        // a trigger made at run time: statements prepared before it may now write more than they said
        var ddl = s.Write(new SqliteStatement("CREATE TRIGGER kv_other AFTER INSERT ON kv BEGIN UPDATE other SET v = v + 1; END"));
        Assert.True((await Sqlite.Run<Value>(ddl.Hold(), null, default)).IsOk);
        Assert.False(await AnsweredAtOnce(s, GetOtherO));
    }

    [Fact]
    public void PreparingAStatementLeavesTheOthersPrepared()
    {
        var s = Open();
        var a = new SqliteStatement("SELECT v FROM other WHERE k = 'o'");
        var b = new SqliteStatement("SELECT count(*) FROM other");
        Assert.True(Kv.Done(Sqlite.Run<Value>(s.Read(a).Hold(), null, default)).IsOk);
        Assert.True(Kv.Done(Sqlite.Run<Value>(s.Read(a).Hold(), null, default)).IsOk);
        Assert.True(Kv.Done(Sqlite.Run<Value>(s.Read(b).Hold(), null, default)).IsOk); // b prepared for the first time
        Assert.True(Kv.Done(Sqlite.Run<Value>(s.Read(a).Hold(), null, default)).IsOk);
        var conn = s.Writer.Connection;
        Assert.Equal(0, Sqlite3.StmtStatus(conn.Statement(a), Sqlite3.StmtStatusReprepare, 0));
    }

    [Fact]
    public void EveryCallbackGivenToSqliteIsCdecl()
    {
        var callbacks = typeof(SqliteDataSource).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttributes(typeof(System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute), false).OfType<System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute>().SingleOrDefault()))
            .Where(c => c.Attribute is not null)
            .ToList();
        Assert.NotEmpty(callbacks);
        foreach (var (method, attribute) in callbacks)
            Assert.True(attribute!.CallConvs?.Contains(typeof(System.Runtime.CompilerServices.CallConvCdecl)) == true, $"{method.DeclaringType!.Name}.{method.Name} has no Cdecl");
    }

    /// <summary>What a reader sees of a row, every way it can ask for each column.</summary>
    sealed class EveryWay : IRowReader
    {
        public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct
        {
            var seen = (System.Collections.Generic.List<string>)state!;
            for (int c = 0; c < row.FieldCount; c++)
            {
                string bytes;
                try
                {
                    bytes = System.Text.Encoding.UTF8.GetString(row.GetBytes(c));
                }
                catch (InvalidOperationException e)
                {
                    bytes = "throws " + e.GetType().Name;
                }
                seen.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{c}: null {row.IsNull(c)} int {row.GetInt64(c)} double {row.GetDouble(c)} bool {row.GetBoolean(c)} bytes {bytes}"));
            }
            return row.GetInt64(0);
        }
    }

    static readonly SqliteStatement Mixed = new(
        "SELECT max(v), 'x' || 12, '12abc', 3.75, -7, NULL, '' FROM kv WHERE k = 'none'");

    /// <summary>A read answers the same whether it runs alone or joins a batch (it keeps its rows till the COMMIT): every
    /// column, asked for as a number, a double, a flag or text, NULL and the crossovers between text and numbers included.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AJoinedReadAnswersExactlyAsItWouldAlone()
    {
        var s = Open();
        var alone = new System.Collections.Generic.List<string>();
        var a = await Sqlite.Run<EveryWay>(s.Read(Mixed).Hold(), alone, default);
        Assert.True(a.IsOk, a.ToString());
        using var loop = new TestLoop(s);
        var joined = new System.Collections.Generic.List<string>();
        ValueTask<DataResult> write = default, read = default;
        bool atOnce = true;
        await loop.WakeUp(() =>
        {
            write = Kv.Write(s, "a", 1);
            read = Sqlite.Run<EveryWay>(s.Read(Mixed).Hold(), joined, default); // reads kv, which the batch wrote: joins
            atOnce = read.IsCompleted;
        });
        Assert.False(atOnce);
        var j = await read;
        Assert.True((await write).IsOk);
        Assert.True(j.IsOk, j.ToString());
        Assert.Equal(a.Value, j.Value);
        Assert.Equal(alone, joined);
    }
}
