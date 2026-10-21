using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ops.Data;
using Xunit;
using Q = Ops.Data.OpsDb.QuerySql;

namespace CodeFusion.Data.Tests;

/// <summary>
/// A page of each source row's children (<c>SelectMany</c> before <c>Page</c>), as the feed has the newest articles of
/// the authors followed: the same rows as the whole list ordered and paged, though each group is read only as far as
/// the page reaches.
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class GroupPageTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    /// <summary>Label 77 on people 9901 (pets 99011 to 99015), 9902 (99021 to 99023) and 9903 (none); team 977 of
    /// 9901 (age 40) and 9902 (age 20).</summary>
    static readonly Lazy<Task> Rows = new(async () =>
    {
        await using var admin = TestDb.Admin("cfdata_ops_w");
        await admin.Execute("INSERT INTO teams VALUES (977, 'groups'); INSERT INTO labels VALUES (77, 'grouped'); " +
            "INSERT INTO people (id, name, age, active, born, team_id) VALUES (9901, 'g9901', 40, true, now(), 977), (9902, 'g9902', 20, true, now(), 977), (9903, 'g9903', 30, true, now(), NULL); " +
            "INSERT INTO person_labels VALUES (9901, 77), (9902, 77), (9903, 77); " +
            "INSERT INTO pets SELECT 99010 + g, 9901, 'a' || g, 4 FROM generate_series(1, 5) g; INSERT INTO pets SELECT 99020 + g, 9902, 'b' || g, 4 FROM generate_series(1, 3) g");
    });

    async Task<OpsDb> Db()
    {
        TestDb.Require();
        await Rows.Value;
        var db = fixture.Writes.CreateContext<OpsDb>();
        db.Bind(_response);
        return db;
    }

    /// <summary>A page's rows' keys, the pets' (or names'), and its count.</summary>
    async Task<string> Page<T>(Page<T> page) where T : struct
    {
        _response.Next();
        await page.WriteAsync(_response);
        Assert.Equal(200, _response.Status);
        using var doc = JsonDocument.Parse(_response.Text);
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.TryGetProperty("pet", out var pet) ? pet.GetString() : e.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(",", items) + "/" + doc.RootElement.GetProperty("count").GetInt64();
    }

    [Fact]
    public async Task EachGroupsNewestAsFarAsThePageReaches()
    {
        // PersonLabel.Person isn't declared [Required]: its join stays, and drops a link whose person is missing
        Assert.Equal("WITH c AS (SELECT count(*) AS n FROM (SELECT 1 FROM person_labels pl JOIN people p3 ON p3.id = pl.person_id CROSS JOIN LATERAL (SELECT p4.id AS id FROM pets p4 WHERE p4.owner_id = pl.person_id) x " +
            "WHERE pl.label_id = $1 LIMIT 5) s) SELECT p.*, c.n FROM c LEFT JOIN LATERAL (SELECT x3.name AS c0, x3.owner_id AS c1, p7.name AS c2, x3.id AS k FROM (SELECT id FROM (" +
            "SELECT x2.id AS id FROM person_labels pl2 JOIN people p5 ON p5.id = pl2.person_id CROSS JOIN LATERAL (SELECT p6.id AS id FROM pets p6 WHERE p6.owner_id = pl2.person_id ORDER BY p6.id DESC LIMIT $2) x2 " +
            "WHERE pl2.label_id = $1 ORDER BY x2.id DESC LIMIT 3 OFFSET $3) b ORDER BY id DESC LIMIT $4) p2 JOIN pets x3 ON x3.id = p2.id JOIN people p7 ON p7.id = x3.owner_id ORDER BY x3.id DESC) p ON true ORDER BY p.k DESC",
            Q.PetsOfLabel);
        var db = await Db();
        // every pet of both, newest first: 99023, 99022, 99021, 99015, ..., 99011; the count stops at 5
        Assert.Equal("b3,b2,b1/5", await Page(db.PetsOfLabel(77, new Paging(3, 0))));
        Assert.Equal("b1,a5/5", await Page(db.PetsOfLabel(77, new Paging(2, 2))));
        Assert.Equal("a2,a1/5", await Page(db.PetsOfLabel(77, new Paging(3, 6))));
        Assert.Equal("/5", await Page(db.PetsOfLabel(77, new Paging(3, 8))));
        // the page's own maxLimit caps it, and an offset near long.MaxValue doesn't overflow what a group reads
        Assert.Equal("b3,b2,b1/5", await Page(db.PetsOfLabel(77, new Paging(50, 0))));
        Assert.Equal("/5", await Page(db.PetsOfLabel(77, new Paging(3, long.MaxValue - 1))));
        Assert.Equal("/0", await Page(db.PetsOfLabel(78, new Paging(3, 0))));
        Assert.Equal("/5", await Page(db.PetsOfLabel(77, new Paging(-1, -1))));
    }

    [Fact]
    public async Task ATakeOfTheChildrenIsEachGroupsOwnCap()
    {
        Assert.Contains("CROSS JOIN LATERAL (SELECT x.id AS id FROM pets x WHERE x.owner_id = pl.person_id ORDER BY x.id DESC LIMIT 1) x2", Q.NewestPetOfLabel, StringComparison.Ordinal);
        Assert.Contains("ORDER BY x5.id DESC LIMIT LEAST(1, $2)) x6", Q.NewestPetOfLabel, StringComparison.Ordinal);
        var db = await Db();
        // each person's newest pet: 9902's b3, 9901's a5; 9903 has none
        Assert.Equal("b3,a5/2", await Page(db.NewestPetOfLabel(77, new Paging(20, 0))));
        Assert.Equal("a5/2", await Page(db.NewestPetOfLabel(77, new Paging(20, 1))));
    }

    [Fact]
    public async Task AWhereOnTheChildrenAndAnAscendingOrder()
    {
        Assert.Contains("CROSS JOIN LATERAL (SELECT p4.id AS id FROM people p4 WHERE p4.team_id = t2.id AND p4.age >= $2 ORDER BY p4.id LIMIT $3) x2", Q.MembersOf, StringComparison.Ordinal);
        var db = await Db();
        Assert.Equal("9901,9902/2", await Page(db.MembersOf(977, 0, new Paging(10, 0))));
        Assert.Equal("9901/1", await Page(db.MembersOf(977, 30, new Paging(10, 0))));
        Assert.Equal("9902/2", await Page(db.MembersOf(977, 0, new Paging(10, 1))));
        Assert.Equal("/0", await Page(db.MembersOf(978, 0, new Paging(10, 0))));
    }

    [Fact]
    public async Task WithoutForeignKeysARowWhoseParentIsMissingIsLeftOutAsTheJoinLeavesIt()
    {
        TestDb.Require();
        // the tables without their foreign keys, and a link row whose person is missing, with a pet: the model doesn't
        // declare the navigation required, so its join stays, and drops the link as LINQ's inner join does
        const string schema = "cfdata_ghosts";
        await using var admin = TestDb.Admin();
        await admin.Execute($"DROP SCHEMA IF EXISTS {schema} CASCADE; CREATE SCHEMA {schema}; SET search_path = {schema}; {OpsFixture.Schema}; {OpsFixture.Seed}; " +
            "ALTER TABLE person_labels DROP CONSTRAINT person_labels_person_id_fkey; ALTER TABLE pets DROP CONSTRAINT pets_owner_id_fkey; " +
            "INSERT INTO person_labels VALUES (9999, 3); INSERT INTO pets VALUES (99991, 9999, 'ghost', 4)");
        var source = TestDb.Open(schema, readers: 1, writers: 1);
        try
        {
            var db = source.CreateContext<OpsDb>();
            db.Bind(_response);
            // label 3 is cid's (pet nemo) and the missing 9999's
            Assert.Equal("nemo/1", await Page(db.PetsOfLabel(3, new Paging(3, 0))));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }
}
