using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// A write answered Ok is in the file, and one answered as failed is not: when SQLite rolls a transaction back by
/// itself (out of memory, an I/O error), when a statement can't be prepared, when a COMMIT fails, in a group, in Exclusive
/// mode and in an event-loop batch. <see cref="Failing"/> registers SQL functions that fail as SQLite does when memory runs
/// out, which rolls the whole transaction back.
/// </summary>
public sealed class DurabilityTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Every write answered Ok is there; every write answered as failed is not.</summary>
    static async Task AnsweredAsStored(SqliteDataSource s, params (string Key, ValueTask<DataResult> Write)[] writes)
    {
        foreach (var (key, write) in writes)
        {
            var r = await write;
            long rows = Kv.Read(s, key).Rows;
            Assert.True(r.IsOk ? rows == 1 : rows == 0, $"{key} was answered {r.Status} and has {rows} rows");
        }
    }

    static ValueTask<DataResult> Put(SqliteDataSource s, string key, string valueSql)
    {
        var c = s.Write(Statement($"INSERT INTO kv (k, v) VALUES (?1, {valueSql})", DataType.Text));
        c.Text(key);
        return Sqlite.Run<Value>(c.Hold(), null, default);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SqliteStatement> Statements = new();

    static SqliteStatement Statement(string sql, params DataType[] types) => Statements.GetOrAdd(sql, _ => new SqliteStatement(sql, types));

    /// <summary>Holds the file's write lock from another connection until disposed, so writes queue into one group.</summary>
    sealed class HeldLock : IDisposable
    {
        readonly SqliteConn _conn;

        public HeldLock(SqliteDatabase db)
        {
            _conn = SqliteConn.Open(db.Path, db, writer: true);
            Assert.Equal(0, _conn.Once(new SqliteStatement("BEGIN IMMEDIATE")));
        }

        public void Dispose()
        {
            _ = _conn.Once(new SqliteStatement("ROLLBACK"));
            _conn.Dispose();
        }
    }

    // ---------- 1: a read on the loop rolls the batch's transaction back ----------

    [Fact(Timeout = 30_000)]
    public async Task AReadOnTheLoopThatRollsBackLosesNoAnsweredWrite()
    {
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        Failing.Register(s.Writer.Connection.Db);
        using var loop = new TestLoop(s);
        ValueTask<DataResult> a = default;
        DataResult read = default;
        await loop.WakeUp(() =>
        {
            a = Kv.Write(s, "a", 1);
            read = Kv.Done(Sqlite.Run<Value>(s.Read(Statement("SELECT cf_nomem()")).Hold(), null, default));
        });
        Assert.False(read.IsOk);
        await AnsweredAsStored(s, ("a", a));
    }

    // ---------- 2: the transaction is rolled back twice ----------

    [Fact(Timeout = 30_000)]
    public async Task AGroupRolledBackTwiceAnswersNoneOfItsWritesOk()
    {
        var db = _db.Database();
        var s = _db.Open(db);
        Failing.Register(s.Writer.Connection.Db);
        ValueTask<DataResult> a1, a2, b;
        using (new HeldLock(db))
        {
            a1 = Kv.Write(s, "a1", 1);
            await Task.Delay(50, TestContext.Current.CancellationToken); // the writer took a1 and waits on the lock with it
            a2 = Put(s, "a2", "cf_flaky()");
            b = Put(s, "b", "cf_nomem()");
            Failing.FlakyCalls = 0;
        }
        await AnsweredAsStored(s, ("a1", a1), ("a2", a2), ("b", b));
        Assert.True((await Kv.Write(s, "after", 1)).IsOk); // and the writer goes on
    }

    [Fact(Timeout = 30_000)]
    public async Task ABatchRolledBackTwiceAnswersNoneOfItsWritesOkAndGoesOn()
    {
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        Failing.Register(s.Writer.Connection.Db);
        Failing.FlakyCalls = 0;
        using var loop = new TestLoop(s);
        ValueTask<DataResult> a1 = default, a2 = default, b = default, c = default;
        await loop.WakeUp(() =>
        {
            a1 = Kv.Write(s, "a1", 1);
            a2 = Put(s, "a2", "cf_flaky()");
            b = Put(s, "b", "cf_nomem()"); // rolls back; the rerun's a2 rolls back again
            c = Kv.Write(s, "c", 1);       // after both, in the same wake-up
        });
        await AnsweredAsStored(s, ("a1", a1), ("a2", a2), ("b", b), ("c", c));
    }

    // ---------- 3: a statement that can't be prepared ----------

    [Fact(Timeout = 30_000)]
    public async Task AStatementThatCantBePreparedFailsAloneAndTheWriterGoesOn()
    {
        var db = _db.Database();
        var s = _db.Open(db);
        ValueTask<DataResult> good, bad;
        using (new HeldLock(db))
        {
            good = Kv.Write(s, "good", 1);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            bad = Sqlite.Run<Value>(s.Write(Statement("INSERT INTO nowhere VALUES (1)")).Hold(), null, default);
        }
        Assert.Equal(DataStatus.Failed, (await bad).Status);
        await AnsweredAsStored(s, ("good", good));
        Assert.True((await Kv.Write(s, "after", 1)).IsOk);
        Assert.Equal(1, Kv.Read(s, "after").Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task AUnitThatCantBePreparedLeavesNothingInExclusiveMode()
    {
        var s = _db.Open(_db.Database(exclusive: true));
        var c = s.Write(Kv.Put);
        c.Text("half");
        c.Int64(1);
        c.Then(Statement("INSERT INTO nowhere VALUES (1)"));
        Assert.Equal(DataStatus.Failed, (await Sqlite.Run<Value>(c.Hold(), null, default)).Status);
        Assert.True((await Kv.Write(s, "next", 1)).IsOk); // the next group commits; the failed unit must not ride along
        Assert.Equal(0, Kv.Read(s, "half").Rows);
    }

    [Fact(Timeout = 30_000)]
    public async Task AUnitThatCantBePreparedLeavesNothingInABatch()
    {
        var s = _db.Open(_db.Database(exclusive: true, batches: true));
        using var loop = new TestLoop(s);
        ValueTask<DataResult> unit = default, other = default;
        await loop.WakeUp(() =>
        {
            var c = s.Write(Kv.Put);
            c.Text("half");
            c.Int64(1);
            c.Then(Statement("INSERT INTO nowhere VALUES (1)"));
            unit = Sqlite.Run<Value>(c.Hold(), null, default);
            other = Kv.Write(s, "other", 1);
        });
        Assert.Equal(DataStatus.Failed, (await unit).Status);
        Assert.Equal(0, Kv.Read(s, "half").Rows);
        await AnsweredAsStored(s, ("other", other));
    }

    [Fact]
    public void AReadThatCantBePreparedFailsAndGivesItsCommandBack()
    {
        var s = _db.Open(_db.Database());
        var r = Kv.Done(Sqlite.Run<Value>(s.Read(Statement("SELECT * FROM nowhere")).Hold(), null, default));
        Assert.Equal(DataStatus.Failed, r.Status);
        Assert.Equal(1, Kv.Count(s) + 1); // reads go on (and in Debug builds, Stop finds every job back)
    }

    // ---------- 4: SQL of several statements is one unit ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlOfSeveralStatementsIsUndoneWholeWhenOneFails(bool batches)
    {
        var s = _db.Open(_db.Database(exclusive: batches, batches: batches));
        Assert.True((await Kv.Write(s, "taken", 0)).IsOk);
        var both = Statement("INSERT INTO kv (k, v) VALUES ('x', 1); INSERT INTO kv (k, v) VALUES ('taken', 2)");
        DataResult r;
        if (batches)
        {
            using var loop = new TestLoop(s);
            ValueTask<DataResult> w = default;
            await loop.WakeUp(() => w = Sqlite.Run<Value>(s.Write(both).Hold(), null, default));
            r = await w;
        }
        else r = await Sqlite.Run<Value>(s.Write(both).Hold(), null, default);
        Assert.Equal(DataStatus.UniqueViolation, r.Status);
        Assert.Equal(0, Kv.Read(s, "x").Rows);
    }

    // ---------- 7: a read never sees a write that isn't committed ----------

    [Fact(Timeout = 30_000)]
    public async Task AReadOnTheLoopNeverSeesAWriteWhoseCommitFails()
    {
        var path = _db.File("deferred.db");
        var s = _db.Open(new SqliteDatabase(path)
        {
            Synchronous = SqliteSync.Normal,
            Exclusive = true,
            EventLoopBatches = true,
            Migrations =
            [
                "CREATE TABLE parent (id INTEGER PRIMARY KEY); " +
                "CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL REFERENCES parent (id) DEFERRABLE INITIALLY DEFERRED) WITHOUT ROWID",
            ],
        });
        using var loop = new TestLoop(s);
        ValueTask<DataResult> orphan = default;
        long seen = -1;
        await loop.WakeUp(() =>
        {
            orphan = Kv.Write(s, "orphan", 42); // no parent 42: only the COMMIT finds out
            seen = Kv.Count(s);                 // another request's read in the same wake-up
        });
        Assert.Equal(DataStatus.Failed, (await orphan).Status);
        Assert.Equal(0, seen);
        Assert.Equal(0, Kv.Count(s));
    }

    // ---------- 8: Stop while a loop holds a batch ----------

    [Fact(Timeout = 30_000)]
    public async Task StopEndsInTimeWhileALoopHoldsABatch()
    {
        var s = _db.Database(exclusive: true, batches: true).OpenSource();
        using var loop = new TestLoop(s);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ValueTask<DataResult> held = default;
        var wake = loop.WakeUp(() =>
        {
            held = Kv.Write(s, "held", 1);
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(20)); // a handler stuck in the wake-up, its batch open
        });
        holding.Wait(TestContext.Current.CancellationToken);
        var stopped = Stopwatch.StartNew();
        var stopping = Task.Run(s.Stop, TestContext.Current.CancellationToken);
        Assert.True(await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == stopping,
            "Stop didn't end within 10 s while a loop held its batch");
        Assert.True(stopped.Elapsed < TimeSpan.FromSeconds(10));
        release.Set();
        await wake;
        var r = await held;
        Assert.True(r.Status is DataStatus.Ok or DataStatus.Stopped, r.ToString());
        SqliteChecks.StopOne(s);
    }

    [Fact(Timeout = 30_000)]
    public async Task StopDuringBatchesEndsCleanlyAndAnswersEveryWrite()
    {
        var db = _db.Database(exclusive: true, batches: true);
        var s = db.OpenSource();
        using var loop = new TestLoop(s);
        var answers = new System.Collections.Generic.List<(string, ValueTask<DataResult>)>();
        var wakes = new System.Collections.Generic.List<Task>();
        for (int k = 0; k < 200; k++)
        {
            int n = k;
            wakes.Add(loop.WakeUp(() =>
            {
                lock (answers) answers.Add(($"s{n}", Kv.Write(s, $"s{n}", n)));
            }));
            if (k == 100) _ = Task.Run(s.Stop, TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(wakes);
        var stopped = Task.Run(s.Stop, TestContext.Current.CancellationToken);
        Assert.True(await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == stopped);
        var again = _db.Open(_db.Database(exclusive: true));
        foreach (var (key, write) in answers)
        {
            var r = await write;
            Assert.True(r.Status is DataStatus.Ok or DataStatus.Stopped, $"{key}: {r}");
            if (r.IsOk) Assert.Equal(1, Kv.Read(again, key).Rows);
        }
        SqliteChecks.StopOne(s);
    }

    // ---------- 15: the watchdog times only the statement running ----------

    [Fact(Timeout = 30_000)]
    public async Task AFailedStatementLeavesNothingForTheWatchdogToInterrupt()
    {
        var db = _db.Database(timeout: TimeSpan.FromMilliseconds(200));
        var s = _db.Open(db);
        Assert.Equal(DataStatus.Failed, (await Sqlite.Run<Value>(s.Write(Statement("INSERT INTO nowhere VALUES (1)")).Hold(), null, default)).Status);
        await Task.Delay(500, TestContext.Current.CancellationToken); // past the timeout: a stale start would be "over" now
        for (int i = 0; i < 50; i++) Assert.True((await Kv.Write(s, "w" + i, i)).IsOk);
        Assert.Equal(0, s.Interrupted);
    }
}

/// <summary>SQL functions that fail as SQLite does when memory runs out: <c>cf_nomem()</c> always,
/// <c>cf_flaky()</c> on every call after its first since <see cref="FlakyCalls"/> was reset.</summary>
static unsafe class Failing
{
    public static int FlakyCalls;

    public static void Register(nint db)
    {
        fixed (byte* nomem = "cf_nomem\0"u8, flaky = "cf_flaky\0"u8)
        {
            Assert.Equal(0, Sqlite3.CreateFunction(db, nomem, 0, Sqlite3.Utf8, 0, &NoMem, 0, 0, 0));
            Assert.Equal(0, Sqlite3.CreateFunction(db, flaky, 0, Sqlite3.Utf8, 0, &Flaky, 0, 0, 0));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void NoMem(nint context, int argc, nint* argv) => Sqlite3.ResultErrorNoMem(context);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Flaky(nint context, int argc, nint* argv)
    {
        if (Interlocked.Increment(ref FlakyCalls) == 1) Sqlite3.ResultInt64(context, 1);
        else Sqlite3.ResultErrorNoMem(context);
    }
}
