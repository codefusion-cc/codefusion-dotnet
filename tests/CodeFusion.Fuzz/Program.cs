using System;
using System.Globalization;
using System.IO;
using System.Linq;
using SharpFuzz;
using SharpFuzz.Common;

namespace CodeFusion.Fuzz;

/// <summary>
/// Without CODEFUSION_FUZZ this is the test project (dotnet test). With CODEFUSION_FUZZ=&lt;target&gt;:
/// <list type="bullet">
/// <item>no arguments, under libfuzzer-dotnet: libFuzzer drives the target;</item>
/// <item>a file: runs that one input, as libFuzzer's crash files are replayed;</item>
/// <item>--local &lt;seconds&gt; &lt;corpus dir&gt; &lt;findings dir&gt;: the in-process coverage-guided loop (any OS);</item>
/// <item>--report &lt;dir&gt;...: every input in the folders, findings minimized, and the classes only observed;</item>
/// <item>--max-len: the target's longest input, for libFuzzer's -max_len.</item>
/// </list>
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        string? name = Environment.GetEnvironmentVariable("CODEFUSION_FUZZ");
        if (string.IsNullOrEmpty(name)) return Tests(args);
        Target t = Targets.Get(name);
        switch (args)
        {
            case ["--local", var seconds, var corpus, var found]:
                return LocalFuzzer.Run(t, TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)), corpus, found);
            case ["--report", .. var dirs]:
                return Report(t, dirs);
            case ["--max-len"]:
                Console.WriteLine(t.MaxLength);
                return 0;
            default:
                Fuzzer.LibFuzzer.Run(t.Run);
                return 0;
        }
    }

    static unsafe int Report(Target t, string[] dirs)
    {
        // Instrumented libraries count edges into SharpFuzz's map, so a report run gives them one to write to.
        byte* map = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(LocalFuzzer.MapSize);
        Trace.SharedMem = map;
        try
        {
            return ReportAll(t, dirs);
        }
        finally
        {
            Trace.SharedMem = null;
            System.Runtime.InteropServices.NativeMemory.Free(map);
        }
    }

    static int ReportAll(Target t, string[] dirs)
    {
        var findings = new Findings();
        int n = 0;
        foreach (string file in dirs.SelectMany(d => Directory.GetFiles(d)).Order(StringComparer.Ordinal))
        {
            findings.Run(t, File.ReadAllBytes(file));
            n++;
        }
        Console.WriteLine($"{t.Name}: {n} inputs, {findings.Count} kinds of finding");
        Console.Write(findings.Report(t));
        string observed = Observed.Report(t.Name);
        if (observed.Length > 0) Console.Write("observed, not failures:\n" + observed);
        return findings.Count == 0 ? 0 : 1;
    }

    /// <summary>What xunit.v3 generates as the entry point when it is allowed to.</summary>
    static int Tests(string[] args) =>
        args.Any(a => a is "--server" or "--internal-msbuild-node")
            ? Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult()
            : Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args).GetAwaiter().GetResult();
}
