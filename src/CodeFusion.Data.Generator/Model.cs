using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CodeFusion.Data.Generator;

/// <summary>The types the generator looks for, resolved once per compilation.</summary>
sealed class Known
{
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol Utf8, Timestamp, RawJson, DataContext, DataContextOf, Many, One, Page, WriteOf, Write;
    public readonly INamedTypeSymbol Query, OrderedQuery, Table, PageQuery, DeleteQuery, WriteQuery, UpdateQuery, Insert, SqlQuery, SqlString, FoundQuery;
    public readonly INamedTypeSymbol QueryOperators, QueryDefinition, SqlTemplate, JoinTable, JsonList, SqlDialect, Provider, SqlFunctions, Utf8Operators, Utf8ListOperators;
    public readonly INamedTypeSymbol? JsonEnvelope, JsonPropertyName, JsonIgnore, TableAttribute, ColumnAttribute, KeyAttribute, ForeignKey, InverseProperty, NotMapped;

    Known(Compilation c)
    {
        Compilation = c;
        INamedTypeSymbol T(string name) => c.GetTypeByMetadataName(name) ?? throw new InvalidOperationException(name);
        Utf8 = T("CodeFusion.Utf8");
        Timestamp = T("CodeFusion.Timestamp");
        RawJson = T("CodeFusion.RawJson");
        JsonEnvelope = c.GetTypeByMetadataName("CodeFusion.JsonEnvelopeAttribute");
        DataContext = T("CodeFusion.Data.DataContext");
        DataContextOf = T("CodeFusion.Data.DataContext`1");
        Many = T("CodeFusion.Data.Many`1");
        One = T("CodeFusion.Data.One`1");
        Page = T("CodeFusion.Data.Page`1");
        WriteOf = T("CodeFusion.Data.Write`1");
        Write = T("CodeFusion.Data.Write");
        Query = T("CodeFusion.Data.Query`1");
        OrderedQuery = T("CodeFusion.Data.OrderedQuery`1");
        Table = T("CodeFusion.Data.Table`1");
        PageQuery = T("CodeFusion.Data.PageQuery`1");
        DeleteQuery = T("CodeFusion.Data.DeleteQuery`1");
        WriteQuery = T("CodeFusion.Data.WriteQuery`1");
        UpdateQuery = T("CodeFusion.Data.UpdateQuery`1");
        FoundQuery = T("CodeFusion.Data.FoundQuery`1");
        Insert = T("CodeFusion.Data.Insert`1");
        SqlQuery = T("CodeFusion.Data.SqlQuery`1");
        SqlString = T("CodeFusion.Data.SqlString");
        QueryOperators = T("CodeFusion.Data.QueryOperators");
        QueryDefinition = T("CodeFusion.Data.QueryDefinitionAttribute");
        SqlTemplate = T("CodeFusion.Data.SqlTemplateAttribute");
        JoinTable = T("CodeFusion.Data.JoinTableAttribute");
        JsonList = T("CodeFusion.Data.JsonListAttribute");
        SqlDialect = T("CodeFusion.Data.SqlDialectAttribute");
        Provider = T("CodeFusion.Data.IDataProvider`3");
        SqlFunctions = T("CodeFusion.Data.SqlFunctions");
        Utf8Operators = T("CodeFusion.Data.Utf8Operators");
        Utf8ListOperators = T("CodeFusion.Data.Utf8ListOperators");
        JsonPropertyName = c.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute");
        JsonIgnore = c.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
        TableAttribute = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.TableAttribute");
        ColumnAttribute = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.ColumnAttribute");
        KeyAttribute = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.KeyAttribute");
        ForeignKey = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute");
        InverseProperty = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.InversePropertyAttribute");
        NotMapped = c.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute");
    }

    /// <summary>The symbols, or null when the compilation doesn't reference CodeFusion.Data.</summary>
    public static Known? Of(Compilation c) =>
        c.GetTypeByMetadataName("CodeFusion.Data.DataContext") is null || c.GetTypeByMetadataName("CodeFusion.Utf8") is null ? null : new Known(c);

