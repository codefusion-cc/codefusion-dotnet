// Writes src/CodeFusion.Http/EndpointRouteBuilderExtensions.cs: the Map overloads the endpoint generator intercepts.
// Run it after changing it: dotnet run scripts/MapOverloads.cs (an argument writes somewhere else instead, to compare).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

string[] methods = ["Get", "Post", "Put", "Delete"];
const int Max = 8;

static string[] TypeParameters(int n) => [.. Enumerable.Range(1, n).Select(i => $"T{i}")];

static string TypeParameterDocs(int n) =>
    string.Concat(Enumerable.Range(1, n).Select(i => $"    /// <typeparam name=\"T{i}\">The handler's parameter {i}.</typeparam>\n"));

const string Header = """
    // Written by scripts/MapOverloads.cs; change that and run it again rather than editing this file.
    using System;

    namespace CodeFusion.Http;

    /// <summary>
    /// <c>MapGet</c>, <c>MapPost</c>, <c>MapPut</c> and <c>MapDelete</c>. Each call is compiled by the endpoint generator,
    /// which replaces it (an interceptor) with code that binds the handler's parameters and sends its reply, so these
    /// bodies only run when the generator didn't: they throw. A handler takes route values, a <c>[FromQuery]</c>
    /// struct, a body struct, <see cref="Viewer"/>, services, <see cref="IResponse"/> and
    /// <see cref="System.Threading.CancellationToken"/>: up to 8 through the typed overloads, and through the
    /// <see cref="System.Delegate"/> overload up to 16 for a lambda or any number for a method the generator can call by
    /// name (CFH0107 reports one it can't). It returns a reply: a struct implementing <see cref="IReply"/>
    /// (such as <see cref="Reply"/>), a <see cref="Reply{T}"/>, an <see cref="ErrorReply"/>, or, from an <c>async</c>
    /// handler, any of them through <see cref="Pending{T}"/>.
    /// </summary>
    public static class EndpointRouteBuilderExtensions
    {
    """;

const string Footer = """
        static InvalidOperationException NotCompiled(string pattern) =>
            new($"the endpoint '{pattern}' was not compiled by the CodeFusion.Http endpoint generator: the package's analyzers are off, or the call was made through reflection or a delegate");
    }

    """;

var members = new List<string>();
foreach (string m in methods)
{
    string verb = m.ToUpperInvariant();
    for (int n = 0; n <= Max; n++)
    {
        string[] tp = TypeParameters(n);
        foreach (string shape in (string[])["struct", "error", "pending"])
        {
            (string gen, string func, string where, string what, string resultDoc) = shape switch
            {
                "struct" => ($"<{string.Join(", ", [.. tp, "TResult"])}>", $"Func<{string.Join(", ", [.. tp, "TResult"])}>",
                    " where TResult : struct", "a handler that returns its reply",
                    "    /// <typeparam name=\"TResult\">The reply: a struct implementing <see cref=\"IReply\"/>, or a <see cref=\"Reply{T}\"/>.</typeparam>\n"),
                "error" => (tp.Length > 0 ? $"<{string.Join(", ", tp)}>" : "", $"Func<{string.Join(", ", [.. tp, "ErrorReply"])}>",
                    "", "a handler that returns an error reply", ""),
                _ => ($"<{string.Join(", ", [.. tp, "TResult"])}>", $"Func<{string.Join(", ", [.. tp, "Pending<TResult>"])}>",
                    "", "an <c>async</c> handler", "    /// <typeparam name=\"TResult\">The reply the handler returns.</typeparam>\n"),
            };
            members.Add(
                $"    /// <summary>Maps {verb} <paramref name=\"pattern\"/> to {what}.</summary>\n" +
                TypeParameterDocs(n) + resultDoc +
                "    /// <param name=\"routes\">The app or group.</param>\n" +
                "    /// <param name=\"pattern\">The route template, a literal such as <c>\"/articles/{slug}\"</c>.</param>\n" +
                "    /// <param name=\"handler\">A static lambda or a static method.</param>\n" +
                $"    public static EndpointBuilder Map{m}{gen}(this IEndpointRouteBuilder routes, string pattern, {func} handler){where} =>\n" +
                "        throw NotCompiled(pattern);");
        }
    }
    members.Add(
        $"    /// <summary>Maps {verb} <paramref name=\"pattern\"/> to a static method, such as <c>Map{m}(\"/x\", Handlers.X)</c>,\n" +
        "    /// whose parameter types the generic overloads can't infer.</summary>\n" +
        "    /// <param name=\"routes\">The app or group.</param>\n" +
        "    /// <param name=\"pattern\">The route template, a literal such as <c>\"/articles/{slug}\"</c>.</param>\n" +
        "    /// <param name=\"handler\">A static method.</param>\n" +
        $"    public static EndpointBuilder Map{m}(this IEndpointRouteBuilder routes, string pattern, Delegate handler) =>\n" +
        "        throw NotCompiled(pattern);");
}

string text = Header + "\n" + string.Join("\n\n", members) + "\n\n" + Footer;
string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, ".."));
string path = args.Length > 0 ? args[0] : Path.Combine(root, "src", "CodeFusion.Http", "EndpointRouteBuilderExtensions.cs");
File.WriteAllText(path, text);
Console.WriteLine($"wrote {path}: {members.Count} overloads");

static string Source([CallerFilePath] string path = "") => path;
