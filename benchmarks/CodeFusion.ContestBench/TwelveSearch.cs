using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace CodeFusion.ContestBench;

/// <summary>What a phase means for the search.</summary>
internal enum Verdict { Pass, Fail, LgLimited }

/// <summary>What the search needs of a finished phase; <see cref="Raw"/> is its merged.json.</summary>
internal sealed record PhaseResult(string Name, int Users, bool Pass, bool Crashed, double ErrorPct, bool LgLimited)
{
    public JsonObject? Raw { get; init; }

    /// <summary>
    /// A crash or 1% errors fails the phase whatever the k6 boxes did: a dead entry makes k6 spin on refused
    /// connections, so busy load boxes are its effect, not the cause. Busy load boxes only void a pass or a latency fail.
    /// </summary>
    public Verdict Verdict => Crashed || ErrorPct >= 1 ? Verdict.Fail : LgLimited ? Verdict.LgLimited : Pass ? Verdict.Pass : Verdict.Fail;

    public static PhaseResult From(JsonObject r) =>
        new((string)r["phase"]!, (int)r["users"]!, (bool)r["pass"]!, (bool?)r["crashed"] ?? false, (double)r["error_pct"]!, (bool)r["lg_limited"]!) { Raw = r };
}

/// <summary>The $12 search's phases: lengths in seconds, the bisect step, where probes start and stop.</summary>
internal sealed record TwelvePlan(int WarmRamp, int Warm, int ProbeRamp, int Probe, int HoldRamp, int Hold, int Step, int Start, int Cap, int? Fixed)
{
    public const int WarmUsers = 1000;

    public static TwelvePlan For(SearchOptions o) => o.Quick
        ? new(20, 40, 20, 40, 20, 60, 500, o.Start ?? 2500, o.Max ?? 400000, o.Fixed)
        : new(60, 120, 60, 120, 60, 300, 250, o.Start ?? 2500, o.Max ?? 400000, o.Fixed);
}

/// <summary>The box, as the search loop sees it.</summary>
internal interface IPhases
{
    /// <summary>The phase from an earlier run of this search (same label, same build), or null.</summary>
    PhaseResult? Cached(string name);

    /// <summary>Runs a phase, and restarts the entry on a fresh database when it crashed.</summary>
    PhaseResult Run(string name, int users, int ramp, int duration);

    /// <summary>Switches the box to the search's entry when it runs another; true when it switched.</summary>
    bool EnsureLive();
}

internal sealed record TwelveOutcome(int Score, int HighestPass, int LowestFail, int LgLimitedAt, IReadOnlyList<PhaseResult> Phases);

/// <summary>
/// The $12 score as its README describes it: a warm-up at 1,000 users, probes from 2,500 doubling until one fails,
/// narrowing to the step, then 5-minute holds stepping down until one passes. The entry is warm before every phase
/// measured: a crash restart, a switch or a resumed search runs the warm-up again first.
/// </summary>
internal static class TwelveSearch
{
    public static TwelveOutcome Run(TwelvePlan p, IPhases box, Action<string> log)
    {
        var phases = new List<PhaseResult>();
        bool cold = true;
        PhaseResult Phase(string name, int users, int ramp, int duration)
        {
            if (box.Cached(name) is { } cached)
            {
                phases.Add(cached);
                return cached;
            }
            if (box.EnsureLive()) cold = true;
            if (cold && name != "warmup")
            {
                log($"warm-up again before {name}: the entry started since it last ran warm");
                phases.Add(box.Run($"rewarm-{name}", TwelvePlan.WarmUsers, p.WarmRamp, p.Warm));
            }
            var r = box.Run(name, users, ramp, duration);
            phases.Add(r);
            cold = r.Crashed;
            return r;
        }

        if (p.Fixed is int f)
        {
            var h = Phase($"hold-{f}", f, p.HoldRamp, p.Hold);
            return new(h.Verdict == Verdict.Pass ? f : 0, 0, 0, h.Verdict == Verdict.LgLimited ? f : 0, phases);
        }
        Phase("warmup", TwelvePlan.WarmUsers, p.WarmRamp, p.Warm);
        int lo = 0, hi = 0, lgAt = 0, step = p.Step;
        int u = p.Start;
        while (true)
        {
            var v = Phase($"probe-{u}", u, p.ProbeRamp, p.Probe).Verdict;
            if (v == Verdict.LgLimited) { lgAt = u; break; }
            if (v == Verdict.Pass)
            {
                lo = u;
                if (hi != 0 || u >= p.Cap) break;
                u = Math.Min(u * 2, p.Cap);
            }
            else
            {
                hi = u;
                if (lo > 0 || u <= step) break;
                u = Math.Max(step, u / 2 / step * step);
            }
        }
        while (lgAt == 0 && lo > 0 && hi > 0 && hi - lo > step)
        {
            int m = (int)Math.Round((lo + hi) / 2.0 / step) * step;
            if (m <= lo || m >= hi) break;
            var v = Phase($"probe-{m}", m, p.ProbeRamp, p.Probe).Verdict;
            if (v == Verdict.LgLimited) { lgAt = m; break; }
            if (v == Verdict.Pass) lo = m; else hi = m;
        }
        int score = 0;
        for (int h = lo; lgAt == 0 && h > 0; h -= step)
        {
            var v = Phase($"hold-{h}", h, p.HoldRamp, p.Hold).Verdict;
            if (v == Verdict.LgLimited) { lgAt = h; break; }
            if (v == Verdict.Pass) { score = h; break; }
        }
        return new(score, lo, hi, lgAt, phases);
    }
}

/// <summary>Which build a phase measured: a cached phase counts only for the build the search is scoring.</summary>
internal static class Builds
{
    public static string? CacheConflict(JsonObject phase, string build) => (string?)phase["build"] switch
    {
        null => $"{phase["phase"]} names no build (it ran before builds were recorded): score this build under a new --label",
        var b when b != build => $"{phase["phase"]} measured build {b}, the entry is now {build}: score it under a new --label",
        _ => null,
    };
}