    public static bool Is(ITypeSymbol? t, INamedTypeSymbol def) => t is INamedTypeSymbol n && SymbolEqualityComparer.Default.Equals(n.OriginalDefinition, def);

    public static bool Same(ISymbol? a, ISymbol? b) => SymbolEqualityComparer.Default.Equals(a, b);

    public static AttributeData? Attribute(ISymbol s, INamedTypeSymbol? type) =>
        type is null ? null : s.GetAttributes().FirstOrDefault(a => Same(a.AttributeClass, type));

    public bool DerivesFromContext(ITypeSymbol? t)
    {
        for (var b = t; b is not null; b = b.BaseType)
            if (Same(b, DataContext)) return true;
        return false;
    }

    /// <summary>The <c>TProvider</c> of the context's <c>DataContext&lt;TProvider&gt;</c> base.</summary>
    public INamedTypeSymbol? ProviderOf(ITypeSymbol context)
    {
        for (var b = context.BaseType; b is not null; b = b.BaseType)
            if (Is(b, DataContextOf)) return b.TypeArguments[0] as INamedTypeSymbol;
        return null;
    }
}

/// <summary>What a column or view member holds.</summary>
enum Scalar { Text, Timestamp, Raw, Int64, Int32, Int16, Boolean, Double, Single }

/// <summary>A value type's classification: a scalar, or a nested view.</summary>
readonly record struct ValueKind(Scalar? Scalar, bool Nullable, ITypeSymbol Type)
{
    public bool IsScalar => Scalar is not null;
}

static class Kinds
{
    /// <summary>What <paramref name="t"/> is as a column or view member; null when it is neither scalar nor a struct.</summary>
    public static ValueKind? Of(Known k, ITypeSymbol t)
    {
        bool nullable = false;
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            t = n.TypeArguments[0];
            nullable = true;
        }
        Scalar? s = t.SpecialType switch
        {
            SpecialType.System_Int64 => Scalar.Int64,
            SpecialType.System_Int32 => Scalar.Int32,
            SpecialType.System_Int16 => Scalar.Int16,
            SpecialType.System_Boolean => Scalar.Boolean,
            SpecialType.System_Double => Scalar.Double,
            SpecialType.System_Single => Scalar.Single,
            _ => null,
        };
        if (s is null)
        {
            if (Known.Same(t, k.Utf8)) s = Scalar.Text;
            else if (Known.Same(t, k.Timestamp)) s = Scalar.Timestamp;
            else if (Known.Same(t, k.RawJson)) s = Scalar.Raw;
        }
        if (s is not null) return new ValueKind(s, nullable, t);
        if (t.IsValueType && t is INamedTypeSymbol { TypeKind: TypeKind.Struct } && !nullable) return new ValueKind(null, false, t);
        return null;
    }
}

/// <summary>A view member: one column if scalar, its nested view's columns otherwise.</summary>
sealed record ViewMember(IPropertySymbol Property, string JsonName, bool Ignored, ValueKind Kind, ViewInfo? Nested);

/// <summary>
/// A view: a struct whose members are the row's columns, in declaration order, nested views flattened. The same
/// order is the SQL's select list, the JSON's properties and the constructor's arguments.
/// </summary>
sealed class ViewInfo
{
    public readonly INamedTypeSymbol Type;
    public readonly List<ViewMember> Members = new();
    public readonly string? Envelope, ListName, CountName;
    /// <summary>The constructor whose parameters are the members by name, or null for an object initializer.</summary>
    public readonly IMethodSymbol? Constructor;
    public int Width;

    ViewInfo(INamedTypeSymbol type, string? envelope, string? listName, string? countName, IMethodSymbol? ctor)
    {
        Type = type;
        Envelope = envelope;
        ListName = listName;
        CountName = countName;
        Constructor = ctor;
    }

