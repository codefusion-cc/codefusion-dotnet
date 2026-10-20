using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion;

/// <summary>
/// The response a reply writes into. CodeFusion.Http implements it over its connection; nothing is sent before the
/// handler's reply has finished writing, so a reply may replace what it began (<see cref="Reset"/>).
/// </summary>
public interface IResponse
{
    /// <summary>The status code, 200 unless set. 204 and 304 go out without a body.</summary>
    int Status { get; set; }

    /// <summary>Seconds for a Retry-After header; 0 sends none.</summary>
    int RetryAfter { get; set; }

    /// <summary>The body's JSON writer, ready at the start of the body (Content-Type: application/json).</summary>
    Utf8JsonWriter Json { get; }

    /// <summary>The body as bytes, for a body written without the JSON writer (a pre-encoded error).</summary>
    IBufferWriter<byte> Body { get; }

    /// <summary>Drops what was written, in <see cref="Json"/> and <see cref="Body"/>, sets the status back to 200 and
    /// <see cref="RetryAfter"/> back to 0.</summary>
    void Reset();

    /// <summary>Memory that lives until the response has been sent.</summary>
    IRequestArena Arena { get; }

    /// <summary>Canceled when the client has gone; work for it may be dropped.</summary>
    CancellationToken Aborted { get; }
}

/// <summary>
/// Memory a request may use until its response has been sent, reset for the next request on the same connection. A
/// row a handler awaits is copied here, and its <see cref="Utf8"/> values point into the copy.
/// </summary>
public interface IRequestArena
{
    /// <summary><paramref name="length"/> bytes valid until the response has been sent: write them through
    /// <see cref="ArenaBytes.Span"/>, then hand out <see cref="Utf8"/> views of them.</summary>
    ArenaBytes Allocate(int length);
}

/// <summary>Bytes in a request's arena (<see cref="IRequestArena.Allocate"/>).</summary>
public readonly struct ArenaBytes
{
    /// <summary>The array holding them.</summary>
    // The arena's own array, as ArraySegment's Array is: a copy would be the allocation this type avoids.
#pragma warning disable CA1819
    public byte[] Buffer { get; }
#pragma warning restore CA1819

    /// <summary>Where they start in <see cref="Buffer"/>.</summary>
    public int Start { get; }

    /// <summary>How many there are.</summary>
    public int Length { get; }

    /// <summary>The <paramref name="length"/> bytes of <paramref name="buffer"/> from <paramref name="start"/>.</summary>
    public ArenaBytes(byte[] buffer, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)start > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - start))
            throw new ArgumentOutOfRangeException(nameof(length), "the bytes must lie inside the buffer");
        Buffer = buffer;
        Start = start;
        Length = length;
    }

    /// <summary>The bytes, to write into.</summary>
    public Span<byte> Span => Buffer.AsSpan(Start, Length);

    /// <summary>A view of <paramref name="length"/> of them from <paramref name="offset"/>.</summary>
    public Utf8 Text(int offset, int length)
    {
        if ((uint)offset > (uint)Length || (uint)length > (uint)(Length - offset)) throw new ArgumentOutOfRangeException(nameof(length));
        return Buffer is null ? default : new Utf8(Buffer, Start + offset, length); // default(ArenaBytes) holds nothing
    }
}

/// <summary>
/// What a handler hands back. It writes itself into the response: a query result runs its query and writes the rows
/// it gets, an error writes its pre-encoded body. CodeFusion.Http's generator calls <see cref="WriteAsync"/> on the
/// handler's own return type, which is a struct, so there is no boxing and no virtual call on the reply.
/// </summary>
public interface IReply
{
    /// <summary>Writes the reply into <paramref name="response"/>, completing when it is all written (a reply
    /// that waits for a database completes when its rows have been written). It must not throw for an outcome a
    /// client can cause; that is a status.</summary>
    ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse;
}

/// <summary>
/// A status with a pre-encoded body, such as <c>{"errors":{"body":["article not found"]}}</c>: writing it copies the
/// bytes, with no JSON writer and no string. Make each once (a static field).
/// </summary>
public sealed class ErrorReply : IReply
{
    readonly byte[] _body;

    /// <summary>The status code.</summary>
    public int Status { get; }

    /// <summary>Seconds for a Retry-After header (a 503 the client may retry); 0 sends none.</summary>
    public int RetryAfter { get; }

    /// <summary><paramref name="status"/> (200 to 999: a 1xx is never a final response) with <paramref name="body"/>,
    /// which must be the whole JSON body. A 204 or 304 goes out without a body, so it takes none.</summary>
    public ErrorReply(int status, ReadOnlySpan<byte> body, int retryAfter = 0)
    {
        if (status is < 200 or > 999) throw new ArgumentOutOfRangeException(nameof(status));
        if (status is 204 or 304 && !body.IsEmpty) throw new ArgumentException($"a {status} goes out without a body", nameof(body));
        ArgumentOutOfRangeException.ThrowIfNegative(retryAfter);
        Status = status;
        RetryAfter = retryAfter;
        _body = body.ToArray();
    }

    /// <summary>The body's bytes.</summary>
    public ReadOnlySpan<byte> Body => _body;

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Reset();
        response.Status = Status;
        response.RetryAfter = RetryAfter;
        response.Body.Write(_body);
        return default;
    }
}

/// <summary>
/// Wraps a type's JSON in an object with one property, as <c>{"article": {...}}</c>, wherever the type is a whole
/// reply. Both generators (replies built by a handler, rows a query writes) follow the same JSON rules:
/// <list type="bullet">
/// <item>a property's JSON name is its name in camelCase, or <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>'s;</item>
/// <item><see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> leaves a property out;</item>
/// <item><see cref="Utf8"/> is a JSON string (escaped as JSON needs, UTF-8 passed through), <c>Utf8?</c> a string or null;</item>
/// <item><see cref="RawJson"/> is copied as it is; <see cref="Timestamp"/> is its RFC 3339 text;</item>
/// <item>numbers and bools as JSON numbers and bools; a nested struct is a nested object; a sequence is an array;</item>
/// <item>properties come in declaration order.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class JsonEnvelopeAttribute(string name) : Attribute
{
    /// <summary>The wrapping property's name.</summary>
    public string Name { get; } = name;
}
