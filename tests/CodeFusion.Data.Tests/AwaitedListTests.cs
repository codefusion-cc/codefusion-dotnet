using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Conduit.Data;
using Ops.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// A <see cref="Many{T}"/> awaited instead of written: every row copied into the request's arena, in the order the
/// query gives them, with the same values its JSON reply has. The rows stay as they are until the next request.
/// </summary>
[Collection(nameof(OpsTests))]
public sealed class AwaitedListTests(OpsFixture fixture)
{
    readonly FakeResponse _response = new();

    OpsDb Db()
    {
        TestDb.Require();
        var db = fixture.Reads.CreateContext<OpsDb>();
        Next(db);
        return db;
    }

    void Next(OpsDb db)
    {
        _response.Next();
        db.Bind(_response);
    }

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    [Fact]
    public async Task EveryRowComesWithEveryKindOfValue()
    {
        var rows = await Db().Cards();
        Assert.True(rows.Found);
        Assert.False(rows.Failed);
        Assert.Null(rows.Error);
        Assert.Equal(4, rows.Count);
        Assert.Equal([1L, 2, 3, 4], rows.Span.ToArray().Select(r => r.Id));
        Assert.Equal(["ann", "ben", "cid", "dee"], rows.Span.ToArray().Select(r => r.Name.ToString()));
        Assert.Equal(["A", "Benny", "c", null], rows.Span.ToArray().Select(r => r.Nick?.ToString()));
        Assert.Equal([30, 25, 40, 30], rows.Span.ToArray().Select(r => r.Age));
        Assert.Equal([1.5, null, 3.25, 0.0], rows.Span.ToArray().Select(r => r.Score));
        Assert.Equal([true, false, true, true], rows.Span.ToArray().Select(r => r.Active));
        Assert.Equal("1990-06-15T08:30:00.000Z", rows[1].Born.ToString());
        int seen = 0;
        foreach (ref readonly var r in rows) seen += (int)r.Id;
        Assert.Equal(10, seen);
    }

    [Fact]
    public async Task AListOfScalarsAndAnEmptyList()
    {
        var db = Db();
        var names = await db.NamesFrom(30);
        Assert.Equal(["ann", "cid", "dee"], names.Span.ToArray().Select(n => n.ToString()));
        var none = await db.NamesFrom(1000);
        Assert.True(none.Found);
        Assert.Equal(0, none.Count);
        Assert.True(none.Span.IsEmpty);
    }

    [Fact]
    public async Task TwoListsOfOneTypeInOneRequestKeepTheirOwnRows()
    {
        var db = Db();
        var older = await db.NamesFrom(35);
        var all = await db.NamesFrom(0);
        Assert.Equal(["cid"], older.Span.ToArray().Select(n => n.ToString()));
        Assert.Equal(["ann", "ben", "cid", "dee"], all.Span.ToArray().Select(n => n.ToString()));
    }

    [Fact]
    public async Task TheNextRequestReusesTheListAndItsArray()
    {
        var db = Db();
        var first = await db.NickIs(U("c"));
        Assert.Equal(3, first[0].Id);
        Next(db);
        var second = await db.NickIs(null);
        Assert.Equal(4, second[0].Id);
        // the first request's rows were valid until the next one: its list, array and all, now holds the second's,
        // which a debug build catches
        if (EachTests.Debug()) Assert.Throws<InvalidOperationException>(() => first[0]);
        else Assert.Equal(4, first[0].Id);
    }

    [Fact]
    public async Task AListNobodyTookIsNotReusedWhileItMayStillFill()
    {
        var db = Db();
        // started, never taken: the run may still be adding rows after the request has ended
        var dropped = db.Cards().GetAwaiter();
        Next(db);
        var next = await db.Cards();
        Assert.Equal(4, next.Count);
        while (!dropped.IsCompleted) await Task.Delay(1);
        Assert.Equal(4, next.Count);
        Assert.Equal([1L, 2, 3, 4], next.Span.ToArray().Select(r => r.Id));
        // the dropped run wrote nothing over this request's text either
        Assert.Equal(["ann", "ben", "cid", "dee"], next.Span.ToArray().Select(r => r.Name.ToString()));
        Assert.Equal(["A", "Benny", "c", "null"], next.Span.ToArray().Select(r => r.Nick?.ToString() ?? "null"));
        // read at last, so its command goes back to the pool (Debug counts the ones that don't); it has completed
#pragma warning disable xUnit1031
        _ = dropped.GetResult();
#pragma warning restore xUnit1031
    }

    [Fact]
    public async Task AnAbortedRequestsListIsCanceledWithItsReply()
    {
        var db = Db();
        _response.Abort();
        try
        {
            var rows = await db.Cards();
            Assert.Equal(DataStatus.Canceled, rows.Status);
            Assert.True(rows.Failed);
            Assert.False(rows.Found);
            Assert.Equal(0, rows.Count);
            Assert.NotNull(rows.Error);
        }
        finally
        {
            _response.Unabort();
        }
    }

    [Fact]
    public async Task AResultRunsOnce()
    {
        var db = Db();
        var cards = db.Cards();
        _ = await cards;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cards);
        Assert.Throws<InvalidOperationException>(() => default(Many<Card>).GetAwaiter());
    }
}

/// <summary>Lists of a row's children, awaited: a missing row is told from an empty list, as the reply tells them.</summary>
[Collection(nameof(ConduitCollection))]
public sealed class AwaitedChildListTests(ConduitFixture fixture)
{
    readonly FakeResponse _response = new();

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    static readonly ErrorReply ArticleNotFound = new(404, """{"errors":{"body":["article not found"]}}"""u8);

    [Fact]
    public async Task ChildrenEmptyChildrenAndAMissingSource()
    {
        var db = fixture.Context(_response);
        var comments = await db.CommentsOf(U("how-to-train-your-dragon"), 1);
        Assert.True(comments.Found);
        Assert.Equal([2L, 1], comments.Span.ToArray().Select(c => c.Id));
        Assert.Equal(["Second", "First!"], comments.Span.ToArray().Select(c => c.Body.ToString()));
        Assert.Equal("2025-04-02T10:00:00.000Z", comments[1].CreatedAt.ToString());

        var none = await db.CommentsOf(U("dragons-2"), 1);
        Assert.True(none.Found);
        Assert.Equal(0, none.Count);

        var missing = await db.CommentsOf(U("missing"), 1);
        Assert.False(missing.Found);
        Assert.False(missing.Failed);
        Assert.Equal(404, missing.Error!.Status);
        Assert.Equal("""{"errors":{"body":["not found"]}}""", Encoding.UTF8.GetString(missing.Error.Body));

        var mine = await db.CommentsOf(U("missing"), 1).OrNotFound(ArticleNotFound);
        Assert.Same(ArticleNotFound, mine.Error);
    }
}
