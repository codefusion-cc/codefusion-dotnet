using System;
using System.Linq;
using System.Threading.Tasks;
using Ops.Data;
using Xunit;
using Q = Ops.Data.OpsDb.QuerySql;

namespace CodeFusion.Data.Tests;

/// <summary>A write from the rows a query found (<see cref="Query{T}.Then"/>): one statement, so what it finds,
/// writes and gives back is one snapshot, one round trip, and all or nothing.</summary>
[Collection(nameof(OpsTests))]
public sealed class FoundWriteTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    OpsDb Db()
    {
        TestDb.Require();
        var db = fixture.Writes.CreateContext<OpsDb>();
        db.Bind(_response);
        return db;
    }

    static Utf8 U(string s) => Texts.U(s);

    async Task<string> Json<TReply>(TReply reply) where TReply : IReply
    {
        _response.Next();
        await reply.WriteAsync(_response);
        return _response.Text;
    }

    static async Task<object?> Scalar(string sql)
    {
        await using var admin = TestDb.Admin("cfdata_ops_w");
        return await admin.Scalar(sql);
    }

    [Fact]
    public async Task AnInsertFromTheRowFoundAndTheRowGivenBack()
    {
        Assert.Equal(Q.GivePet, "WITH t AS (SELECT p.id, p.name FROM people p WHERE p.name = $1), w AS (INSERT INTO pets (id, owner_id, name, legs) SELECT $2, t.id, $3, 4 FROM t " +
            "ON CONFLICT DO NOTHING) SELECT t.id, t.name FROM t" +
            Guards.Then("pets", [("id", "bigint"), ("owner_id", "bigint"), ("name", "text"), ("legs", "integer")], ["people"], delete: false, corrected: false) + " ORDER BY t.id LIMIT 1");
        var db = Db();
        Assert.Equal("""{"id":1,"text":"ann"}""", await Json(db.GivePet(U("ann"), 8001, U("fido"))));
        Assert.Equal(1L, await Scalar("SELECT owner_id FROM pets WHERE id = 8001 AND name = 'fido'"));
        // again: the pet is there, skipped, and the row still given back
        Assert.Equal("""{"id":1,"text":"ann"}""", await Json(db.GivePet(U("ann"), 8001, U("rex2"))));
        Assert.Equal("fido", await Scalar("SELECT name FROM pets WHERE id = 8001"));
        // no row found: 404, and nothing written
        await Json(db.GivePet(U("nobody"), 8002, U("x")));
        Assert.Equal(404, _response.Status);
        Assert.Null(await Scalar("SELECT id FROM pets WHERE id = 8002"));
        // awaited
        _response.Next();
        var row = await db.GivePet(U("cid"), 8005, U("goldie"));
        Assert.True(row.Found);
        Assert.Equal(3, row.Value.Id);
    }

    [Fact]
    public async Task AConflictWritesNothingAndTheSelectJoinsAsAnyOther()
    {
        Assert.Equal(Q.GivePetStrict, "WITH t AS (SELECT p.id, p.name, p.team_id FROM people p WHERE p.name = $1), w AS (INSERT INTO pets (id, owner_id, name, legs) SELECT $2, t.id, t.name, 3 FROM t) " +
            "SELECT t.name, t2.name FROM t LEFT JOIN teams t2 ON t2.id = t.team_id" +
            Guards.Then("pets", [("id", "bigint"), ("owner_id", "bigint"), ("name", "text"), ("legs", "integer")], ["teams", "people"], delete: false, corrected: false) + " ORDER BY t.id LIMIT 1");
        var db = Db();
        Assert.Equal("""{"name":"ben","team":"blue"}""", await Json(db.GivePetStrict(U("ben"), 8003)));
        Assert.Equal("""{"name":"cid","team":null}""", await Json(db.GivePetStrict(U("cid"), 8004)));
        await Json(db.GivePetStrict(U("ann"), 8003));
        Assert.Equal(409, _response.Status);
        Assert.Equal(2L, await Scalar("SELECT owner_id FROM pets WHERE id = 8003"));
    }

    [Fact]
    public async Task ADeleteOfAnotherTablesRowsJoinedToTheRowFound()
    {
        Assert.Equal(Q.TakePets, "WITH t AS (SELECT p.id, p.age FROM people p WHERE p.name = $1), w AS (DELETE FROM pets x WHERE x.owner_id = ANY(ARRAY(SELECT t.id FROM t)) AND x.legs = $2) " +
            "SELECT t.id, t.age FROM t" + Guards.Then("pets", [], ["people"], delete: true, corrected: false) + " ORDER BY t.id LIMIT 1");
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute("INSERT INTO pets VALUES (8010, 4, 'two', 2), (8011, 4, 'four', 4), (8012, 4, 'two too', 2)");
        var db = Db();
        Assert.Equal("""{"id":4,"value":30}""", await Json(db.TakePets(U("dee"), 2)));
        Assert.Equal("8011", await Scalar("SELECT string_agg(id::text, ',') FROM pets WHERE owner_id = 4 AND id >= 8010"));
        // nothing left to delete: the row is still given back
        Assert.Equal("""{"id":4,"value":30}""", await Json(db.TakePets(U("dee"), 2)));
        await Json(db.TakePets(U("nobody"), 4));
        Assert.Equal(404, _response.Status);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM pets WHERE id = 8011"));
    }

    [Fact]
    public async Task OfSeveralRowsFoundEachIsWrittenFromAndTheOneOfTheSmallestKeyIsTheReply()
    {
        Assert.EndsWith("FROM t" + Guards.Then("pets", [("id", "bigint"), ("owner_id", "bigint"), ("name", "text"), ("legs", "integer")], ["people"], delete: false, corrected: false) +
            " ORDER BY t.id LIMIT 1", Q.GivePetsOfAge, StringComparison.Ordinal);
        await using var admin = TestDb.Admin("cfdata_ops_w");
        // the larger key first in the heap, so a scan meets it first
        await admin.Execute("INSERT INTO people (id, name, age, active, born) VALUES (9712, 'a9712', 9771, true, now()); " +
            "INSERT INTO people (id, name, age, active, born) VALUES (9711, 'a9711', 9771, true, now())");
        Assert.Equal("""{"id":9711,"text":"a9711"}""", await Json(Db().GivePetsOfAge(9771)));
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM pets WHERE id IN (69711, 69712)"));
    }

    [Fact]
    public async Task NoRowFoundDeletesNothingEvenWhereTheLinkIsOneOfTwoConditions()
    {
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute("INSERT INTO pets VALUES (8301, 2, 'many legs', 777)");
        var db = Db();
        await Json(db.TakeLegsOrOwn(U("nobody"), 776));
        Assert.Equal(204, _response.Status);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM pets WHERE id = 8301"));
        await Json(db.TakeLegsOrOwn(U("ben"), 776));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM pets WHERE id = 8301"));
    }

    [Fact]
    public async Task EachRowFoundIsWrittenFrom()
    {
        Assert.Equal("WITH t AS (SELECT p.id, p.name FROM people p WHERE p.age = $1) INSERT INTO pets (id, owner_id, name, legs) SELECT t.id + 50000, t.id, t.name, 1 FROM t " +
            "ON CONFLICT DO NOTHING", Q.PetsForAge);
        var db = Db();
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute("INSERT INTO people (id, name, age, active, born) VALUES (9701, 'a9701', 977, true, now()), (9702, 'a9702', 977, false, now())");
        await Json(db.PetsForAge(977));
        Assert.Equal(204, _response.Status);
        Assert.Equal("a9701,a9702", await Scalar("SELECT string_agg(name, ',' ORDER BY id) FROM pets WHERE id IN (59701, 59702)"));
        await Json(db.PetsForAge(977));
        Assert.Equal(204, _response.Status);
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM pets WHERE id IN (59701, 59702)"));
        await Json(db.PetsForAge(-1));
        Assert.Equal(204, _response.Status);
    }

    [Fact]
    public async Task ConcurrentWritesFromTheSameRowAllSucceedAndWriteOnce()
    {
        TestDb.Require();
        // two writer connections, so the writes are concurrent transactions
        var source = TestDb.Open("cfdata_ops_w", readers: 1, writers: 2);
        try
        {
            for (int round = 0; round < 10; round++)
            {
                long id = 8100 + round;
                var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    var response = new FakeResponse();
                    var db = source.CreateContext<OpsDb>();
                    db.Bind(response);
                    response.Next();
                    var r = await db.GivePet(U("ben"), id, U("p" + id));
                    return r.Found && r.Value.Id == 2;
                })).ToArray();
                Assert.All(await Task.WhenAll(tasks), Assert.True);
                Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pets WHERE id = {id}"));
            }
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }
}
