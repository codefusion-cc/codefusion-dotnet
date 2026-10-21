using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace CodeFusion.Data;

// The LINQ a query method's body is written in. None of it runs: the generator compiles each query method at
// build time and takes over every call to it, so these members only give the definition its types. Calling one
// anywhere else is a build error (they would throw, and the lambdas passed to them would allocate).

static class Definition
{
    [DoesNotReturn]
    public static void Never([CallerMemberName] string member = "") =>
        throw new NotSupportedException($"{member} is part of a query definition: it is compiled at build time, and runs only inside a query method's body, which never runs.");

    public static T Never<T>([CallerMemberName] string member = "")
    {
        Never(member);
        return default!;
    }
}

/// <summary>A query being defined: rows of <typeparamref name="T"/>, an entity or a view.</summary>
public class Query<T>
{
    private protected Query() { }

    /// <summary>The rows <paramref name="predicate"/> holds for: <c>WHERE</c>.</summary>
    [QueryDefinition] public Query<T> Where(Func<T, bool> predicate) => Definition.Never<Query<T>>();

    /// <summary>Each row as <typeparamref name="TResult"/>: a view, a nested view, or one value.</summary>
    [QueryDefinition] public Query<TResult> Select<TResult>(Func<T, TResult> selector) => Definition.Never<Query<TResult>>();

    /// <summary>For each row, its children, kept with the row even when it has none (<c>LEFT JOIN LATERAL</c>): a
    /// missing row gives no rows at all, which <see cref="Many{T}.OrNotFound"/> answers, and a row without children an
    /// empty list.</summary>
    [QueryDefinition] public Query<TResult> SelectMany<TChild, TResult>(Func<T, Many<TChild>> children, Func<T, TChild, TResult> selector) =>
        Definition.Never<Query<TResult>>();

    /// <summary>Ordered by <paramref name="key"/>, ascending.</summary>
    [QueryDefinition] public OrderedQuery<T> OrderBy<TKey>(Func<T, TKey> key) => Definition.Never<OrderedQuery<T>>();

    /// <summary>Ordered by <paramref name="key"/>, descending.</summary>
    [QueryDefinition] public OrderedQuery<T> OrderByDescending<TKey>(Func<T, TKey> key) => Definition.Never<OrderedQuery<T>>();

    /// <summary>At most <paramref name="count"/> rows (none for a negative count, as LINQ's). Where and OrderBy can't
    /// follow it, as SQL would apply them first.</summary>
    [QueryDefinition] public Query<T> Take(long count) => Definition.Never<Query<T>>();

    /// <summary>The rows after the first <paramref name="count"/> (all for a negative count). Where and OrderBy can't
    /// follow it, as SQL would apply them first.</summary>
    [QueryDefinition] public Query<T> Skip(long count) => Definition.Never<Query<T>>();

    /// <summary>Each row once (<c>SELECT DISTINCT</c>). Only the rows of an insert from a list take it
    /// (<see cref="Utf8ListOperators.AsRows"/>), where the order, if any, is by the text itself.</summary>
    [QueryDefinition] public Query<T> Distinct() => Definition.Never<Query<T>>();

    /// <summary>
    /// One page of the list, ordered by its one key, with the list's length counted up to
    /// <paramref name="countUpTo"/>: the page's keys are picked first and only its rows are filled in. The query
    /// must be ordered by one column that identifies the row the page's <c>Select</c> projects.
    /// </summary>
    /// <param name="page">Which rows.</param>
    /// <param name="countUpTo">The count stops here: a longer list counts as this many.</param>
    /// <param name="count">How the count is taken.</param>
    /// <param name="maxLimit">The largest page: a constant in the SQL, so Postgres plans the page's size once.</param>
    [QueryDefinition] public PageQuery<T> Page(Paging page, long countUpTo, PageCount count = PageCount.Limited, int maxLimit = 100) =>
        Definition.Never<PageQuery<T>>();

    /// <summary>Every row.</summary>
    [QueryDefinition] public Many<T> Many() => Definition.Never<Many<T>>();

    /// <summary>How many rows there are.</summary>
    [QueryDefinition] public One<long> Count() => Definition.Never<One<long>>();

    /// <summary>Whether there is a row.</summary>
    [QueryDefinition] public One<bool> Any() => Definition.Never<One<bool>>();

    /// <summary>The one column of every row as one JSON array, aggregated by the database.</summary>
    [QueryDefinition] public One<RawJson> AsJson() => Definition.Never<One<RawJson>>();

    /// <summary>Inside another query's lambda: the one row <paramref name="predicate"/> holds for, looked up once
    /// (a scalar subquery). Use one of its columns, as in <c>Users.Single(u => u.Username == name).Id</c>. More than
    /// one row is the database's error.</summary>
    [QueryDefinition] public T Single(Func<T, bool> predicate) => Definition.Never<T>();

