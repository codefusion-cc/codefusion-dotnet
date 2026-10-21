using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>SQLite's own keywords, which are not all Postgres's reserved words, are quoted as identifiers.</summary>
public sealed class KeywordTests
{
    [Fact]
    public unsafe void TheDialectQuotesEveryKeywordOfTheLibrary()
    {
        SqliteConn.InitializeOnce();
        var dialect = typeof(Sqlite).GetCustomAttribute<SqlDialectAttribute>()!;
        var quoted = dialect.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new System.Collections.Generic.List<string>();
        int count = Sqlite3.KeywordCount();
        Assert.True(count > 100);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0, Sqlite3.KeywordName(i, out byte* name, out int bytes));
            string word = System.Text.Encoding.UTF8.GetString(name, bytes);
            if (!quoted.Contains(word)) missing.Add(word.ToLowerInvariant());
        }
        Assert.True(missing.Count == 0, "keywords of this SQLite the dialect doesn't quote: " + string.Join(" ", missing));
    }
}
