// Coverage-guided fuzzing of the parsers (tests/CodeFusion.Fuzz/README.md). Everything goes to artifacts/fuzz/.
//   dotnet run scripts/Fuzz.cs -- build                          publish (Release) and instrument the libraries with sharpfuzz
//   dotnet run scripts/Fuzz.cs -- local <target> [seconds]       the in-process coverage loop: any OS, no libFuzzer
//   dotnet run scripts/Fuzz.cs -- libfuzzer <target> [seconds]   libFuzzer through libfuzzer-dotnet (Linux; $LIBFUZZER is its path)
//   dotnet run scripts/Fuzz.cs -- report <target> <dir>...       every input in the folders, findings minimized, observed classes
// The corpus grows in artifacts/fuzz/corpus/<target>, seeded from Corpus/<target>; findings land in
// artifacts/fuzz/findings/<target>. A finding worth keeping becomes a regression test and a file in Corpus/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, ".."));
string here = Path.Combine(root, "tests", "CodeFusion.Fuzz");
string output = Path.Combine(root, "artifacts", "fuzz");
string bin = Path.Combine(output, "bin");
string tools = Path.Combine(output, "tools");
// Keep in step with the SharpFuzz PackageVersion in CodeFusion.Fuzz.csproj.
const string SharpFuzzVersion = "2.3.0";
string harness = Path.Combine(bin, OperatingSystem.IsWindows() ? "CodeFusion.Fuzz.exe" : "CodeFusion.Fuzz");

switch (args)
{
    case ["build"]:
        // publish keeps an instrumented library that is newer than its build
        if (Directory.Exists(bin)) Directory.Delete(bin, recursive: true);
        Check(Run("dotnet", ["publish", here, "-c", "Release", "-o", bin, "--nologo", "-v", "q"]));
        string sharpfuzz = Path.Combine(tools, OperatingSystem.IsWindows() ? "sharpfuzz.exe" : "sharpfuzz");
        if (!File.Exists(sharpfuzz))
            Check(Run("dotnet", ["tool", "install", "SharpFuzz.CommandLine", "--version", SharpFuzzVersion, "--tool-path", tools]));
        // The code under test; not the harness, the test runner or the framework.
        foreach (string lib in (string[])["CodeFusion.Core", "CodeFusion.Http", "CodeFusion.Data", "CodeFusion.Data.Postgres", "CodeFusion.Data.Sqlite"])
            Check(Run(sharpfuzz, [Path.Combine(bin, lib + ".dll")]));
        return 0;

    case ["local", var target, .. var rest] when rest.Length <= 1:
        Seed(target);
        return Run(harness, ["--local", rest.Length > 0 ? rest[0] : "60", Corpus(target), Findings(target)], target);

    case ["libfuzzer", var target, .. var rest] when rest.Length <= 1:
    {
        string libFuzzer = Environment.GetEnvironmentVariable("LIBFUZZER") is { Length: > 0 } l ? l
            : throw new InvalidOperationException("set LIBFUZZER to the libfuzzer-dotnet binary");
        Seed(target);
        string maxLength = Capture(harness, ["--max-len"], target).Trim();
        return Run(libFuzzer,
        [
            $"--target_path={harness}", $"-dict={Path.Combine(here, "Corpus", target + ".dict")}", $"-max_len={maxLength}",
            "-timeout=10", $"-max_total_time={(rest.Length > 0 ? rest[0] : "600")}", "-print_final_stats=1",
            $"-artifact_prefix={Findings(target)}{Path.DirectorySeparatorChar}", Corpus(target),
        ], target);
    }

    case ["report", var target, .. var dirs] when dirs.Length > 0:
        return Run(harness, ["--report", .. dirs], target);

    default:
        foreach (string line in File.ReadLines(Source()))
        {
            if (!line.StartsWith("//", StringComparison.Ordinal)) break;
            Console.Error.WriteLine(line.Length > 3 ? line[3..] : "");
        }
        return 2;
}

string Corpus(string target) => Path.Combine(output, "corpus", target);

string Findings(string target) => Path.Combine(output, "findings", target);

// The checked-in seeds join the corpus that earlier runs grew; nothing already there is overwritten.
void Seed(string target)
{
    Directory.CreateDirectory(Corpus(target));
    Directory.CreateDirectory(Findings(target));
    string seeds = Path.Combine(here, "Corpus", target);
    if (!Directory.Exists(seeds)) return;
    foreach (string file in Directory.EnumerateFiles(seeds))
    {
        string to = Path.Combine(Corpus(target), Path.GetFileName(file));
        if (!File.Exists(to)) File.Copy(file, to);
    }
}

// Runs a program with this one's console; CODEFUSION_FUZZ names the target for the harness.
static int Run(string program, IEnumerable<string> arguments, string? target = null)
{
    using var p = Start(program, arguments, target, capture: false);
    p.WaitForExit();
    return p.ExitCode;
}

static string Capture(string program, IEnumerable<string> arguments, string target)
{
    using var p = Start(program, arguments, target, capture: true);
    string text = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{program} exited with {p.ExitCode}");
    return text;
}

static Process Start(string program, IEnumerable<string> arguments, string? target, bool capture)
{
    var info = new ProcessStartInfo(program) { RedirectStandardOutput = capture };
    foreach (string a in arguments) info.ArgumentList.Add(a);
    if (target is not null) info.Environment["CODEFUSION_FUZZ"] = target;
    return Process.Start(info) ?? throw new InvalidOperationException($"couldn't start {program}");
}

static void Check(int exitCode)
{
    if (exitCode != 0) Environment.Exit(exitCode);
}

static string Source([CallerFilePath] string path = "") => path;
