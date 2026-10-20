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

    /// <summary>An offset alone; {0} is the offset: <c>OFFSET {0}</c>, or <c>LIMIT -1 OFFSET {0}</c> for SQLite, whose OFFSET
    /// needs a LIMIT.</summary>
    public string Offset { get; init; } = "OFFSET {0}";

    /// <summary>How changed rows come back.</summary>
    public SqlReturning Returning { get; init; } = SqlReturning.Returning;

    /// <summary>How an insert skips rows a unique key already holds.</summary>
    public SqlInsertOrIgnore InsertOrIgnore { get; init; } = SqlInsertOrIgnore.OnConflictDoNothing;

    /// <summary>A list of a correlated subquery's single column as one JSON array (never NULL); {0} is the subquery:
    /// <c>array_to_json(ARRAY({0}))</c>. A template that names {1} gets the subquery's column aliased so, for an
    /// aggregate around it: SQLite <c>(SELECT json_group_array({1}) FROM ({0}))</c>.</summary>
    public string JsonArray { get; init; } = "array_to_json(ARRAY({0}))";

    /// <summary>An aggregate of a whole query's single column as one JSON array (NULL without rows); {0} is the
    /// value, {1} its <c>ORDER BY</c> clause or empty: <c>array_to_json(array_agg({0}{1}))</c>.</summary>
    public string JsonAggregate { get; init; } = "array_to_json(array_agg({0}{1}))";

    /// <summary>The key the last insert into the table in this unit drew; {0} is the table as SQL (quoted where it
    /// must be), {1} the key column, {2} the table's name as it is, each escaped for a string literal:
    /// <c>currval(pg_get_serial_sequence('{0}', '{1}'))</c>; SQLite's is the table's row of <c>sqlite_sequence</c>.</summary>
    public string LastInsertedKey { get; init; } = "currval(pg_get_serial_sequence('{0}', '{1}'))";

    /// <summary>Draws a new key before the row is inserted, so the row's other values can use it; {0}, {1} and {2} as
    /// in <see cref="LastInsertedKey"/>. Empty: the dialect can't, and an insert whose values use its own key is refused.</summary>
    public string NextKey { get; init; } = "nextval(pg_get_serial_sequence('{0}', '{1}'))";

    /// <summary>Whether {0} is one of the elements of the array parameter {1}: <c>{0} = ANY({1})</c>.</summary>
    public string InArray { get; init; } = "{0} = ANY({1})";

    /// <summary>Equality that holds for two NULLs: <c>{0} IS NOT DISTINCT FROM {1}</c>; SQLite <c>{0} IS {1}</c>.</summary>
    public string NullSafeEqual { get; init; } = "{0} IS NOT DISTINCT FROM {1}";

    /// <summary>The current instant.</summary>
    public string Now { get; init; } = "now()";

    /// <summary>Text concatenation; {0} and {1} are the parts: <c>{0} || {1}</c>, or <c>{0} + {1}</c> for SQL Server.</summary>
    public string Concat { get; init; } = "{0} || {1}";

    /// <summary>The literals for true and false: <c>true false</c>, or <c>1 0</c> where there is no bool type.</summary>
    public string Booleans { get; init; } = "true false";

    /// <summary>Whether a condition can be selected as a column as it is. False (SQL Server): it is written
    /// <c>CASE WHEN ... THEN 1 ELSE 0 END</c>.</summary>
    public bool SelectsConditions { get; init; } = true;

    /// <summary>A bigint literal for a count's cap; {0} is the number: <c>{0}::bigint</c>.</summary>
    public string BigIntLiteral { get; init; } = "{0}::bigint";

    /// <summary>A floating-point value as an integer, cut toward zero as C# casts it; {0} is the value, {1} the SQL integer
    /// type: <c>CAST(trunc({0}) AS {1})</c>; SQLite <c>CAST({0} AS {1})</c>, whose cast already cuts toward zero.</summary>
    public string ToInteger { get; init; } = "CAST(trunc({0}) AS {1})";

    /// <summary>The smaller of two counts (never NULL): <c>LEAST({0}, {1})</c>; SQLite <c>min({0}, {1})</c>.</summary>
    public string Least { get; init; } = "LEAST({0}, {1})";

    /// <summary>The larger of two counts (never NULL): <c>GREATEST({0}, {1})</c>; SQLite <c>max({0}, {1})</c>.</summary>
    public string Greatest { get; init; } = "GREATEST({0}, {1})";

    /// <summary>Whether a subquery in FROM may refer to the rows before it (<c>LEFT JOIN LATERAL</c>). False (SQLite): a
    /// page's rows join without it, and a row's children are joined by their keys being in the correlated top n.</summary>
    public bool Lateral { get; init; } = true;

    /// <summary>Whether a CTE may write (<c>WITH d AS (DELETE ... RETURNING ...)</c>). False (SQLite): a conditional write
    /// is two statements of one unit, the write with RETURNING and the refused rows after it, and the rows a write
    /// returns read their navigations through scalar subqueries. The provider hands the reader every row of every
    /// statement of the unit, in order.</summary>
    public bool WritableCtes { get; init; } = true;

    /// <summary>Whether <c>INSERT ... SELECT ... ON CONFLICT</c> needs a WHERE in its SELECT to parse (SQLite reads the ON
    /// as a join's otherwise): the generator adds <c>WHERE true</c> where there is none.</summary>
    public bool InsertSelectNeedsWhere { get; init; }

    /// <summary>The lock a conditional write (<c>UpdateIf</c>, <c>DeleteIf</c>) takes on the rows it finds, so their
    /// condition is checked as a concurrent write left them; {0} is the table's alias: <c>FOR UPDATE OF {0}</c>.
    /// Empty: no lock.</summary>
    public string LockRows { get; init; } = "FOR UPDATE OF {0}";
}
