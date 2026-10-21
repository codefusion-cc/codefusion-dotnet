using System;
using System.Linq;
using System.Threading.Tasks;
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
        Assert.Equal("WITH t AS (SELECT p.id FROM people p WHERE p.name = $1), w AS (INSERT INTO person_labels (person_id, label_id) SELECT t.id, $2 FROM t " +
            "ON CONFLICT (person_id, label_id) DO NOTHING) SELECT t.id, ((SELECT count(*) FROM person_labels pl WHERE pl.person_id = t.id) + " +
            "CASE WHEN NOT EXISTS (SELECT 1 FROM person_labels pl2 WHERE pl2.person_id = t.id AND pl2.label_id = $2) THEN 1 ELSE 0 END), true FROM t", Q.GiveLabel);
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
        Assert.Contains("($2 = $3 OR EXISTS (SELECT 1 FROM person_labels", Q.GiveLabelCountOther, StringComparison.Ordinal);
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
        Assert.Equal("WITH t AS (SELECT p.id FROM people p WHERE p.name = $1), w AS (DELETE FROM person_labels l WHERE l.person_id = ANY(ARRAY(SELECT t.id FROM t)) " +
            "AND l.label_id = $2) SELECT t.id, ((SELECT count(*) FROM person_labels pl WHERE pl.person_id = t.id) - " +
            "CASE WHEN EXISTS (SELECT 1 FROM person_labels pl WHERE pl.person_id = t.id AND pl.label_id = $2) THEN 1 ELSE 0 END), false FROM t", Q.TakeLabel);
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
}
