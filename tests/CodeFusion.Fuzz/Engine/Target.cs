using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpFuzz;

namespace CodeFusion.Fuzz;

/// <summary>
/// One fuzz target: bytes in, an exception out when an invariant breaks. The same <see cref="Run"/> runs under
/// libFuzzer and in the replay test.
/// </summary>
/// <param name="Name">The CODEFUSION_FUZZ value, and the folder under Corpus/ with its seeds and its .dict.</param>
/// <param name="MaxLength">Longer inputs are cut (libFuzzer's -max_len, the mutator's bound).</param>
/// <param name="Mutations">How many seeded mutations the replay test runs on every build.</param>
sealed record Target(string Name, ReadOnlySpanAction Run, int MaxLength, int Mutations);

/// <summary>An invariant broke. <see cref="Signature"/> names the kind, so that repeats of one bug group together.</summary>
sealed class FuzzFinding(string signature, string detail) : Exception(signature + ": " + detail)
{
    public string Signature { get; } = signature;
}

static class Targets
{
    public static readonly Target[] All =
    [
        new("http-parser", HttpParserTarget.Run, 4096, 6000),
        new("http-path", PathTarget.Run, 1024, 6000),
        new("http-kestrel", KestrelDifferential.Run, 4096, 2500),
        new("json-body", JsonBodyTarget.Run, 4096, 6000),
        new("json-endpoint", JsonEndpointTarget.Run, 4096, 800),
        new("pg-row", PgTargets.Row, 4096, 6000),
        new("pg-error", PgTargets.Error, 4096, 6000),
        new("pg-startup", PgTargets.Startup, 4096, 1500),
        new("pg-scram-first", PgTargets.ScramClientFinal, 1024, 600),
        new("pg-scram-final", PgTargets.ScramServerFinal, 1024, 6000),
        new("pg-settings", PgTargets.Settings, 1024, 6000),
    ];

    public static Target Get(string name) =>
        All.FirstOrDefault(t => t.Name == name)
        ?? throw new ArgumentException($"no fuzz target {name}; there are {string.Join(", ", All.Select(t => t.Name))}");

    /// <summary>Where the checked-in seeds are: copied next to the assembly, or the source folder when run from it.</summary>
    public static string CorpusRoot
    {
        get
        {
            string copied = Path.Combine(AppContext.BaseDirectory, "Corpus");
            return Directory.Exists(copied) ? copied : Path.Combine(Directory.GetCurrentDirectory(), "Corpus");
        }
    }

    public static string CorpusDirectory(Target t) => Path.Combine(CorpusRoot, t.Name);

    public static string DictionaryPath(Target t) => Path.Combine(CorpusRoot, t.Name + ".dict");

    /// <summary>The seeds, in name order so a replay is the same everywhere.</summary>
    public static List<byte[]> Seeds(Target t)
    {
        string dir = CorpusDirectory(t);
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.GetFiles(dir).Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
    }
}