    /// <summary>The view of <paramref name="type"/>, with an error for each member it can't hold.</summary>
    public static ViewInfo? Of(Known k, INamedTypeSymbol type, List<Diagnostic> errors, Location at, int depth = 0)
    {
        if (depth > 16)
        {
            errors.Add(Diagnostic.Create(Diagnostics.BadViewMember, at, type.Name, "a view nested too deep"));
            return null;
        }
        var props = PublicProperties(type);
        var env = Known.Attribute(type, k.JsonEnvelope)?.ConstructorArguments.FirstOrDefault().Value as string;
        var list = Known.Attribute(type, k.JsonList);
        string? listName = list?.ConstructorArguments.FirstOrDefault().Value as string;
        string? countName = list?.NamedArguments.FirstOrDefault(a => a.Key == "CountName").Value.Value as string;
        IMethodSymbol? ctor = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal && k.Compilation.IsSymbolAccessibleWithin(c, k.Compilation.Assembly)
                && c.Parameters.Length == props.Count && c.Parameters.Length > 0)
            .FirstOrDefault(c => c.Parameters.All(p => props.Any(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase) && Known.Same(m.Type, p.Type))));
        var view = new ViewInfo(type, env, listName, countName, ctor);
        bool ok = true;
        foreach (var p in props)
        {
            var kind = Kinds.Of(k, p.Type);
            if (kind is null)
            {
                errors.Add(Diagnostic.Create(Diagnostics.BadViewMember, Here(k, p, at), p.Name, p.Type.ToDisplayString()));
                ok = false;
                continue;
            }
            string json = Known.Attribute(p, k.JsonPropertyName)?.ConstructorArguments.FirstOrDefault().Value as string ?? Names.Camel(p.Name);
            bool ignored = Known.Attribute(p, k.JsonIgnore) is not null;
            ViewInfo? nested = null;
            if (!kind.Value.IsScalar)
            {
                nested = Of(k, (INamedTypeSymbol)kind.Value.Type, errors, at, depth + 1);
                if (nested is null)
                {
                    ok = false;
                    continue;
                }
            }
            view.Members.Add(new ViewMember(p, json, ignored, kind.Value, nested));
            view.Width += nested?.Width ?? 1;
        }
        if (view.Members.Count == 0 && ok)
        {
            errors.Add(Diagnostic.Create(Diagnostics.BadViewMember, at, type.Name, "a struct without public properties"));
            ok = false;
        }
        // the generated reader that copies a row sets the members from outside the view, in the app's assembly (an
        // internal setter of another assembly's view is out of its reach)
        if (ctor is null && ok)
            foreach (var m in view.Members)
                if (m.Property.SetMethod is not { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal } set
                    || !k.Compilation.IsSymbolAccessibleWithin(set, k.Compilation.Assembly))
                {
                    errors.Add(Diagnostic.Create(Diagnostics.BadViewMember, Here(k, m.Property, at), m.Property.Name,
                        "a view needs a constructor taking every member, or members with a public or internal set or init"));
                    ok = false;
                    break;
                }
        return ok ? view : null;
    }

    /// <summary>Where <paramref name="s"/> is declared, if in the app's source (a view of another assembly's is told
    /// where it is used).</summary>
    static Location Here(Known k, ISymbol s, Location at) =>
        s.Locations.FirstOrDefault(l => l.SourceTree is { } t && k.Compilation.ContainsSyntaxTree(t)) ?? at;

    /// <summary>The public instance properties in declaration order (a record's positional ones first, as declared).</summary>
    static List<IPropertySymbol> PublicProperties(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && p.Name != "EqualityContract" && p.GetMethod is not null)
            .ToList();

    public ViewMember? Member(string name) => Members.FirstOrDefault(m => m.Property.Name == name);
}

/// <summary>A mapped column of an entity.</summary>
sealed record ColumnInfo(IPropertySymbol Property, string Name, ValueKind Kind);

/// <summary>A one-navigation: <c>User Author</c> with its foreign key <c>AuthorId</c>.</summary>
sealed record OneNav(IPropertySymbol Property, INamedTypeSymbol Target, ColumnInfo ForeignKey);

/// <summary>A many-navigation: children holding this entity's key, or rows through a join table.</summary>
sealed record ManyNav(IPropertySymbol Property, INamedTypeSymbol Target, string? InverseNavigation, string? JoinTable, string? ThisColumn, string? OtherColumn)
{
    public bool IsThrough => JoinTable is not null;
}

