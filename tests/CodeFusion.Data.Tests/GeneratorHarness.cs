using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeFusion.Data.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeFusion.Data.Tests;

/// <summary>Runs the query generator in memory on a source file, as the compiler runs it in a build.</summary>
static class GeneratorHarness
{
    static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(Utf8).Assembly.Location);
        paths.Add(typeof(DataContext).Assembly.Location);
        paths.Add(typeof(CodeFusion.Data.Postgres.Postgres).Assembly.Location);
        return paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "CodeFusion.Data.Generated")]);

    public const string Usings = """
        using System;
        using System.Linq;
        using System.ComponentModel.DataAnnotations.Schema;
        using CodeFusion;
        using CodeFusion.Data;
        using CodeFusion.Data.Postgres;

        """;

    /// <summary>What a build of <paramref name="source"/> gives: the generator's diagnostics, the compiler's errors after
    /// it ran, and the generated source.</summary>
    public sealed record Result(ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Errors, string Generated)
    {
        /// <summary>The SQL constant <paramref name="name"/> the generator wrote.</summary>
        public string Sql(string name)
        {
            var tree = CSharpSyntaxTree.ParseText(Generated);
            var field = tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == name) && f.Modifiers.Any(SyntaxKind.ConstKeyword))
                ?? throw new InvalidOperationException($"no SQL constant {name} in:\n{Generated}");
            return ((LiteralExpressionSyntax)field.Declaration.Variables.Single().Initializer!.Value).Token.ValueText;
        }

        /// <summary>The C# the binder of the query method <paramref name="method"/> binds, parameter by parameter.</summary>
        public List<string> Bound(string method)
        {
            var (interceptor, names) = Binds(method);
            // the binder binds the values the interceptor computed first: give back their C#
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(interceptor.Body!.ToString(), @"(__v\d+_\d+) = (?:__c\d+ \? )?(.*?)(?: : default!)?;\r?\n"))
                values[m.Groups[1].Value] = m.Groups[2].Value;
            return names.Select(n => values.TryGetValue(n, out var code) ? code : n).ToList();
        }

        /// <summary>The interceptor's locals the binder of <paramref name="method"/> binds, parameter by parameter.</summary>
        public List<string> Binder(string method) => Binds(method).Names;

        (MethodDeclarationSyntax Interceptor, List<string> Names) Binds(string method)
        {
            var tree = CSharpSyntaxTree.ParseText(Generated);
            var interceptor = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == method);
            string binder = Regex.Match(interceptor.ToString(), @"(Q\d+)Bind\(").Groups[1].Value + "Bind";
            var bind = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == binder);
            var bound = new List<string>();
            foreach (Match m in Regex.Matches(bind.Body!.ToString(), @"__command\.(Int64|Int32|Boolean|Double|Text|Timestamp)\(([^;]*?)\);|if \((\w+) is \{ \}"))
                bound.Add(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value);
            // a nullable value is bound under its 'if (x is { } y)': keep the name it was tested with
            var names = new List<string>();
            for (int i = 0; i < bound.Count; i++)
            {
                if (Regex.IsMatch(bound[i], @"^\w+$") && i + 1 < bound.Count && bound[i + 1].StartsWith("__p", StringComparison.Ordinal))
                {
                    names.Add(bound[i]);
                    i++;
                }
                else names.Add(Regex.Replace(bound[i], @"\.Span$", ""));
            }
            return (interceptor, names);
        }
    }

    public static Result Run(string source, bool checkedArithmetic = false, string? library = null)
    {
        var compilation = Compilation(source, checkedArithmetic, library);
        var driver = CSharpGeneratorDriver.Create([new QueryGenerator().AsSourceGenerator()], parseOptions: Parse);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = output.SyntaxTrees.Skip(1).Select(t => t.ToString()).FirstOrDefault() ?? "";
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return new Result(diagnostics, errors, generated);
    }

    /// <summary>A compilation of <paramref name="source"/>, referencing <paramref name="library"/>'s compiled as
    /// another assembly when given.</summary>
    static CSharpCompilation Compilation(string source, bool checkedArithmetic = false, string? library = null)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, checkOverflow: checkedArithmetic);
        var references = References.Value.ToList();
        if (library is not null)
        {
            var lib = CSharpCompilation.Create("GeneratorTestLibrary", [CSharpSyntaxTree.ParseText(Usings + library, Parse, path: "Library.cs")], References.Value, options);
            // as an IDE references a project of the solution: its internal members are seen, and inaccessible
            references.Add(lib.ToMetadataReference());
        }
        return CSharpCompilation.Create("GeneratorTest", [CSharpSyntaxTree.ParseText(Usings + source, Parse, path: "Test.cs")], references, options);
    }

    /// <summary>The generator's diagnostics for <paramref name="before"/>, then for <paramref name="after"/> as an edit
    /// of it: one driver, run twice, as an IDE runs it after each edit.</summary>
    public static (ImmutableArray<Diagnostic> Before, ImmutableArray<Diagnostic> After) RunTwice(string before, string after)
    {
        var compilation = Compilation(before);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new QueryGenerator().AsSourceGenerator()], parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var first);
        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(Usings + after, Parse, path: "Test.cs"));
        driver.RunGeneratorsAndUpdateCompilation(edited, out _, out var second);
        return (first, second);
    }

    /// <summary>A context of <see cref="Entities"/> with <paramref name="members"/> in its body.</summary>
    public static string Context(string members, string extra = "") => $$"""
        {{Entities}}
        public sealed partial class Db : DataContext<Postgres>
        {
            public Table<Person> People => Table<Person>();
            public Table<Pet> Pets => Table<Pet>();
        {{members}}
        }
        {{extra}}
        """;

    public const string Entities = """
        public sealed class Person
        {
            public long Id { get; init; }
            public Utf8 Name { get; init; }
            public Utf8? Nick { get; init; }
            public int Age { get; init; }
            public double? Score { get; init; }
            public bool Active { get; init; }
            public long? TeamId { get; init; }
            public Many<Pet> Pets { get; init; }
        }
        public sealed class Pet
        {
            public long Id { get; init; }
            public long OwnerId { get; init; }
            public Person Owner { get; init; } = null!;
            public Utf8 Name { get; init; }
        }
        public readonly record struct Name(long Id, Utf8 Text);
        """;
}
