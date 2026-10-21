using Microsoft.CodeAnalysis;

namespace CodeFusion.Data.Generator;

/// <summary>The generator's build errors. Each that refuses a query says what to write instead.</summary>
static class Diagnostics
{
    const string Category = "CodeFusion.Data";

    static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>A construct the translator can't compile.</summary>
    public static readonly DiagnosticDescriptor Unsupported = Error("CFDATA001", "The query can't be compiled to SQL",
        "{0} can't be compiled to SQL; write this query with Sql<T>($\"...\") instead");

    /// <summary>A definition-only member used where it would run.</summary>
    public static readonly DiagnosticDescriptor RunsAtRunTime = Error("CFDATA002", "Query definitions run only inside query methods",
        "'{0}' is part of a query definition and would run here (it throws, and its lambdas allocate): move the query into a query method of the data context");

    /// <summary>A query method whose body is not one expression.</summary>
    public static readonly DiagnosticDescriptor NotADefinition = Error("CFDATA003", "A query method's body is its definition",
        "Query method '{0}' must have an expression body (=> ...) or a block with one return: the body is the query's definition and never runs");

    /// <summary>A hole of Sql that isn't a value of the call.</summary>
    public static readonly DiagnosticDescriptor BadHole = Error("CFDATA004", "An Sql hole must be a parameter",
        "The hole {{{0}}} must be a value known when the query is called (a parameter of the query method, or a member of one)");

    /// <summary>A constant string hole of Sql where SQL expects a name, which a parameter can't be.</summary>
    public static readonly DiagnosticDescriptor NameHole = Error("CFDATA010", "An Sql hole is a value, not SQL",
        "The hole {{{0}}} stands where SQL expects a name, but a hole is a parameter, a constant string too: for SQL text, call a [SqlTemplate] method in the hole, or write the text in the string");

    /// <summary>SQL that writes, run as a read.</summary>
    public static readonly DiagnosticDescriptor WriteAsRead = Error("CFDATA005", "SQL that writes must run as a write",
        "This SQL writes ({0}): end it with .Write() so it runs on the writer, in a group commit");

    /// <summary>A view member of a type views can't hold.</summary>
    public static readonly DiagnosticDescriptor BadViewMember = Error("CFDATA006", "Unsupported view member",
        "'{0}' has type '{1}', which a view can't hold: use Utf8, Utf8?, Timestamp, RawJson, a number, a bool, or a nested view");

    /// <summary>The context isn't set up for the generator.</summary>
    public static readonly DiagnosticDescriptor BadContext = Error("CFDATA007", "The data context can't be compiled",
        "Data context '{0}': {1}");

    /// <summary>An entity the model can't map.</summary>
    public static readonly DiagnosticDescriptor BadEntity = Error("CFDATA008", "The entity can't be mapped",
        "Entity '{0}': {1}");

    /// <summary>The generator's own failure on a query: a bug to report, with where it happened.</summary>
    public static readonly DiagnosticDescriptor Internal = Error("CFDATA009", "The query generator failed",
        "The query generator failed on '{0}': {1}");
}
