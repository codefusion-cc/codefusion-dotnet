using System;

namespace CodeFusion.Data;

/// <summary>How a dialect writes rows a statement changed back to the client.</summary>
public enum SqlReturning
{
    /// <summary><c>INSERT ... RETURNING a, b</c> after the statement (Postgres, SQLite).</summary>
    Returning,
    /// <summary><c>INSERT ... OUTPUT INSERTED.a, INSERTED.b VALUES ...</c> inside it (SQL Server).</summary>
    Output,
}

/// <summary>How a dialect inserts a row only if no unique key holds its values yet.</summary>
public enum SqlInsertOrIgnore
{
    /// <summary><c>INSERT ... ON CONFLICT DO NOTHING</c> (Postgres, SQLite).</summary>
    OnConflictDoNothing,
    /// <summary><c>INSERT OR IGNORE INTO ...</c> (SQLite's older form).</summary>
    InsertOrIgnore,
    /// <summary>None: the generator refuses it (SQL Server would need a MERGE written with Sql).</summary>
    None,
}

/// <summary>
/// How a provider's SQL is spelled, read by the query generator at build time from the provider struct (the
/// <c>TProvider</c> of <see cref="DataContext{TProvider}"/>). Nothing here runs: every query's SQL is written once,
/// into the build. The defaults are Postgres's. Templates take their arguments as <c>{0}</c>, <c>{1}</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class SqlDialectAttribute : Attribute
{
    /// <summary>The dialect's name, for diagnostics.</summary>
    public SqlDialectAttribute(string name) => Name = name;

    /// <summary>The dialect's name, for diagnostics.</summary>
    public string Name { get; }

    /// <summary>A parameter's marker; {0} is its number from 1: <c>${0}</c> (Postgres), <c>?{0}</c> (SQLite), <c>@p{0}</c> (SQL Server).</summary>
    public string Parameter { get; init; } = "${0}";

    /// <summary>The characters a quoted identifier starts and ends with: <c>""</c>, or <c>[]</c> for SQL Server.
    /// Identifiers are quoted only where they need it (reserved words, capitals).</summary>
    public string Quotes { get; init; } = "\"\"";

    /// <summary>Paging; {0} is the limit, {1} the offset: <c>LIMIT {0} OFFSET {1}</c>, or
    /// <c>OFFSET {1} ROWS FETCH NEXT {0} ROWS ONLY</c> for SQL Server.</summary>
    public string Paging { get; init; } = "LIMIT {0} OFFSET {1}";

    /// <summary>A limit alone; {0} is the limit.</summary>
    public string Limit { get; init; } = "LIMIT {0}";

    /// <summary>How changed rows come back.</summary>
    public SqlReturning Returning { get; init; } = SqlReturning.Returning;

    /// <summary>How an insert skips rows a unique key already holds.</summary>
    public SqlInsertOrIgnore InsertOrIgnore { get; init; } = SqlInsertOrIgnore.OnConflictDoNothing;

    /// <summary>A list of a correlated subquery's single column as one JSON array (never NULL); {0} is the subquery:
    /// <c>array_to_json(ARRAY({0}))</c>; SQLite <c>(SELECT json_group_array(v) FROM ({0}))</c>.</summary>
    public string JsonArray { get; init; } = "array_to_json(ARRAY({0}))";

    /// <summary>An aggregate of a whole query's single column as one JSON array (NULL without rows); {0} is the
    /// value, {1} its <c>ORDER BY</c> clause or empty: <c>array_to_json(array_agg({0}{1}))</c>.</summary>
    public string JsonAggregate { get; init; } = "array_to_json(array_agg({0}{1}))";

    /// <summary>The key the last insert in this unit drew; {0} is the table, {1} the key column:
    /// <c>currval(pg_get_serial_sequence('{0}', '{1}'))</c>; SQLite <c>last_insert_rowid()</c>.</summary>
    public string LastInsertedKey { get; init; } = "currval(pg_get_serial_sequence('{0}', '{1}'))";

    /// <summary>Draws a new key before the row is inserted, so the row's other values can use it; {0} is the table,
    /// {1} the key column. Empty: the dialect can't, and an insert whose values use its own key is refused.</summary>
    public string NextKey { get; init; } = "nextval(pg_get_serial_sequence('{0}', '{1}'))";

    /// <summary>Whether {0} is one of the elements of the array parameter {1}: <c>{0} = ANY({1})</c>.</summary>
    public string InArray { get; init; } = "{0} = ANY({1})";

    /// <summary>The elements of an array parameter as rows of one column named v, for an insert of a list's texts; {0}
    /// is the parameter, {1} the rows' alias: <c>unnest({0}) {1}(v)</c>; SQLite <c>(SELECT value AS v FROM json_each({0})) {1}</c>.</summary>
    public string ListRows { get; init; } = "unnest({0}) {1}(v)";

    /// <summary>Equality that holds for two NULLs: <c>{0} IS NOT DISTINCT FROM {1}</c>; SQLite <c>{0} IS {1}</c>.</summary>
    public string NullSafeEqual { get; init; } = "{0} IS NOT DISTINCT FROM {1}";

    /// <summary>The current instant.</summary>
    public string Now { get; init; } = "now()";

    /// <summary>Text concatenation; {0} and {1} are the parts: <c>{0} || {1}</c>, or <c>{0} + {1}</c> for SQL Server.</summary>
    public string Concat { get; init; } = "{0} || {1}";

    /// <summary>Text with A to Z lowered and nothing else (<see cref="Utf8Operators.ToLowerAscii"/>); {0} is the text:
    /// <c>translate({0}, 'A…Z', 'a…z')</c>, alike in every collation, and the result keeps the text's own collation, so
    /// an order by it is the column's (<c>lower({0} COLLATE "C")</c> would order bytewise). SQLite's <c>lower({0})</c>
    /// lowers A to Z only already.</summary>
    public string LowerAscii { get; init; } = "translate({0}, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')";

    /// <summary>Text with a to z raised and nothing else (<see cref="Utf8Operators.ToUpperAscii"/>): <c>translate({0}, 'a…z', 'A…Z')</c>.</summary>
    public string UpperAscii { get; init; } = "translate({0}, 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ')";

    /// <summary>An integer as text, for <see cref="Utf8Operators.Concat(Utf8, long)"/>; {0} is the number:
    /// <c>CAST({0} AS text)</c>, or <c>CAST({0} AS varchar(20))</c> where <see cref="Concat"/> is <c>+</c>.</summary>
    public string IntegerText { get; init; } = "CAST({0} AS text)";

    /// <summary>Text without a character at either end; {0} is the text, {1} the character as a string literal:
    /// <c>trim(both {1} from {0})</c>; SQLite <c>trim({0}, {1})</c>.</summary>
    public string Trim { get; init; } = "trim(both {1} from {0})";

    /// <summary>Text without a character at its start: <c>trim(leading {1} from {0})</c>; SQLite <c>ltrim({0}, {1})</c>.</summary>
    public string TrimStart { get; init; } = "trim(leading {1} from {0})";

    /// <summary>Text without a character at its end: <c>trim(trailing {1} from {0})</c>; SQLite <c>rtrim({0}, {1})</c>.</summary>
    public string TrimEnd { get; init; } = "trim(trailing {1} from {0})";

    /// <summary>Each occurrence of {1} in {0} replaced by {2}, an empty {1} replacing nothing: <c>replace({0}, {1}, {2})</c>.
    /// Before Postgres 18 it fails on text of a nondeterministic collation.</summary>
    public string Replace { get; init; } = "replace({0}, {1}, {2})";

    /// <summary>Each match of the pattern {1} in {0} replaced by {2}, both string literals:
    /// <c>regexp_replace({0}, {1}, {2}, 'g')</c>, which fails on text of a nondeterministic collation. Empty: the
    /// dialect has none, and ReplaceMatches is refused.</summary>
    public string RegexReplace { get; init; } = "regexp_replace({0}, {1}, {2}, 'g')";

    /// <summary>The literals for true and false: <c>true false</c>, or <c>1 0</c> where there is no bool type.</summary>
    public string Booleans { get; init; } = "true false";

    /// <summary>Whether a condition can be selected as a column as it is. False (SQL Server): it is written
    /// <c>CASE WHEN ... THEN 1 ELSE 0 END</c>.</summary>
    public bool SelectsConditions { get; init; } = true;

    /// <summary>A bigint literal for a count's cap; {0} is the number: <c>{0}::bigint</c>.</summary>
    public string BigIntLiteral { get; init; } = "{0}::bigint";

    /// <summary>Whether a subquery in FROM may refer to the rows before it (<c>CROSS JOIN LATERAL</c>). False (SQLite): a
    /// page of the newest rows per group (<c>SelectMany</c> before <c>Page</c>) is refused.</summary>
    public bool Lateral { get; init; } = true;

    /// <summary>Whether a CTE may write (<c>WITH w AS (INSERT ...) SELECT ...</c>), and a DELETE join other rows
    /// (<c>DELETE ... USING</c>). False (SQLite): a write from the rows a query found (<c>Query.Then</c>) is refused.</summary>
    public bool WritableCtes { get; init; } = true;

    /// <summary>The lock a conditional write (<c>UpdateIf</c>, <c>DeleteIf</c>) takes on the rows it finds, so their
    /// condition is checked as a concurrent write left them; {0} is the table's alias: <c>FOR UPDATE OF {0}</c>.
    /// Empty: no lock.</summary>
    public string LockRows { get; init; } = "FOR UPDATE OF {0}";
}
