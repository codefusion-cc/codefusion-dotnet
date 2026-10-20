using System;

namespace CodeFusion.Data;

/// <summary>
/// The replies a result sends when it has no rows to send: a failure, a refused or empty result. Each is encoded
/// once. The defaults are the RealWorld spec's shape, <c>{"errors":{"body":["..."]}}</c>.
/// </summary>
public sealed class DataErrors
{
    /// <summary>The defaults.</summary>
    public static DataErrors Default { get; } = new();

    /// <summary>503, retry in a second: too many reads were waiting.</summary>
    public ErrorReply ReadsBusy { get; init; } = new(503, """{"errors":{"body":["too many reads are waiting"]}}"""u8, retryAfter: 1);

    /// <summary>503, retry in a second: too many writes were waiting.</summary>
    public ErrorReply WritesBusy { get; init; } = new(503, """{"errors":{"body":["too many writes are waiting"]}}"""u8, retryAfter: 1);

    /// <summary>503, retry in a second: a lock or statement timeout, or a request aborted before its command ran.</summary>
    public ErrorReply DatabaseBusy { get; init; } = new(503, """{"errors":{"body":["the database is busy"]}}"""u8, retryAfter: 1);

    /// <summary>409: a unique key already held a value, when the result names no reply of its own (<c>Conflict</c>).</summary>
    public ErrorReply Conflict { get; init; } = new(409, """{"errors":{"body":["already exists"]}}"""u8);

    /// <summary>404: no row, when the result names no reply of its own (<c>OrNotFound</c>).</summary>
    public ErrorReply NotFound { get; init; } = new(404, """{"errors":{"body":["not found"]}}"""u8);

    /// <summary>403: a conditional write found its row but the condition didn't hold (<c>OrForbidden</c>).</summary>
    public ErrorReply Forbidden { get; init; } = new(403, """{"errors":{"body":["forbidden"]}}"""u8);

    /// <summary>500: the connection broke, the database refused the statement, or a row couldn't be read.</summary>
    public ErrorReply Internal { get; init; } = new(500, """{"errors":{"body":["internal error"]}}"""u8);

    /// <summary>The reply for a command that ended with <paramref name="status"/>; null for <see cref="DataStatus.Ok"/>.
    /// <paramref name="write"/> picks which queue was full for <see cref="DataStatus.Busy"/>.</summary>
    public ErrorReply? For(DataStatus status, bool write, ErrorReply? conflict = null) => status switch
    {
        DataStatus.Ok => null,
        DataStatus.Busy => write ? WritesBusy : ReadsBusy,
        DataStatus.Canceled or DataStatus.LockTimeout or DataStatus.StatementTimeout => DatabaseBusy,
        DataStatus.UniqueViolation => conflict ?? Conflict,
        DataStatus.Broken or DataStatus.Stopped or DataStatus.Failed or DataStatus.ReaderFailed => Internal,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
