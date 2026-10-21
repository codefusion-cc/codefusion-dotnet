using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using PgProvider = CodeFusion.Data.Postgres.Postgres;
using Ops.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// Updates of one row by its key, merged by the writer into one statement per run of them in a group: every outcome
/// must be what running them one by one gives. Each test queues its writes behind <see cref="OpsDb.Pause"/>, so they
/// go to the writer as one group.
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class KeyedUpdateTests(OpsFixture fixture) : IAsyncLifetime
{
    readonly FakeResponse _response = new();
    OpsDb _db = null!;

    public async ValueTask InitializeAsync()
    {
        if (TestDb.ConnectionString is null) return;
        _db = fixture.Writes.CreateContext<OpsDb>();
        _response.Next();
        _db.Bind(_response);
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute("DELETE FROM people WHERE id BETWEEN 500 AND 799; INSERT INTO people (id, name, nick, age, score, active, born) " +
            "SELECT i, 'keyed-' || i, NULL, 1, NULL, true, '2000-01-01T00:00:00Z' FROM generate_series(500, 799) AS g(i); " +
            "DELETE FROM counters; INSERT INTO counters (id, n) SELECT i, 0 FROM generate_series(700, 719) AS g(i); " +
            "DELETE FROM audited; INSERT INTO audited SELECT i, 0 FROM generate_series(1, 5) AS g(i); " +
            "DELETE FROM ruled; INSERT INTO ruled SELECT i, 0 FROM generate_series(1, 5) AS g(i); TRUNCATE audit_log; " +
            "DELETE FROM counters WHERE id BETWEEN 800 AND 899; INSERT INTO counters (id, n) SELECT i, 0 FROM generate_series(800, 899) AS g(i); " +
            "DELETE FROM parted; INSERT INTO parted VALUES (1, 'part-1'), (2, 'part-2'); ALTER SEQUENCE once_seq RESTART; TRUNCATE deferred_keys");
    }

    public ValueTask DisposeAsync() => default;

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    static async Task<DataResult> Run(Write w) => await w;

    long Merged => fixture.Writes.Writer.Counters.Merged;

    /// <summary>The pause first, then the writes, all queued before the pause ends: one group after it.</summary>
    Task<DataResult[]> Group(params Func<OpsDb, Write>[] writes) => Group(_db, writes);

    static async Task<T[]> Group<T>(OpsDb db, Func<OpsDb, Task<T>>[] writes)
    {
        TestDb.Require();
        var pause = Run(db.Pause());
        await Task.Delay(30);
        var results = writes.Select(w => w(db)).ToArray();
        Assert.True((await pause).IsOk);
        return await Task.WhenAll(results);
    }

    static Task<DataResult[]> Group(OpsDb db, params Func<OpsDb, Write>[] writes) =>
        Group(db, writes.Select(w => (Func<OpsDb, Task<DataResult>>)(d => Run(w(d)))).ToArray());

    static async Task<object?> Scalar(string sql)
    {
        await using var admin = TestDb.Admin("cfdata_ops_w");
        return await admin.Scalar(sql);
    }

    static async Task<int> Age(long id) => (int)(await Scalar($"SELECT age FROM people WHERE id = {id}"))!;

    [Fact]
    public async Task TheLastWriteToAKeyWins()
    {
        long before = Merged;
        long[] ids = [501, 502, 501, 503, 502, 501];
        var results = await Group(ids.Select((id, i) => (Func<OpsDb, Write>)(db => db.SetAge(id, 11 + i))).ToArray());
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(5, Merged - before); // 501 502, then 501 503 502, then 501 alone: a run ends before a key comes twice
        Assert.Equal((16, 15, 14), (await Age(501), await Age(502), await Age(503)));
    }

    [Fact]
    public async Task ARunEndsWhereAnotherStatementComesAndOrderHolds()
    {
        long before = Merged;
        var results = await Group(db => db.SetAge(511, 1), db => db.SetName(511, U("between")), db => db.SetAge(511, 2));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(0, Merged - before);
        Assert.Equal(2, await Age(511));
        Assert.Equal("between", await Scalar("SELECT name FROM people WHERE id = 511"));

        before = Merged;
        results = await Group(db => db.SetAge(512, 1), db => db.SetAge(513, 1), db => db.SetAge(512, 3),
            db => db.AgeOneYear(512), db => db.SetAge(513, 7), db => db.SetAge(514, 8));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(4, Merged - before); // 512 513, then 512 alone (512 came again), the other statement, then 513 514
        Assert.Equal((4, 7, 8), (await Age(512), await Age(513), await Age(514)));
    }

    [Fact]
    public async Task EveryParameterTypeAndNullsGoThroughTheArrays()
    {
        long before = Merged;
        var born = new Timestamp(946_684_800_000_000 + 123_456); // 2000-01-01T00:00:00.123456Z
        var results = await Group(
            db => db.SetCard(521, U("ünï ✓"), 1.25, false, born),
            db => db.SetCard(522, null, null, true, new Timestamp(0)),
            db => db.SetCard(523, U(""), -0.5, false, born));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(3, Merged - before);
        Assert.Equal("ünï ✓|1.25|False|2000-01-01 00:00:00.123456", await Scalar("SELECT nick || '|' || score || '|' || (CASE WHEN active THEN 'True' ELSE 'False' END) || '|' || to_char(born AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') FROM people WHERE id = 521"));
        Assert.Equal("null|null|True|1970", await Scalar("SELECT coalesce(nick, 'null') || '|' || coalesce(score::text, 'null') || '|' || (CASE WHEN active THEN 'True' ELSE 'False' END) || '|' || extract(year FROM born AT TIME ZONE 'UTC') FROM people WHERE id = 522"));
        Assert.Equal("|-0.5", await Scalar("SELECT nick || '|' || score FROM people WHERE id = 523"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AFailingWriteIsBlamedAloneAndTheOthersCommit(int bad)
    {
        // a name another row has: a unique violation, for that write only
        var writes = Enumerable.Range(0, 3).Select(i => i == bad
            ? (Func<OpsDb, Write>)(db => db.SetName(530 + i, U("keyed-599")))
            : db => db.SetName(530 + i, U($"renamed-{530 + i}-{bad}"))).ToArray();
        var results = await Group(writes);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i == bad ? DataStatus.UniqueViolation : DataStatus.Ok, results[i].Status);
            Assert.Equal(i == bad ? $"keyed-{530 + i}" : $"renamed-{530 + i}-{bad}", await Scalar($"SELECT name FROM people WHERE id = {530 + i}"));
        }
    }

    [Fact]
    public async Task MoreThanAGroupHoldsIsMergedGroupByGroup()
    {
        long before = Merged;
        var results = await Group(Enumerable.Range(0, 300).Select(i => (Func<OpsDb, Write>)(db => db.SetAge(600 + i % 50, i))).ToArray());
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(300, Merged - before);
        for (int k = 0; k < 50; k += 7) Assert.Equal(250 + k, await Age(600 + k));
    }

    [Fact]
    public async Task AnUpdateThatIsntOneRowByKeyWithParametersIsNeverMerged()
    {
        long before = Merged;
        var results = await Group(db => db.AgeOneYear(540), db => db.AgeOneYear(540), db => db.SetAgeOfName(U("keyed-541"), 9), db => db.SetAgeOfName(U("keyed-541"), 10));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(0, Merged - before);
        Assert.Equal((3, 10), (await Age(540), await Age(541)));
    }

    static async Task<int> Count(long id) => (int)(await Scalar($"SELECT n FROM counters WHERE id = {id}"))!;

    static async Task<string> Log() => (string)(await Scalar("SELECT coalesce(string_agg(kind || coalesce(':' || id || '=' || n, ''), ' ' ORDER BY seq), '') FROM audit_log"))!;

    [Fact]
    public async Task AWriteToAKeyThatIsntThereIsOkAsAloneAndTheOthersCommit()
    {
        long before = Merged;
        var results = await Group(db => db.SetCount(700, 5), db => db.SetCount(99_999, 6), db => db.SetCount(701, 7));
        Assert.Equal(3, Merged - before);
        Assert.Equal([DataStatus.Ok, DataStatus.Ok, DataStatus.Ok], results.Select(r => r.Status));
        Assert.Equal(DataStatus.Ok, (await _db.SetCount(99_999, 6)).Status);
        Assert.Equal((5, 7), (await Count(700), await Count(701)));
        Assert.Null(await Scalar("SELECT n FROM counters WHERE id = 99999"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ACheckFailingInAMergedRunIsBlamedOnItsWriteAloneAndTheOthersCommit(int bad)
    {
        long before = Merged;
        var results = await Group(Enumerable.Range(0, 4).Select(i => (Func<OpsDb, Write>)(db => db.SetCount(702 + i, i == bad ? -1 : 10 + i))).ToArray());
        Assert.True(Merged - before >= 4, "the run wasn't merged first");
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i == bad ? (DataStatus.Failed, "23514") : (DataStatus.Ok, null), (results[i].Status, results[i].NativeCode));
            Assert.Equal(i == bad ? 0 : 10 + i, await Count(702 + i));
        }
    }

    [Fact]
    public async Task TwoFaultyWritesInAMergedRunAreEachBlamedAndTheRestCommit()
    {
        var results = await Group(db => db.SetCount(706, 1), db => db.SetNote(707, null), db => db.SetCount(708, 2),
            db => db.SetCount(709, -5), db => db.SetCount(710, 3), db => db.SetNote(711, U("kept")));
        Assert.Equal(
            [(DataStatus.Ok, null), (DataStatus.Failed, "23502"), (DataStatus.Ok, null), (DataStatus.Failed, "23514"), (DataStatus.Ok, null), (DataStatus.Ok, null)],
            results.Select(r => (r.Status, r.NativeCode)));
        Assert.Equal((1, 2, 0, 3), (await Count(706), await Count(708), await Count(709), await Count(710)));
        Assert.Equal("kept", await Scalar("SELECT note FROM counters WHERE id = 711"));
        Assert.Equal("", await Scalar("SELECT note FROM counters WHERE id = 707"));
    }

    [Fact]
    public async Task ARowLockedElsewhereTimesOutOnlyItsWriteAndTheOthersCommit()
    {
        TestDb.Require();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops_w");
        var source = new PostgresDatabase(settings) { WriterLockTimeout = TimeSpan.FromMilliseconds(150) }.Open(1, 1);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            await using var admin = TestDb.Admin("cfdata_ops_w");
            await using var conn = await admin.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var lockRow = new Npgsql.NpgsqlCommand("SELECT 1 FROM counters WHERE id = 713 FOR UPDATE", conn, tx))
                await lockRow.ExecuteScalarAsync();
            long before = source.Writer.Counters.Merged;
            var results = await Group(db, d => d.SetCount(712, 1), d => d.SetCount(713, 2), d => d.SetCount(714, 3));
            Assert.True(source.Writer.Counters.Merged - before >= 3, "the run wasn't merged first");
            Assert.Equal([DataStatus.Ok, DataStatus.LockTimeout, DataStatus.Ok], results.Select(r => r.Status));
            Assert.Equal((1, 0, 3), (await Count(712), await Count(713), await Count(714)));
            await tx.RollbackAsync();
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task ALockedKeyInAMergedRunCostsOneMoreLockTimeoutNotOnePerWrite()
    {
        TestDb.Require();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops_w");
        var source = new PostgresDatabase(settings) { WriterLockTimeout = TimeSpan.FromMilliseconds(300) }.Open(1, 1);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            await using var admin = TestDb.Admin("cfdata_ops_w");
            await using var conn = await admin.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var lockRow = new Npgsql.NpgsqlCommand("SELECT 1 FROM people WHERE id = 560 FOR UPDATE", conn, tx))
                await lockRow.ExecuteScalarAsync();
            var counters = source.Writer.Counters;
            long merged = counters.Merged, reruns = counters.Reruns;
            var pause = Run(db.Pause());
            await Task.Delay(30);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var writes = Enumerable.Range(551, 10).Select(id => Run(db.SetAge(id, 40))).ToArray();
            Assert.True((await pause).IsOk);
            var results = await Task.WhenAll(writes);
            var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            Assert.Equal(10, counters.Merged - merged);
            Assert.All(results.Take(9), r => Assert.Equal(DataStatus.Ok, r.Status));
            Assert.Equal(DataStatus.LockTimeout, results[9].Status);
            // the merged attempt waits out the lock once, the writes one by one once more: not again for the write
            // alone, nor once per write before it
            Assert.Equal(1, counters.Reruns - reruns);
            // two lock timeouts (600 ms) and the pause: the old fallback took about 3 s; slack for a busy runner
            Assert.True(took < TimeSpan.FromMilliseconds(1500), $"{took.TotalMilliseconds:F0} ms");
            for (long id = 551; id < 560; id++) Assert.Equal(40, await Age(id));
            await tx.RollbackAsync();
            Assert.Equal(1, await Age(560));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    /// <summary>A writer of its own, on a new connection: nothing is parsed there yet.</summary>
    static PgDataSource FreshSource(TimeSpan? lockTimeout = null)
    {
        TestDb.Require();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops_w");
        return new PostgresDatabase(settings) { WriterLockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2) }.Open(1, 1);
    }

    static readonly PgRowHandler NoRows = static (in PgRow r, object? _) => 0;

    [Fact]
    public async Task AWriteAfterAFailedMultiStatementWriteInTheFallbackStillRuns()
    {
        var source = FreshSource();
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            // statements this connection never parsed: the second's Parse goes out with the first write that uses it
            var insert = new PgStatement("INSERT INTO counters (id, n) VALUES ($1, 0)", PgType.Int8);
            var touch = new PgStatement("UPDATE counters SET note = 'touched' WHERE id = $1", PgType.Int8);
            var pause = Run(db.Pause());
            await Task.Delay(30);
            var bad = Run(db.SetCount(702, -1));
            var good = Run(db.SetCount(703, 1));
            var both = source.Writer.Command(insert).Int8(700).Then(touch).Int8(704).Run(NoRows, null).AsTask(); // 700 exists
            var after = source.Writer.Command(touch).Int8(705).Run(NoRows, null).AsTask();
            Assert.True((await pause).IsOk);
            Assert.Equal((DataStatus.Failed, "23514"), ((await bad).Status, (await bad).NativeCode));
            Assert.Equal(DataStatus.Ok, (await good).Status);
            Assert.Equal(PgErrorCodes.UniqueViolation, (await both).SqlState);
            var last = await after;
            Assert.True(last.Status == PgStatus.Ok, $"{last.Status} {last.SqlState} {last.Exception?.Message}");
            Assert.Equal("touched", await Scalar("SELECT note FROM counters WHERE id = 705"));
            Assert.Equal("", await Scalar("SELECT note FROM counters WHERE id = 704"));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AFaultyWriteInAMergedRunCostsAFewCommitsNotOnePerWrite()
    {
        var counters = fixture.Writes.Writer.Counters;
        long merged = Merged, commits = counters.Commits;
        var results = await Group(Enumerable.Range(0, 100).Select(i => (Func<OpsDb, Write>)(db => db.SetCount(800 + i, i == 60 ? -1 : 1000 + i))).ToArray());
        Assert.True(Merged - merged >= 100, "the run wasn't merged first");
        for (int i = 0; i < 100; i++) Assert.Equal(i == 60 ? DataStatus.Failed : DataStatus.Ok, results[i].Status);
        Assert.Equal(0, await Count(860));
        Assert.Equal(1099, await Count(899));
        // the pause's group and the group's transactions, in rounds of at most 64 writes (more would overflow the
        // backend's cache of subtransactions): one commit per write would be a hundred
        Assert.Equal(3, counters.Commits - commits);
    }

    [Fact]
    public async Task ACommitThatFailsInTheFallbackCommitsNothingAndEachWriteGetsItsOwnOutcome()
    {
        // the merged run fails (a CHECK), so the group goes again in savepoints; there the two inserts of one deferred
        // key both run, and the COMMIT fails: nothing of that transaction committed, so each write runs again alone
        var results = await Group(db => db.SetCount(702, -1), db => db.SetCount(703, 1), db => db.InsertDeferred(1, 5), db => db.InsertDeferred(2, 5));
        Assert.Equal(
            [(DataStatus.Failed, "23514"), (DataStatus.Ok, null), (DataStatus.Ok, null), (DataStatus.UniqueViolation, "23505")],
            results.Select(r => (r.Status, r.NativeCode)));
        Assert.Equal(1, await Count(703));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM deferred_keys"));
    }

    [Fact]
    public async Task AWriteWhoseStatementFailsToParseInTheFallbackLeavesTheNextWriteItsParse()
    {
        var source = FreshSource();
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            var bad = new PgStatement("SELECT 1 FROM no_such_table WHERE $1::bigint > 0", PgType.Int8); // fails at Parse
            var touch = new PgStatement("UPDATE counters SET note = 'parsed' WHERE id = $1", PgType.Int8);
            var pause = Run(db.Pause());
            await Task.Delay(30);
            var failing = Run(db.SetCount(702, -1));
            var good = Run(db.SetCount(703, 1));
            var both = source.Writer.Command(bad).Int8(1).Then(touch).Int8(704).Run(NoRows, null).AsTask();
            var after = source.Writer.Command(touch).Int8(705).Run(NoRows, null).AsTask();
            Assert.True((await pause).IsOk);
            Assert.Equal(DataStatus.Failed, (await failing).Status);
            Assert.Equal(DataStatus.Ok, (await good).Status);
            Assert.Equal("42P01", (await both).SqlState);
            var last = await after;
            Assert.True(last.Status == PgStatus.Ok, $"{last.Status} {last.SqlState} {last.Exception?.Message}");
            Assert.Equal("parsed", await Scalar("SELECT note FROM counters WHERE id = 705"));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AMergedRunThatAnotherTransactionFailedRunsAgainAndStaysMerged()
    {
        // the merged update fails once with a serialization failure (the table's CHECK), as a deadlock or a lock
        // wait would: no write was at fault, but neither was merging, so the statement keeps merging
        long unmerged = fixture.Writes.Writer.Counters.Unmerged, merged = Merged;
        var results = await Group(db => db.SetFlaky(1, 5), db => db.SetFlaky(2, 6));
        Assert.All(results, r => Assert.Equal(DataStatus.Ok, r.Status));
        // the writes complete before their loop has decided about the statement: a write after them waits for it
        Assert.True((await Run(_db.SetCount(719, 1))).IsOk);
        Assert.Equal(2, Merged - merged);
        Assert.Equal(0, fixture.Writes.Writer.Counters.Unmerged - unmerged);
        Assert.Equal((5, 6), ((int)(await Scalar("SELECT n FROM flaky WHERE id = 1"))!, (int)(await Scalar("SELECT n FROM flaky WHERE id = 2"))!));
    }

    [Fact]
    public async Task AWriteRetriedAfterASerializationFailureStillComesBeforeTheWritesQueuedAfterIt()
    {
        // the merged run fails (a CHECK), so the group goes again unmerged; there the next write fails once with
        // 40001 and must run again before the last one, which writes the same row
        var results = await Group(db => db.SetCount(706, -1), db => db.SetCount(707, 1), db => db.SetCountFailingOnce(708, 5), db => db.SetCount(708, 9));
        Assert.Equal([DataStatus.Failed, DataStatus.Ok, DataStatus.Ok, DataStatus.Ok], results.Select(r => r.Status));
        Assert.Equal(9, await Count(708));
    }

    [Fact]
    public async Task AStatementThatStoppedMergingIsTriedMergedAgainLater()
    {
        var ttl = PgWriter.UnmergedFor;
        PgWriter.UnmergedFor = TimeSpan.FromMilliseconds(300);
        await using var admin = TestDb.Admin("cfdata_ops_w");
        try
        {
            await admin.Execute("CREATE TRIGGER counters_audit BEFORE UPDATE ON counters FOR EACH ROW EXECUTE FUNCTION audit_row()");
            long unmerged = fixture.Writes.Writer.Counters.Unmerged;
            var results = await Group(db => db.SetCount(712, 1), db => db.SetCount(713, 2));
            Assert.All(results, r => Assert.True(r.IsOk));
            Assert.Equal(1, fixture.Writes.Writer.Counters.Unmerged - unmerged);
            await admin.Execute("DROP TRIGGER counters_audit ON counters");
            await Task.Delay(400);
            long merged = Merged;
            results = await Group(db => db.SetCount(714, 3), db => db.SetCount(715, 4));
            Assert.All(results, r => Assert.True(r.IsOk));
            Assert.Equal(2, Merged - merged);
            Assert.Equal((3, 4), (await Count(714), await Count(715)));
        }
        finally
        {
            await admin.Execute("DROP TRIGGER IF EXISTS counters_audit ON counters");
            PgWriter.UnmergedFor = ttl;
        }
    }

    [Fact]
    public async Task APartitionedTableIsNeverMerged()
    {
        // a unique index on one partition only: merged, the outcome would depend on the order Postgres updates rows
        var results = await Group(db => db.SetPartedName(2, U("part-1")), db => db.SetPartedName(1, U("free-1")));
        Assert.Equal([DataStatus.UniqueViolation, DataStatus.Ok], results.Select(r => r.Status));
        long before = Merged;
        results = await Group(db => db.SetPartedName(1, U("again-1")), db => db.SetPartedName(2, U("again-2")));
        Assert.All(results, r => Assert.Equal(DataStatus.Ok, r.Status));
        Assert.Equal(0, Merged - before);
    }

    [Fact]
    public async Task AnUpdateOfItsOwnKeyIsNeverMergedAndLaterWritesFindTheMovedRow()
    {
        long before = Merged;
        // in order: row 715 moves to 716 (deleted first, so free), then the second write finds it at 716 and moves it on
        await using (var admin = TestDb.Admin("cfdata_ops_w")) await admin.Execute("DELETE FROM counters WHERE id IN (716, 717)");
        var results = await Group(db => db.MoveCounter(715, 716), db => db.MoveCounter(716, 717));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(0, Merged - before);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM counters WHERE id = 717"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM counters WHERE id IN (715, 716)"));
        Assert.Null(PgProvider.KeyedUpdate("UPDATE counters SET id = $2 WHERE id = $1", "counters", "id", 0, ["id"], [1], DataType.Int64, DataType.Int64).Batch);
    }

    [Fact]
    public async Task AnUpdateReturningItsRowIsNeverMergedAndEachGetsItsOwnRow()
    {
        long before = Merged;
        var rows = await Group(_db, [db => Rows(db.SetAgeReturning(550, 31)), db => Rows(db.SetAgeReturning(99_999, 32)), db => Rows(db.SetAgeReturning(551, 33))]);
        Assert.Equal(0, Merged - before);
        Assert.Equal([(true, 550L, "keyed-550"), (false, 0L, ""), (true, 551L, "keyed-551")], rows);
        Assert.Equal((31, 33), (await Age(550), await Age(551)));

        static async Task<(bool, long, string)> Rows(Write<Name> w)
        {
            var r = await w;
            return r.Found ? (true, r.Value.Id, r.Value.Text.ToString()) : (false, 0L, "");
        }
    }

    [Fact]
    public async Task AColumnUnderAUniqueIndexIsNeverMergedSoOrderDecidesWhoGetsTheValue()
    {
        // in order: 553 takes 552's name while 552 still has it (refused), then 552 lets it go
        var results = await Group(db => db.SetName(553, U("keyed-552")), db => db.SetName(552, U("free-552")));
        Assert.Equal([DataStatus.UniqueViolation, DataStatus.Ok], results.Select(r => r.Status));
        Assert.Equal(("keyed-553", "free-552"), ((string)(await Scalar("SELECT name FROM people WHERE id = 553"))!, (string)(await Scalar("SELECT name FROM people WHERE id = 552"))!));
        // the other way round both get theirs; and the table's check keeps the statement unmerged from then on
        long before = Merged;
        results = await Group(db => db.SetName(554, U("free-554")), db => db.SetName(555, U("keyed-554")));
        Assert.Equal([DataStatus.Ok, DataStatus.Ok], results.Select(r => r.Status));
        Assert.Equal(0, Merged - before);
        Assert.Equal("keyed-554", await Scalar("SELECT name FROM people WHERE id = 555"));
    }

    [Fact]
    public async Task ATableWithTriggersSeesEveryWriteAsItsOwnStatementInOrder()
    {
        long unmerged = fixture.Writes.Writer.Counters.Unmerged;
        var results = await Group(db => db.SetAudited(3, 1), db => db.SetAudited(1, 2), db => db.SetAudited(3, 3));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal("row:3=1 statement row:1=2 statement row:3=3 statement", await Log());
        Assert.Equal(1, fixture.Writes.Writer.Counters.Unmerged - unmerged); // counted, so an app can see it
        // and from then on it isn't tried merged at all
        long before = Merged;
        results = await Group(db => db.SetAudited(2, 4), db => db.SetAudited(4, 5));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(0, Merged - before);
        Assert.Equal("row:3=1 statement row:1=2 statement row:3=3 statement row:2=4 statement row:4=5 statement", await Log());
    }

    [Fact]
    public async Task ATableWithARuleSeesEveryWriteInOrder()
    {
        var results = await Group(db => db.SetRuled(2, 1), db => db.SetRuled(1, 2), db => db.SetRuled(2, 3));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal("rule:2=1 rule:1=2 rule:2=3", await Log());
        long before = Merged;
        results = await Group(db => db.SetRuled(4, 4), db => db.SetRuled(5, 5));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(0, Merged - before);
        Assert.Equal("rule:2=1 rule:1=2 rule:2=3 rule:4=4 rule:5=5", await Log());
    }

    /// <summary>
    /// Random groups of writes to a few keys, some refused by the CHECK: each write's status and the rows at the end
    /// must be what applying them one by one in order gives, a refused write changing nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RandomGroupsEndAsApplyingTheWritesInOrder(int seed)
    {
        var random = new Random(seed);
        var model = Enumerable.Range(700, 6).ToDictionary(id => (long)id, _ => 0);
        for (int round = 0; round < 8; round++)
        {
            var writes = Enumerable.Range(0, random.Next(1, 60))
                .Select(_ => (Id: (long)random.Next(700, 706), N: random.Next(10) == 0 ? -random.Next(1, 9) : random.Next(1000))).ToArray();
            var results = await Group(writes.Select(w => (Func<OpsDb, Write>)(db => db.SetCount(w.Id, w.N))).ToArray());
            for (int i = 0; i < writes.Length; i++)
            {
                bool refused = writes[i].N < 0;
                Assert.True(results[i].Status == (refused ? DataStatus.Failed : DataStatus.Ok), $"seed {seed} round {round} write {i}: {results[i].Status}");
                if (!refused) model[writes[i].Id] = writes[i].N;
            }
            foreach (var (id, n) in model) Assert.True(n == await Count(id), $"seed {seed} round {round}: counter {id} is {await Count(id)}, in order {n}");
        }
    }

    [Fact]
    public void TheMergedStatementUnnestsTheParametersAndItsTableIsCheckedFirst()
    {
        var s = PgProvider.KeyedUpdate("UPDATE people SET age = $1 WHERE id = $2", "people", "id", 1, ["age"], [0], DataType.Int32, DataType.Int64);
        Assert.Equal("UPDATE people AS t0 SET age = v.p1 FROM unnest($1, $2) AS v(p1, p2) WHERE t0.id = v.p2", s.Batch!.Sql);
        Assert.Equal("SELECT 1 FROM pg_class c WHERE c.oid = 'people'::regclass AND CASE WHEN c.relhasrules OR c.relkind = 'p' OR c.relhassubclass " +
            "OR EXISTS (SELECT 1 FROM pg_trigger tr WHERE tr.tgrelid = c.oid AND NOT tr.tgisinternal) " +
            "OR EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND (i.indisunique OR i.indisexclusion) AND (i.indexprs IS NOT NULL OR i.indpred IS NOT NULL " +
            "OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum = ANY (i.indkey) AND a.attname IN ('age')))) " +
            "THEN ('CodeFusion: no merged updates on ' || c.relname)::int ELSE 0 END = 1", s.Batch.Check!.Sql);
        Assert.Contains("a.attname IN ('Odd \"name', 'it''s')", PgProvider.KeyedUpdate("x", "\"T\"", "id", 2, ["\"Odd \"\"name\"", "\"it's\""], [0, 1], DataType.Int32, DataType.Int32, DataType.Int64).Batch!.Check!.Sql);
        Assert.Equal([23u, 20u], s.Batch.ElementTypes!);
        Assert.Equal("UPDATE people SET age = $1 WHERE id = $2", s.Sql);
        // shapes it can't run as one: no batch, the plain statement
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 2, ["age"], [0], DataType.Int32, DataType.Int64).Batch);
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 1, ["tags"], [0], DataType.TextArray, DataType.Int64).Batch);
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 1, ["age"], [5], DataType.Int32, DataType.Int64).Batch);
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 1, [], [], DataType.Int32, DataType.Int64).Batch);
        // a key that isn't an integer: equal values needn't have equal bytes (a collation, -0.0)
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "name", 1, ["age"], [0], DataType.Int32, DataType.Text).Batch);
        Assert.Equal("UPDATE people SET age = $1 WHERE id = $2", OpsDb.QuerySql.SetAge);
    }
}