/// <summary>An entity: a class describing a table for the translator. It is never built at run time.</summary>
sealed class EntityInfo
{
    public readonly INamedTypeSymbol Type;
    public readonly string Table;
    /// <summary>The table's schema, from <c>[Table(Schema = ...)]</c>; null for the search path's.</summary>
    public readonly string? Schema;
    public readonly Dictionary<string, ColumnInfo> Columns = new();
    public readonly Dictionary<string, OneNav> Ones = new();
    public readonly Dictionary<string, ManyNav> Manys = new();
    public ColumnInfo? Key;

    EntityInfo(INamedTypeSymbol type, string table, string? schema)
    {
        Type = type;
        Table = table;
        Schema = schema;
    }

    /// <summary>Maps <paramref name="type"/> with <paramref name="table"/> as its table's name, in <paramref name="schema"/>.</summary>
    public static EntityInfo Build(Known k, INamedTypeSymbol type, string table, string? schema, List<string> problems)
    {
        var e = new EntityInfo(type, table, schema);
        var props = new List<IPropertySymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            props.InsertRange(0, t.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public));
        foreach (var p in props)
        {
            if (Known.Attribute(p, k.NotMapped) is not null) continue;
            if (Known.Is(p.Type, k.Many))
            {
                var target = ((INamedTypeSymbol)p.Type).TypeArguments[0] as INamedTypeSymbol;
                if (target is null || target.TypeKind != TypeKind.Class)
                {
                    problems.Add($"{p.Name}: a Many<T> navigation needs an entity class as T");
                    continue;
                }
                var join = Known.Attribute(p, k.JoinTable);
                string? inverse = Known.Attribute(p, k.InverseProperty)?.ConstructorArguments.FirstOrDefault().Value as string;
                if (join is not null)
                {
                    var args = join.ConstructorArguments;
                    e.Manys[p.Name] = new ManyNav(p, target, null, (string)args[0].Value!,
                        args.Length > 1 ? (string?)args[1].Value : null, args.Length > 2 ? (string?)args[2].Value : null);
                }
                else e.Manys[p.Name] = new ManyNav(p, target, inverse, null, null, null);
                continue;
            }
            var kind = Kinds.Of(k, p.Type);
            if (kind is { IsScalar: true })
            {
                string name = Known.Attribute(p, k.ColumnAttribute)?.ConstructorArguments.FirstOrDefault().Value as string ?? Names.Snake(p.Name);
                var col = new ColumnInfo(p, name, kind.Value);
                e.Columns[p.Name] = col;
                if (Known.Attribute(p, k.KeyAttribute) is not null || (p.Name == "Id" && e.Key is null)) e.Key = col;
                continue;
            }
            if (p.Type is INamedTypeSymbol { TypeKind: TypeKind.Class } nav && nav.SpecialType != SpecialType.System_String)
                continue; // a one-navigation: resolved below, once every column is known
            problems.Add($"{p.Name}: type {p.Type.ToDisplayString()} can't be a column (mark it [NotMapped] if it isn't one)");
        }
        foreach (var p in props)
        {
            if (Known.Attribute(p, k.NotMapped) is not null || Known.Is(p.Type, k.Many) || e.Columns.ContainsKey(p.Name)) continue;
            if (p.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class } target || target.SpecialType == SpecialType.System_String) continue;
            // [ForeignKey("AuthorId")] on the navigation, [ForeignKey("Author")] on the key, or AuthorId by convention
            string? fkName = Known.Attribute(p, k.ForeignKey)?.ConstructorArguments.FirstOrDefault().Value as string;
            fkName ??= props.FirstOrDefault(q => Known.Attribute(q, k.ForeignKey)?.ConstructorArguments.FirstOrDefault().Value as string == p.Name)?.Name;
            fkName ??= p.Name + "Id";
            if (!e.Columns.TryGetValue(fkName, out var fk))
            {
                problems.Add($"{p.Name}: no foreign key {fkName} for the navigation");
                continue;
            }
            e.Ones[p.Name] = new OneNav(p, target, fk);
        }
        return e;
    }
}
