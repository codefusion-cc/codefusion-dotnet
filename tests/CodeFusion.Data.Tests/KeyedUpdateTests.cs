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
            "SELECT i, 'keyed-' || i, NULL, 1, NULL, true, '2000-01-01T00:00:00Z' FROM generate_series(500, 799) AS g(i)");
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
    async Task<DataResult[]> Group(params Func<OpsDb, Write>[] writes)
    {
        TestDb.Require();
        var pause = Run(_db.Pause());
        await Task.Delay(30);
        var results = writes.Select(w => Run(w(_db))).ToArray();
        Assert.True((await pause).IsOk);
        return await Task.WhenAll(results);
    }

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
        Assert.Equal(6, Merged - before);
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
        Assert.Equal(6 - 1, Merged - before); // a run of three, the other statement alone, a run of two
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

    [Fact]
    public void TheMergedStatementUnnestsTheParametersAndKeepsTheLastPerKey()
    {
        var s = PgProvider.KeyedUpdate("UPDATE people SET age = $1 WHERE id = $2", "people", "id", 1, ["age"], [0], DataType.Int32, DataType.Int64);
        Assert.Equal("UPDATE people AS t0 SET age = v.p1 FROM (SELECT DISTINCT ON (p2) * FROM unnest($1, $2) WITH ORDINALITY AS u(p1, p2, o) ORDER BY p2, o DESC) AS v WHERE t0.id = v.p2", s.Batch!.Sql);
        Assert.Equal([23u, 20u], s.Batch.ElementTypes!);
        Assert.Equal("UPDATE people SET age = $1 WHERE id = $2", s.Sql);
        // shapes it can't run as one: no batch, the plain statement
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 2, ["age"], [0], DataType.Int32, DataType.Int64).Batch);
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 1, ["tags"], [0], DataType.TextArray, DataType.Int64).Batch);
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 1, ["age"], [5], DataType.Int32, DataType.Int64).Batch);
        Assert.Null(PgProvider.KeyedUpdate("x", "people", "id", 1, [], [], DataType.Int32, DataType.Int64).Batch);
        Assert.Equal("UPDATE people SET age = $1 WHERE id = $2", OpsDb.QuerySql.SetAge);
    }
}
