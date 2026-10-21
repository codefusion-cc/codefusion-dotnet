using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace CodeFusion.Data.Generator;

/// <summary>
/// Compiles every query method of every data context (a <c>partial</c> class deriving from
/// <c>DataContext&lt;TProvider&gt;</c>) to SQL at build time, and takes over each call to one: the generated method
/// builds the command, binds its parameters and returns the result, so the LINQ in the method's body never runs.
/// Members marked <c>[QueryDefinition]</c> used anywhere else are build errors.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class QueryGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax,
                static (c, ct) => Call(c, ct))
            .Where(static c => c is not null)
            .Collect();
        var contexts = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (c, ct) => ContextOf(c, ct))
            .Where(static c => c is not null)
            .Collect();
        var all = context.CompilationProvider.Combine(calls).Combine(contexts);
        context.RegisterSourceOutput(all, static (spc, input) => Run(spc, input.Left.Left, input.Left.Right!, input.Right!));
    }

    /// <summary>
    /// A call site worth a look: a query method's call to take over, or a definition used where it would run. All
    /// values, so an edit that moves a call is a change the pipeline sees (a site equal to the last run's is the last
    /// run's, location and all), and no syntax tree is kept alive.
    /// </summary>
    sealed record CallSite(string Method, string? Attribute, string File, int Line, bool Misused, string Member, TextSpan Span, LinePositionSpan Lines);

    static CallSite? Call(GeneratorSyntaxContext c, CancellationToken ct)
    {
        var node = (InvocationExpressionSyntax)c.Node;
        var (dc, def) = Symbols(c.SemanticModel.Compilation);
        if (dc is null || def is null) return null;
        if (c.SemanticModel.GetSymbolInfo(node, ct).Symbol is not IMethodSymbol m) return null;
        bool definition = m.OriginalDefinition.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, def));
        if (definition)
        {
            if (AllowedHere(c.SemanticModel, node, m, dc, ct)) return null;
            var at = node.GetLocation().GetLineSpan();
            return new CallSite("", null, node.SyntaxTree.FilePath, at.StartLinePosition.Line + 1, true, m.Name, node.Span, at.Span);
        }
        if (m.IsStatic || m.DeclaringSyntaxReferences.Length == 0 || !Derives(m.ContainingType, dc) || !ReturnsResult(m)) return null;
        // calls inside a definition are compiled with it, not taken over
        if (InsideQueryMethod(c.SemanticModel, node, dc, ct)) return null;
#pragma warning disable RSEXPERIMENTAL002 // the interceptable location API is stable in Roslyn 4.12+
        var location = c.SemanticModel.GetInterceptableLocation(node, ct);
