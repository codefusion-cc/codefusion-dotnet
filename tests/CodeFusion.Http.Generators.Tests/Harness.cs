using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CodeFusion.Http.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeFusion.Http.Generators.Tests;

/// <summary>Compiles sources against CodeFusion.Http with the generator, the analyzers or a code fix.</summary>
internal static class Harness
{
    static readonly CSharpParseOptions Parse = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "CodeFusion.Http.Generated")]);

    static readonly CSharpCompilationOptions Options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithNullableContextOptions(NullableContextOptions.Enable);

    /// <summary>The runtime's assemblies, CodeFusion.Core and CodeFusion.Http.</summary>
    public static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(CodeFusion.Utf8).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(CodeFusion.Http.HttpApp).Assembly.Location),
    ];

    public const string Usings = """
        using System;
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using CodeFusion;
        using CodeFusion.Http;

        """;

    /// <summary>The source, after the usings and after <paramref name="head"/> (such as a file's header comment), in
    /// <paramref name="path"/>.</summary>
    public static CSharpCompilation Compilation(string source, string path = "Program.cs", string head = "", params MetadataReference[] more) =>
        CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(head + Usings + source, Parse, path)], [.. References, .. more], Options);

    /// <summary>Another assembly, <paramref name="name"/>, built from <paramref name="source"/>, to reference.</summary>
    public static MetadataReference Library(string source, string name = "Lib")
    {
        CSharpCompilation lib = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(Usings + source, Parse)], References, Options);
        using var image = new MemoryStream();
        var emitted = lib.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    /// <summary>Runs the generator: what it wrote, what it reported, and the compilation with its output.</summary>
    public static (ImmutableArray<GeneratedSourceResult> Sources, ImmutableArray<Diagnostic> Diagnostics, Compilation Output) Generate(string source, params MetadataReference[] more) =>
        GenerateOn(Compilation(source, "Program.cs", "", more));

    /// <summary>The compilation's errors and warnings, as text: none means the generated code compiles cleanly.</summary>
    public static string[] Problems(Compilation c) =>
        c.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()).ToArray();

    /// <summary>What the analyzers report on the source (with the generator's output, as in a build).</summary>
    public static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string path = "Program.cs", string head = "", params MetadataReference[] more)
    {
        var (_, _, output) = GenerateOn(Compilation(source, path, head, more));
        return await output.WithAnalyzers([new HandlerAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>"ID line:column" for each diagnostic in Program.cs, lines counted from the source as given.</summary>
    public static string[] Brief(IEnumerable<Diagnostic> diagnostics)
    {
        int offset = Usings.Split('\n').Length - 1;
        return diagnostics
            .Select(d => (d, Span: d.Location.GetLineSpan()))
            .Where(x => x.Span.Path == "Program.cs")
            .OrderBy(x => x.d.Location.SourceSpan.Start)
            .Select(x => $"{x.d.Id} {x.Span.StartLinePosition.Line + 1 - offset}:{x.Span.StartLinePosition.Character + 1}")
            .ToArray();
    }

    /// <summary>Applies the first fix <paramref name="fixer"/> offers for each of the analyzers' diagnostics with
    /// <paramref name="id"/>, one at a time; the source after.</summary>
    public static async Task<string> Fix(string source, CodeFixProvider fixer, string id)
    {
        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("App", LanguageNames.CSharp)
            .WithParseOptions(Parse).WithCompilationOptions(Options).WithMetadataReferences(References);
        Document document = project.AddDocument("Program.cs", SourceText.From(Usings + source));
        for (int round = 0; round < 10; round++)
        {
            Compilation c = (await document.Project.GetCompilationAsync())!;
            var (_, _, output) = GenerateOn(c);
            ImmutableArray<Diagnostic> ds = await output.WithAnalyzers([new HandlerAnalyzer()]).GetAnalyzerDiagnosticsAsync();
            Diagnostic? d = ds.Where(x => x.Id == id && x.Location.SourceTree?.FilePath == "Program.cs").OrderBy(x => x.Location.SourceSpan.Start).FirstOrDefault();
            if (d is null) break;
            // the diagnostic is on the analysed tree; the fix works on the document's
            Diagnostic onDocument = Diagnostic.Create(d.Descriptor, Location.Create((await document.GetSyntaxTreeAsync())!, d.Location.SourceSpan), d.Properties, d.GetMessage());
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, onDocument, (a, _) => actions.Add(a), default);
            await fixer.RegisterCodeFixesAsync(context);
            if (actions.Count == 0) break;
            ImmutableArray<CodeActionOperation> ops = await actions[0].GetOperationsAsync(default);
            Solution changed = ops.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            document = changed.GetDocument(document.Id)!;
        }
        string text = (await document.GetTextAsync()).ToString();
        return text[Usings.Length..];
    }

    static (ImmutableArray<GeneratedSourceResult>, ImmutableArray<Diagnostic>, Compilation) GenerateOn(Compilation input)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new EndpointGenerator().AsSourceGenerator()], parseOptions: Parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation output, out _);
        GeneratorDriverRunResult run = driver.GetRunResult();
        return (run.Results.Single().GeneratedSources, run.Diagnostics, output);
    }

    /// <summary>
    /// Compares each generated file with its snapshot in Snapshots/<paramref name="name"/>/. A difference fails and
    /// writes the new text next to it as .received; UPDATE_SNAPSHOTS=1 writes the snapshots instead.
    /// </summary>
    public static void Verify(string name, ImmutableArray<GeneratedSourceResult> sources, [CallerFilePath] string here = "")
    {
        string dir = Path.Combine(Path.GetDirectoryName(here)!, "Snapshots", name);
        bool update = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";
        Directory.CreateDirectory(dir);
        var expected = Directory.GetFiles(dir, "*.cs").Select(f => Path.GetFileName(f)).ToHashSet();
        var failures = new List<string>();
        foreach (GeneratedSourceResult s in sources)
        {
            string file = Path.Combine(dir, s.HintName);
            string actual = s.SourceText.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            expected.Remove(s.HintName);
            if (update)
            {
                File.WriteAllText(file, actual);
                continue;
            }
            string? snapshot = File.Exists(file) ? File.ReadAllText(file) : null;
            if (snapshot != actual)
            {
                File.WriteAllText(file + ".received", actual);
                failures.Add(snapshot is null ? $"{s.HintName} is new" : $"{s.HintName} differs");
            }
        }
        if (update)
            foreach (string stale in expected) File.Delete(Path.Combine(dir, stale));
        else
            failures.AddRange(expected.Select(e => $"{e} wasn't generated"));
        Assert.True(failures.Count == 0, $"Snapshots/{name}: {string.Join("; ", failures)} (the .received files have what was generated; UPDATE_SNAPSHOTS=1 takes them)");
    }
}
