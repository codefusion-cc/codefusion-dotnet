using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Trace = SharpFuzz.Common.Trace;

namespace CodeFusion.Fuzz;

/// <summary>
/// Coverage-guided fuzzing without libFuzzer, for machines that can't run it (macOS): the libraries instrumented by
/// sharpfuzz count edges into <see cref="Trace.SharedMem"/>, which this loop points at its own map. An input that
/// reaches a new edge, or an edge a new number of times (AFL's buckets), joins the corpus. Uninstrumented, the map
/// stays empty and this is plain mutation fuzzing.
/// </summary>
static unsafe class LocalFuzzer
{
    internal const int MapSize = 1 << 16; // SharpFuzz's map

    public static int Run(Target t, TimeSpan duration, string corpusDir, string findingsDir)
    {
        Directory.CreateDirectory(corpusDir);
        Directory.CreateDirectory(findingsDir);
        byte* map = (byte*)NativeMemory.AllocZeroed(MapSize);
        Trace.SharedMem = map;
        var seen = new byte[MapSize];
        var findings = new Findings();
        var corpus = new List<byte[]>();
        var saved = new HashSet<string>();

        var start = new List<byte[]>(Targets.Seeds(t));
        foreach (string f in Directory.GetFiles(corpusDir)) start.Add(File.ReadAllBytes(f));
        foreach (byte[] input in start)
        {
            if (!saved.Add(Hash(input))) continue;
            findings.Run(t, input);
            if (Fresh(map, seen) || corpus.Count == 0) corpus.Add(input);
        }
        int edges = Edges(seen);
        Console.WriteLine($"{t.Name}: {saved.Count} inputs, {edges} edges");
        if (edges == 0) Console.WriteLine("  no coverage: the libraries are not instrumented (dotnet run scripts/Fuzz.cs -- build), so this is blind mutation");

        var mutator = new Mutator(Environment.TickCount, Mutator.LoadDictionary(Targets.DictionaryPath(t)));
        var clock = Stopwatch.StartNew();
        long execs = 0, nextStatus = 5000;
        while (clock.Elapsed < duration)
        {
            byte[] input = mutator.Next(corpus, t.MaxLength);
            int before = findings.Count;
            bool held = findings.Run(t, input);
            execs++;
            bool fresh = Fresh(map, seen);
            if (!held && findings.Count > before)
            {
                string kind = Findings.Catch(t, input) is { } e ? Findings.Kind(e) : "a finding that didn't repeat";
                string path = Path.Combine(findingsDir, Hash(input));
                File.WriteAllBytes(path, input);
                Console.WriteLine($"  finding: {kind}\n    {path}");
            }
            else if (held && fresh)
            {
                string name = Hash(input);
                if (saved.Add(name))
                {
                    corpus.Add(input);
                    File.WriteAllBytes(Path.Combine(corpusDir, name), input);
                }
            }
            if (clock.ElapsedMilliseconds >= nextStatus)
            {
                nextStatus += 5000;
                Console.WriteLine($"  {clock.Elapsed:mm\\:ss} {execs} execs ({execs / clock.Elapsed.TotalSeconds:0}/s), corpus {corpus.Count}, edges {Edges(seen)}, findings {findings.Count}");
            }
        }
        Console.WriteLine($"{t.Name}: {execs} execs, corpus {corpus.Count}, edges {Edges(seen)}, findings {findings.Count}");
        if (findings.Count > 0) Console.Write(findings.Report(t));
        string observed = Observed.Report(t.Name);
        if (observed.Length > 0) Console.Write("observed, not failures:\n" + observed);
        Trace.SharedMem = null;
        NativeMemory.Free(map);
        return findings.Count == 0 ? 0 : 1;
    }

    /// <summary>Folds the run's map into <paramref name="seen"/> and clears it; true when a bucket was new.</summary>
    static bool Fresh(byte* map, byte[] seen)
    {
        bool fresh = false;
        ulong* words = (ulong*)map;
        for (int w = 0; w < MapSize / 8; w++)
        {
            if (words[w] == 0) continue;
            for (int i = w * 8; i < w * 8 + 8; i++)
            {
                byte hits = map[i];
                if (hits == 0) continue;
                byte bucket = Bucket(hits);
                if ((seen[i] & bucket) == 0)
                {
                    seen[i] |= bucket;
                    fresh = true;
                }
            }
            words[w] = 0;
        }
        Trace.PrevLocation = 0;
        return fresh;
    }

    static byte Bucket(byte hits) => hits switch
    {
        1 => 1,
        2 => 2,
        3 => 4,
        <= 7 => 8,
        <= 15 => 16,
        <= 31 => 32,
        <= 127 => 64,
        _ => 128,
    };

    static int Edges(byte[] seen)
    {
        int n = 0;
        foreach (byte b in seen)
            if (b != 0) n++;
        return n;
    }

    static string Hash(byte[] input) => Convert.ToHexStringLower(SHA256.HashData(input));
}
