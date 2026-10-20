using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace CodeFusion.Http.CodeFixes;

/// <summary>CFH0001: marks a handler lambda or anonymous method that captures nothing <c>static</c>.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MakeHandlerStatic)), Shared]
public sealed class MakeHandlerStatic : CodeFixProvider
{
    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [Diagnostics.NotStatic.Id];

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        foreach (Diagnostic d in context.Diagnostics)
        {
            if (!d.Properties.TryGetValue(HandlerAnalyzer.CapturesProperty, out string? captures) || captures != "false") continue;
            // the innermost function where the diagnostic starts: the handler, not a lambda around the Map call
            if (root.FindToken(d.Location.SourceSpan.Start).Parent?.AncestorsAndSelf().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault() is not { } lambda) continue;
            context.RegisterCodeFix(
                CodeAction.Create("Make the handler static", ct => Fix(context.Document, root, lambda, ct), nameof(MakeHandlerStatic)),
                d);
        }
    }

    static Task<Document> Fix(Document document, SyntaxNode root, AnonymousFunctionExpressionSyntax lambda, CancellationToken ct)
    {
        _ = ct;
        SyntaxToken first = lambda.GetFirstToken();
        SyntaxToken keyword = SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithLeadingTrivia(first.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space);
        AnonymousFunctionExpressionSyntax stripped = lambda.ReplaceToken(first, first.WithLeadingTrivia());
        AnonymousFunctionExpressionSyntax fixedLambda = stripped.WithModifiers(stripped.Modifiers.Insert(0, keyword));
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(lambda, fixedLambda)));
    }
}

/// <summary>CFH0002: takes a handler's string parameter as Utf8.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(TakeUtf8)), Shared]
public sealed class TakeUtf8 : CodeFixProvider
{
    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = [Diagnostics.StringParameter.Id];

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        foreach (Diagnostic d in context.Diagnostics)
        {
            if (root.FindToken(d.Location.SourceSpan.Start).Parent?.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault() is not { Type: { } type }) continue;
            context.RegisterCodeFix(
                CodeAction.Create("Take a Utf8", _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(type,
                    SyntaxFactory.ParseTypeName("global::CodeFusion.Utf8").WithTriviaFrom(type).WithAdditionalAnnotations(Simplifier.Annotation)))), nameof(TakeUtf8)),
                d);
        }
    }
}
