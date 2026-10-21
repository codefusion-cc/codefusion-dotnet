using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace CodeFusion.Data.Generator;

/// <summary>A provider's SQL spelling, read from its <c>[SqlDialect]</c> (the defaults are Postgres's).</summary>
sealed class Dialect
{
    public string Name = "postgres", Parameter = "${0}", Quotes = "\"\"", Paging = "LIMIT {0} OFFSET {1}", Limit = "LIMIT {0}";
    public string JsonArray = "array_to_json(ARRAY({0}))", JsonAggregate = "array_to_json(array_agg({0}{1}))";
    public string LastInsertedKey = "currval(pg_get_serial_sequence('{0}', '{1}'))", NextKey = "nextval(pg_get_serial_sequence('{0}', '{1}'))";
    public string InArray = "{0} = ANY({1})", NullSafeEqual = "{0} IS NOT DISTINCT FROM {1}", Now = "now()", Concat = "{0} || {1}";
    public string True = "true", False = "false", BigIntLiteral = "{0}::bigint", LockRows = "FOR UPDATE OF {0}";
    // C#'s conversions between integers and floating point ({1} is the SQL type), the smaller and larger of two
    // counts; Postgres's spelling, not a dialect's setting yet
    public string ToFloat = "CAST({0} AS {1})", ToInteger = "CAST(trunc({0}) AS {1})", Least = "LEAST({0}, {1})", Greatest = "GREATEST({0}, {1})";
    public bool SelectsConditions = true, ReturningAfter = true;
    public int InsertOrIgnore; // 0 ON CONFLICT DO NOTHING, 1 INSERT OR IGNORE, 2 none

    public static Dialect Of(INamedTypeSymbol provider, Known k)
    {
        var d = new Dialect();
        var a = Known.Attribute(provider, k.SqlDialect);
        if (a is null) return d;
        d.Name = a.ConstructorArguments.FirstOrDefault().Value as string ?? d.Name;
        foreach (var n in a.NamedArguments)
        {
            object? v = n.Value.Value;
            switch (n.Key)
            {
                case "Parameter": d.Parameter = (string)v!; break;
                case "Quotes": d.Quotes = (string)v!; break;
                case "Paging": d.Paging = (string)v!; break;
                case "Limit": d.Limit = (string)v!; break;
                case "Returning": d.ReturningAfter = (int)v! == 0; break;
                case "InsertOrIgnore": d.InsertOrIgnore = (int)v!; break;
                case "JsonArray": d.JsonArray = (string)v!; break;
                case "JsonAggregate": d.JsonAggregate = (string)v!; break;
                case "LastInsertedKey": d.LastInsertedKey = (string)v!; break;
                case "NextKey": d.NextKey = (string)v!; break;
                case "InArray": d.InArray = (string)v!; break;
                case "NullSafeEqual": d.NullSafeEqual = (string)v!; break;
                case "Now": d.Now = (string)v!; break;
                case "Concat": d.Concat = (string)v!; break;
                case "Booleans":
                    var parts = ((string)v!).Split(' ');
                    d.True = parts[0];
                    d.False = parts.Length > 1 ? parts[1] : parts[0];
                    break;
                case "SelectsConditions": d.SelectsConditions = (bool)v!; break;
                case "BigIntLiteral": d.BigIntLiteral = (string)v!; break;
                case "LockRows": d.LockRows = (string)v!; break;
            }
        }
        return d;
    }

    public static string Format(string template, params string[] args)
    {
        var b = new StringBuilder(template.Length + 32);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '{' && i + 2 < template.Length && char.IsDigit(template[i + 1]))
            {
                int end = template.IndexOf('}', i);
                if (end > 0 && int.TryParse(template.Substring(i + 1, end - i - 1), out int n) && n < args.Length)
                {
                    b.Append(args[n]);
                    i = end;
                    continue;
                }
            }
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>
    /// The words that can't be a bare column or table alias: Postgres's reserved keywords and those reserved but
    /// allowed as a function or type name (pg_get_keywords' R and T, which KeywordTests compares with the server's),
    /// and a few SQL keeps for itself elsewhere.
    /// </summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both", "case", "cast", "check", "collate", "column",
        "constraint", "create", "current_catalog", "current_date", "current_role", "current_time", "current_timestamp", "current_user",
        "default", "deferrable", "desc", "distinct", "do", "else", "end", "except", "false", "fetch", "for", "foreign", "from", "grant",
        "group", "having", "in", "initially", "intersect", "into", "lateral", "leading", "limit", "localtime", "localtimestamp", "not", "null",
        "offset", "on", "only", "or", "order", "placing", "primary", "references", "returning", "select", "session_user", "some", "symmetric",
        "system_user", "table", "then", "to", "trailing", "true", "union", "unique", "user", "using", "variadic", "when", "where", "window",
        "with",
        "authorization", "binary", "collation", "concurrently", "cross", "current_schema", "freeze", "full", "ilike", "inner", "is", "isnull",
        "join", "left", "like", "natural", "notnull", "outer", "overlaps", "right", "similar", "tablesample", "verbose",
        "key", "value", "values",
    };

    /// <summary>An identifier, quoted only where it needs it: a reserved word, or anything but lower case, digits and _.</summary>
    public string Quote(string name)
    {
        bool plain = name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
        if (plain && !Reserved.Contains(name)) return name;
        char open = Quotes[0], close = Quotes.Length > 1 ? Quotes[1] : Quotes[0];
        return open + name.Replace(close.ToString(), new string(close, 2)) + close;
    }

    public string Param(int n) => Format(Parameter, Names.Number(n));
}

