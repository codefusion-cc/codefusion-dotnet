using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeFusion.ContestBench;

/// <summary>One row per search in artifacts/contest/results/results.jsonl, and the table made from them.</summary>
internal static class Results
{
    static string File_ => Path.Combine(Paths.State, "results", "results.jsonl");

    /// <summary>The scorer's -max; above it a score is reported as capped.</summary>
    public const int ScorerMax = 120000;

    public static void AddMulticore(string entry, string label, string mode, string dir)
    {
        var sum = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "SUMMARY.json")))!;
        int score = (int)sum["score_users"]!;
        // The scored hold, or the last hold tried when none passed.
        string? holdDir = score > 0 ? Path.Combine(dir, $"hold-{score}")
            : Directory.GetDirectories(dir, "hold-*").OrderBy(Directory.GetCreationTimeUtc).LastOrDefault();
        var row = new JsonObject
        {
            ["contest"] = "multicore", ["entry"] = entry, ["label"] = label, ["mode"] = mode, ["status"] = (string)sum["status"]!,
            ["score"] = score, ["official_score"] = Math.Min(score, ScorerMax),
            ["highest_pass"] = (int)sum["highest_passing_probe"]!, ["lowest_fail"] = (int?)sum["lowest_failing_probe"] ?? 0,
            ["lg_limited_at"] = (int?)sum["lg_limited_at"] ?? 0, ["time"] = DateTime.UtcNow.ToString("o"),
        };
        if (holdDir != null && File.Exists(Path.Combine(holdDir, "merged.json")))
        {
            var m = JsonNode.Parse(File.ReadAllText(Path.Combine(holdDir, "merged.json")))!;
            var ov = m["overall"]!;
            row["users"] = (int)m["users"]!;
            row["rps"] = (double)ov["rps"]!;
            row["p50_ms"] = (double)ov["p50_ms"]!;
            row["p95_ms"] = (double)ov["p95_ms"]!;
            row["p99_ms"] = (double)ov["p99_ms"]!;
            row["error_pct"] = 100 * (double)ov["error_rate"]!;
            row["lg_cpu_max_avg"] = m["boxes"]!.AsArray().Max(b => (double?)b!["cpu_avg_measure_pct"] ?? 0);
            long from = (long)m["start_at_ms"]! / 1000 + (long)(double)m["ramp_s"]!;
            long to = from + (long)(double)m["measure_s"]!;
            row["target"] = Monitor.Window(Path.Combine(holdDir, "monitor.csv"), from, to).ToJson();
        }
        Add(row);
    }

    public static void AddTwelve(string entry, string label, string mode, string dir, List<JsonObject> phases, int score,
        int lo = 0, int hi = 0, int lgAt = 0)
    {
        var hold = phases.LastOrDefault(p => ((string)p["phase"]!).StartsWith("hold-", StringComparison.Ordinal) && (bool)p["pass"]!)
            ?? phases.LastOrDefault(p => ((string)p["phase"]!).StartsWith("hold-", StringComparison.Ordinal));
        var row = new JsonObject
        {
            ["contest"] = "twelve", ["entry"] = entry, ["label"] = label, ["mode"] = mode,
            ["status"] = lgAt > 0 ? "lg_limited" : score > 0 ? "ok" : "no_pass", ["score"] = score, ["official_score"] = score,
            ["highest_pass"] = lo, ["lowest_fail"] = hi, ["lg_limited_at"] = lgAt, ["time"] = DateTime.UtcNow.ToString("o"),
        };
        if (hold != null)
        {
            foreach (string k in new[] { "users", "rps", "p50_ms", "p95_ms", "p99_ms", "error_pct", "lg_cpu_max_avg" }) row[k] = hold[k]!.DeepClone();
            row["target"] = hold["target"]!.DeepClone();
        }
        File.WriteAllText(Path.Combine(dir, "SUMMARY.json"), new JsonObject { ["row"] = row.DeepClone(), ["phases"] = new JsonArray(phases.Select(p => p.DeepClone()).ToArray()) }.ToJsonString(Paths.Json));
        Add(row);
    }

    static void Add(JsonObject row)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.AppendAllText(File_, row.ToJsonString() + "\n");
        Console.WriteLine();
        Console.WriteLine(Table([row]));
    }

    public static string Table(IEnumerable<JsonObject>? rows = null)
    {
        rows ??= File.Exists(File_) ? File.ReadLines(File_).Select(l => JsonNode.Parse(l)!.AsObject()) : [];
        var sb = new StringBuilder();
        sb.AppendLine("| contest | entry | mode | score (users) | at hold: req/s | p95 ms | p99 ms | errors | box CPU | server CPU | Postgres CPU | nginx CPU | server RSS avg/max MB | Postgres mem MB | load box CPU |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            var t = r["target"];
            string D(JsonNode? n, string fmt) => n == null ? "" : ((double)n).ToString(fmt, CultureInfo.InvariantCulture);
            int score = (int)r["score"]!;
            string scoreText = (string)r["status"]! switch
            {
                "lg_limited" => $"LG-limited at {(int)r["lg_limited_at"]!:N0} (pass {(int)r["highest_pass"]!:N0})",
                "no_pass" => "no pass",
                _ => score.ToString("N0", CultureInfo.InvariantCulture) + ((string)r["contest"]! == "multicore" && score > ScorerMax ? $" (scorer's cap {ScorerMax:N0})" : ""),
            };
            sb.Append(CultureInfo.InvariantCulture, $"| {r["contest"]} | {r["entry"]} | {r["mode"]} | {scoreText} | {D(r["rps"], "N0")} | {D(r["p95_ms"], "0.0")} | {D(r["p99_ms"], "0.0")} | {D(r["error_pct"], "0.00")}% | ");
            sb.Append(CultureInfo.InvariantCulture, $"{D(t?["cpu%"], "0")}% | {D(t?["app%"], "0")}% | {D(t?["db%"], "0")}% | {D(t?["nginx%"], "0")}% | ");
            sb.AppendLine(CultureInfo.InvariantCulture, $"{D(t?["app_rss_mb"], "0")} / {D(t?["app_rss_mb_max"], "0")} | {D(t?["db_mem_mb"], "0")} | {D(r["lg_cpu_max_avg"], "0")}% |");
        }
        return sb.ToString();
    }
}
