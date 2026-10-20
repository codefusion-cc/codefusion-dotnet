using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodeFusion.Fuzz;

/// <summary>What broke while inputs ran through a target, grouped by kind, each with the shortest input that showed it.</summary>
sealed class Findings
{
    readonly Dictionary<string, (byte[] Input, Exception Error, int Count)> _byKind = [];

    public int Count => _byKind.Count;

    public IEnumerable<(string Kind, byte[] Input, Exception Error, int Count)> All =>
        _byKind.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (k.Key, k.Value.Input, k.Value.Error, k.Value.Count));

    /// <summary>Runs one input; false when an invariant broke or the code under test threw.</summary>
    public bool Run(Target t, byte[] input)
    {
        try
        {
            t.Run(input);
            return true;
        }
        catch (Exception e)
        {
            string kind = Kind(e);
            _byKind[kind] = _byKind.TryGetValue(kind, out var seen)
                ? (input.Length < seen.Input.Length ? input : seen.Input, input.Length < seen.Input.Length ? e : seen.Error, seen.Count + 1)
                : (input, e, 1);
            return false;
        }
    }

    /// <summary>A finding's signature, or the exception's type and the frame in the code under test it came from.</summary>
    public static string Kind(Exception e)
    {
        if (e is FuzzFinding f) return f.Signature;
        string? frame = e.StackTrace?.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("at CodeFusion.", StringComparison.Ordinal) && !l.StartsWith("at CodeFusion.Fuzz.", StringComparison.Ordinal));
        if (frame is not null)
        {
            int paren = frame.IndexOf('(', StringComparison.Ordinal);
            frame = frame[3..(paren < 0 ? frame.Length : paren)];
        }
        return $"{e.GetType().Name} in {frame ?? "the harness"}";
    }

    /// <summary>The input cut down (ddmin) while it still shows the same kind of finding.</summary>
    public static byte[] Minimize(Target t, byte[] input, string kind, int budget = 3000)
    {
        byte[] best = input;
        for (int chunk = Math.Max(1, best.Length / 2); budget > 0; chunk /= 2)
        {
            for (int at = 0; at + chunk <= best.Length && budget > 0;)
            {
                byte[] candidate = [.. best.AsSpan(0, at), .. best.AsSpan(at + chunk)];
                budget--;
                if (Shows(t, candidate, kind)) best = candidate;
                else at += chunk;
            }
            if (chunk == 1) break;
        }
        return best;
    }

    static bool Shows(Target t, byte[] input, string kind) => Catch(t, input) is { } e && Kind(e) == kind;

    /// <summary>What running the input throws, or null.</summary>
    public static Exception? Catch(Target t, byte[] input)
    {
        try
        {
            t.Run(input);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>The bytes as a C# string literal: printable ASCII as it is, the rest escaped.</summary>
    public static string Show(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder("\"");
        foreach (byte c in b)
        {
            sb.Append(c switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n",
                (byte)'\t' => "\\t",
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                >= 0x20 and < 0x7F => ((char)c).ToString(),
                _ => $"\\x{c:X2}",
            });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A report: each kind with its count, its shortest input minimized, and the error.</summary>
    public string Report(Target t)
    {
        var sb = new StringBuilder();
        foreach (var (kind, input, error, count) in All)
        {
            byte[] small = Minimize(t, input, kind);
            Exception shown = Catch(t, small) ?? error;
            sb.Append("- ").Append(kind).Append(" (").Append(count).Append("x)\n    input: ").Append(Show(small))
                .Append("\n    ").Append(shown.GetType().Name).Append(": ").Append(shown.Message.ReplaceLineEndings(" ")).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Disagreements that are not failures but are worth knowing: the differential target's "one side refuses" classes.
/// Counted per class with the shortest input seen; the replay test and --report print them.
/// </summary>
static class Observed
{
    static readonly ConcurrentDictionary<string, (byte[] Input, int Count)> Classes = new();

    public static void Record(string kind, ReadOnlySpan<byte> input)
    {
        byte[] copy = input.ToArray();
        Classes.AddOrUpdate(kind, (copy, 1), (_, seen) => (copy.Length < seen.Input.Length ? copy : seen.Input, seen.Count + 1));
    }

    public static string Report(string prefix)
    {
        var sb = new StringBuilder();
        foreach (var (kind, (input, count)) in Classes.Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(c => c.Key, StringComparer.Ordinal))
            sb.Append("- ").Append(kind).Append(" (").Append(count).Append("x): ").Append(Findings.Show(input)).Append('\n');
        return sb.ToString();
    }
}
