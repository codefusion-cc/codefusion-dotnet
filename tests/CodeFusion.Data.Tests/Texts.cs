using System;
using System.Linq;
using System.Text;

namespace CodeFusion.Data.Tests;

/// <summary>Text values as a request hands them over.</summary>
static class Texts
{
    public static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    /// <summary>A list as CodeFusion.Http's body binder writes one: each text a 4-byte length and its bytes.</summary>
    public static Utf8List List(params string[] texts)
    {
        var bytes = texts.Select(Encoding.UTF8.GetBytes).ToArray();
        var buffer = new byte[Utf8List.RecordBytes(bytes.Sum(b => b.Length), bytes.Length)];
        int at = 0;
        foreach (var b in bytes) at += Utf8List.Write(b, buffer.AsSpan(at));
        return new Utf8List(buffer, 0, buffer.Length, bytes.Length);
    }
}

/// <summary>The SQL the generator writes for the Postgres dialect, for exact expectations.</summary>
static class Guards
{
    /// <summary>The check of the Select after Then (Dialect.ThenGuard), as the WHERE it is.</summary>
    public static string Then(string written, (string Name, string Types)[] columns, string[] reads, string found, bool delete, bool corrected) =>
        " WHERE " + CodeFusion.Data.Generator.Dialect.Format(CodeFusion.Data.Generator.Dialect.ThenGuardOfPostgres, "'" + written + "'",
            columns.Length == 0 ? "('', '{}'::regtype[])" : string.Join(", ", columns.Select(c => $"('{c.Name}', '{c.Types}'::regtype[])")), string.Join(", ", reads.Select(r => "'" + r + "'")),
            delete ? "true" : "false", corrected ? "true" : "false", "'" + found + "'");
}