/// <summary>A parameter of a statement: the C# that gives its value when the query is called, and its type.
/// <see cref="Key"/>: the value it is (<see cref="Host.Key"/>), computed once wherever it is bound; <see cref="Made"/>:
/// the order C# makes it in.</summary>
sealed record Param(string Code, ITypeSymbol Type, ParamType Kind, bool Nullable, string Key, int Made)
{
    /// <summary>The values it binds, all of its key.</summary>
    public List<Host> Origins { get; } = new();
}

/// <summary>How a parameter is bound.</summary>
enum ParamType { Int64, Int32, Boolean, Double, Text, String, Span, Timestamp, TextArray }

/// <summary>One statement's parameters, numbered as they are first used; the same value bound the same way is one
/// parameter (bound as a double and as a long, it is two: the long keeps every digit).</summary>
sealed class ParamList
{
    readonly Dialect _dialect;
    public readonly List<Param> Items = new();

    public ParamList(Dialect dialect) => _dialect = dialect;

    /// <summary>How many there are, to go back to after a translation that was given up.</summary>
    public int Mark => Items.Count;

    public void Reset(int mark) => Items.RemoveRange(mark, Items.Count - mark);

    public string Add(Param p)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Key == p.Key && Items[i].Kind == p.Kind)
            {
                Items[i].Origins.AddRange(p.Origins);
                return _dialect.Param(i + 1);
            }
        Items.Add(p);
        return _dialect.Param(Items.Count);
    }
}

/// <summary>
/// A SELECT being built. Joins are added as the translator meets navigations, which may come after the WHERE was
/// written, so it renders only at the end.
/// </summary>
sealed class Select
{
    public readonly List<string> Ctes = new();
    public readonly List<(string Sql, string? Alias)> Columns = new();
    public string From = "";
    public readonly List<string> Joins = new();
    public readonly List<string> Where = new();
    public readonly List<(string Sql, bool Desc)> OrderBy = new();
    public string? Limit, Offset;
    /// <summary>The row lock it takes, after everything else (<c>FOR UPDATE OF t</c>).</summary>
    public string? Lock;

    public string Render(Dialect d)
    {
        var b = new StringBuilder();
        if (Ctes.Count > 0) b.Append("WITH ").Append(string.Join(", ", Ctes)).Append(' ');
        b.Append("SELECT ");
        b.Append(Columns.Count == 0 ? "1" : string.Join(", ", Columns.Select(c => c.Alias is null ? c.Sql : c.Sql + " AS " + c.Alias)));
        if (From.Length > 0) b.Append(" FROM ").Append(From);
        foreach (var j in Joins) b.Append(' ').Append(j);
        if (Where.Count > 0) b.Append(" WHERE ").Append(string.Join(" AND ", Where.Select(w => Sql.Wrap(w, " OR "))));
        if (OrderBy.Count > 0) b.Append(" ORDER BY ").Append(string.Join(", ", OrderBy.Select(o => o.Desc ? o.Sql + " DESC" : o.Sql)));
        if (Limit is not null && Offset is not null) b.Append(' ').Append(Dialect.Format(d.Paging, Limit, Offset));
        else if (Limit is not null) b.Append(' ').Append(Dialect.Format(d.Limit, Limit));
        else if (Offset is not null) b.Append(" OFFSET ").Append(Offset);
        if (Lock is not null) b.Append(' ').Append(Lock);
        return b.ToString();
    }
}

/// <summary>SQL text helpers.</summary>
static class Sql
{
    /// <summary>Parentheses around <paramref name="sql"/> when it holds <paramref name="op"/> at its top level.</summary>
    public static string Wrap(string sql, string op) => TopLevel(sql, op) ? "(" + sql + ")" : sql;

