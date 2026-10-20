using System;
using System.Threading.Tasks;

namespace CodeFusion.Http;

/// <summary>
/// A reply with no value of its own: a status, or a status with a pre-encoded error body. It converts to any
/// <see cref="Reply{T}"/>, so a handler can return <c>Reply.NotFound(...)</c> in one branch and
/// <c>Reply.Ok(view)</c> in another.
/// </summary>
public readonly struct Reply : IReply, IEquatable<Reply>
{
    readonly int _status;
    readonly int _retryAfter;

    Reply(int status, ErrorReply? error, int retryAfter)
    {
        _status = status;
        Error = error;
        _retryAfter = retryAfter;
    }

    /// <summary>The status; 200 for <c>default</c>.</summary>
    public int Status => _status == 0 ? 200 : _status;

    /// <summary>The error body, or null for a reply with none.</summary>
    public ErrorReply? Error { get; }

    /// <summary>Seconds for a Retry-After header; 0 sends none.</summary>
    public int RetryAfter => _retryAfter;

    /// <summary>201 Created with <paramref name="value"/> as the body.</summary>
    public static Reply<T> Created<T>(T value) => new(201, value);

    /// <summary>200 OK with <paramref name="value"/> as the body.</summary>
    public static Reply<T> Ok<T>(T value) => new(200, value);

    /// <summary>204 No Content.</summary>
    public static Reply NoContent() => new(204, null, 0);

    /// <summary><paramref name="status"/> with an empty body.</summary>
    public static Reply WithStatus(int status) => new(Check(status), null, 0);

    /// <summary>401 with <paramref name="error"/>'s body.</summary>
    public static Reply Unauthorized(ErrorReply error) => new(401, NotNull(error), 0);

    /// <summary>403 with <paramref name="error"/>'s body.</summary>
    public static Reply Forbidden(ErrorReply error) => new(403, NotNull(error), 0);

    /// <summary>404 with <paramref name="error"/>'s body.</summary>
    public static Reply NotFound(ErrorReply error) => new(404, NotNull(error), 0);

    /// <summary>422 with <paramref name="error"/>'s body.</summary>
    public static Reply Unprocessable(ErrorReply error) => new(422, NotNull(error), 0);

    /// <summary>503 with <paramref name="error"/>'s body and a Retry-After of <paramref name="retryAfter"/> seconds:
    /// a full queue, or a database that was blocked, which the client may try again.</summary>
    public static Reply Busy(ErrorReply error, int retryAfter = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryAfter);
        return new(503, NotNull(error), retryAfter);
    }

    /// <summary><paramref name="error"/> with its own status and Retry-After.</summary>
    public static Reply Failed(ErrorReply error) => new(NotNull(error).Status, error, error.RetryAfter);

    /// <summary><paramref name="error"/> with its own status and Retry-After.</summary>
    public static implicit operator Reply(ErrorReply error) => Failed(error);

    /// <inheritdoc/>
    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse
    {
        ArgumentNullException.ThrowIfNull(response);
        if (Error is { } error)
        {
            // An ErrorReply writes at once; the status it carries gives way to this reply's.
            ValueTask written = error.WriteAsync(response);
            response.Status = Status;
            response.RetryAfter = _retryAfter;
            return written;
        }
        response.Reset();
        response.Status = Status;
        response.RetryAfter = _retryAfter;
        return default;
    }

    /// <inheritdoc/>
    public bool Equals(Reply other) => Status == other.Status && _retryAfter == other._retryAfter && ReferenceEquals(Error, other.Error);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Reply other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Status, _retryAfter, Error);

    /// <summary>The same status, body and Retry-After.</summary>
    public static bool operator ==(Reply left, Reply right) => left.Equals(right);

    /// <summary>A different status, body or Retry-After.</summary>
    public static bool operator !=(Reply left, Reply right) => !left.Equals(right);

    static int Check(int status) => status is >= 200 and <= 999 ? status : throw new ArgumentOutOfRangeException(nameof(status));

    static ErrorReply NotNull(ErrorReply error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error;
    }
}

/// <summary>
/// A reply that carries a value written as the JSON body (<see cref="Reply.Ok{T}"/>, <see cref="Reply.Created{T}"/>),
/// or any <see cref="Reply"/>. The endpoint generator writes <typeparamref name="T"/> with generated code that follows
/// <see cref="JsonEnvelopeAttribute"/>'s rules; a <typeparamref name="T"/> that is itself an <see cref="IReply"/>
/// writes itself after the status is set.
/// </summary>
/// <typeparam name="T">The body's type.</typeparam>
public readonly struct Reply<T>
{
    readonly Reply _reply;
    readonly int _status;

    internal Reply(int status, T value)
    {
        _status = status;
        Value = value;
        HasValue = true;
        _reply = default;
    }

    Reply(Reply reply)
    {
        _reply = reply;
        _status = 0;
        Value = default!;
        HasValue = false;
    }

    /// <summary>Whether there is a value to write.</summary>
    public bool HasValue { get; }

    /// <summary>The value; <c>default</c> without one.</summary>
    public T Value { get; }

    /// <summary>The status.</summary>
    public int Status => HasValue ? _status : _reply.Status;

    /// <summary>The error body, or null.</summary>
    public ErrorReply? Error => HasValue ? null : _reply.Error;

    /// <summary>The reply without a value this one holds (its status and error body); <c>default</c> when it has a value.</summary>
    public Reply WithoutValue => HasValue ? default : _reply;

    /// <summary>A reply without a value, such as <c>Reply.NotFound(...)</c>.</summary>
    public static implicit operator Reply<T>(Reply reply) => new(reply);

    /// <summary><paramref name="error"/> with its own status.</summary>
    public static implicit operator Reply<T>(ErrorReply error) => new(Reply.Failed(error));
}
