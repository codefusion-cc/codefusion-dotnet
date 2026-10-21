using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeFusion.Http.Generators;

/// <summary>
/// What a Map call's handler is: its parameters, its reply and the structs they bind, checked against the rules.
/// The generator and the analyzers share it, so they agree on what compiles.
/// </summary>
internal sealed class Analysis(KnownTypes known)
{
    public static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly SymbolDisplayFormat Short = SymbolDisplayFormat.MinimallyQualifiedFormat;

    readonly List<DiagnosticInfo> _diagnostics = [];
    readonly Dictionary<string, ObjectShape?> _objects = [];
    readonly Dictionary<string, BodyModel?> _bodies = [];
    readonly Dictionary<string, QueryModel?> _queries = [];

    public IReadOnlyList<DiagnosticInfo> Reported => _diagnostics;

    void Report(DiagnosticDescriptor d, Location? at, params string[] args) =>
        _diagnostics.Add(new DiagnosticInfo(d.Id, LocationInfo.From(at), args.ToEquatable()));

    /// <summary>Whether <paramref name="method"/> is one of the Map overloads, and which verb.</summary>
    public static string? Verb(IMethodSymbol method, KnownTypes known)
    {
        IMethodSymbol m = (method.ReducedFrom ?? method).OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(m.ContainingType, known.Extensions)) return null;
        return m.Name switch { "MapGet" => "Get", "MapPost" => "Post", "MapPut" => "Put", "MapDelete" => "Delete", _ => null };
    }

    /// <summary>The handler's method and how it was written: a lambda, a method group, or neither.</summary>
    public static (IMethodSymbol? Method, IAnonymousFunctionOperation? Lambda, IMethodReferenceOperation? Group) Handler(IOperation? argument)
    {
        IOperation? op = argument;
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c: op = c.Operand; continue;
                case IDelegateCreationOperation d: op = d.Target; continue;
                case IParenthesizedOperation p: op = p.Operand; continue;
                case IAnonymousFunctionOperation a: return (a.Symbol, a, null);
                case IMethodReferenceOperation r: return (r.Method, null, r);
                default: return (null, null, null);
            }
        }
    }

    // ---- The generator's entry ----

    /// <summary>Analyses a Map call for the generator; null when it isn't one.</summary>
    public static EndpointResult? Endpoint(SemanticModel model, InvocationExpressionSyntax call, CancellationToken ct)
    {
        if (model.GetOperation(call, ct) is not IInvocationOperation invocation) return null;
        if (KnownTypes.From(model.Compilation) is not { } known) return null;
        string? verb = Verb(invocation.TargetMethod, known);
        if (verb is null) return null;
        var a = new Analysis(known);
        EndpointModel? endpoint = a.Endpoint(model, call, invocation, verb, ct);
        return new EndpointResult(endpoint, a._diagnostics.ToEquatable());
    }

    EndpointModel? Endpoint(SemanticModel model, InvocationExpressionSyntax call, IInvocationOperation invocation, string verb, CancellationToken ct)
    {
        IArgumentOperation? patternArg = invocation.Arguments.FirstOrDefault(x => x.Parameter?.Name == "pattern");
        IArgumentOperation? handlerArg = invocation.Arguments.FirstOrDefault(x => x.Parameter?.Name == "handler");
        if (patternArg is null || handlerArg is null) return null;

        bool ok = true;
        string? pattern = patternArg.Value.ConstantValue is { HasValue: true, Value: string s } ? s : null;
        if (pattern is null)
        {
            Report(Diagnostics.BadPattern, patternArg.Syntax.GetLocation(), "The route must be a string literal or constant: the generator reads its parameters at build time");
            ok = false;
        }
        else if (Template(pattern) is { } why)
        {
            Report(Diagnostics.BadPattern, patternArg.Syntax.GetLocation(), $"The route '{pattern}' is not a template: {why}");
            ok = false;
        }

        var (method, lambda, group) = Handler(handlerArg.Value);
        if (method is null)
        {
            Report(Diagnostics.BadHandler, handlerArg.Syntax.GetLocation());
            return null;
        }
        Location at = handlerArg.Syntax.GetLocation();

        var parameters = new List<ParameterModel>();
        bool hasBody = false, hasQuery = false;
        var bodies = new List<BodyModel>();
        var queries = new List<QueryModel>();
        foreach (IParameterSymbol p in method.Parameters)
        {
            Location pAt = lambda is not null && p.Locations.FirstOrDefault() is { IsInSource: true } l ? l : at;
            ParameterModel? pm = Parameter(p, pAt, ref hasBody, ref hasQuery, bodies, queries);
            if (pm is null) ok = false;
            else parameters.Add(pm);
        }

        ReplyModel? reply = Reply(method.ReturnType, at, out string? cannotSend);
        if (reply is null)
        {
            Report(Diagnostics.CannotSend, at, cannotSend!);
            ok = false;
        }

        foreach (IParameterSymbol p in method.Parameters)
            if (!Accessible(p.Type, at)) ok = false;
        // a reply type the generator can't name is already a reason it can't send it
        if (reply is not null && !Accessible(method.ReturnType, at)) ok = false;
        if (!ok) return null;

        IParameterSymbol handlerParameter = invocation.TargetMethod.Parameters.Last();
        string delegateParameterType = handlerParameter.Type.ToDisplayString(Fq);
        string? direct = group is not null ? Direct(method) : null;
        string? delegateType = null;
        if (direct is null)
        {
            if (handlerParameter.Type.SpecialType == SpecialType.System_Delegate)
            {
                // the endpoint holds the handler as the Func its parameters make, which is its delegate's type only if
                // it has one: otherwise the cast would fail when the endpoint is mapped
                if (Unnamed(method) is { } why)
                {
                    Report(Diagnostics.UnnamedDelegate, at, why);
                    return null;
                }
                delegateType = "global::System.Func<" + string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(Fq)).Append(method.ReturnType.ToDisplayString(Fq))) + ">";
            }
            else delegateType = delegateParameterType;
        }

        if (model.GetInterceptableLocation(call, ct) is not { } location)
        {
            Report(Diagnostics.NotInterceptable, call.GetLocation());
            return null;
        }

        return new EndpointModel(
            call.SyntaxTree.FilePath, call.SpanStart, location.Version, location.Data, location.GetDisplayLocation(), verb, delegateParameterType, direct, delegateType,
            parameters.ToEquatable(), reply!, bodies.ToEquatable(), queries.ToEquatable(),
            _objects.Values.Where(o => o is not null).Select(o => o!).OrderBy(o => o.Type, StringComparer.Ordinal).ToEquatable());
    }

    /// <summary>How generated code calls the static method <paramref name="m"/> by name, or null when it can't.</summary>
    string? Direct(IMethodSymbol m)
    {
        if (!m.IsStatic || !known.Visible(m) || !Nameable(m.ContainingType) || !m.TypeArguments.All(Nameable)) return null;
        string name = Identifier(m.Name);
        if (m.IsGenericMethod) name += "<" + string.Join(", ", m.TypeArguments.Select(a => a.ToDisplayString(Fq))) + ">";
        return $"{m.ContainingType.ToDisplayString(Fq)}.{name}";
    }

    /// <summary>Whether generated code can write <paramref name="t"/>: no type parameter in it, every part visible.</summary>
    bool Nameable(ITypeSymbol t) => Parts(t).All(x => x is not ITypeParameterSymbol && (x is not INamedTypeSymbol n || known.Visible(n)));

    /// <summary>Why a handler passed as a Delegate has no Func type, or null. Up to 16 parameters, none with a default
    /// or params, it is a Func; past that the compiler makes a delegate type up, which generated code can't name.</summary>
    static string? Unnamed(IMethodSymbol m)
    {
        const string ByName = "map an internal or public static method, which the endpoint calls by name";
        if (m.Parameters.Length > 16)
            return $"The handler takes {m.Parameters.Length} parameters, and the Func the endpoint holds it as takes 16 at most: put some in a body or [FromQuery] struct, or {ByName}";
        if (m.Parameters.FirstOrDefault(p => p.HasExplicitDefaultValue || p.IsParams) is { } odd)
            return $"The handler's parameter '{odd.Name}' {(odd.IsParams ? "is params" : "has a default value")}, which gives the handler a delegate type the generated endpoint can't name: drop {(odd.IsParams ? "params" : "the default")}, or {ByName}";
        return null;
    }

    /// <summary>A name as C# source: a keyword gets its '@' back.</summary>
    public static string Identifier(string name) =>
        SyntaxFacts.IsReservedKeyword(SyntaxFacts.GetKeywordKind(name)) ? "@" + name : name;

    /// <summary>Why <paramref name="pattern"/> isn't a route template, or null; the rules of CodeFusion.Http's RouteTemplate.</summary>
    public static string? Template(string pattern)
    {
        if (pattern.Length == 0 || pattern == "/") return null;
        if (pattern[0] != '/') return "it must start with '/'";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in pattern.Substring(1).Split('/'))
        {
            if (p.Length == 0) return "a segment is empty";
            if (p[0] == '{')
            {
                string name = p.Length > 2 && p[p.Length - 1] == '}' ? p.Substring(1, p.Length - 2) : "";
                if (!IsIdentifier(name)) return $"'{p}' is not a parameter like {{name}}";
                if (!names.Add(name)) return $"{{{name}}} appears twice";
            }
            else if (p.IndexOf('{') >= 0 || p.IndexOf('}') >= 0) return $"'{p}' mixes a literal and a parameter";
        }
        return null;
    }

    static bool IsIdentifier(string s) =>
        s.Length > 0 && (IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => IsAsciiLetter(c) || c is >= '0' and <= '9' || c == '_');

    static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    // ---- Parameters ----

    ParameterModel? Parameter(IParameterSymbol p, Location at, ref bool hasBody, ref bool hasQuery, List<BodyModel> bodies, List<QueryModel> queries)
    {
        ITypeSymbol t = p.Type;
        string type = t.ToDisplayString(Fq);
        if (p.RefKind != RefKind.None)
        {
            Report(Diagnostics.BadParameter, at, $"The handler's parameter '{p.Name}' is passed by reference: take it by value");
            return null;
        }
        if (t.SpecialType == SpecialType.System_String)
        {
            Report(Diagnostics.StringParameter, at, p.Name);
            return null;
        }
        if (KnownTypes.Is(t, known.Utf8))
        {
            if (Attribute(p, known.Range) is null) return new(p.Name, ParameterKind.RouteText, type);
            Report(Diagnostics.BadParameter, at, $"The handler's parameter '{p.Name}' is a Utf8 route value: [Range] is for long and int ones");
            return null;
        }
        if (t.SpecialType is SpecialType.System_Int64 or SpecialType.System_Int32) return RouteNumber(p, at, type, t.SpecialType == SpecialType.System_Int32);
        if (KnownTypes.Is(t, known.Viewer)) return new(p.Name, ParameterKind.Viewer, type);
        if (KnownTypes.Is(t, known.IResponse) || KnownTypes.Is(t, known.RequestContext)) return new(p.Name, ParameterKind.Response, type);
        if (KnownTypes.Is(t, known.HttpExchange)) return new(p.Name, ParameterKind.Exchange, type);
        if (KnownTypes.Is(t, known.CancellationToken)) return new(p.Name, ParameterKind.Aborted, type);

        if (t is INamedTypeSymbol { TypeKind: TypeKind.Struct } s && known.Underlying(s) is null && s.SpecialType == SpecialType.None && !IsFramework(s))
        {
            if (HasAttribute(s, known.FromQuery))
            {
                if (hasQuery)
                {
                    Report(Diagnostics.BadParameter, at, $"The handler takes a second [FromQuery] struct, '{p.Name}': put all of its query parameters in one");
                    return null;
                }
                hasQuery = true;
                QueryModel? q = Query(s, at);
                if (q is null) return null;
                queries.Add(q);
                return new(p.Name, ParameterKind.Query, type);
            }
            if (hasBody)
            {
                Report(Diagnostics.BadParameter, at, $"The handler takes a second body struct, '{p.Name}': a request has one body");
                return null;
            }
            hasBody = true;
            if (!Body(s, at, top: true, bodies)) return null;
            return new(p.Name, ParameterKind.Body, type);
        }

        // a service taken as T? is resolved as T: what the handler gets is never null
        if (t.TypeKind is TypeKind.Class or TypeKind.Interface && t.SpecialType == SpecialType.None)
            return new(p.Name, KnownTypes.Implements(t, known.IRequestScoped) ? ParameterKind.ScopedService : ParameterKind.Service,
                t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(Fq));

        Report(Diagnostics.BadParameter, at,
            $"The handler's parameter '{p.Name}' is a {t.ToDisplayString(Short)}, which can't be bound: route values are Utf8, long or int; the query and the body are read into structs");
        return null;
    }

    /// <summary>A long or int route value, with the bounds of its [Range]: only digits are read, so nothing is below 0,
    /// and the type's own top needs no check.</summary>
    ParameterModel? RouteNumber(IParameterSymbol p, Location at, string type, bool int32)
    {
        if (!RangeOf(p, $"The handler's parameter '{p.Name}'", Diagnostics.BadParameter, at, out RangeEnds? range)) return null;
        long top = int32 ? int.MaxValue : long.MaxValue;
        if (Whole(range, 0, top) is not { } bounds)
        {
            Report(Diagnostics.BadParameter, at, $"The handler's parameter '{p.Name}''s [Range] leaves no {(int32 ? "int" : "long")} of digits");
            return null;
        }
        return new(p.Name, int32 ? ParameterKind.RouteInt32 : ParameterKind.RouteInt64, type,
            bounds.Lo == 0 ? null : Literal(bounds.Lo), bounds.Hi == top ? null : Literal(bounds.Hi));
    }

    static bool IsFramework(INamedTypeSymbol t)
    {
        string ns = t.ContainingNamespace?.ToDisplayString() ?? "";
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns == "CodeFusion" || ns == "CodeFusion.Http";
    }

    static bool HasAttribute(ISymbol s, INamedTypeSymbol? attribute) =>
        attribute is not null && s.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

    static AttributeData? Attribute(ISymbol s, INamedTypeSymbol? attribute) =>
        attribute is null ? null : s.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

    // ---- Bound structs ----

    /// <summary>Where a bound struct is declared: it must be a partial struct of this project, in partial containers.</summary>
    TypeHome? Home(INamedTypeSymbol s, Location at)
    {
        string name = s.ToDisplayString(Short);
        if (s.IsGenericType)
        {
            Report(Diagnostics.BadStruct, at, $"'{name}' is generic: the generator binds plain structs");
            return null;
        }
        if (s.IsFileLocal)
        {
            Report(Diagnostics.BadStruct, at, $"'{name}' is file-local: the binder the generator adds would be another type in another file");
            return null;
        }
        if (!s.Locations.Any(l => l.IsInSource) || !IsPartial(s))
        {
            Report(Diagnostics.BadStruct, at, $"'{name}' must be a partial struct of this project: the generator adds its binder to it");
            return null;
        }
        var containers = new List<string>();
        for (INamedTypeSymbol? c = s.ContainingType; c is not null; c = c.ContainingType)
        {
            if (!IsPartial(c))
            {
                Report(Diagnostics.BadStruct, at, $"'{name}' is nested in '{c.ToDisplayString(Short)}', which must be partial too");
                return null;
            }
            containers.Insert(0, $"partial {Keyword(c)} {Identifier(c.Name)}");
        }
        string? ns = s.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString(Namespace) : null;
        string full = s.ToDisplayString(Fq);
        string hint = full.Replace("global::", "").Replace("@", "").Replace('<', '_').Replace('>', '_');
        return new TypeHome(ns, containers.ToEquatable(), Keyword(s), Identifier(s.Name), full, hint);
    }

    static readonly SymbolDisplayFormat Namespace = SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    static string Keyword(INamedTypeSymbol t) =>
        t.IsRecord ? (t.TypeKind == TypeKind.Struct ? "record struct" : "record") : t.TypeKind == TypeKind.Struct ? "struct" : t.TypeKind == TypeKind.Interface ? "interface" : "class";

    static bool IsPartial(INamedTypeSymbol t) =>
        t.DeclaringSyntaxReferences.Length > 0 && t.DeclaringSyntaxReferences.All(r =>
            r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

    /// <summary>The struct's static ErrorReply field or property named <paramref name="name"/>.</summary>
    bool HasErrorReply(INamedTypeSymbol s, string name) =>
        s.GetMembers(name).Any(m => m.IsStatic && (m is IFieldSymbol f && KnownTypes.Is(f.Type, known.ErrorReply) || m is IPropertySymbol p && KnownTypes.Is(p.Type, known.ErrorReply) && p.GetMethod is not null));

    /// <summary>The instance members a binder sets, in declaration order: public fields, and public properties with a
    /// public setter, which is what a caller of the struct could set; the rest a request must not reach. Computed
    /// properties are skipped; public read-only storage is an error, and so is a required member the binder doesn't
    /// set, since it creates the struct with what it set. Internal storage it could set is a warning at the member:
    /// it bound before, and could look like data.</summary>
    IEnumerable<(ISymbol Member, ITypeSymbol Type)> Settable(INamedTypeSymbol s, Location at, string what)
    {
        var autoProperties = new HashSet<ISymbol>(s.GetMembers().OfType<IFieldSymbol>().Where(f => f.AssociatedSymbol is not null).Select(f => f.AssociatedSymbol!), SymbolEqualityComparer.Default);
        foreach (ISymbol m in s.GetMembers())
        {
            if (m.IsStatic || m.IsImplicitlyDeclared) continue;
            bool ignored = HasAttribute(m, known.JsonIgnore);
            string? left = ignored ? "it is [JsonIgnore]"
                : m.DeclaredAccessibility != Accessibility.Public ? "it isn't public"
                : m is IPropertySymbol { SetMethod: { } setter } && setter.DeclaredAccessibility != Accessibility.Public ? "its setter isn't public"
                : null;
            if (left is not null)
            {
                if (m is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true })
                {
                    Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is required, and the {what} binder doesn't set it ({left}), so it can't create the struct: let the binder set it, or drop required");
                    yield return (m, null!);
                }
                else if (!ignored && m.DeclaredAccessibility is not (Accessibility.Private or Accessibility.Public)
                    && (m is IFieldSymbol { IsConst: false, IsReadOnly: false } || m is IPropertySymbol { SetMethod: not null } && autoProperties.Contains(m)))
                    Report(Diagnostics.NotBound, m.Locations.FirstOrDefault(), $"{s.Name}.{m.Name}", what);
                continue;
            }
            if (m is IFieldSymbol f)
            {
                if (f.IsConst) continue;
                if (f.IsReadOnly)
                {
                    Report(Diagnostics.BadMember, at, $"'{s.Name}.{f.Name}' is readonly: the {what} binder sets it");
                    yield return (f, null!);
                    continue;
                }
                yield return (f, f.Type);
            }
            else if (m is IPropertySymbol p && !p.IsIndexer)
            {
                if (p.SetMethod is null)
                {
                    if (autoProperties.Contains(p))
                    {
                        Report(Diagnostics.BadMember, at, $"'{s.Name}.{p.Name}' has no setter: give it set or init, the {what} binder sets it");
                        yield return (p, null!);
                    }
                    continue;
                }
                yield return (p, p.Type);
            }
        }
    }

    QueryModel? Query(INamedTypeSymbol s, Location at)
    {
        string key = s.ToDisplayString(Fq);
        if (_queries.TryGetValue(key, out QueryModel? done)) return done;
        _queries[key] = null;
        TypeHome? home = Home(s, at);
        if (home is null) return null;
        bool ok = true, numbers = false;
        var fields = new List<QueryField>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (m, t) in Settable(s, at, "query"))
        {
            if (t is null)
            {
                ok = false;
                continue;
            }
            ITypeSymbol inner = known.Underlying(t) ?? t;
            bool nullable = known.Underlying(t) is not null;
            QueryFieldKind? kind = KnownTypes.Is(inner, known.Utf8) ? QueryFieldKind.Text
                : inner.SpecialType == SpecialType.System_Int64 ? QueryFieldKind.Int64
                : inner.SpecialType == SpecialType.System_Int32 ? QueryFieldKind.Int32
                : null;
            if (kind is null)
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is a {t.ToDisplayString(Short)}: query parameters are read as Utf8, Utf8?, long, int or their nullables");
                ok = false;
                continue;
            }
            string name = Attribute(m, known.FromQuery)?.NamedArguments.FirstOrDefault(a => a.Key == "Name").Value.Value as string ?? CamelCase(m.Name);
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 32)
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is read as '{name}': a query parameter's name is 1 to 32 bytes");
                ok = false;
                continue;
            }
            if (!keys.Add(name))
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is read as '{name}', as another member is");
                ok = false;
                continue;
            }
            // only digits are read, so nothing is below 0; an int can't hold more than int.MaxValue
            long top = kind == QueryFieldKind.Int32 ? int.MaxValue : long.MaxValue;
            long lo = 0, hi = top;
            string? @default = null;
            if (kind == QueryFieldKind.Text && Attribute(m, known.Range) is not null)
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}''s [Range] is for numbers, and it is a {t.ToDisplayString(Short)}");
                ok = false;
                continue;
            }
            if (kind != QueryFieldKind.Text)
            {
                numbers = true;
                if (!RangeOf(m, s.Name, at, out RangeEnds? range))
                {
                    ok = false;
                    continue;
                }
                if (Whole(range, 0, top) is not { } bounds)
                {
                    Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}''s [Range] leaves no {(kind == QueryFieldKind.Int32 ? "int" : "long")} of digits");
                    ok = false;
                    continue;
                }
                (lo, hi) = bounds;
                if (Attribute(m, known.DefaultValue) is { ConstructorArguments.Length: 1 } dv)
                {
                    long? d = Number(dv.ConstructorArguments[0], ceiling: true);
                    if (d is null || kind == QueryFieldKind.Int32 && d is < int.MinValue or > int.MaxValue)
                    {
                        Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}''s [DefaultValue] isn't a whole number its type holds");
                        ok = false;
                        continue;
                    }
                    @default = Literal(d.Value);
                }
            }
            fields.Add(new QueryField(m.Name, name, kind.Value, nullable, Literal(lo), Literal(hi), @default));
        }
        string? invalid = HasErrorReply(s, "Invalid") ? "Invalid" : null;
        if (numbers && invalid is null)
        {
            Report(Diagnostics.BadStruct, at, $"'{s.Name}' reads numbers from the query: declare the reply to an invalid one, 'static ErrorReply Invalid'");
            ok = false;
        }
        if (!ok) return null;
        var model = new QueryModel(home, invalid, fields.ToEquatable());
        _queries[key] = model;
        return model;
    }

    /// <summary>A [Range]'s ends, each a long or a double, and whether each is left out.</summary>
    readonly record struct RangeEnds(object Min, bool MinExclusive, object Max, bool MaxExclusive);

    /// <summary>The member's [Range] in any of its forms, null when it has none; false (reported) when it can't be
    /// read. The string form's numbers are read in the invariant culture, as the generator has no other.</summary>
    bool RangeOf(ISymbol m, string owner, Location at, out RangeEnds? range) =>
        RangeOf(m, $"'{owner}.{m.Name}'", Diagnostics.BadMember, at, out range);

    /// <summary>The same, for <paramref name="what"/> (how the message names it), reported as <paramref name="rule"/>.</summary>
    bool RangeOf(ISymbol m, string what, DiagnosticDescriptor rule, Location at, out RangeEnds? range)
    {
        range = null;
        if (Attribute(m, known.Range) is not { } a) return true;
        ImmutableArray<TypedConstant> args = a.ConstructorArguments;
        object? min = null, max = null;
        if (args.Length == 2)
        {
            min = End(args[0].Value);
            max = End(args[1].Value);
        }
        else if (args.Length == 3 && args[0].Value is ITypeSymbol { SpecialType: SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Byte
            or SpecialType.System_SByte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal })
        {
            min = End(args[1].Value as string);
            max = End(args[2].Value as string);
        }
        if (min is null || max is null)
        {
            Report(rule, at, $"{what}'s [Range] isn't one the generator can read: write [Range(min, max)] with numbers, or [Range(typeof(long), \"min\", \"max\")] with a number type and numbers written as in C#");
            return false;
        }
        bool Set(string name) => a.NamedArguments.Any(x => x.Key == name && x.Value.Value is true);
        range = new RangeEnds(min, Set("MinimumIsExclusive"), max, Set("MaximumIsExclusive"));
        return true;
    }

    static object? End(object? v) => v switch
    {
        int i => (long)i,
        long l => l,
        double d when !double.IsNaN(d) => d,
        string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) => d,
        _ => null,
    };

    /// <summary>The whole numbers from <paramref name="floor"/> to <paramref name="ceiling"/> a range lets in, its ends
    /// rounded inward and an exclusive end moved in by one; null when there are none.</summary>
    static (long Lo, long Hi)? Whole(RangeEnds? range, long floor, long ceiling)
    {
        if (range is not { } r) return (floor, ceiling);
        decimal lo = Math.Max(floor, WholeEnd(r.Min, r.MinExclusive, low: true));
        decimal hi = Math.Min(ceiling, WholeEnd(r.Max, r.MaxExclusive, low: false));
        return lo > hi ? null : ((long)lo, (long)hi);
    }

    static decimal WholeEnd(object end, bool exclusive, bool low)
    {
        // past ±1e28 a double is beyond every long, and still a decimal
        decimal d = end is long l ? l : (double)end switch { >= 1e28 => 1e28m, <= -1e28 => -1e28m, var x => (decimal)x };
        return low ? (exclusive ? Math.Floor(d) + 1 : Math.Ceiling(d)) : (exclusive ? Math.Ceiling(d) - 1 : Math.Floor(d));
    }

    static double ToDouble(object end) => end is long l ? l : (double)end;

    static string DoubleLiteral(object end) => end switch
    {
        long l => Literal(l),
        double.PositiveInfinity => "double.PositiveInfinity",
        double.NegativeInfinity => "double.NegativeInfinity",
        _ => ((double)end).ToString("R", CultureInfo.InvariantCulture) + "d",
    };

    /// <summary>A DefaultValue argument as a whole number (a double rounded inward), or null.</summary>
    static long? Number(TypedConstant c, bool ceiling) => c.Value switch
    {
        int i => i,
        long l => l,
        double d when !double.IsNaN(d) => (ceiling ? Math.Ceiling(d) : Math.Floor(d)) switch
        {
            >= long.MaxValue => long.MaxValue,
            <= long.MinValue => long.MinValue,
            var r => (long)r,
        },
        _ => null,
    };

    static string Literal(long v) => v switch
    {
        long.MaxValue => "long.MaxValue",
        int.MaxValue => "int.MaxValue",
        _ => v.ToString(CultureInfo.InvariantCulture) + "L",
    };

    bool Body(INamedTypeSymbol s, Location at, bool top, List<BodyModel> bodies)
    {
        string key = s.ToDisplayString(Fq);
        if (_bodies.TryGetValue(key, out BodyModel? done))
        {
            if (done is null) return false;
            if (!bodies.Contains(done)) bodies.Add(done);
            return true;
        }
        _bodies[key] = null;
        TypeHome? home = Home(s, at);
        if (home is null) return false;
        bool ok = true;
        var fields = new List<BodyField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (m, t) in Settable(s, at, "body"))
        {
            if (t is null)
            {
                ok = false;
                continue;
            }
            string jsonName = JsonName(m);
            if (jsonName.Length == 0 || jsonName.IndexOf('"') >= 0)
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is read as '{jsonName}': a body field's name is non-empty and has no quote");
                ok = false;
                continue;
            }
            if (!names.Add(jsonName))
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is read as '{jsonName}', as another member is: only one of them could ever be set");
                ok = false;
                continue;
            }
            ITypeSymbol inner = known.Underlying(t) ?? t;
            bool nullable = known.Underlying(t) is not null;
            string? error = null;
            if (Attribute(m, known.BodyError) is { ConstructorArguments.Length: 1 } be && be.ConstructorArguments[0].Value is string member)
            {
                if (!HasErrorReply(s, member))
                {
                    Report(Diagnostics.BadMember, at, $"[BodyError(\"{member}\")] on '{s.Name}.{m.Name}' names no static ErrorReply field or property of '{s.Name}'");
                    ok = false;
                    continue;
                }
                error = Identifier(member);
            }
            BodyFieldKind? kind = KnownTypes.Is(inner, known.Utf8) ? BodyFieldKind.Text
                : KnownTypes.Is(inner, known.Utf8List) ? BodyFieldKind.TextList
                : inner.SpecialType == SpecialType.System_Int64 ? BodyFieldKind.Int64
                : inner.SpecialType == SpecialType.System_Int32 ? BodyFieldKind.Int32
                : inner.SpecialType == SpecialType.System_Double ? BodyFieldKind.Double
                : inner.SpecialType == SpecialType.System_Boolean ? BodyFieldKind.Bool
                : inner is INamedTypeSymbol { TypeKind: TypeKind.Struct } n && n.SpecialType == SpecialType.None && !IsFramework(n) ? BodyFieldKind.Nested
                : null;
            if (kind is null)
            {
                Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}' is a {t.ToDisplayString(Short)}: body fields are Utf8, Utf8List, long, int, double, bool, a nested body struct, or their nullables");
                ok = false;
                continue;
            }
            string? min = null, max = null;
            bool minExclusive = false, maxExclusive = false;
            if (Attribute(m, known.Range) is not null)
            {
                if (kind is not (BodyFieldKind.Int64 or BodyFieldKind.Int32 or BodyFieldKind.Double))
                {
                    Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}''s [Range] is for numbers, and it is a {t.ToDisplayString(Short)}");
                    ok = false;
                    continue;
                }
                if (!RangeOf(m, s.Name, at, out RangeEnds? range))
                {
                    ok = false;
                    continue;
                }
                RangeEnds r = range!.Value;
                if (kind == BodyFieldKind.Double)
                {
                    double lo = ToDouble(r.Min), hi = ToDouble(r.Max);
                    if (lo > hi || lo == hi && (r.MinExclusive || r.MaxExclusive))
                    {
                        Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}''s [Range] leaves no double");
                        ok = false;
                        continue;
                    }
                    (min, minExclusive, max, maxExclusive) = (DoubleLiteral(r.Min), r.MinExclusive, DoubleLiteral(r.Max), r.MaxExclusive);
                }
                else
                {
                    // the reader already keeps an int field inside int: only a tighter end is checked
                    long floor = kind == BodyFieldKind.Int32 ? int.MinValue : long.MinValue, ceiling = kind == BodyFieldKind.Int32 ? int.MaxValue : long.MaxValue;
                    if (Whole(r, floor, ceiling) is not { } bounds)
                    {
                        Report(Diagnostics.BadMember, at, $"'{s.Name}.{m.Name}''s [Range] leaves no {(kind == BodyFieldKind.Int32 ? "int" : "long")}");
                        ok = false;
                        continue;
                    }
                    min = bounds.Lo == floor ? null : Literal(bounds.Lo);
                    max = bounds.Hi == ceiling ? null : Literal(bounds.Hi);
                }
            }
            string? nested = null;
            if (kind == BodyFieldKind.Nested)
            {
                if (!Body((INamedTypeSymbol)inner, at, top: false, bodies))
                {
                    ok = false;
                    continue;
                }
                nested = inner.ToDisplayString(Fq);
            }
            bool nonEmpty = kind == BodyFieldKind.Text && nullable
                && Attribute(m, known.MinLength) is { ConstructorArguments.Length: 1 } ml && ml.ConstructorArguments[0].Value is int len && len >= 1;
            fields.Add(new BodyField(m.Name, jsonName, kind.Value, t.ToDisplayString(Fq), nullable, nonEmpty, error, nested, min, minExclusive, max, maxExclusive));
        }
        if (!ok) return false;
        // __ReadNested never reads the envelope, so the struct keeps it whichever way it was first met
        string? envelope = Attribute(s, known.JsonEnvelope) is { ConstructorArguments.Length: 1 } env ? env.ConstructorArguments[0].Value as string : null;
        var model = new BodyModel(home, envelope,
            HasErrorReply(s, "Invalid") ? "Invalid" : null,
            HasErrorReply(s, "InvalidBody") ? "InvalidBody" : null,
            fields.ToEquatable());
        _bodies[key] = model;
        bodies.Add(model);
        return true;
    }

    string JsonName(ISymbol m) =>
        Attribute(m, known.JsonPropertyName) is { ConstructorArguments.Length: 1 } a && a.ConstructorArguments[0].Value is string s ? s : CamelCase(m.Name);

    /// <summary>System.Text.Json's JsonNamingPolicy.CamelCase.</summary>
    public static string CamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0])) return name;
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i])) break;
            bool hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                if (chars[i + 1] == ' ') chars[i] = char.ToLowerInvariant(chars[i]);
                break;
            }
            chars[i] = char.ToLowerInvariant(chars[i]);
        }
        return new string(chars);
    }

    // ---- Replies ----

    /// <summary>How the handler's return type is sent, or null with why it can't be.</summary>
    public ReplyModel? Reply(ITypeSymbol returned, Location at, out string? why)
    {
        why = null;
        bool async = false;
        ITypeSymbol t = returned;
        if (t is INamedTypeSymbol { IsGenericType: true } p && KnownTypes.Is(p, known.Pending))
        {
            async = true;
            t = p.TypeArguments[0];
        }
        string type = t.ToDisplayString(Fq);
        if (KnownTypes.Is(t, known.ErrorReply)) return new(ReplyKind.Error, async, type, null, null, null);
        if (t is INamedTypeSymbol { IsGenericType: true } r && KnownTypes.Is(r, known.ReplyOf))
        {
            ITypeSymbol v = r.TypeArguments[0];
            string vt = v.ToDisplayString(Fq);
            if (KnownTypes.Implements(v, known.IReply)) return new(ReplyKind.Nested, async, type, vt, null, null);
            ValueShape? shape = Value(v, out string? vwhy);
            if (shape is null)
            {
                why = $"The handler's reply {t.ToDisplayString(Short)} can't be written as JSON: {vwhy}";
                return null;
            }
            string? envelope = Attribute(v, known.JsonEnvelope) is { ConstructorArguments.Length: 1 } e ? e.ConstructorArguments[0].Value as string : null;
            return new(ReplyKind.Json, async, type, vt, shape, envelope);
        }
        if (t.TypeKind == TypeKind.Struct && KnownTypes.Implements(t, known.IReply)) return new(ReplyKind.Self, async, type, null, null, null);
        why = $"The handler returns {returned.ToDisplayString(Short)}, which can't be sent: " + (
            returned.SpecialType == SpecialType.System_Void ? "return a reply, such as Reply.NoContent()"
            : returned.Name is "Task" or "ValueTask" ? "an async handler is an async lambda passed to Map, or a method returning Pending<T>, so its state is pooled"
            : "return a struct implementing IReply, a Reply<T>, or an ErrorReply");
        return null;
    }

    /// <summary>How a value of <paramref name="t"/> is written as JSON, or null with why it can't be.</summary>
    ValueShape? Value(ITypeSymbol t, out string? why)
    {
        why = null;
        // a class written as T or T? is one shape: null is checked where the value is read
        if (t.IsReferenceType) t = t.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        string type = t.ToDisplayString(Fq);
        if (known.Underlying(t) is { } u)
        {
            ValueShape? inner = Value(u, out why);
            return inner is null ? null : inner with { Type = type, Nullable = true };
        }
        if (KnownTypes.Is(t, known.Utf8)) return new(ValueKind.Utf8, type, false, false, null, null, null);
        if (KnownTypes.Is(t, known.RawJson)) return new(ValueKind.RawJson, type, false, false, null, null, null);
        if (KnownTypes.Is(t, known.Timestamp)) return new(ValueKind.Timestamp, type, false, false, null, null, null);
        if (KnownTypes.Is(t, known.Utf8List)) return new(ValueKind.Utf8List, type, false, false, null, null, null);
        switch (t.SpecialType)
        {
            case SpecialType.System_String: return new(ValueKind.String, type, true, true, null, null, null);
            case SpecialType.System_Boolean: return new(ValueKind.Bool, type, false, false, null, null, null);
            case SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16:
                return new(ValueKind.Number, type, false, false, "int", null, null);
            case SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal:
                return new(ValueKind.Number, type, false, false, null, null, null);
        }
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol { EnumUnderlyingType: { } eu })
            return new(ValueKind.Enum, type, false, false, eu.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 ? "int" : eu.ToDisplayString(Fq), null, null);
        if (t is IArrayTypeSymbol { Rank: 1 } array)
        {
            ValueShape? e = Value(array.ElementType, out why);
            return e is null ? null : new(ValueKind.Sequence, type, true, true, null, e, null);
        }
        if (t is not INamedTypeSymbol n || n.TypeKind is not (TypeKind.Struct or TypeKind.Class) || n.IsAbstract && n.TypeKind == TypeKind.Class && n.IsStatic)
        {
            why = $"{t.ToDisplayString(Short)} isn't a struct, a class, text, a number, a bool or a sequence";
            return null;
        }
        if (Enumerator(n) is { } element)
        {
            if (element.Type is null)
            {
                why = $"{n.ToDisplayString(Short)}'s enumerator is a class, which allocates: use an array, a List<T> or ImmutableArray<T>";
                return null;
            }
            ValueShape? e = Value(element.Type, out why);
            return e is null ? null : new(ValueKind.Sequence, type, n.IsReferenceType, n.IsReferenceType, null, e, null);
        }
        if (!known.Visible(n))
        {
            why = $"{n.ToDisplayString(Short)} is private, protected, file-local or internal to another assembly, so the generated writer can't name it";
            return null;
        }
        if (IsFramework(n) && !KnownTypes.Is(n, known.Viewer))
        {
            why = $"{n.ToDisplayString(Short)} has no JSON form here" + (n.Name is "DateTime" or "DateTimeOffset" ? ": use Timestamp" : "");
            return null;
        }
        // a type nested in a generic class takes its type parameters too
        if (Parts(n).Any(p => p is ITypeParameterSymbol))
        {
            why = $"{n.ToDisplayString(Short)} is open generic";
            return null;
        }
        if (!_objects.ContainsKey(type))
        {
            _objects[type] = null; // a type that holds itself is written by a recursive call
            var members = new List<MemberShape>();
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            var chain = new List<INamedTypeSymbol>();
            for (INamedTypeSymbol? b = n; b is not null && b.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); b = b.BaseType)
                chain.Insert(0, b);
            foreach (ISymbol m in chain.SelectMany(b => b.GetMembers()))
            {
                if (m.IsStatic || m.IsImplicitlyDeclared || m.DeclaredAccessibility != Accessibility.Public || HasAttribute(m, known.JsonIgnore)) continue;
                ITypeSymbol? mt = m switch
                {
                    IFieldSymbol { IsConst: false } f => f.Type,
                    // as System.Text.Json writes: a getter kept from public callers isn't
                    IPropertySymbol { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public } p when p.Name != "EqualityContract" => p.Type,
                    _ => null,
                };
                if (mt is null) continue;
                ValueShape? ms = Value(mt, out string? mwhy);
                if (ms is null)
                {
                    why = $"{n.Name}.{m.Name}: {mwhy}";
                    _objects.Remove(type);
                    return null;
                }
                var shape = new MemberShape(m.Name, JsonName(m), ms);
                if (at.TryGetValue(m.Name, out int i)) members[i] = shape; // an override or a hiding member
                else
                {
                    at[m.Name] = members.Count;
                    members.Add(shape);
                }
            }
            if (members.GroupBy(x => x.JsonName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                why = $"{n.Name} has two members written as '{twice.Key}'";
                _objects.Remove(type);
                return null;
            }
            _objects[type] = new ObjectShape(type, n.IsReferenceType, members.ToEquatable());
        }
        return new(ValueKind.Object, type, n.IsReferenceType, n.IsReferenceType, null, null, type);
    }

    /// <summary>The element of a type foreach can walk by pattern: (null, ...) when its enumerator is a class.</summary>
    static (ITypeSymbol? Type, bool Found)? Enumerator(INamedTypeSymbol t)
    {
        IMethodSymbol? get = t.GetMembers("GetEnumerator").OfType<IMethodSymbol>()
            .FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0 && m.DeclaredAccessibility == Accessibility.Public);
        if (get is null) return null;
        ITypeSymbol e = get.ReturnType;
        IPropertySymbol? current = e.GetMembers("Current").OfType<IPropertySymbol>().FirstOrDefault()
            ?? e.AllInterfaces.SelectMany(i => i.GetMembers("Current")).OfType<IPropertySymbol>().FirstOrDefault(p => p.Type.SpecialType != SpecialType.System_Object);
        if (current is null) return null;
        return e.TypeKind == TypeKind.Struct ? (current.Type, true) : (null, true);
    }

    // ---- Accessibility ----

    /// <summary>Whether generated code in the same assembly can name <paramref name="t"/>; reports when it can't.</summary>
    bool Accessible(ITypeSymbol t, Location at)
    {
        foreach (ITypeSymbol x in Parts(t))
        {
            if (x is ITypeParameterSymbol)
            {
                Report(Diagnostics.BadParameter, at, $"The handler uses the type parameter '{x.Name}': a handler's types are known at build time");
                return false;
            }
            if (x is INamedTypeSymbol n && !known.Visible(n))
            {
                Report(Diagnostics.NotAccessible, at, n.ToDisplayString(Short));
                return false;
            }
        }
        return true;
    }

    /// <summary>The type and the types it is made of: array elements, type arguments, and those of the types it is
    /// nested in (a type in a generic class is written with the class's arguments).</summary>
    static IEnumerable<ITypeSymbol> Parts(ITypeSymbol t)
    {
        yield return t;
        if (t is IArrayTypeSymbol a)
            foreach (var x in Parts(a.ElementType)) yield return x;
        for (INamedTypeSymbol? n = t as INamedTypeSymbol; n is not null; n = n.ContainingType)
            foreach (ITypeSymbol arg in n.TypeArguments)
                foreach (var x in Parts(arg)) yield return x;
    }
}