    /// <summary>
    /// Deletes each row found (locked first) if <paramref name="condition"/> holds for it, and gives back the row as
    /// it was found. A missing row and a refused one are told apart in one statement: a refused one is
    /// <see cref="Write{T}.OrForbidden"/>'s reply, or <see cref="Row{T}.Refused"/> awaited. Of several rows found, the
    /// reply is the deleted one with the smallest key, and refused only when none was deleted.
    /// </summary>
    [QueryDefinition] public DeleteQuery<T> DeleteIf(Func<T, bool> condition) => Definition.Never<DeleteQuery<T>>();

    /// <summary>Deletes every row found.</summary>
    [QueryDefinition] public DeleteQuery<T> Delete() => Definition.Never<DeleteQuery<T>>();

    /// <summary>
    /// Updates each row found (locked first, so the condition sees it as a concurrent write left it) with
    /// <paramref name="set"/> (<c>a => new Article { Title = title ?? a.Title }</c>) if <paramref name="condition"/>
    /// holds for it. Its <c>Select</c> sees the row as updated; a missing row and a refused one answer
    /// <see cref="Write{T}.OrNotFound"/> and <see cref="Write{T}.OrForbidden"/>. Of several rows found, the reply is the
    /// updated one with the smallest key, and refused only when none was updated.
    /// </summary>
    [QueryDefinition] public UpdateQuery<T> UpdateIf(Func<T, bool> condition, Func<T, T> set) => Definition.Never<UpdateQuery<T>>();

    /// <summary>Updates every row found with <paramref name="set"/>.</summary>
    [QueryDefinition] public UpdateQuery<T> Update(Func<T, T> set) => Definition.Never<UpdateQuery<T>>();

    /// <summary>
    /// For each row found, <paramref name="write"/>, which may use the row: an insert, or a delete of another table's
    /// rows (<c>u =&gt; Insert(Follows, new Follow { FollowerId = viewer, FolloweeId = u.Id }).OrIgnore()</c>). One
    /// statement, so one round trip and atomic, run on the writer; no row found writes nothing. Its <c>Select</c> gives
    /// back the rows as found, a missing one <see cref="Write{T}.OrNotFound"/>'s reply.
    /// </summary>
    [QueryDefinition] public FoundQuery<T> Then(Func<T, Write> write) => Definition.Never<FoundQuery<T>>();
}

/// <summary>The query operators whose results need a struct row.</summary>
public static class QueryOperators
{
    /// <summary>The first row, or none: the query asks for one row (<c>LIMIT 1</c>).</summary>
    [QueryDefinition] public static One<T> One<T>(this Query<T> query) where T : struct => Definition.Never<One<T>>();
}

/// <summary>An ordered query being defined; more keys follow with ThenBy.</summary>
public sealed class OrderedQuery<T> : Query<T>
{
    OrderedQuery() { }

    /// <summary>Then by <paramref name="key"/>, ascending.</summary>
    [QueryDefinition] public OrderedQuery<T> ThenBy<TKey>(Func<T, TKey> key) => Definition.Never<OrderedQuery<T>>();

    /// <summary>Then by <paramref name="key"/>, descending.</summary>
    [QueryDefinition] public OrderedQuery<T> ThenByDescending<TKey>(Func<T, TKey> key) => Definition.Never<OrderedQuery<T>>();
}

/// <summary>A table of entities, for query definitions: a context's property, <c>Table&lt;User&gt; Users =&gt; Table&lt;User&gt;();</c>.</summary>
public sealed class Table<T> : Query<T> where T : class
{
    Table() { }

    /// <summary>The one instance: a table is only a name for the generator.</summary>
    internal static readonly Table<T> Instance = new();
}

/// <summary>A page being defined (<see cref="Query{T}.Page"/>); its <c>Select</c> gives the result.</summary>
public sealed class PageQuery<T>
{
    PageQuery() { }

    /// <summary>The page's rows as <typeparamref name="TResult"/>.</summary>
    [QueryDefinition] public Page<TResult> Select<TResult>(Func<T, TResult> selector) where TResult : struct => Definition.Never<Page<TResult>>();
}

/// <summary>A delete being defined.</summary>
public sealed class DeleteQuery<T>
{
    DeleteQuery() { }

    /// <summary>What to give back of the row as it was found: its own columns only.</summary>
    [QueryDefinition] public WriteQuery<TResult> Select<TResult>(Func<T, TResult> selector) where TResult : struct => Definition.Never<WriteQuery<TResult>>();

    /// <summary>The delete alone, giving back nothing.</summary>
    [QueryDefinition] public static implicit operator Write(DeleteQuery<T> delete) => Definition.Never<Write>();
}

/// <summary>A write's rows being defined.</summary>
public sealed class WriteQuery<T> where T : struct
{
    WriteQuery() { }

    /// <summary>The first row, or none.</summary>
    [QueryDefinition] public One<T> One() => Definition.Never<One<T>>();
}

/// <summary>An update being defined.</summary>
public sealed class UpdateQuery<T>
{
    UpdateQuery() { }

    /// <summary>What to give back of the row as updated.</summary>
    [QueryDefinition] public Write<TResult> Select<TResult>(Func<T, TResult> selector) where TResult : struct => Definition.Never<Write<TResult>>();

