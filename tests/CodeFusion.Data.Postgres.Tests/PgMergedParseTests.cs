using CodeFusion.Data;
using CodeFusion.Data.Postgres;
using PgProvider = CodeFusion.Data.Postgres.Postgres;
using Xunit;

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// A merged run of keyed updates binds its batch statement, not the statement its jobs carry: a group sent while the
/// batch statement's Parse is still unanswered must wait like one binding any other statement in that state
/// (Postgres skips that Parse if a statement before it in its batch fails).
/// </summary>
public sealed class PgMergedParseTests
{
    [Fact]
    public void AGroupBindingAMergedStatementWhoseParseIsUnansweredWaitsForIt()
    {
        var server = new FakeServer();
        using var conn = PgConnection.Open(server.Settings());
        var s = PgProvider.KeyedUpdate("UPDATE t SET n = $1 WHERE id = $2", "t", "id", 1, ["n"], [0], DataType.Int32, DataType.Int64);
        Assert.NotNull(s.Batch);
        var group = new PgJob?[2];
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var job = group[i] = PgJob.Rent(s);
                job.Int4(i);
                job.Int8(100 + i);
                job.Seal();
            }
            Assert.False(conn.BindsPendingParse(group, 0, 2)); // nothing sent yet
            conn.Begin();
            conn.AddBatch(group, 0, 2, s.Batch!); // the batch statement's Parse goes out with it
            conn.Flush();
            Assert.True(conn.BindsPendingParse(group, 0, 2), "a group binding the merged statement would go out before its Parse is answered");
        }
        finally
        {
            foreach (var job in group) job?.Discard();
        }
    }
}
