using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Core.Tests;

public class RepliesTests
{
    /// <summary>A response that records what a reply did to it.</summary>
    sealed class FakeResponse : IResponse
    {
        public readonly ArrayBufferWriter<byte> Buffer = new();
        public int Resets;
        public FakeResponse() => Json = new Utf8JsonWriter(Buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        public int Status { get; set; } = 200;
        public int RetryAfter { get; set; }
        public MediaType? ContentType { get; set; }
        public Utf8JsonWriter Json { get; }
        public IBufferWriter<byte> Body => Buffer;
        public void Reset() { Resets++; Json.Reset(Buffer); Buffer.ResetWrittenCount(); Status = 200; RetryAfter = 0; ContentType = null; }
        public IRequestArena Arena => throw new NotSupportedException();
        public CancellationToken Aborted => default;
        public string Text => Encoding.UTF8.GetString(Buffer.WrittenSpan);
    }

    [Fact]
    public async Task AnErrorReplaysItsStatusRetryAfterAndBodyOverWhatWasBegun()
    {
        var error = new ErrorReply(503, """{"errors":{"body":["the database is busy"]}}"""u8, retryAfter: 1);
        var response = new FakeResponse();
        response.Status = 200;
        response.Buffer.Write("""{"articles":[{"slug":"a"""u8); // a page begun before the read failed
        await error.WriteAsync(response);
        Assert.Equal(1, response.Resets);
        Assert.Equal(503, response.Status);
        Assert.Equal(1, response.RetryAfter);
        Assert.Equal("""{"errors":{"body":["the database is busy"]}}""", response.Text);
        Assert.Equal(503, error.Status);
        Assert.Equal(1, error.RetryAfter);
    }

    [Fact]
    public void AnErrorKeepsItsOwnCopyOfTheBody()
    {
        byte[] body = Encoding.ASCII.GetBytes("""{"errors":{"body":["x"]}}""");
        var error = new ErrorReply(404, body);
        body[0] = (byte)'#';
        Assert.Equal((byte)'{', error.Body[0]);
        Assert.Equal(0, error.RetryAfter);
    }

    // A 1xx is an interim response, never a final one: the response's own Status refuses it, so a handler returning
    // one would answer 500. It is refused where it is made, with or without a body.
    [Theory]
    [InlineData(199)]
    [InlineData(103)]
    [InlineData(101)]
    [InlineData(100)]
    [InlineData(99)]
    [InlineData(1000)]
    [InlineData(0)]
    [InlineData(-404)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AStatusOutside200To999IsRefused(int status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorReply(status, "{}"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorReply(status, default));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(999)]
    public void TheEdgesOf200To999AreAccepted(int status) => Assert.Equal(status, new ErrorReply(status, "{}"u8).Status);

    [Theory]
    [InlineData(204)]
    [InlineData(304)]
    public void AStatusThatGoesOutWithoutABodyTakesNone(int status)
    {
        Assert.Throws<ArgumentException>(() => new ErrorReply(status, "{}"u8)); // it would be dropped unseen
        Assert.Equal(status, new ErrorReply(status, default).Status);
    }

    [Fact]
    public void DefaultArenaBytesHoldNothing()
    {
        Assert.True(default(ArenaBytes).Text(0, 0).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => default(ArenaBytes).Text(0, 1));
        Assert.Equal(0, default(ArenaBytes).Span.Length);
    }

    [Fact]
    public void ANegativeRetryAfterIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorReply(503, "{}"u8, retryAfter: -1));

