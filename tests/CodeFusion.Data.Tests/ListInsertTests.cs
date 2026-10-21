using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Ops.Data;
using Xunit;
using Q = Ops.Data.OpsDb.QuerySql;

namespace CodeFusion.Data.Tests;

/// <summary>The list inserts' own copy of the operator tests' tables: they draw many keys from the sequence, which the
/// other write tests' rows of their own would meet.</summary>
public sealed class ListFixture : IAsyncLifetime
{
    public const string SchemaName = "cfdata_lists";

    /// <summary>One writer loop, and two, so that inserts are concurrent transactions.</summary>
    public PgDataSource Writes { get; private set; } = null!;
    public PgDataSource TwoWriters { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (TestDb.ConnectionString is null) return;
        await using var db = TestDb.Admin();
        await db.Execute($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE; CREATE SCHEMA {SchemaName}; SET search_path = {SchemaName}; {OpsFixture.Schema}; {OpsFixture.Seed}; ANALYZE");
        Writes = TestDb.Open(SchemaName);
        TwoWriters = TestDb.Open(SchemaName, readers: 1, writers: 2);
    }

    public ValueTask DisposeAsync()
    {
        LoopChecks.Stop(Writes, TwoWriters);
        return default;
    }
}

/// <summary>An insert of a list's texts (<see cref="Utf8ListOperators.AsRows"/>): each new one once, in the order
/// asked, skipping those a unique key holds; alone and in a unit.</summary>
public sealed class ListInsertTests(ListFixture fixture) : IClassFixture<ListFixture>
{
    readonly FakeResponse _response = new();

    OpsDb Db()
    {
        TestDb.Require();
        var db = fixture.Writes.CreateContext<OpsDb>();
        db.Bind(_response);
        return db;
    }

    async Task<int> Status<TReply>(TReply reply) where TReply : IReply
    {
        _response.Next();
        await reply.WriteAsync(_response);
        return _response.Status;
    }

    static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    static async Task<List<(long Id, string Name)>> People(string like)
    {
        await using var admin = TestDb.Admin(ListFixture.SchemaName);
        await using var cmd = admin.CreateCommand("SELECT id, name FROM people WHERE name LIKE $1 ORDER BY id");
        cmd.Parameters.AddWithValue(like);
        await using var r = await cmd.ExecuteReaderAsync();
        var all = new List<(long, string)>();
        while (await r.ReadAsync()) all.Add((r.GetInt64(0), r.GetString(1)));
        return all;
    }

    [Fact]
    public async Task EachNewTextOnceInSortedOrderAndTakenOnesSkipped()
    {
        Assert.Equal("INSERT INTO people (name, age, active, born) SELECT DISTINCT n.v, $2, false, now() FROM unnest($1) n(v) ORDER BY n.v ON CONFLICT DO NOTHING", Q.AddPeopleNamed);
        string p = Unique("l");
        var db = Db();
        Assert.Equal(204, await Status(db.AddPeopleNamed(Texts.List(p + "c", p + "a", p + "b", p + "a", "ann", p + "c"), 61)));
        // inserted in the names' order, so their keys follow it
        Assert.Equal([p + "a", p + "b", p + "c"], (await People(p + "%")).Select(x => x.Name));
        await using var admin = TestDb.Admin(ListFixture.SchemaName);
        Assert.Equal(30, await admin.Scalar("SELECT age FROM people WHERE name = 'ann'"));
        // again, and a list of nothing new or nothing at all: no error, nothing more
        Assert.Equal(204, await Status(db.AddPeopleNamed(Texts.List(p + "b", p + "d"), 61)));
        Assert.Equal(204, await Status(db.AddPeopleNamed(Texts.List(p + "a"), 61)));
        Assert.Equal(204, await Status(db.AddPeopleNamed(Utf8List.Empty, 61)));
        Assert.Equal([p + "a", p + "b", p + "c", p + "d"], (await People(p + "%")).Select(x => x.Name));
        // unicode and long texts are texts like any
        string longName = p + new string('ž', 3000) + "😀";
        Assert.Equal(204, await Status(db.AddPeopleNamed(Texts.List(longName, longName), 61)));
        Assert.Equal(longName, (await People(p + "ž%")).Single().Name);
    }

    [Fact]
    public async Task WithoutDistinctATextTwiceIsAConflictAndNothingIsWritten()
    {
        Assert.Equal("INSERT INTO people (name, nick, age, active, born) SELECT n.v, lower(n.v COLLATE \"C\"), 62, false, now() FROM unnest($1) n(v) " +
            "WHERE n.v <> '' ORDER BY upper(n.v COLLATE \"C\") DESC", Q.AddPeopleNamedOnce);
        string p = Unique("o");
        var db = Db();
        Assert.Equal(204, await Status(db.AddPeopleNamedOnce(Texts.List(p + "-A", p + "-b", ""))));
        // ordered by the upper-cased name, descending: b first; the empty text filtered out
        Assert.Equal([p + "-b", p + "-A"], (await People(p + "%")).Select(x => x.Name));
        Assert.Empty(await People(""));
        Assert.Equal(409, await Status(db.AddPeopleNamedOnce(Texts.List(p + "-c", p + "-c"))));
        Assert.Empty(await People(p + "-c"));
    }

    [Fact]
    public async Task InAUnitTheTextsUseTheInsertedRow()
    {
        Assert.Equal("INSERT INTO people (name, age, active, born, team_id) SELECT DISTINCT m.v, 63, true, now(), $2 FROM unnest($1) m(v) ON CONFLICT DO NOTHING", Q.AddTeamOf_2);
        string p = Unique("t");
        var db = Db();
        _response.Next();
        var team = await db.AddTeamOf(901, Texts.U(p), Texts.List(p + "x", p + "y", p + "x"));
        Assert.Equal(901, team.Value.Id);
        await using var admin = TestDb.Admin(ListFixture.SchemaName);
        Assert.Equal(2L, await admin.Scalar($"SELECT count(*) FROM people WHERE team_id = 901 AND name LIKE '{p}%'"));
        // no members: the ThenIf doesn't send the insert at all
        _response.Next();
        Assert.True((await db.AddTeamOf(902, Texts.U(p + "-empty"), Utf8List.Empty)).Found);
        Assert.Equal(0L, await admin.Scalar("SELECT count(*) FROM people WHERE team_id = 902"));
    }

    [Fact]
    public async Task ConcurrentInsertsOfTheSameNewTextsAllSucceedAndInsertEachOnce()
    {
        TestDb.Require();
        for (int round = 0; round < 20; round++)
        {
            string p = Unique("c");
            string[] names = [p + "a", p + "b", p + "c", p + "d"];
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                var response = new FakeResponse();
                var db = fixture.TwoWriters.CreateContext<OpsDb>();
                db.Bind(response);
                response.Next();
                // half the callers send the names in the other order
                var r = await db.AddPeopleNamed(Texts.List(i % 2 == 0 ? names : Enumerable.Reverse(names).ToArray()), 64);
                return r.Status;
            })).ToArray();
            Assert.All(await Task.WhenAll(tasks), s => Assert.Equal(DataStatus.Ok, s));
            Assert.Equal(names, (await People(p + "%")).Select(x => x.Name));
        }
    }
}