    /// <summary>The update alone, giving back nothing.</summary>
    [QueryDefinition] public static implicit operator Write(UpdateQuery<T> update) => Definition.Never<Write>();
}

/// <summary>The rows a query found, each with a write made from it (<see cref="Query{T}.Then"/>).</summary>
public sealed class FoundQuery<T>
{
    FoundQuery() { }

    /// <summary>What to give back of each row found: one of them is the reply, a missing one
    /// <see cref="Write{T}.OrNotFound"/>'s.</summary>
    [QueryDefinition] public Write<TResult> Select<TResult>(Func<T, TResult> selector) where TResult : struct => Definition.Never<Write<TResult>>();

    /// <summary>The writes alone, giving back nothing.</summary>
    [QueryDefinition] public static implicit operator Write(FoundQuery<T> found) => Definition.Never<Write>();
}

/// <summary>An insert being defined (<see cref="DataContext.Insert{T}(Table{T}, T)"/>), and the statements that follow it in its unit.</summary>
public sealed class Insert<T> where T : class
{
    Insert() { }

    /// <summary>Skips the row if a unique key already holds its values.</summary>
    [QueryDefinition] public Insert<T> OrIgnore() => Definition.Never<Insert<T>>();

    /// <summary>What to give back of the inserted row: its own columns only (<c>RETURNING</c>).</summary>
    [QueryDefinition] public Write<TResult> Select<TResult>(Func<T, TResult> selector) where TResult : struct => Definition.Never<Write<TResult>>();

    /// <summary>Another write in the same unit.</summary>
    [QueryDefinition] public Insert<T> Then(Write next) => Definition.Never<Insert<T>>();

    /// <summary>
    /// Another write in the same unit, which may use the inserted row's key: the key the insert set, or the one it
    /// drew from the table's sequence, read as its current value, so only until another insert into the table. An
    /// insert with <see cref="OrIgnore"/> that draws its key, or of a query's rows, has no key to use: a build error.
    /// </summary>
    [QueryDefinition] public Insert<T> Then(Func<T, Write> next) => Definition.Never<Insert<T>>();

    /// <summary>Another write in the same unit, only when <paramref name="condition"/> (C#, decided when the query is
    /// called) holds.</summary>
    [QueryDefinition] public Insert<T> ThenIf(bool condition, Write next) => Definition.Never<Insert<T>>();

    /// <summary>Another write in the same unit, using the inserted row's key, only when <paramref name="condition"/> holds.</summary>
    [QueryDefinition] public Insert<T> ThenIf(bool condition, Func<T, Write> next) => Definition.Never<Insert<T>>();

    /// <summary>Ends the unit by reading the inserted row back, navigations and all, as <typeparamref name="TResult"/>.</summary>
    [QueryDefinition] public Write<TResult> ThenSelect<TResult>(Func<T, TResult> selector) where TResult : struct => Definition.Never<Write<TResult>>();

    /// <summary>The insert alone, giving back nothing.</summary>
    [QueryDefinition] public static implicit operator Write(Insert<T> insert) => Definition.Never<Write>();
}

/// <summary>A query written in SQL (<see cref="DataContext.Sql{T}(SqlString)"/>). Its columns are
/// <typeparamref name="T"/>'s values in declaration order, nested views flattened.</summary>
public sealed class SqlQuery<T> where T : struct
{
    SqlQuery() { }

    /// <summary>The first row, or none.</summary>
    [QueryDefinition] public One<T> One() => Definition.Never<One<T>>();

    /// <summary>Every row.</summary>
    [QueryDefinition] public Many<T> Many() => Definition.Never<Many<T>>();

    /// <summary>A page: each row holds <typeparamref name="T"/>'s columns, then the row's key (NULL in the one row of an
    /// empty page), then the capped count.</summary>
    [QueryDefinition] public Page<T> Page() => Definition.Never<Page<T>>();

    /// <summary>A statement that writes, run on the writer in a group commit; its first row, or none.</summary>
    [QueryDefinition] public Write<T> Write() => Definition.Never<Write<T>>();
}

/// <summary>
/// The SQL of <see cref="DataContext.Sql{T}(SqlString)"/>: an interpolated string whose holes become the statement's
/// parameters at build time (<c>$"... WHERE id = {id}"</c> is <c>... WHERE id = $1</c>), constants too (a number is
/// written as its literal); a hole calling a <see cref="SqlTemplateAttribute"/> method is its SQL. It is never formatted.
/// </summary>
[InterpolatedStringHandler]
public ref struct SqlString
{
    // The compiler calls these on the instance; they never run.
#pragma warning disable CA1822
    /// <summary>Called by the compiler.</summary>
    public SqlString(int literalLength, int formattedCount) { }

    /// <summary>Called by the compiler.</summary>
    [QueryDefinition] public readonly void AppendLiteral(string literal) => Definition.Never();

    /// <summary>Called by the compiler: a parameter.</summary>
    [QueryDefinition] public readonly void AppendFormatted<T>(T value) where T : allows ref struct => Definition.Never();
#pragma warning restore CA1822
}
