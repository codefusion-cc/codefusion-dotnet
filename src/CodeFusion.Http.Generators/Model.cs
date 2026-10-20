using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CodeFusion.Http.Generators;

/// <summary>An array compared by its items, so the incremental pipeline can tell when a model really changed.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items) => _items = items.ToArray();

    public static readonly EquatableArray<T> Empty = new([]);

    public int Count => _items?.Length ?? 0;
    public T this[int i] => _items![i];

    public bool Equals(EquatableArray<T> other) => AsSpan().SequenceEqual(other.AsSpan());
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int h = 17;
        foreach (T item in AsSpan()) h = h * 31 + (item?.GetHashCode() ?? 0);
        return h;
    }

    ReadOnlySpan<T> AsSpan() => _items ?? [];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class EquatableArray
{
    public static EquatableArray<T> ToEquatable<T>(this IEnumerable<T> items) where T : IEquatable<T> => new(items);
}

/// <summary>A place in source, without holding on to the syntax tree.</summary>
internal sealed record LocationInfo(string Path, TextSpan Span, LinePositionSpan Lines)
{
    public Location ToLocation() => Location.Create(Path, Span, Lines);

    public static LocationInfo? From(Location? location) =>
        location is null || location.SourceTree is null ? null
        : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Diagnostics.ById[Id], Location?.ToLocation(), Args.Cast<object>().ToArray());
}

/// <summary>What one Map call compiles to, or why it can't be.</summary>
internal sealed record EndpointModel(
    string File,
    int Position,
    int Version,
    string Data,
    string Display,
    string Method,
    string DelegateParameterType,
    string? DirectCall,
    string? HandlerDelegateType,
    EquatableArray<ParameterModel> Parameters,
    ReplyModel Reply,
    EquatableArray<BodyModel> Bodies,
    EquatableArray<QueryModel> Queries,
    EquatableArray<ObjectShape> Objects);

internal enum ParameterKind
{
    RouteText,
    RouteInt64,
    RouteInt32,
    Query,
    Body,
    Viewer,
    Service,
    ScopedService,
    Response,
    Exchange,
    Aborted,
}

internal sealed record ParameterModel(string Name, ParameterKind Kind, string Type);

internal enum ReplyKind
{
    /// <summary>A struct implementing IReply, Reply among them: it writes itself.</summary>
    Self,
    /// <summary>ErrorReply.</summary>
    Error,
    /// <summary>Reply&lt;T&gt; whose T is written as JSON.</summary>
    Json,
    /// <summary>Reply&lt;T&gt; whose T is an IReply.</summary>
    Nested,
}

/// <param name="Type">The reply's type (inside Pending for an async handler).</param>
/// <param name="ValueType">T of Reply&lt;T&gt;.</param>
/// <param name="Value">How T is written, for <see cref="ReplyKind.Json"/>.</param>
/// <param name="Envelope">T's JsonEnvelope name.</param>
internal sealed record ReplyModel(ReplyKind Kind, bool Async, string Type, string? ValueType, ValueShape? Value, string? Envelope);

/// <summary>How a value is written into JSON.</summary>
internal enum ValueKind
{
    Utf8,
    RawJson,
    Timestamp,
    Utf8List,
    String,
    Bool,
    Number,
    Enum,
    Object,
    Sequence,
}

/// <param name="Type">The value's type, fully qualified.</param>
/// <param name="Nullable">A Nullable&lt;T&gt; (or a class that may be null): null is written as JSON null.</param>
/// <param name="Cast">For numbers and enums: the type to write it as.</param>
/// <param name="Element">For sequences: the element.</param>
/// <param name="ObjectType">For objects: the shape's type name.</param>
internal sealed record ValueShape(ValueKind Kind, string Type, bool Nullable, bool IsClass, string? Cast, ValueShape? Element, string? ObjectType);

internal sealed record MemberShape(string Member, string JsonName, ValueShape Value);

/// <summary>A struct or class written as a JSON object.</summary>
internal sealed record ObjectShape(string Type, bool IsClass, EquatableArray<MemberShape> Members);

/// <summary>Where a body or query struct is declared, for the partial declaration that adds its binder.</summary>
/// <param name="Containers">Outer types, outermost first, as "partial struct Name" headers.</param>
internal sealed record TypeHome(string? Namespace, EquatableArray<string> Containers, string Keyword, string Name, string FullName, string HintName);

internal enum BodyFieldKind
{
    Text,
    TextList,
    Int64,
    Int32,
    Double,
    Bool,
    Nested,
}

/// <param name="Type">The member's type, fully qualified, as declared.</param>
/// <param name="Nullable">Optional: Missing is null.</param>
/// <param name="NonEmpty">For optional text: an empty string is invalid.</param>
/// <param name="Error">The static ErrorReply member named by [BodyError].</param>
/// <param name="NestedType">For nested structs.</param>
/// <param name="Min">For numbers with [Range]: the lowest value let in, a C# literal; null when the type's own.</param>
/// <param name="MinExclusive">The value <paramref name="Min"/> itself is out (a double's exclusive end).</param>
/// <param name="Max">The highest value let in.</param>
/// <param name="MaxExclusive">The value <paramref name="Max"/> itself is out.</param>
internal sealed record BodyField(string Member, string JsonName, BodyFieldKind Kind, string Type, bool Nullable, bool NonEmpty, string? Error, string? NestedType,
    string? Min, bool MinExclusive, string? Max, bool MaxExclusive);

internal sealed record BodyModel(TypeHome Home, string? Envelope, string? Invalid, string? InvalidBody, EquatableArray<BodyField> Fields);

internal enum QueryFieldKind
{
    Text,
    Int64,
    Int32,
}

internal sealed record QueryField(string Member, string Key, QueryFieldKind Kind, bool Nullable, string Min, string Max, string? Default);

internal sealed record QueryModel(TypeHome Home, string? Invalid, EquatableArray<QueryField> Fields);

/// <summary>One Map call's analysis: the model when it compiles, the diagnostics either way.</summary>
internal sealed record EndpointResult(EndpointModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);

internal static class Immutables
{
    public static ImmutableArray<T> OrEmpty<T>(this ImmutableArray<T> a) => a.IsDefault ? [] : a;
}