    /// <summary>Whether <paramref name="op"/> (" AND ", " OR ", ...) occurs outside parentheses and quotes.</summary>
    public static bool TopLevel(string sql, string op)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(sql, i, op, 0, op.Length) == 0) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="sql"/> is one term: no operator outside parentheses.</summary>
    public static bool Atomic(string sql)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && c == ' ') return false;
        }
        return true;
    }

    /// <summary><paramref name="sql"/> in parentheses unless it is one term.</summary>
    public static string Group(string sql) => Atomic(sql) ? sql : "(" + sql + ")";

    /// <summary>A string literal.</summary>
    public static string String(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// <paramref name="sql"/> with what isn't SQL's own words blanked, at the same positions: comments become spaces,
    /// and the inside of a string literal (dollar-quoted too) or a quoted name spaces between its quotes, so a keyword
    /// or parenthesis in them is never read as one.
    /// </summary>
    public static string Code(string sql)
    {
        var b = new StringBuilder(sql);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            int end;
            if (c == '-' && At(sql, i, "--"))
            {
                end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                Blank(b, i, end);
            }
            else if (c == '/' && At(sql, i, "/*"))
            {
                // Postgres's block comments nest
                int depth = 0;
                end = i;
                while (end < sql.Length)
                {
                    if (At(sql, end, "/*"))
                    {
                        depth++;
                        end += 2;
                    }
                    else if (At(sql, end, "*/"))
                    {
                        end += 2;
                        if (--depth == 0) break;
                    }
                    else end++;
                }
                end = Math.Min(end, sql.Length);
                Blank(b, i, end);
            }
            else if (c is '\'' or '"')
            {
                // a doubled quote is one in the text; E'...' takes backslash escapes
                bool escapes = c == '\'' && i > 0 && sql[i - 1] is 'E' or 'e' && (i == 1 || !IsWord(sql[i - 2]));
                for (end = i + 1; end < sql.Length; end++)
                {
                    if (escapes && sql[end] == '\\') end++;
                    else if (sql[end] == c)
                    {
                        if (end + 1 < sql.Length && sql[end + 1] == c) end++;
                        else break;
                    }
                }
                Blank(b, i + 1, Math.Min(end, sql.Length));
                end = Math.Min(end + 1, sql.Length);
            }
            else if (c == '$' && (i == 0 || !IsWord(sql[i - 1])) && DollarTag(sql, i) is { } tag)
            {
                int close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                end = close < 0 ? sql.Length : close + tag.Length;
                Blank(b, i + tag.Length, close < 0 ? sql.Length : close);
            }
            else end = i + 1;
            i = end;
        }
        return b.ToString();
    }

    static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    static void Blank(StringBuilder b, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (b[i] != '\n') b[i] = ' ';
    }

    /// <summary>The tag that opens a dollar-quoted string at <paramref name="i"/> (<c>$$</c>, <c>$x$</c>), if one does:
    /// <c>$1</c> is a parameter.</summary>
    static string? DollarTag(string sql, int i)
    {
        int j = i + 1;
        if (j < sql.Length && (char.IsLetter(sql[j]) || sql[j] == '_'))
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
        return j < sql.Length && sql[j] == '$' ? sql.Substring(i, j - i + 1) : null;
    }
}

/// <summary>Gives each table reference in a statement its own alias, preferring the lambda's parameter name.</summary>
sealed class Aliases
{
    readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public HashSet<string> Mark() => new(_used, StringComparer.Ordinal);

    public void Reset(HashSet<string> mark)
    {
        _used.Clear();
        _used.UnionWith(mark);
    }

    public string Take(string preferred)
    {
        string a = string.IsNullOrEmpty(preferred) ? "t" : Names.Snake(preferred);
        if (a.Length == 0 || !(char.IsLetter(a[0]) || a[0] == '_') || !a.All(c => char.IsLetterOrDigit(c) || c == '_')) a = "t";
        if (Reserved(a)) a += "_";
        string name = a;
        for (int n = 2; !_used.Add(name); n++) name = a + Names.Number(n);
        return name;
    }

    /// <summary>The initials of a table: <c>article_tags</c> is <c>at</c>.</summary>
    public string ForTable(string table) => Take(string.Concat(table.Split('_').Where(p => p.Length > 0).Select(p => p[0])));

    /// <summary>Keeps <paramref name="name"/> from the aliases taken after it: a name a statement refers to its target by.</summary>
    public void Reserve(string name) => _used.Add(name);

    static bool Reserved(string a) => Dialect.Reserved.Contains(a);
}