#pragma warning restore RSEXPERIMENTAL002
        if (location is null) return null;
        var span = node.GetLocation().GetLineSpan();
        return new CallSite(DocumentationCommentId.CreateDeclarationId(m.OriginalDefinition) ?? m.Name, location.GetInterceptsLocationAttributeSyntax(),
            span.Path, span.StartLinePosition.Line + 1, false, m.Name, node.Span, span.Span);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, Tuple<INamedTypeSymbol?, INamedTypeSymbol?>> SymbolCache = new();

    /// <summary>DataContext and QueryDefinitionAttribute, looked up once per compilation rather than per invocation.</summary>
    static (INamedTypeSymbol? Context, INamedTypeSymbol? Definition) Symbols(Compilation compilation)
    {
        var s = SymbolCache.GetValue(compilation, static c => Tuple.Create(
            c.GetTypeByMetadataName("CodeFusion.Data.DataContext"), c.GetTypeByMetadataName("CodeFusion.Data.QueryDefinitionAttribute")));
        return (s.Item1, s.Item2);
    }

    static bool Derives(ITypeSymbol? t, INamedTypeSymbol dc)
    {
        for (var b = t; b is not null; b = b.BaseType)
            if (SymbolEqualityComparer.Default.Equals(b, dc)) return true;
        return false;
    }

    static bool ReturnsResult(IMethodSymbol m) => m.ReturnType is INamedTypeSymbol r
        && r.ContainingNamespace?.ToDisplayString() == "CodeFusion.Data" && r.Name is "One" or "Many" or "Page" or "Write";

    /// <summary>The member a node is in, lambdas skipped.</summary>
    static ISymbol? Member(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        var s = model.GetEnclosingSymbol(node.SpanStart, ct);
        while (s is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }) s = s.ContainingSymbol;
        return s;
    }

    static bool InsideQueryMethod(SemanticModel model, SyntaxNode node, INamedTypeSymbol dc, CancellationToken ct) =>
        Member(model, node, ct) is IMethodSymbol m && !m.IsStatic && Derives(m.ContainingType, dc) && ReturnsResult(m);

    /// <summary>
    /// Definitions are compiled where they appear in a query method. A static method with an expression body is a
    /// projection the query methods inline, so the operators of an entity's navigations and the SQL functions may
    /// appear there too; a table's operators can't (a projection has no context to reach one, and a handler that
    /// does is running a query it shouldn't). Anywhere else a definition would run.
    /// </summary>
    static bool AllowedHere(SemanticModel model, SyntaxNode node, IMethodSymbol called, INamedTypeSymbol dc, CancellationToken ct)
    {
        var member = Member(model, node, ct);
        if (member is not IMethodSymbol m) return false;
        if (!m.IsStatic && Derives(m.ContainingType, dc) && ReturnsResult(m)) return true;
        bool projection = m.IsStatic && m.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) is MethodDeclarationSyntax { ExpressionBody: not null });
        return projection && called.ContainingType.OriginalDefinition.Name is "Many" or "SqlFunctions";
    }

    static string? ContextOf(GeneratorSyntaxContext c, CancellationToken ct)
    {
        // an abstract context too: the query methods it declares are compiled with it, and called on its subclasses
        if (c.SemanticModel.GetDeclaredSymbol(c.Node, ct) is not INamedTypeSymbol t) return null;
        var (dc, _) = Symbols(c.SemanticModel.Compilation);
        if (dc is null || !Derives(t, dc)) return null;
        return DocumentationCommentId.CreateDeclarationId(t);
    }

    static void Run(SourceProductionContext spc, Compilation compilation, ImmutableArray<CallSite?> calls, ImmutableArray<string?> contexts)
    {
        var k = Known.Of(compilation);
        if (k is null) return;
        foreach (var misuse in calls.Where(c => c!.Misused))
        {
            var tree = compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == misuse!.File);
            var at = tree is null ? Location.Create(misuse!.File, misuse.Span, misuse.Lines) : Location.Create(tree, misuse!.Span);
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.RunsAtRunTime, at, misuse.Member));
        }
        var byMethod = calls.Where(c => !c!.Misused).GroupBy(c => c!.Method).ToDictionary(g => g.Key, g => g.Select(c => c!).Distinct().ToList());
        foreach (var id in contexts.Distinct())
        {
            spc.CancellationToken.ThrowIfCancellationRequested();
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(id!, compilation) is not INamedTypeSymbol type) continue;
            Context(spc, k, type, byMethod);
        }
    }

    static void Context(SourceProductionContext spc, Known k, INamedTypeSymbol type, Dictionary<string, List<CallSite>> calls)
    {
        var at = type.Locations.FirstOrDefault();
        // a query method of a base, compiled with the base, implementing an interface this class adds
        foreach (var m in Inherited(k, type)) ReportInterface(spc, type, m, at);
        // a class that declares no queries (a base with the tables, generic or not partial, say) gets nothing
        // generated, so nothing is asked of it
        if (!type.GetMembers().OfType<IMethodSymbol>().Any(m => !m.IsStatic && ReturnsResult(m))) return;
        var provider = k.ProviderOf(type);
        if (provider is null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadContext, at, type.Name, "derive from DataContext<TProvider>, naming the provider (DataContext<Postgres>)"));
            return;
        }
        var contract = provider.AllInterfaces.FirstOrDefault(i => Known.Is(i, k.Provider));
        if (contract is null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadContext, at, type.Name, $"'{provider.Name}' isn't a provider (IDataProvider<TSelf, TStatement, TCommand>)"));
            return;
        }
        for (var t = type; t is not null; t = t.ContainingType)
            if (t.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && !d.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadContext, at, type.Name, $"'{t.Name}' must be partial: the generator adds its SQL constants"));
                return;
            }
        bool generic = false;
        for (var t = type; t is not null; t = t.ContainingType) generic |= t.IsGenericType;
        if (generic)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadContext, at, type.Name,
                "a generic context with queries (its interceptors would name its type parameters, which they can't): make it non-generic, or move the queries to a class deriving from it"));
            return;
        }
        // the tables of the context and of its bases: a derived context's property names the table first
        var tables = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        for (var b = type; b is not null; b = b.BaseType)
        {
            var level = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
            foreach (var p in b.GetMembers().OfType<IPropertySymbol>())
                if (Known.Is(p.Type, k.Table)) level[((INamedTypeSymbol)p.Type).TypeArguments[0]] = Names.Snake(p.Name);
            foreach (var kv in level)
                if (!tables.ContainsKey(kv.Key)) tables[kv.Key] = kv.Value;
        }
        var tr = new Translator(k, Dialect.Of(provider, k), tables) { ContextType = type };
        var compiled = new List<Compiled>();
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            if (m.MethodKind == MethodKind.ExplicitInterfaceImplementation && ReturnsResult(m)) ReportInterface(spc, type, m);
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>().Where(tr.IsQueryMethod).Where(m => Known.Same(m.ContainingType, type)))
        {
            spc.CancellationToken.ThrowIfCancellationRequested();
            var mat = m.Locations.FirstOrDefault();
            if (m.IsGenericMethod)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.Unsupported, mat, $"The generic query method '{m.Name}'"));
                continue;
            }
            // a call is taken over by the method it names, at build time: through a base reference, an override
            // would never run
            if (m.IsVirtual || m.IsOverride || m.IsAbstract)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadContext, mat, type.Name,
                    $"query method '{m.Name}' is virtual, abstract or an override, and a call to it is compiled for the method it names, so an override " +
                    "wouldn't run through a base reference: make it non-virtual (a different name for each context's query)"));
                continue;
            }
            if (Implements(type, m))
            {
                ReportInterface(spc, type, m);
                continue;
            }
            if (m.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax decl) continue;
            var expr = Translator.DefinitionOf(decl);
            if (expr is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotADefinition, mat, m.Name));
                continue;
            }
            var body = tr.Model(decl.SyntaxTree).GetOperation(expr, spc.CancellationToken);
            if (body is null) continue;
            Compiled? q;
            try
            {
                q = tr.Compile(m, body);
            }
