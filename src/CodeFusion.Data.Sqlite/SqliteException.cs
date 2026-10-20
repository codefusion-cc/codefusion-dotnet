using System;

namespace CodeFusion.Data.Sqlite;

/// <summary>A failure SQLite reported, with its extended result code.</summary>
public sealed class SqliteException : Exception
{
    /// <summary>A failure with SQLite's message and its extended code.</summary>
    public SqliteException(string message, int code) : base(message) => Code = code;

    /// <summary>A failure.</summary>
    public SqliteException() : base("SQLite failed") { }

    /// <summary>A failure.</summary>
    public SqliteException(string message) : base(message) { }

    /// <summary>A failure.</summary>
    public SqliteException(string message, Exception inner) : base(message, inner) { }

    /// <summary>The extended result code (SQLITE_CONSTRAINT_UNIQUE is 2067).</summary>
    public int Code { get; }

    /// <summary>The primary result code, the extended code's low byte.</summary>
    public int Primary => Code & 0xFF;

    /// <summary>Its name, such as SQLITE_BUSY.</summary>
    public string Name => Sqlite3.CodeName(Code);

    internal static SqliteException From(nint db, int code) => new($"{Sqlite3.CodeName(code)}: {Sqlite3.Message(db)}", code);
}
