using System.Text;
using System.Threading.Tasks;
using Ops.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// UpdateIf and DeleteIf as a caller sees them, the same on every provider: the rows whose condition holds are written,
/// the reply is the written row with the smallest key, refused when rows were found and none was written, not found when
/// none was. Each provider's test project runs them against its own database (its subclass gives the database and SQL).
/// </summary>
public abstract class ConditionalWriteTests
{
    protected FakeResponse Response { get; } = new();

    /// <summary>The operator tests' write database, bound to <see cref="Response"/>.</summary>
    protected abstract OpsDb Writes();

    /// <summary>Runs SQL on the write database, outside the context.</summary>
    protected abstract Task Execute(string sql);

    /// <summary>The first column of every row of <paramref name="sql"/>, joined with commas.</summary>
    protected abstract Task<string> Column(string sql);

    /// <summary>The provider's SQL for the current time, as the born column takes it.</summary>
    protected abstract string Now { get; }

    async Task<string> Json<TReply>(TReply reply) where TReply : IReply
    {
        Response.Next();
        await reply.WriteAsync(Response);
        return Response.Text;
    }

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    [Fact]
    public async Task EachRowWhoseConditionHoldsIsWritten()
    {
        var db = Writes();
        await Execute("INSERT INTO teams VALUES (71, 'green'); INSERT INTO people (id, name, age, active, born, team_id) VALUES " +
            $"(7111, 'cw7111', 50, true, {Now}, 71), (7112, 'cw7112', 50, true, {Now}, 71), (7113, 'cw7113', 50, false, {Now}, 71), (7121, 'cw7121', 50, true, {Now}, NULL); " +
            "INSERT INTO pets VALUES (71211, 7121, 'a', 4), (71212, 7121, 'b', 4), (71213, 7121, 'c', 2)");
        await Json(db.DeactivateTeam(71));
        Assert.Equal(204, Response.Status);
        Assert.Equal("", await Column("SELECT id FROM people WHERE team_id = 71 AND active"));
        await Json(db.DeletePetsIf(7121, 4));
        Assert.Equal(204, Response.Status);
        Assert.Equal("71213", await Column("SELECT id FROM pets WHERE owner_id = 7121 ORDER BY id"));
    }

    [Fact]
    public async Task TheReplyIsTheWrittenRowWithTheSmallestKey()
    {
        var db = Writes();
        // the larger keys first in the table, and a refused row before both
        await Execute("INSERT INTO teams VALUES (72, 'gray'); INSERT INTO people (id, name, age, active, born, team_id) VALUES " +
            $"(7231, 'cw7231', 50, false, {Now}, 72), (7233, 'cw7233', 50, true, {Now}, 72), (7232, 'cw7232', 50, true, {Now}, 72), (7221, 'cw7221', 50, true, {Now}, NULL); " +
            "INSERT INTO pets VALUES (72211, 7221, 'two', 2), (72213, 7221, 'four', 4), (72212, 7221, 'four', 4)");
        Response.Next();
        var deleted = await db.DeleteAPetIf(7221, 4);
        Assert.False(deleted.Refused);
        Assert.Equal(72212, deleted.Value);
        Assert.Equal("72211", await Column("SELECT id FROM pets WHERE owner_id = 7221 ORDER BY id"));
        Assert.Equal("""{"id":7232,"text":"cw7232"}""", await Json(db.NickTeamIfActive(72, U("n"))));
        Assert.Equal(200, Response.Status);
        Assert.Equal("7232,7233", await Column("SELECT id FROM people WHERE team_id = 72 AND nick = 'n' ORDER BY id"));
    }

    [Fact]
    public async Task ARowItsOwnSetMakesFailTheConditionIsWrittenNotRefused()
    {
        var db = Writes();
        await Execute("INSERT INTO teams VALUES (73, 'teal'); INSERT INTO people (id, name, age, active, born, team_id) VALUES " +
            $"(7331, 'cw7331', 50, false, {Now}, 73), (7333, 'cw7333', 50, true, {Now}, 73), (7332, 'cw7332', 50, true, {Now}, 73)");
        Assert.Equal("""{"id":7332,"text":"cw7332"}""", await Json(db.DeactivateTeamReturning(73)));
        Assert.Equal(200, Response.Status);
        Assert.Equal("", await Column("SELECT id FROM people WHERE team_id = 73 AND active"));
        // now every row found is refused
        await Json(db.DeactivateTeamReturning(73));
        Assert.Equal(403, Response.Status);
    }

    [Fact]
    public async Task AllRowsRefusedIsForbiddenAndWritesNothing()
    {
        var db = Writes();
        await Execute("INSERT INTO teams VALUES (74, 'navy'); INSERT INTO people (id, name, age, active, born, team_id) VALUES " +
            $"(7441, 'cw7441', 50, false, {Now}, 74), (7442, 'cw7442', 50, false, {Now}, 74); " +
            "INSERT INTO pets VALUES (74411, 7441, 'two', 2), (74412, 7441, 'three', 3)");
        await Json(db.NickTeamIfActive(74, U("n")));
        Assert.Equal(403, Response.Status);
        Assert.Equal("", await Column("SELECT id FROM people WHERE team_id = 74 AND nick IS NOT NULL"));
        Response.Next();
        var refused = await db.DeleteAPetIf(7441, 4);
        Assert.True(refused.Refused);
        Assert.Equal("74411,74412", await Column("SELECT id FROM pets WHERE owner_id = 7441 ORDER BY id"));
    }

    [Fact]
    public async Task NoRowFoundIsNotFound()
    {
        var db = Writes();
        await Json(db.DeactivateTeamReturning(7999));
        Assert.Equal(404, Response.Status);
        Response.Next();
        var none = await db.DeleteAPetIf(7999, 4);
        Assert.False(none.Found);
        Assert.False(none.Refused);
    }
}
