using System;

namespace CodeFusion.Data;

// Entities use EF's conventions and its attributes from System.ComponentModel.DataAnnotations(.Schema): [Table],
// [Column], [Key], [ForeignKey], [InverseProperty], [NotMapped]. These are the ones EF has no attribute for.

/// <summary>
/// A many-to-many navigation (<c>Many&lt;Tag&gt; Tags</c>) through a join table. Its columns are named for the two
/// entities, <c>article_id</c> and <c>tag_id</c>, unless given.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class JoinTableAttribute : Attribute
{
    /// <summary>Through <paramref name="table"/>, with columns named by convention.</summary>
    public JoinTableAttribute(string table) => Table = table;

    /// <summary>Through <paramref name="table"/>, whose <paramref name="thisColumn"/> holds this entity's key and
    /// <paramref name="otherColumn"/> the other's.</summary>
    public JoinTableAttribute(string table, string thisColumn, string otherColumn)
    {
        Table = table;
        ThisColumn = thisColumn;
        OtherColumn = otherColumn;
    }

    /// <summary>The join table.</summary>
    public string Table { get; }

    /// <summary>Its column holding this entity's key; null: <c>{entity}_id</c>.</summary>
    public string? ThisColumn { get; }

    /// <summary>Its column holding the other entity's key; null: <c>{other entity}_id</c>.</summary>
    public string? OtherColumn { get; }
}

/// <summary>
/// The JSON names of a list of this view as a whole reply: <c>[JsonList("comments")]</c> writes
/// <c>{"comments": [...]}</c> for a <c>Many</c>, and with <see cref="CountName"/> a <c>Page</c> writes
/// <c>{"articles": [...], "articlesCount": n}</c>. On a query method it applies to that query alone. Without it a
/// <c>Many</c> is a bare array and a <c>Page</c> is <c>{"items": [...], "count": n}</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
public sealed class JsonListAttribute(string name) : Attribute
{
    /// <summary>The list's property.</summary>
    public string Name { get; } = name;

    /// <summary>A page's count property.</summary>
    public string? CountName { get; init; }
}

/// <summary>
/// On a query method: its commands run alone, on connections of their own and each in a transaction of its own,
/// never in a group commit or a read group. For a query slow enough to hold up the others it would be grouped with
/// (a sign-up whose unique checks wait on locks, a report), at the cost of a round trip and a commit each. A provider
/// that can't run them apart (one that doesn't declare IDataProvider.Alone) runs them as any other.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RunsAloneAttribute : Attribute;

/// <summary>
/// A static method that stands for SQL inside query definitions: the generator writes <see cref="Template"/> with
/// the arguments' SQL for <c>{0}</c>, <c>{1}</c> and so on. A hole of <c>Sql($"...")</c> calling one is its SQL, so a
/// fragment several statements share is a template without arguments. The method's body never runs; make it throw.
/// <code>
/// [SqlTemplate("lower({0})")] public static Utf8 Lower(Utf8 text) => throw new NotSupportedException();
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class SqlTemplateAttribute(string template) : Attribute
{
    /// <summary>The SQL, with <c>{0}</c> for the first argument.</summary>
    public string Template { get; } = template;

    /// <summary>The dialect (its <see cref="SqlDialectAttribute.Name"/>, such as <c>sqlite</c>) this template is for; null:
    /// any dialect without one of its own. A method may carry one template per dialect; in a dialect it has none for, a
    /// method whose body is an expression it translates is that expression (a slug rule in LINQ, and SQLite's own SQL).</summary>
    public string? Dialect { get; init; }
}

/// <summary>
/// Marks a member that exists only for query definitions: the generator compiles calls to it, and using it
/// anywhere else is a build error, because it would run and throw (and allocate the lambdas it is given).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor, Inherited = false)]
public sealed class QueryDefinitionAttribute : Attribute;

/// <summary>SQL functions for query definitions; they never run.</summary>
public static class SqlFunctions
{
    /// <summary>The current instant, in the database's clock (Postgres <c>now()</c>: the transaction's start).</summary>
    [QueryDefinition] public static Timestamp Now() => Definition.Never<Timestamp>();
}

/// <summary>What a query definition asks of a <see cref="Utf8List"/>, such as a request's tag list.</summary>
public static class Utf8ListOperators
{
    /// <summary>Whether <paramref name="text"/> is one of the list's texts. In a definition, <c>tags.Contains(t.Name)</c>
    /// is <c>t.name = ANY($1)</c> with the list as the array parameter; it also runs as C#.</summary>
    public static bool Contains(this Utf8List list, Utf8 text)
    {
        foreach (var t in list)
            if (t == text) return true;
        return false;
    }

    /// <summary>
    /// The list's texts as the rows of a query, for an insert of each (<c>unnest($1)</c>, the list the array
    /// parameter): <c>Insert(Tags, names.AsRows().Distinct().OrderBy(n =&gt; n).Select(n =&gt; new Tag { Name = n })).OrIgnore()</c>
    /// inserts each new name once, in sorted order. Before the Select come Where, Distinct and one OrderBy.
    /// </summary>
    [QueryDefinition] public static Query<Utf8> AsRows(this Utf8List list) => Definition.Never<Query<Utf8>>();
}