    [Fact]
    public async Task WritingIntoNoResponseIsRefused() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => new ErrorReply(500, "{}"u8).WriteAsync<FakeResponse>(null!).AsTask());

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(3, 7)]
    public void ArenaBytesInsideTheBufferAreAccepted(int start, int length)
    {
        var buffer = new byte[10];
        var bytes = new ArenaBytes(buffer, start, length);
        Assert.Equal(length, bytes.Span.Length);
        Assert.Same(buffer, bytes.Buffer);
        Assert.Equal(start, bytes.Start);
        Assert.Equal(length, bytes.Length);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(11, 0)]
    [InlineData(0, 11)]
    [InlineData(3, 8)]
    [InlineData(10, 1)]
    public void ArenaBytesOutsideTheBufferAreRefused(int start, int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArenaBytes(new byte[10], start, length));

    [Fact]
    public void ArenaBytesWithoutABufferAreRefused() => Assert.Throws<ArgumentNullException>(() => new ArenaBytes(null!, 0, 0));

    [Fact]
    public void TextIsAViewOfTheArenaBytesAtItsOffset()
    {
        var buffer = Encoding.ASCII.GetBytes("##hello world##");
        var bytes = new ArenaBytes(buffer, 2, 11);
        Assert.True(bytes.Text(0, 5) == "hello"u8);
        Assert.True(bytes.Text(6, 5) == "world"u8);
        Assert.True(bytes.Text(11, 0).IsEmpty);
        bytes.Span[0] = (byte)'H';
        Assert.True(bytes.Text(0, 5) == "Hello"u8); // a view of the same bytes
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(12, 0)]
    [InlineData(0, 12)]
    [InlineData(6, 6)]
    public void TextOutsideTheArenaBytesIsRefused(int offset, int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArenaBytes(new byte[15], 2, 11).Text(offset, length));

    [Fact]
    public async Task AContentReplyReplacesWhatWasBegunWithItsTypeStatusAndBytes()
    {
        var hello = new ContentReply(MediaType.PlainText, "Hello, World!"u8);
        var response = new FakeResponse { RetryAfter = 5 };
        response.Buffer.Write("""{"half":"""u8);
        response.Status = 404;
        await hello.WriteAsync(response);
        Assert.Equal(1, response.Resets);
        Assert.Equal(200, response.Status);
        Assert.Equal(0, response.RetryAfter);
        Assert.Same(MediaType.PlainText, response.ContentType);
        Assert.Equal("Hello, World!", response.Text);
        // made once, written again and again: the same bytes each time
        await hello.WriteAsync(response);
        Assert.Equal("Hello, World!", response.Text);
        Assert.Equal(418, (await Written(new ContentReply(MediaType.Html, "<p>tea</p>"u8, 418))).Status);
    }

    static async Task<FakeResponse> Written(ContentReply reply)
    {
        var response = new FakeResponse();
        await reply.WriteAsync(response);
        return response;
    }

    [Fact]
    public async Task AContentReplyRefusesWhatHttpCantSend()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentReply(MediaType.PlainText, "x"u8, 199));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentReply(MediaType.PlainText, "x"u8, 1000));
        Assert.Throws<ArgumentException>(() => new ContentReply(MediaType.PlainText, "x"u8, 204));
        Assert.Throws<ArgumentNullException>(() => new ContentReply(null!, "x"u8));
        Assert.Equal(204, (await Written(new ContentReply(MediaType.PlainText, default, 204))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await default(ContentReply).WriteAsync(new FakeResponse()));
    }

    [Theory]
    [InlineData("application/json", "Content-Type: application/json\r\n")]
    [InlineData("text/html; charset=utf-8", "Content-Type: text/html; charset=utf-8\r\n")]
    [InlineData("a/b", "Content-Type: a/b\r\n")]
    public void AMediaTypeIsItsHeaderLine(string value, string line)
    {
        var type = new MediaType(value);
        Assert.Equal(value, type.Value);
        Assert.Equal(value, type.ToString());
        Assert.Equal(line, Encoding.ASCII.GetString(type.HeaderLine));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" text/plain")]
    [InlineData("text/plain ")]
    [InlineData("text/plain\r\nSet-Cookie: a=b")]
    [InlineData("text/plain\n")]
    [InlineData("text/plän")]
    [InlineData("text/\u007f")]
    public void AMediaTypeThatWouldBreakTheHeadIsRefused(string value) =>
        Assert.Throws<ArgumentException>(() => new MediaType(value));

    [Fact]
    public void AMediaTypeIsAtMostMaxLengthCharacters()
    {
        Assert.Equal(MediaType.MaxLength, new MediaType(new string('a', MediaType.MaxLength)).Value.Length);
        Assert.Throws<ArgumentException>(() => new MediaType(new string('a', MediaType.MaxLength + 1)));
        Assert.Throws<ArgumentNullException>(() => new MediaType(null!));
    }

    [Fact]
    public void TheWellKnownTypesAreMadeOnce()
    {
        Assert.Same(MediaType.Json, MediaType.Json);
        Assert.Equal("application/json", MediaType.Json.Value);
        Assert.Equal("text/plain", MediaType.PlainText.Value);
        Assert.Equal("text/html; charset=utf-8", MediaType.Html.Value);
    }

    [Fact]
    public async Task AnErrorReplySetsTheTypeBackToJson()
    {
        var response = new FakeResponse { ContentType = MediaType.Html };
        await new ErrorReply(404, "{}"u8).WriteAsync(response);
        Assert.Null(response.ContentType);
    }
}
