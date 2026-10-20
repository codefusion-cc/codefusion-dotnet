using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeFusion.Http.Generators;

/// <summary>
/// Compiles each <c>MapGet</c>/<c>MapPost</c>/<c>MapPut</c>/<c>MapDelete</c> call into an endpoint: an interceptor
/// that registers it, a class that binds the handler's parameters and calls it, a sender for its reply, JSON writers
/// for the reply's types, and a binder in each partial body and <c>[FromQuery]</c> struct.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EndpointGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // routes.MapGet(...), routes?.MapGet(...) and EndpointRouteBuilderExtensions.MapGet(routes, ...)
        IncrementalValuesProvider<EndpointResult> results = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 2 or 3 } call
                    && call.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name,
                        MemberBindingExpressionSyntax b => b.Name,
                        _ => null,
                    } is { Identifier.ValueText: "MapGet" or "MapPost" or "MapPut" or "MapDelete" },
                static (ctx, ct) => Analysis.Endpoint(ctx.SemanticModel, (InvocationExpressionSyntax)ctx.Node, ct))
            .Where(static r => r is not null)!;

        context.RegisterSourceOutput(results.Collect(), static (spc, all) => Emit(spc, all));
    }

    static void Emit(SourceProductionContext spc, ImmutableArray<EndpointResult> results)
    {
        // once each: a struct's member is reported at the member by every Map call that binds the struct
        var reported = new HashSet<DiagnosticInfo>();
        foreach (EndpointResult r in results)
            foreach (DiagnosticInfo d in r.Diagnostics)
                if (!Diagnostics.IsAnalyzerRule(d.Id) && reported.Add(d)) spc.ReportDiagnostic(d.ToDiagnostic());

        // in source order, so the output is the same from build to build
        List<EndpointModel> endpoints = results.Where(r => r.Model is not null).Select(r => r.Model!)
            .OrderBy(e => e.File, System.StringComparer.Ordinal).ThenBy(e => e.Position).ToList();
        if (endpoints.Count == 0) return;
        spc.AddSource("CodeFusion.Http.Endpoints.g.cs", Emitter.Endpoints(endpoints));

        var bodies = new Dictionary<string, BodyModel>(System.StringComparer.Ordinal);
        var queries = new Dictionary<string, QueryModel>(System.StringComparer.Ordinal);
        foreach (EndpointModel e in endpoints)
        {
            foreach (BodyModel b in e.Bodies) bodies[b.Home.FullName] = b;
            foreach (QueryModel q in e.Queries) queries[q.Home.FullName] = q;
        }
        foreach (BodyModel b in bodies.Values.OrderBy(b => b.Home.FullName, System.StringComparer.Ordinal))
            spc.AddSource($"{b.Home.HintName}.Body.g.cs", Emitter.Body(b));
        foreach (QueryModel q in queries.Values.OrderBy(q => q.Home.FullName, System.StringComparer.Ordinal))
            spc.AddSource($"{q.Home.HintName}.Query.g.cs", Emitter.Query(q));
    }
}
