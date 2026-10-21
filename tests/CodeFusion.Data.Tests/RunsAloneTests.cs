using System.Text;
using System.Threading.Tasks;
using Ops.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>A query method marked <see cref="RunsAloneAttribute"/> runs apart from the groups, on the provider's own
/// connection for it; one without the mark never does.</summary>
[Collection(nameof(OpsTests))]
public sealed class RunsAloneTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    OpsDb Db(bool write)
    {
        TestDb.Require();
        var db = (write ? fixture.Writes : fixture.Reads).CreateContext<OpsDb>();
        _response.Next();
        db.Bind(_response);
        return db;
    }

    [Fact]
    public async Task AWriteMarkedRunsAloneRunsAloneAndCommits()
    {
        var counters = fixture.Writes.Writer.Counters;
        long alone = counters.Alone;
        Assert.True((await Db(write: true).AddLabelAlone(9301, U("alone-9301"))).IsOk);
        Assert.Equal(1, counters.Alone - alone);
        Assert.True((await Db(write: true).AddLabelOnce(9302, U("grouped-9302"))).IsOk);
        Assert.Equal(1, counters.Alone - alone);
        await using var admin = TestDb.Admin("cfdata_ops_w");
        Assert.Equal(2L, await admin.Scalar("SELECT count(*) FROM labels WHERE id IN (9301, 9302)"));
    }

    [Fact]
    public async Task AReadMarkedRunsAloneRunsAloneAndAnswers()
    {
        var counters = fixture.Reads.Reader.Counters;
        long alone = counters.Alone;
        var row = await Db(write: false).ByNameAlone(U("cid"));
        Assert.Equal(3, row.Value.Id);
        Assert.Equal(1, counters.Alone - alone);
        Assert.Equal(3, (await Db(write: false).ByName(U("cid"))).Value.Id);
        Assert.Equal(1, counters.Alone - alone);
    }

    [Fact]
    public async Task AKeyedUpdateMarkedRunsAloneRunsAloneAndIsNeverMerged()
    {
        var db = Db(write: true);
        var counters = fixture.Writes.Writer.Counters;
        long alone = counters.Alone, merged = counters.Merged;
        var pause = db.Pause();
        await Task.Delay(30);
        var first = Db(write: true).SetAgeAlone(1, 31);
        var second = Db(write: true).SetAgeAlone(2, 26);
        Assert.True((await pause).IsOk);
        Assert.True((await first).IsOk);
        Assert.True((await second).IsOk);
        Assert.Equal(2, counters.Alone - alone);
        Assert.Equal(0, counters.Merged - merged);
        await using var admin = TestDb.Admin("cfdata_ops_w");
        Assert.Equal("31 26", await admin.Scalar("SELECT string_agg(age::text, ' ' ORDER BY id) FROM people WHERE id IN (1, 2)"));
        await admin.Execute("UPDATE people SET age = 30 WHERE id = 1; UPDATE people SET age = 25 WHERE id = 2");
    }
}
