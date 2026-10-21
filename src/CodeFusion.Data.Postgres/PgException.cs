using System;

namespace CodeFusion.Data.Postgres;

/// <summary>An ErrorResponse from the server: the statement's (or, when <see cref="IsFatal"/>, the connection's) error.</summary>
public sealed class PgException(string severity, string sqlState, string messageText, string? detail, string? constraint)
    : Exception($"{severity} {sqlState}: {messageText}")
{
    /// <summary>ERROR, FATAL or PANIC (or a localized form of them).</summary>
    public string Severity { get; } = severity;
    /// <summary>The five-character SQLSTATE, compared with <see cref="PgErrorCodes"/>.</summary>
    public string SqlState { get; } = sqlState;
    /// <summary>The server's primary message.</summary>
    public string MessageText { get; } = messageText;
    /// <summary>The server's detail line, when it sent one.</summary>
    public string? Detail { get; } = detail;
    /// <summary>The constraint the statement violated, when there was one: which unique key a 23505 hit.</summary>
    public string? Constraint { get; } = constraint;
    /// <summary>The server ends the connection after it.</summary>
    public bool IsFatal => Severity is "FATAL" or "PANIC";
}

/// <summary>The connection broke, could not be opened, or the server sent bytes that break the protocol.</summary>
public sealed class PgConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A queue was full: the command was refused at once instead of waiting without limit.</summary>
public sealed class PgBusyException(string message) : Exception(message);

/// <summary>The SQLSTATEs the callers and the writer's retries look at.</summary>
public static class PgErrorCodes
{
    /// <summary>A unique or primary key already holds the value.</summary>
    public const string UniqueViolation = "23505";
    /// <summary>lock_timeout passed while waiting for a row lock.</summary>
    public const string LockNotAvailable = "55P03";
    /// <summary>statement_timeout passed, or the statement was canceled.</summary>
    public const string QueryCanceled = "57014";
    /// <summary>Postgres broke a deadlock by failing this transaction; the writer runs its group again.</summary>
    public const string DeadlockDetected = "40P01";
    /// <summary>A serializable or repeatable-read transaction lost a race; the writer runs its group again.</summary>
    public const string SerializationFailure = "40001";
    /// <summary>Division by zero.</summary>
    public const string DivisionByZero = "22012";
    /// <summary>The server is shutting down and ended the connection.</summary>
    public const string AdminShutdown = "57P01";
    /// <summary>The password was wrong.</summary>
    public const string InvalidPassword = "28P01";
    /// <summary>The server refused the login, for example no pg_hba.conf entry.</summary>
    public const string InvalidAuthorization = "28000";

    /// <summary>The code as a string: a constant for the ones above, so the common errors allocate no code string.</summary>
    internal static string Intern(ReadOnlySpan<byte> code)
    {
        foreach (string known in Known)
            if (Same(code, known)) return known;
        return System.Text.Encoding.ASCII.GetString(code);
    }

    static readonly string[] Known =
        [UniqueViolation, LockNotAvailable, QueryCanceled, DeadlockDetected, SerializationFailure, DivisionByZero, AdminShutdown, InvalidPassword, InvalidAuthorization];

    static bool Same(ReadOnlySpan<byte> a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
