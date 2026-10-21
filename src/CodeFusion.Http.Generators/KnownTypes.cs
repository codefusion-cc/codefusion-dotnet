using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace CodeFusion.Http.Generators;

/// <summary>The types the generator and the analyzers recognise, resolved once per compilation.</summary>
internal sealed class KnownTypes
{
    // Every Map call of a compilation is analysed against the same one, and the analyzers' start action gets it too.
    static readonly ConditionalWeakTable<Compilation, Resolved> Cache = new();

    sealed class Resolved(KnownTypes? known)
    {
        public readonly KnownTypes? Known = known;
    }

    readonly Compilation _compilation;
    public readonly INamedTypeSymbol Extensions;
    public readonly INamedTypeSymbol? Utf8, RawJson, Timestamp, IReply, ErrorReply, IResponse, IRequestScoped, JsonEnvelope, ArenaBytes;
    public readonly INamedTypeSymbol? Reply, ReplyOf, Pending, Viewer, Utf8List, RequestContext, HttpExchange, FromQuery, BodyError;
    public readonly INamedTypeSymbol? CancellationToken, JsonPropertyName, JsonIgnore, DefaultValue, Range, MinLength, Nullable, Table;

    KnownTypes(Compilation c, INamedTypeSymbol extensions)
    {
        _compilation = c;
        Extensions = extensions;
        Utf8 = c.GetTypeByMetadataName("CodeFusion.Utf8");
        RawJson = c.GetTypeByMetadataName("CodeFusion.RawJson");
        Timestamp = c.GetTypeByMetadataName("CodeFusion.Timestamp");
        IReply = c.GetTypeByMetadataName("CodeFusion.IReply");
        ErrorReply = c.GetTypeByMetadataName("CodeFusion.ErrorReply");
        IResponse = c.GetTypeByMetadataName("CodeFusion.IResponse");
        IRequestScoped = c.GetTypeByMetadataName("CodeFusion.IRequestScoped");
        JsonEnvelope = c.GetTypeByMetadataName("CodeFusion.JsonEnvelopeAttribute");
        ArenaBytes = c.GetTypeByMetadataName("CodeFusion.ArenaBytes");
        Reply = c.GetTypeByMetadataName("CodeFusion.Http.Reply");
        ReplyOf = c.GetTypeByMetadataName("CodeFusion.Http.Reply`1");
        Pending = c.GetTypeByMetadataName("CodeFusion.Http.Pending`1");
        Viewer = c.GetTypeByMetadataName("CodeFusion.Http.Viewer");
        Utf8List = c.GetTypeByMetadataName("CodeFusion.Utf8List");
        RequestContext = c.GetTypeByMetadataName("CodeFusion.Http.RequestContext");
        HttpExchange = c.GetTypeByMetadataName("CodeFusion.Http.HttpExchange");
        FromQuery = c.GetTypeByMetadataName("CodeFusion.Http.FromQueryAttribute");
        BodyError = c.GetTypeByMetadataName("CodeFusion.Http.BodyErrorAttribute");
        CancellationToken = c.GetTypeByMetadataName("System.Threading.CancellationToken");
        JsonPropertyName = c.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
        JsonIgnore = c.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
        DefaultValue = c.GetTypeByMetadataName("System.ComponentModel.DefaultValueAttribute");
        Range = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.RangeAttribute");
        MinLength = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.MinLengthAttribute");
        Table = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.TableAttribute");
        Nullable = c.GetSpecialType(SpecialType.System_Nullable_T);
    }

    /// <summary>The types of a compilation that references CodeFusion.Http, or null.</summary>
    public static KnownTypes? From(Compilation c) => Cache.GetValue(c, static c => new Resolved(
        c.GetTypeByMetadataName("CodeFusion.Http.EndpointRouteBuilderExtensions") is { } ext ? new KnownTypes(c, ext) : null)).Known;

    public static bool Is(ITypeSymbol? t, INamedTypeSymbol? known) =>
        known is not null && t is not null && SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, known);

    /// <summary>T of Nullable&lt;T&gt;, or null.</summary>
    public ITypeSymbol? Underlying(ITypeSymbol t) =>
        t is INamedTypeSymbol { IsGenericType: true } n && Is(n, Nullable) ? n.TypeArguments[0] : null;

    public static bool Implements(ITypeSymbol t, INamedTypeSymbol? i)
    {
        if (i is null) return false;
        foreach (var x in t.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(x, i)) return true;
        return false;
    }

    /// <summary>Whether code generated into this compilation can name <paramref name="s"/>: not private, protected,
    /// file-local or internal to another assembly, nor inside a type that is.</summary>
    public bool Visible(ISymbol s)
    {
        for (ISymbol? x = s; x is not null && x is not INamespaceSymbol; x = x.ContainingType)
            if (x is INamedTypeSymbol { IsFileLocal: true }) return false;
        return _compilation.IsSymbolAccessibleWithin(s, _compilation.Assembly);
    }

    /// <summary>Whether a value of <paramref name="t"/> points into a request's or a row's buffer: it must not be kept
    /// in a class.</summary>
    public bool IsTransient(ITypeSymbol t) =>
        Is(t, Utf8) || Is(t, RawJson) || Is(t, Utf8List) || Is(t, Viewer) || Is(t, ArenaBytes);
}
