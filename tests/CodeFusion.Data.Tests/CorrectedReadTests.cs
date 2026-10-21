using System;
using System.Linq;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Ops.Data;
using Xunit;
using Q = Ops.Data.OpsDb.QuerySql;

namespace CodeFusion.Data.Tests;

/// <summary>
/// The Select after Query.Then reads the table its write changes as the write left it, though the statement's
/// snapshot holds it as it was: a Count or Any of it is the snapshot's, corrected by the write. Right when the row
/// was there already, and when a concurrent writer wrote or deleted it first (the case a count of the rows the write
/// changed gets wrong).
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class CorrectedReadTests(OpsFixture fixture)
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

    async Task<Tally> Tally(Write<Tally> write)
    {
        _response.Next();
        var row = await write;
        Assert.True(row.Found);
        return row.Value;
    }

    /// <summary>A person of the test's own, with these labels.</summary>
    static async Task<string> Person(long id, params int[] labels)
    {
        string name = "cr" + id;
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute($"INSERT INTO people (id, name, age, active, born) VALUES ({id}, '{name}', 90, true, now())" +
            string.Concat(labels.Select(l => $"; INSERT INTO person_labels VALUES ({id}, {l})")));
        return name;
    }

    [Fact]
    public async Task ACountAfterAnInsertCountsTheRowOnceWhetherItWasThereOrNot()
    {
        Assert.Equal(Q.GiveLabel, "WITH t AS (SELECT p.id FROM people p WHERE p.name = $1), w AS (INSERT INTO person_labels (person_id, label_id) SELECT t.id, $2 FROM t " +
            "ON CONFLICT (person_id, label_id) DO NOTHING) SELECT t.id, ((SELECT count(*) FROM person_labels pl WHERE pl.person_id = t.id) + " +
            "CASE WHEN NOT EXISTS (SELECT 1 FROM person_labels pl2 WHERE pl2.person_id = t.id AND pl2.label_id = $2) THEN 1 ELSE 0 END), true FROM t" +
            Guards.Then("person_labels", [("person_id", "bigint"), ("label_id", "integer")], ["person_labels", "people"], delete: false, corrected: true) + " ORDER BY t.id LIMIT 1");
        string name = await Person(9801, 1);
        var db = Db();
        Assert.Equal(new Tally(9801, 2, true), await Tally(db.GiveLabel(U(name), 2)));
        // there already: skipped, and counted once
        Assert.Equal(new Tally(9801, 2, true), await Tally(db.GiveLabel(U(name), 2)));
        Assert.Equal(new Tally(9801, 2, true), await Tally(db.GiveLabel(U(name), 1)));
        Assert.Equal(new Tally(9801, 3, true), await Tally(db.GiveLabel(U(name), 3)));
    }

    [Fact]
    public async Task ACountOfOtherRowsCountsTheRowWrittenOnlyWhenItIsOne()
    {
        Assert.Contains("CASE WHEN $2 = $3 AND NOT EXISTS", Q.GiveLabelCountOther, StringComparison.Ordinal);
        Assert.Contains("(COALESCE($2 = $3, false) OR EXISTS (SELECT 1 FROM person_labels", Q.GiveLabelCountOther, StringComparison.Ordinal);
        string name = await Person(9802, 1);
        var db = Db();
        Assert.Equal(new Tally(9802, 0, false), await Tally(db.GiveLabelCountOther(U(name), 2, 3)));
        Assert.Equal(new Tally(9802, 1, true), await Tally(db.GiveLabelCountOther(U(name), 3, 3)));
        Assert.Equal(new Tally(9802, 1, true), await Tally(db.GiveLabelCountOther(U(name), 2, 2)));
        Assert.Equal(new Tally(9802, 1, true), await Tally(db.GiveLabelCountOther(U(name), 1, 1)));
    }

    [Fact]
    public async Task ACountAfterADeleteLeavesTheRowsDeletedOut()
    {
        Assert.Equal(Q.TakeLabel, "WITH t AS (SELECT p.id FROM people p WHERE p.name = $1), w AS (DELETE FROM person_labels l WHERE l.person_id = ANY(ARRAY(SELECT t.id FROM t)) " +
            "AND l.label_id = $2) SELECT t.id, ((SELECT count(*) FROM person_labels pl WHERE pl.person_id = t.id) - " +
            "CASE WHEN EXISTS (SELECT 1 FROM person_labels pl WHERE pl.person_id = t.id AND pl.label_id = $2) THEN 1 ELSE 0 END), false FROM t" +
            Guards.Then("person_labels", [], ["person_labels", "people"], delete: true, corrected: true) + " ORDER BY t.id LIMIT 1");
        string name = await Person(9803, 1, 2, 3);
        var db = Db();
        Assert.Equal(new Tally(9803, 2, false), await Tally(db.TakeLabel(U(name), 2)));
        // gone already: nothing more to take
        Assert.Equal(new Tally(9803, 2, false), await Tally(db.TakeLabel(U(name), 2)));
        Assert.Equal(new Tally(9803, 1, false), await Tally(db.TakeLabel(U(name), 1)));
    }

    [Fact]
    public async Task ADeleteNoKeyFixesCountsTheRowsGone()
    {
        Assert.Contains("- (SELECT count(*) FROM person_labels pl WHERE pl.person_id = t.id AND pl.label_id > $2)", Q.TakeLabelsAbove, StringComparison.Ordinal);
        Assert.Contains("AND NOT COALESCE(pl2.person_id = t.id AND pl2.label_id > $2, false))", Q.TakeLabelsAbove, StringComparison.Ordinal);
        string name = await Person(9804, 1, 2, 3);
        var db = Db();
        Assert.Equal(new Tally(9804, 1, true), await Tally(db.TakeLabelsAbove(U(name), 1, 1)));
        Assert.Equal(new Tally(9804, 1, false), await Tally(db.TakeLabelsAbove(U(name), 1, 3)));
        Assert.Equal(new Tally(9804, 0, false), await Tally(db.TakeLabelsAbove(U(name), 0, 1)));
    }

    [Fact]
    public async Task AnInsertWithoutOrIgnoreIsTheOneMore()
    {
        Assert.Contains("FROM pets p2 WHERE p2.owner_id = t.id AND p2.legs > 2) + CASE WHEN 3 > 2 THEN 1 ELSE 0 END), true FROM t", Q.AddPetCounted);
        var db = Db();
        // ann has rex (4 legs) and tweety (2)
        Assert.Equal(new Tally(1, 2, true), await Tally(db.AddPetCounted(U("ann"), 8201)));
        _response.Next();
        await db.AddPetCounted(U("ann"), 8201).WriteAsync(_response);
        Assert.Equal(409, _response.Status);
    }

    [Fact]
    public async Task ConcurrentWritesOfTheSameRowAllCountItOnce()
    {
        TestDb.Require();
        // two writer connections: one statement writes the row, the others wait for it and skip it, though their
        // snapshots may not hold it
        var source = TestDb.Open("cfdata_ops_w", readers: 1, writers: 2);
        try
        {
            for (int round = 0; round < 10; round++)
            {
                string name = await Person(9810 + round, 1);
                Task<Tally[]> Run(Func<OpsDb, Write<Tally>> write) => Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    var response = new FakeResponse();
                    var db = source.CreateContext<OpsDb>();
                    db.Bind(response);
                    response.Next();
                    var r = await write(db);
                    return r.Value;
                })));
                Assert.All(await Run(db => db.GiveLabel(U(name), 2)), t => Assert.Equal(new Tally(9810 + round, 2, true), t));
                Assert.All(await Run(db => db.TakeLabel(U(name), 2)), t => Assert.Equal(new Tally(9810 + round, 1, false), t));
            }
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AnAnyAgainstANullColumnIsFalseAsInCSharp()
    {
        var db = Db();
        // no nick: C#'s "x" == null is false, and !false true; SQL's = is NULL there
        Assert.Equal(new Tally(9840, 1, false), await Tally(db.AddPetLikeNick(U(await Person(9840)), 8401, U("x"))));
        Assert.Equal(new Tally(9841, 1, true), await Tally(db.AddPetNotLikeNick(U(await Person(9841)), 8402, U("x"))));
    }

    /// <summary>A source on <paramref name="schema"/> whose failures are kept.</summary>
    static PgDataSource Source(string schema, System.Collections.Concurrent.ConcurrentQueue<DataResult> failures) =>
        new PostgresDatabase(PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", schema)) { OnFailure = (_, r) => failures.Enqueue(r) }.Open(1, 1);

    static void FailedByTheGuard<T>(Row<T> row, System.Collections.Concurrent.ConcurrentQueue<DataResult> failures) where T : struct
    {
        Assert.Equal(DataStatus.Failed, row.Status);
        Assert.True(failures.TryDequeue(out var f));
        Assert.Equal("22P02", f.NativeCode);
        Assert.Contains("CodeFusion: the Select after Then can't read", f.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadADeleteMayHaveCascadedIntoFailsTheStatementAndNothingIsWritten()
    {
        TestDb.Require();
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute("INSERT INTO teams VALUES (9860, 'cascading'); INSERT INTO people (id, name, age, active, born, team_id) VALUES (9860, 'cr9860', 90, true, now(), 9860)");
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source("cfdata_ops_w", failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            _response.Next();
            // person_labels references people ON DELETE CASCADE, and the Select reads it through Person.Labels
            FailedByTheGuard(await db.DropMember(9860, U("cr9860")), failures);
            Assert.Equal(1L, await admin.Scalar("SELECT count(*) FROM people WHERE id = 9860"));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task ACascadeTwoTablesAwayFromTheDeleteFailsTheStatement()
    {
        TestDb.Require();
        const string schema = "cfdata_chain";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}; " +
            "CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN OLD; END $$; " +
            "INSERT INTO teams VALUES (9870, 'chained'); INSERT INTO chain_a VALUES (1, 'one'), (2, 'two'), (3, 'three'); " +
            "INSERT INTO chain_b VALUES (1, 1), (2, 2), (3, 3); INSERT INTO chain_c VALUES (1, 1, 9870), (2, 2, 9870), (3, 3, 9870)");
        await using var db0 = TestDb.Admin(schema);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source(schema, failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            // chain_a cascades into chain_b, chain_b into chain_c, and the Select counts chain_c: the model sees neither key
            _response.Next();
            FailedByTheGuard(await db.DropChain(9870, U("one")), failures);
            Assert.Equal(3L, await db0.Scalar("SELECT count(*) FROM chain_c"));
            // with chain_b's key into chain_a not cascading, nothing reaches chain_c: the count is the snapshot's and right
            await db0.Execute("ALTER TABLE chain_c DROP CONSTRAINT chain_c_b_id_fkey; ALTER TABLE chain_c ADD FOREIGN KEY (b_id) REFERENCES chain_b (id)");
            await db0.Execute("DELETE FROM chain_c WHERE id = 1");
            _response.Next();
            Assert.Equal(new Number(9870, 2), (await db.DropChain(9870, U("one"))).Value);
            // a trigger on chain_b fires on the cascaded delete and may write anywhere
            await db0.Execute("CREATE TRIGGER t BEFORE DELETE ON chain_b FOR EACH ROW EXECUTE FUNCTION touch()");
            _response.Next();
            FailedByTheGuard(await db.DropChain(9870, U("two")), failures);
            Assert.Equal(1L, await db0.Scalar("SELECT count(*) FROM chain_a WHERE id = 2"));
            await db0.Execute("DROP TRIGGER t ON chain_b; CREATE RULE r AS ON DELETE TO chain_b DO ALSO NOTIFY chained");
            _response.Next();
            FailedByTheGuard(await db.DropChain(9870, U("two")), failures);
            Assert.Equal(1L, await db0.Scalar("SELECT count(*) FROM chain_a WHERE id = 2"));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task TheRowFoundIsCheckedWhetherOrNotTheSelectReadsAnotherTable()
    {
        TestDb.Require();
        const string schema = "cfdata_guard_found";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}; " +
            "CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; " +
            "INSERT INTO teams VALUES (9880, 'found'); INSERT INTO chain_a VALUES (1, 'one'), (2, 'two'); " +
            "INSERT INTO chain_b VALUES (1, 1), (2, 2); INSERT INTO chain_c VALUES (1, 1, 9880), (2, 2, 9880)");
        await using var db0 = TestDb.Admin(schema);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source(schema, failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            // the Select gives back only the row found, which the delete may cascade into: the row given back could be gone
            _response.Next();
            FailedByTheGuard(await db.DropChainOf(1, U("one")), failures);
            Assert.Equal(2L, await db0.Scalar("SELECT count(*) FROM chain_c"));
            // a trigger on the table an insert writes could change the row found as well
            await db0.Execute("CREATE TRIGGER t BEFORE INSERT ON pets FOR EACH ROW EXECUTE FUNCTION touch()");
            _response.Next();
            FailedByTheGuard(await db.GivePet(U("ann"), 8951, U("rex")), failures);
            Assert.Equal(0L, await db0.Scalar("SELECT count(*) FROM pets WHERE id = 8951"));
            await db0.Execute("DROP TRIGGER t ON pets");
            _response.Next();
            Assert.Equal(DataStatus.Ok, (await db.GivePet(U("ann"), 8951, U("rex"))).Status);
            // with no key cascading into chain_c, the row found is as the delete left it
            await db0.Execute("ALTER TABLE chain_c DROP CONSTRAINT chain_c_b_id_fkey; DELETE FROM chain_c WHERE id = 1");
            await db0.Execute("INSERT INTO chain_c VALUES (1, 2, 9880)");
            _response.Next();
            Assert.Equal(new Number(1, 9880), (await db.DropChainOf(1, U("one"))).Value);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task ATriggerRowSecurityOrARoundingColumnOnTheTableWrittenFailsTheStatement()
    {
        TestDb.Require();
        const string schema = "cfdata_guard";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}; " +
            "CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$");
        await using var db0 = TestDb.Admin(schema);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source(schema, failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            async Task<Row<Tally>> Give(int label)
            {
                _response.Next();
                return await db.GiveLabel(U("cid"), label);
            }
            // as it is: the label written and counted
            Assert.Equal(new Tally(3, 2, true), (await Give(1)).Value);
            // a trigger could write another row, or change this one: the statement fails, and writes nothing
            await db0.Execute("CREATE TRIGGER t BEFORE INSERT ON person_labels FOR EACH ROW EXECUTE FUNCTION touch()");
            FailedByTheGuard(await Give(2), failures);
            Assert.Equal(0L, await db0.Scalar("SELECT count(*) FROM person_labels WHERE person_id = 3 AND label_id = 2"));
            await db0.Execute("DROP TRIGGER t ON person_labels");
            Assert.Equal(new Tally(3, 3, true), (await Give(2)).Value);
            // row security could hide the row written from the count
            await db0.Execute("ALTER TABLE person_labels ENABLE ROW LEVEL SECURITY");
            FailedByTheGuard(await Give(1), failures);
            await db0.Execute("ALTER TABLE person_labels DISABLE ROW LEVEL SECURITY");
            // a column whose type may store another value than the one written (varchar(n) drops spaces past n)
            await db0.Execute("ALTER TABLE pets ALTER COLUMN name TYPE varchar(20)");
            _response.Next();
            FailedByTheGuard(await db.AddPetCounted(U("ann"), 8701), failures);
            Assert.Equal(0L, await db0.Scalar("SELECT count(*) FROM pets WHERE id = 8701"));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task ACorrectedReadOfAColumnInAnotherCollationFailsTheStatement()
    {
        TestDb.Require();
        const string schema = "cfdata_guard_columns";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}; " +
            "CREATE COLLATION ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false)");
        await using var db0 = TestDb.Admin(schema);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source(schema, failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            async Task<Row<Tally>> Add(long id)
            {
                _response.Next();
                return await db.AddPetCounted(U("ann"), id);
            }
            // the snapshot compares in the column's collation, the correction in the value's
            await db0.Execute("ALTER TABLE pets ALTER COLUMN name TYPE text COLLATE ci");
            FailedByTheGuard(await Add(8801), failures);
            await db0.Execute("ALTER TABLE pets ALTER COLUMN name TYPE text COLLATE \"C\"");
            FailedByTheGuard(await Add(8802), failures);
            Assert.Equal(0L, await db0.Scalar("SELECT count(*) FROM pets WHERE id IN (8801, 8802)"));
            // in the default collation the pet is written and counted
            await db0.Execute("ALTER TABLE pets ALTER COLUMN name TYPE text COLLATE \"default\"");
            Assert.Equal(DataStatus.Ok, (await Add(8803)).Status);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task ACorrectedReadOfAColumnOfAnotherTypeThanTheValueBoundFailsTheStatement()
    {
        TestDb.Require();
        const string schema = "cfdata_guard_types";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}");
        await using var db0 = TestDb.Admin(schema);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source(schema, failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            async Task<Row<Tally>> Add(long id)
            {
                _response.Next();
                return await db.AddPetCounted(U("ann"), id);
            }
            // Pet.Legs is an int, bound as integer: a float column is another kind, which past 2^53 doesn't hold every integer
            await db0.Execute("ALTER TABLE pets ALTER COLUMN legs TYPE double precision");
            FailedByTheGuard(await Add(8901), failures);
            // Pet.Id is a long, bound as bigint: a narrower integer column
            await db0.Execute("ALTER TABLE pets ALTER COLUMN legs TYPE integer, ALTER COLUMN id TYPE integer");
            FailedByTheGuard(await Add(8903), failures);
            // the type of no number: the exact one, numeric(1000) holding every bigint still another
            await db0.Execute("ALTER TABLE pets ALTER COLUMN id TYPE numeric");
            FailedByTheGuard(await Add(8902), failures);
            Assert.Equal(0L, await db0.Scalar("SELECT count(*) FROM pets WHERE id IN (8901, 8902, 8903)"));
            await db0.Execute("ALTER TABLE pets ALTER COLUMN id TYPE bigint");
            Assert.Equal(DataStatus.Ok, (await Add(8904)).Status);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AWiderColumnOfTheKindBoundHoldsTheValueAsBound()
    {
        TestDb.Require();
        const string schema = "cfdata_guard_wider";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}");
        await using var db0 = TestDb.Admin(schema);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<DataResult>();
        var source = Source(schema, failures);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            // ann has rex (4 legs) and tweety (2 legs, below the count)
            async Task<Tally> Counted(long id)
            {
                _response.Next();
                return (await db.AddPetCounted(U("ann"), id)).Value;
            }
            async Task<Tally> Weighed(long id)
            {
                _response.Next();
                return (await db.AddPetWeighed(U("ann"), id)).Value;
            }
            // an int into integer, as bound, and into bigint, which holds every int as it is
            Assert.Equal(new Tally(1, 2, true), await Counted(8961));
            await db0.Execute("ALTER TABLE pets ALTER COLUMN legs TYPE bigint");
            Assert.Equal(new Tally(1, 3, true), await Counted(8962));
            // a float into real, as bound, and into double precision, which holds every float as it is
            Assert.Equal(new Tally(1, 1, true), await Weighed(8963));
            await db0.Execute("ALTER TABLE pets ALTER COLUMN weight TYPE double precision");
            Assert.Equal(new Tally(1, 2, true), await Weighed(8964));
            Assert.Empty(failures);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    /// <summary>Waits until a backend of this database waits for a lock: the statement under test is blocked.</summary>
    static async Task Blocked(Npgsql.NpgsqlDataSource admin)
    {
        for (int i = 0; i < 200; i++)
        {
            if ((long)(await admin.Scalar("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%person_labels%'"))! > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("the statement never waited for the lock");
    }

    [Fact]
    public async Task AWriteTheSnapshotMissedIsCountedOnce()
    {
        string name = await Person(9850, 1);
        var db = Db();
        await using var admin = TestDb.Admin("cfdata_ops_w");
        // another transaction writes the label first and commits only once the statement waits for it: the statement's
        // snapshot holds no such row, and its insert skips the row the other committed
        await using (var other = await admin.OpenConnectionAsync())
        {
            await using var tx = await other.BeginTransactionAsync();
            await using (var cmd = new Npgsql.NpgsqlCommand("INSERT INTO person_labels VALUES (9850, 2)", other, tx)) await cmd.ExecuteNonQueryAsync();
            _response.Next();
            var pending = Task.Run(async () => (await db.GiveLabel(U(name), 2)).Value);
            await Blocked(admin);
            await tx.CommitAsync();
            Assert.Equal(new Tally(9850, 2, true), await pending);
        }
        // and a delete another transaction made first: the snapshot holds the row, the delete finds it gone
        await using (var other = await admin.OpenConnectionAsync())
        {
            await using var tx = await other.BeginTransactionAsync();
            await using (var cmd = new Npgsql.NpgsqlCommand("DELETE FROM person_labels WHERE person_id = 9850 AND label_id = 2", other, tx)) await cmd.ExecuteNonQueryAsync();
            _response.Next();
            var pending = Task.Run(async () => (await db.TakeLabel(U(name), 2)).Value);
            await Blocked(admin);
            await tx.CommitAsync();
            Assert.Equal(new Tally(9850, 1, false), await pending);
        }
    }
}
