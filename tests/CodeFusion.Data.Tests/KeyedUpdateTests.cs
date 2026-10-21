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
            "DELETE FROM ruled; INSERT INTO ruled SELECT i, 0 FROM generate_series(1, 5) AS g(i); TRUNCATE audit_log");
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
    public async Task AnUpdateOfItsOwnKeyIsNeverMergedAndLaterWritesFindTheMovedRow()
    {
        long before = Merged;
        // in order: 715 becomes 716 (free: 716 moved first), then the row now at 716... is the one the second finds
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
        var results = await Group(db => db.SetAudited(3, 1), db => db.SetAudited(1, 2), db => db.SetAudited(3, 3));
        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal("row:3=1 statement row:1=2 statement row:3=3 statement", await Log());
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
    public void TheMergedStatementUnnestsTheParametersAndKeepsTheLastPerKey()
    {
        var s = PgProvider.KeyedUpdate("UPDATE people SET age = $1 WHERE id = $2", "people", "id", 1, ["age"], [0], DataType.Int32, DataType.Int64);
        Assert.Equal("WITH w AS (UPDATE people AS t0 SET age = v.p1 FROM unnest($1, $2) AS v(p1, p2) WHERE t0.id = v.p2) " +
            "SELECT 1 FROM pg_class c WHERE c.oid = 'people'::regclass AND CASE WHEN c.relhasrules OR EXISTS (SELECT 1 FROM pg_trigger tr WHERE tr.tgrelid = c.oid AND NOT tr.tgisinternal) " +
            "OR EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND (i.indisunique OR i.indisexclusion) AND (i.indexprs IS NOT NULL OR i.indpred IS NOT NULL " +
            "OR EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum = ANY (i.indkey) AND a.attname IN ('age')))) " +
            "THEN ('CodeFusion: no merged updates on ' || c.relname)::int ELSE 0 END = 1", s.Batch!.Sql);
        Assert.Contains("a.attname IN ('Odd \"name', 'it''s')", PgProvider.KeyedUpdate("x", "\"T\"", "id", 2, ["\"Odd \"\"name\"", "\"it's\""], [0, 1], DataType.Int32, DataType.Int32, DataType.Int64).Batch!.Sql);
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