#pragma warning disable CA1031 // one query's bug must not take the others down; it is reported as a build error
            catch (Exception e)
#pragma warning restore CA1031
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.Internal, mat, m.Name, e.ToString().Replace("\r", "").Replace("\n", " | ")));
                continue;
            }
            if (q is null) continue;
            if (calls.TryGetValue(DocumentationCommentId.CreateDeclarationId(m) ?? m.Name, out var sites))
                q.Calls = sites.Where(s => s.Attribute is not null).Select(s => new InterceptAt(s.Attribute!, s.File, s.Line)).ToList();
            compiled.Add(q);
        }
        foreach (var d in tr.Errors.Distinct()) spc.ReportDiagnostic(d);
        if (compiled.Count == 0) return;
        NameStatements(compiled);
        string source = new Emitter(type, provider, contract.TypeArguments[1]).Emit(compiled);
        string hint = (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + string.Join(".", TypeNames(type)) + ".Queries.g.cs";
        spc.AddSource(hint, source);
    }

    /// <summary>Whether <paramref name="m"/> is the context's implementation of an interface's method.</summary>
    static bool Implements(INamedTypeSymbol type, IMethodSymbol m) =>
        m.ExplicitInterfaceImplementations.Length > 0 || type.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Any(im => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(im), m));

    /// <summary>The query methods <paramref name="type"/> inherits that implement a method of an interface it adds
    /// (its base doesn't have it): the base's own check never sees that interface.</summary>
    static IEnumerable<IMethodSymbol> Inherited(Known k, INamedTypeSymbol type)
    {
        var bases = type.BaseType?.AllInterfaces ?? ImmutableArray<INamedTypeSymbol>.Empty;
        return type.AllInterfaces.Where(i => !bases.Contains(i, SymbolEqualityComparer.Default))
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Select(im => type.FindImplementationForInterfaceMember(im) as IMethodSymbol)
            .OfType<IMethodSymbol>()
            .Where(m => !Known.Same(m.ContainingType, type) && !m.IsStatic && ReturnsResult(m) && k.DerivesFromContext(m.ContainingType))
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default);
    }

    /// <summary>A call through the interface names the interface's method, which nothing takes over: the definition
    /// would run.</summary>
    static void ReportInterface(SourceProductionContext spc, INamedTypeSymbol type, IMethodSymbol m, Location? at = null) =>
        spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.BadContext, at ?? m.Locations.FirstOrDefault(), type.Name,
            $"query method '{m.Name}' implements an interface's method, and a call through the interface isn't taken over, so its definition " +
            "would run: give the interface no query methods, and call the context's"));

    /// <summary>
    /// The SQL constants' names, unique in the context: a method's own name for its first statement (overloads add
    /// their parameters' types: <c>ById_Int64</c>), and <c>_2</c>, <c>_3</c> after it for the statements that follow,
    /// another underscore where that is a method's name.
    /// </summary>
    static void NameStatements(List<Compiled> compiled)
    {
        var overloaded = new HashSet<string>(compiled.GroupBy(q => q.Method.Name).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var first = new List<string>();
        foreach (var q in compiled)
        {
            string name = overloaded.Contains(q.Method.Name) ? q.Method.Name + string.Concat(q.Method.Parameters.Select(p => "_" + TypeWord(p.Type))) : q.Method.Name;
            while (!used.Add(name)) name += "_";
            first.Add(name);
        }
        for (int i = 0; i < compiled.Count; i++)
        {
            var statements = compiled[i].Statements;
            statements[0] = statements[0] with { Name = first[i] };
            for (int j = 1; j < statements.Count; j++)
            {
                string sep = "_", name;
                while (!used.Add(name = first[i] + sep + Names.Number(j + 1))) sep += "_";
                statements[j] = statements[j] with { Name = name };
            }
        }
    }

    /// <summary>A type as a word of an identifier: <c>Int64</c>, <c>Utf8Nullable</c>, <c>Utf8Array</c>.</summary>
    static string TypeWord(ITypeSymbol t) => t switch
    {
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n => TypeWord(n.TypeArguments[0]) + "Nullable",
        IArrayTypeSymbol a => TypeWord(a.ElementType) + "Array",
        INamedTypeSymbol { TypeArguments.Length: > 0 } g => g.Name + string.Concat(g.TypeArguments.Select(TypeWord)),
        _ => t.Name.Length > 0 ? t.Name : "T",
    };

    static List<string> TypeNames(INamedTypeSymbol t)
    {
        var names = new List<string>();
        for (var c = t; c is not null; c = c.ContainingType) names.Insert(0, c.Name);
        return names;
    }
}
