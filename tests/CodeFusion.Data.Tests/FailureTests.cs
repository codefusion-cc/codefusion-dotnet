using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Ops.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// What a result replies when its command doesn't run to its end, and what happens to results used wrongly: each
/// failure a client can cause is a status and a ready reply, never an exception out of the reply.
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class FailureTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    OpsDb Db(PgDataSource? source = null)
    {
        TestDb.Require();
        var db = (source ?? fixture.Reads).CreateContext<OpsDb>();
        db.Bind(_response);
        return db;
    }

    async Task<string> Json<TReply>(TReply reply) where TReply : IReply
    {
        await reply.WriteAsync(_response);
        return _response.Text;
    }

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    [Fact]
    public async Task AnAbortedRequestRunsNothingAndIsBusy()
    {
        var db = Db();
        _response.Abort();
        Assert.Equal("""{"errors":{"body":["the database is busy"]}}""", await Json(db.ByName(U("ann"))));
        Assert.Equal(503, _response.Status);
        Assert.Equal(1, _response.RetryAfter);
        _response.Next();
        var row = await db.ByName(U("ann"));
        Assert.Equal(DataStatus.Canceled, row.Status);
        Assert.False(row.Found);
        Assert.Equal(503, row.Error!.Status);
    }

    // A timeout is the database's own trouble (contention, a slow plan) as much as a broken connection is, so the
    // failure log hears of both kinds, from a reply and from an awaited row. Before, only Broken, Failed and
    // ReaderFailed reached it, and an app saw its 503s with nothing logged.
    [Fact]
    public async Task ALockHeldTooLongIsBusyReportedAndTheWriteDoesNotHappen()
    {
        TestDb.Require();
        var failures = new ConcurrentQueue<(string, DataResult)>();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops_w");
        var source = new PostgresDatabase(settings)
        {
            WriterLockTimeout = TimeSpan.FromMilliseconds(150),
            OnFailure = (q, r) => failures.Enqueue((q, r)),
        }.Open(1, 1);
        try
        {
            var db = Db(source);
            await using var admin = TestDb.Admin("cfdata_ops_w");
            await using var conn = await admin.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var lockRow = new Npgsql.NpgsqlCommand("SELECT 1 FROM people WHERE id = 4 FOR UPDATE", conn, tx))
                await lockRow.ExecuteScalarAsync();
            Assert.Equal("""{"errors":{"body":["the database is busy"]}}""", await Json(db.Touch(4)));
            Assert.Equal(503, _response.Status);
            Assert.True(failures.TryDequeue(out var f), "the reply's lock timeout wasn't reported");
            Assert.Equal(("OpsDb.Touch", DataStatus.LockTimeout, "55P03"), (f.Item1, f.Item2.Status, f.Item2.NativeCode));
            _response.Next();
            var row = await db.Touch(4);
            Assert.Equal(DataStatus.LockTimeout, row.Status);
            Assert.Equal(503, row.Error!.Status);
            Assert.True(failures.TryDequeue(out f), "the row's lock timeout wasn't reported");
            Assert.Equal(("OpsDb.Touch", DataStatus.LockTimeout), (f.Item1, f.Item2.Status));
            await tx.RollbackAsync();
            _response.Next();
            Assert.True((await db.Touch(4)).Found);
            Assert.Empty(failures);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AStatementPastItsTimeoutIsBusyAndReported()
    {
        TestDb.Require();
        var failures = new ConcurrentQueue<(string, DataResult)>();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops").Set("statement_timeout", "100ms");
        var source = new PostgresDatabase(settings) { OnFailure = (q, r) => failures.Enqueue((q, r)) }.Open(1, 1);
        try
        {
            var db = Db(source);
            Assert.Equal("""{"errors":{"body":["the database is busy"]}}""", await Json(db.Sleep(2)));
            Assert.Equal(503, _response.Status);
            Assert.True(failures.TryDequeue(out var f), "the reply's statement timeout wasn't reported");
            Assert.Equal(("OpsDb.Sleep", DataStatus.StatementTimeout, "57014"), (f.Item1, f.Item2.Status, f.Item2.NativeCode));
            _response.Next();
            Assert.Equal(DataStatus.StatementTimeout, (await db.Sleep(2)).Status);
            Assert.True(failures.TryDequeue(out f), "the row's statement timeout wasn't reported");
            Assert.Equal(("OpsDb.Sleep", DataStatus.StatementTimeout), (f.Item1, f.Item2.Status));
            _response.Next();
            Assert.Equal("1", await Json(db.Sleep(0)));
            // not for a client that left: its own abort isn't the database's trouble
            _response.Next();
            var left = db.Sleep(2);
            _response.Abort();
            await Json(left);
            Assert.Empty(failures);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AnErrorOfTheDatabaseIsInternalAndReported()
    {
        TestDb.Require();
        var failures = new ConcurrentQueue<(string, DataResult)>();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", "cfdata_ops");
        var source = new PostgresDatabase(settings) { OnFailure = (q, r) => failures.Enqueue((q, r)) }.Open(1, 1);
        try
        {
            var db = Db(source);
            Assert.Equal("""{"errors":{"body":["internal error"]}}""", await Json(db.Divide(0)));
            Assert.Equal(500, _response.Status);
            Assert.True(failures.TryDequeue(out var f));
            Assert.Equal("OpsDb.Divide", f.Item1);
            Assert.Equal(DataStatus.Failed, f.Item2.Status);
            Assert.Equal("22012", f.Item2.NativeCode);
            _response.Next();
            var row = await db.Divide(0);
            Assert.Equal(DataStatus.Failed, row.Status);
            Assert.Equal(500, row.Error!.Status);
            _response.Next();
            Assert.Equal("1", await Json(db.Divide(1)));
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task ARowItsReaderCantReadIsInternalAndAWriteStillCommits()
    {
        var db = Db();
        await Json(db.NotAName());
        Assert.Equal(500, _response.Status);
        Assert.Equal("""{"errors":{"body":["internal error"]}}""", _response.Text);
        _response.Next();
        var row = await db.NotAName();
        Assert.Equal(DataStatus.ReaderFailed, row.Status);
        Assert.Equal(500, row.Error!.Status);

        var writer = Db(fixture.Writes);
        _response.Next();
        await Json(writer.NotANameWritten(3));
        Assert.Equal(500, _response.Status);
    }

    [Fact]
    public async Task ABrokenConnectionIsInternalAndReported()
    {
        var failures = new ConcurrentQueue<(string, DataResult)>();
        var settings = PgSettings.Parse("Host=127.0.0.1;Port=1;Username=nobody;Database=none");
        settings.ConnectTimeout = TimeSpan.FromSeconds(2);
        var source = new PostgresDatabase(settings) { OnFailure = (q, r) => failures.Enqueue((q, r)) }.Open(1, 1);
        try
        {
            var db = Db(source);
            await Json(db.ByName(U("ann")));
            Assert.Equal(500, _response.Status);
            Assert.True(failures.TryDequeue(out var f));
            Assert.Equal(DataStatus.Broken, f.Item2.Status);
            Assert.NotNull(f.Item2.Error);
        }
        finally
        {
            LoopChecks.Stop(source);
        }
    }

    [Fact]
    public async Task AStoppedSourceRunsNothing()
    {
        TestDb.Require();
        var source = TestDb.Open("cfdata_ops");
        source.Stop();
        var db = Db(source);
        await Json(db.ByName(U("ann")));
        Assert.Equal(500, _response.Status);
        _response.Next();
        Assert.Equal(DataStatus.Stopped, (await db.ByName(U("ann"))).Status);
    }

    // ---------- results used wrongly ----------

    [Fact]
    public async Task AResultRunsOnce()
    {
        var db = Db();
        var one = db.ByName(U("ann"));
        Assert.Equal("""{"id":1,"text":"ann"}""", await Json(one));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await one.WriteAsync(_response));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await one);
    }

    [Fact]
    public async Task AResultDroppedByItsRequestIsGivenBackWhenTheNextBinds()
    {
        var db = Db();
        var dropped = db.ByName(U("ann"));
        // the next request on the connection
        _response.Next();
        db.Bind(_response);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dropped.WriteAsync(_response));
        // and the connection's next results run as they should
        Assert.Equal("""{"id":3,"text":"cid"}""", await Json(db.ByName(U("cid"))));
    }

    [Fact]
    public async Task ManyDroppedResultsAreAllGivenBack()
    {
        var db = Db();
        var results = new One<Name>[40];
        for (int i = 0; i < results.Length; i++) results[i] = db.ByName(U("ann"));
        db.Bind(_response);
        foreach (var r in results) await Assert.ThrowsAsync<InvalidOperationException>(async () => await r.WriteAsync(_response));
        _response.Next();
        Assert.Equal("""{"id":1,"text":"ann"}""", await Json(db.ByName(U("ann"))));
    }

    [Fact]
    public async Task ADefaultResultHasNoQuery()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await default(One<Name>).WriteAsync(_response));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await default(Many<Name>).WriteAsync(_response));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await default(Page<Name>).WriteAsync(_response));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await default(Write<Name>).WriteAsync(_response));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await default(Write).WriteAsync(_response));
    }

    [Fact]
    public async Task AwaitingNeedsABoundRequest()
    {
        TestDb.Require();
        var db = fixture.Reads.CreateContext<OpsDb>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.ByName(U("ann")));
        // replying needs only the response it is given
        Assert.Equal("""{"id":1,"text":"ann"}""", await Json(db.ByName(U("ann"))));
        Assert.Throws<ArgumentNullException>(() => db.Bind(null!));
    }

    [Fact]
    public void AContextBelongsToOneSource()
    {
        TestDb.Require();
        var db = fixture.Reads.CreateContext<OpsDb>();
        Assert.Same(fixture.Reads, db.Source);
        Assert.Throws<InvalidOperationException>(() => new OpsDb().Source);
    }

    [Fact]
    public async Task OneConnectionsRequestsRunOneAfterAnother()
    {
        // a context is per connection: many requests in a row reuse it, its reply state and its slots
        var db = Db();
        for (int i = 0; i < 200; i++)
        {
            _response.Next();
            db.Bind(_response);
            if (i % 2 == 0) Assert.Equal("""{"id":4,"text":"dee"}""", await Json(db.ByName(U("dee"))));
            else Assert.Equal("dee", (await db.ByName(U("dee"))).Value.Text.ToString());
        }
    }

    [Fact]
    public async Task ManyConnectionsAtOnce()
    {
        TestDb.Require();
        var tasks = new Task[16];
        for (int c = 0; c < tasks.Length; c++)
        {
            int me = c;
            tasks[c] = Task.Run(async () =>
            {
                var response = new FakeResponse();
                var db = fixture.Reads.CreateContext<OpsDb>();
                string[] names = ["ann", "ben", "cid", "dee"];
                for (int i = 0; i < 100; i++)
                {
                    response.Next();
                    db.Bind(response);
                    string name = names[(me + i) % 4];
                    await db.ByName(U(name)).WriteAsync(response);
                    Assert.EndsWith($"\"text\":\"{name}\"}}", response.Text);
                }
            });
        }
        await Task.WhenAll(tasks);
    }

    [Theory]
    [InlineData(DataStatus.Busy, false, 503, "too many reads are waiting")]
    [InlineData(DataStatus.Busy, true, 503, "too many writes are waiting")]
    [InlineData(DataStatus.Canceled, false, 503, "the database is busy")]
    [InlineData(DataStatus.LockTimeout, true, 503, "the database is busy")]
    [InlineData(DataStatus.StatementTimeout, false, 503, "the database is busy")]
    [InlineData(DataStatus.UniqueViolation, true, 409, "already exists")]
    [InlineData(DataStatus.Broken, false, 500, "internal error")]
    [InlineData(DataStatus.Stopped, false, 500, "internal error")]
    [InlineData(DataStatus.Failed, true, 500, "internal error")]
    [InlineData(DataStatus.ReaderFailed, false, 500, "internal error")]
    public void EachStatusHasItsReply(DataStatus status, bool write, int code, string message)
    {
        var reply = DataErrors.Default.For(status, write)!;
        Assert.Equal(code, reply.Status);
        Assert.Equal(code == 503 ? 1 : 0, reply.RetryAfter);
        Assert.Equal("{\"errors\":{\"body\":[\"" + message + "\"]}}", Encoding.UTF8.GetString(reply.Body));
        Assert.Null(DataErrors.Default.For(DataStatus.Ok, write));
        var taken = new ErrorReply(422, "{}"u8);
        Assert.Same(taken, DataErrors.Default.For(DataStatus.UniqueViolation, write, taken));
    }
}
