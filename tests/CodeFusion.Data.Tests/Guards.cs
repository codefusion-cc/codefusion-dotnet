using System.Linq;

namespace CodeFusion.Data.Tests;

/// <summary>The SQL the generator writes for the Postgres dialect, for exact expectations.</summary>
static class Guards
{
    /// <summary>The check of the Select after Then (Dialect.ThenGuard), as the WHERE it is.</summary>
    public static string Then(string written, (string Name, string Types)[] columns, string[] reads, string found, bool delete, bool corrected) =>
        " WHERE " + CodeFusion.Data.Generator.Dialect.Format(CodeFusion.Data.Generator.Dialect.ThenGuardOfPostgres, "'" + written + "'",
            columns.Length == 0 ? "('', '{}'::regtype[])" : string.Join(", ", columns.Select(c => $"('{c.Name}', '{c.Types}'::regtype[])")), string.Join(", ", reads.Select(r => "'" + r + "'")),
            delete ? "true" : "false", corrected ? "true" : "false", "'" + found + "'");
}
