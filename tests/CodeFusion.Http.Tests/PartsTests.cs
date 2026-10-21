using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>The pieces the generated code builds on: the arena, replies, the query source.</summary>
public class PartsTests
{
    /// <summary>A grown request or response buffer goes back to ArrayPool.Shared, which lends it to any code in the
    /// process next: none of the request's bytes (a token, a password) go with it.</summary>
    [Fact]
    public void ALargerArrayGoesBackToTheSharedPoolWithNothingOfTheRequestInIt()
    {
        var pool = new BufferPool(64, 4);
        byte[] big = pool.RentLarger(5000);
        big.AsSpan().Fill((byte)'s');
        pool.Return(big);
        byte[] again = ArrayPool<byte>.Shared.Rent(5000);
        try
        {
            Assert.Same(big, again); // the same thread gets back the array it returned last
            Assert.Equal(-1, again.AsSpan().IndexOf((byte)'s'));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(again);
        }
    }

    [Fact]
    public void TheArenaCutsChunksAndGivesThemBack()
    {
        var pool = new BufferPool(4096, 8);
        var arena = new RequestArena(pool);
        ArenaBytes a = arena.Allocate(4000), b = arena.Allocate(200), big = arena.Allocate(10_000), z = arena.Allocate(0);
        Assert.Same(big.Buffer, z.Buffer);
        Assert.NotSame(a.Buffer, b.Buffer); // 200 more don't fit the first chunk
        Assert.True(big.Buffer.Length >= 10_000);
        a.Span.Fill(1);
        b.Span.Fill(2);
        Assert.All(a.Span.ToArray(), x => Assert.Equal(1, x));
        Assert.Equal(2, pool.Created);
        arena.Reset();
        Assert.Equal(2, pool.Available); // both chunks back; the big array, the current one, went to the shared pool
        ArenaBytes again = arena.Allocate(10);
        Assert.Equal(2, pool.Created);   // reused
        Assert.Equal(0, again.Start);
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Allocate(-1));
        arena.Reset();
        arena.Reset();
    }

    [Fact]
    public void ManyAllocationsInOneRequest()
    {
        var pool = new BufferPool(4096, 64);
        var arena = new RequestArena(pool);
        for (int i = 0; i < 100; i++) arena.Allocate(1000).Span.Fill((byte)i);
        arena.Reset();
        Assert.Equal(pool.Created - 1, pool.Available); // the last chunk is kept for the next request
        arena.Release();
        Assert.Equal(pool.Created, pool.Available);
        arena.Release();
    }

    [Fact]
    public void ABusyConnectionKeepsOneChunk()
    {
        var pool = new BufferPool(4096, 8);
        var arena = new RequestArena(pool);
        ArenaBytes first = arena.Allocate(100);
        arena.Reset();
        ArenaBytes second = arena.Allocate(100);
        Assert.Same(first.Buffer, second.Buffer);  // no trip to the pool between requests
        Assert.Equal(0, second.Start);
        Assert.Equal((1, 0), (pool.Created, pool.Available));
        arena.Release();
        Assert.Equal((1, 1), (pool.Created, pool.Available));
    }

    [Fact]
    public async System.Threading.Tasks.Task AnIdleConnectionHoldsNoChunk()
    {
        await using HttpApp app = Sample.SampleApp.Build();
        await using var check = new AppCheck(app);
        app.Start();
        using Client c = await Client.ConnectAsync(app.LocalEndPoint);
        await c.SendAsync("GET /api/articles/x?tag=a%20b HTTP/1.1\r\nHost: x\r\n\r\n"); // decoding takes arena bytes
        Assert.Equal(200, (await c.ReadAsync()).Status);
        long until = Environment.TickCount64 + 5000;
        while (app.ArenaChunks.Available != app.ArenaChunks.Created && Environment.TickCount64 < until) await System.Threading.Tasks.Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.Equal(1, app.ArenaChunks.Created);
        Assert.Equal(1, app.ArenaChunks.Available);
    }

    sealed class Recorder : IResponse
    {
        public int Status { get; set; } = 200;
        public int RetryAfter { get; set; }
        public MediaType? ContentType { get; set; }
        public System.Text.Json.Utf8JsonWriter Json => throw new NotSupportedException();
        public System.Buffers.ArrayBufferWriter<byte> Written { get; } = new();
        public System.Buffers.IBufferWriter<byte> Body => Written;
        public void Reset()
        {
            Written.ResetWrittenCount();
            Status = 200;
            RetryAfter = 0;
            ContentType = null;
        }
        public IRequestArena Arena => throw new NotSupportedException();
        public System.Threading.CancellationToken Aborted => default;
    }

    static (int, int, string) Write(Reply r)
    {
        var x = new Recorder();
        x.Body.Write("junk"u8);
#pragma warning disable CA2012 // a reply without a database writes at once
        Assert.True(r.WriteAsync(x).IsCompletedSuccessfully);
#pragma warning restore CA2012
        return (x.Status, x.RetryAfter, Encoding.UTF8.GetString(x.Written.WrittenSpan));
    }

    static readonly ErrorReply E = new(418, """{"e":1}"""u8, retryAfter: 9);

    [Fact]
    public void RepliesWriteTheirStatusAndBody()
    {
        Assert.Equal((204, 0, ""), Write(Reply.NoContent()));
        Assert.Equal((200, 0, ""), Write(default));
        Assert.Equal((202, 0, ""), Write(Reply.WithStatus(202)));
        Assert.Equal((404, 0, """{"e":1}"""), Write(Reply.NotFound(E)));        // the reply's status wins
        Assert.Equal((401, 0, """{"e":1}"""), Write(Reply.Unauthorized(E)));
        Assert.Equal((403, 0, """{"e":1}"""), Write(Reply.Forbidden(E)));
        Assert.Equal((422, 0, """{"e":1}"""), Write(Reply.Unprocessable(E)));
        Assert.Equal((503, 2, """{"e":1}"""), Write(Reply.Busy(E, 2)));
        Assert.Equal((418, 9, """{"e":1}"""), Write(E));                          // its own status, by conversion
        Assert.Throws<ArgumentOutOfRangeException>(() => Reply.WithStatus(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Reply.Busy(E, -1));
        Assert.Throws<ArgumentNullException>(() => Reply.NotFound(null!));
        Assert.Equal(Reply.NotFound(E), Reply.NotFound(E));
        Assert.NotEqual(Reply.NotFound(E), Reply.Forbidden(E));
    }

    [Fact]
    public void ReplyOfTCarriesAValueOrAReplyWithout()
    {
        Reply<int> ok = Reply.Ok(5), created = Reply.Created(6), missing = Reply.NotFound(E), error = E;
        Assert.Equal((true, 200, 5, (ErrorReply?)null), (ok.HasValue, ok.Status, ok.Value, ok.Error));
        Assert.Equal((true, 201, 6), (created.HasValue, created.Status, created.Value));
        Assert.Equal((false, 404, (ErrorReply?)E), (missing.HasValue, missing.Status, missing.Error));
        Assert.Equal((false, 418), (error.HasValue, error.Status));
        Assert.Equal(Reply.NotFound(E), missing.WithoutValue);
        Assert.Equal(default, ok.WithoutValue);
    }

    [Theory]
    [InlineData("tag=plain", "plain", true)]
    [InlineData("tag=a+b", "a b", false)]
    [InlineData("tag=%C3%A9", "é", false)]
    [InlineData("tag=%FF", "�", false)]
    [InlineData("tag=é", "é", true)]
    [InlineData("tag=", "", true)]
    public void QueryTextPointsIntoTheRequestUnlessItNeedsDecoding(string query, string value, bool inRequest)
    {
        byte[] buffer = Encoding.UTF8.GetBytes("GET /x?" + query + " HTTP/1.1");
        var arena = new RequestArena(new BufferPool(4096, 4));
        var q = new QuerySource(buffer, 7, Encoding.UTF8.GetByteCount(query), arena);
        Assert.True(q.TryText("tag"u8, out Utf8 text));
        Assert.Equal(value, text.ToString());
        if (text.Length > 0) Assert.Equal(inRequest, text.Span.Overlaps(buffer));
        Assert.False(q.TryText("other"u8, out _));
    }

    [Theory]
    [InlineData("n=5", 0, 10, QueryNumber.Valid, 5)]
    [InlineData("n=10", 0, 10, QueryNumber.Valid, 10)]
    [InlineData("n=11", 0, 10, QueryNumber.Invalid, 0)]
    [InlineData("n=0", 1, 10, QueryNumber.Invalid, 0)]
    [InlineData("n=", 0, 10, QueryNumber.Invalid, 0)]
    [InlineData("n=%35", 0, 10, QueryNumber.Valid, 5)]
    [InlineData("n=+5", 0, 10, QueryNumber.Invalid, 0)]
    [InlineData("n=9223372036854775807", 0, long.MaxValue, QueryNumber.Valid, long.MaxValue)]
    [InlineData("n=9223372036854775808", 0, long.MaxValue, QueryNumber.Invalid, 0)]
    [InlineData("m=5", 0, 10, QueryNumber.Absent, 0)]
    public void QueryNumbers(string query, long min, long max, QueryNumber expected, long value)
    {
        byte[] buffer = Encoding.ASCII.GetBytes(query);
        var q = new QuerySource(buffer, 0, buffer.Length, new RequestArena(new BufferPool(4096, 4)));
        Assert.Equal((expected, value), (q.TryNumber("n"u8, min, max, out long v), v));
    }
}
