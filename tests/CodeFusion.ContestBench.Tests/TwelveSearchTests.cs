using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CodeFusion.ContestBench.Tests;

/// <summary>The $12 search loop against a fake box: which phases run, in which order, and what it scores.</summary>
public sealed class TwelveSearchTests
{
    static readonly TwelvePlan Full = TwelvePlan.For(new SearchOptions(false, null, null, null, false, null));

    /// <summary>A box whose entry passes probes up to <paramref name="probeMax"/> and holds up to <paramref name="holdMax"/>.</summary>
    sealed class FakeBox(int probeMax, int holdMax) : IPhases
    {
        public readonly Dictionary<string, PhaseResult> Cache = [];
        public readonly List<string> Ran = [];
        /// <summary>Above the limit the entry crashes; false makes it fail on latency instead.</summary>
        public bool CrashAbove = true;
        /// <summary>Users at which the k6 boxes run out of CPU.</summary>
        public int LgFrom = int.MaxValue;
        /// <summary>A run that fails on errors whatever the users, while the k6 boxes are busy.</summary>
        public bool ErrorsWhenLg;
        /// <summary>The EnsureLive calls (1 is the first) on which another entry was found running and switched out.</summary>
        public HashSet<int> SwitchOn = [];
        public int EnsureLiveCalls;

        public PhaseResult? Cached(string name) => Cache.GetValueOrDefault(name);

        public bool EnsureLive()
        {
            EnsureLiveCalls++;
            return SwitchOn.Contains(EnsureLiveCalls);
        }

        public PhaseResult Run(string name, int users, int ramp, int duration)
        {
            Ran.Add(name);
            bool hold = name.StartsWith("hold-", StringComparison.Ordinal);
            bool warm = name == "warmup" || name.StartsWith("rewarm-", StringComparison.Ordinal);
            bool lg = !warm && users >= LgFrom;
            bool ok = warm || users <= (hold ? holdMax : probeMax);
            if (lg && ErrorsWhenLg) return new(name, users, false, false, 40, true);
            if (ok) return new(name, users, true, false, 0, lg);
            return CrashAbove ? new(name, users, false, true, 85, lg) : new(name, users, false, false, 0, lg);
        }

        public List<string> Measured => Ran.Where(n => !n.StartsWith("rewarm-", StringComparison.Ordinal)).ToList();
    }

    static TwelveOutcome Search(FakeBox box, TwelvePlan? plan = null) => TwelveSearch.Run(plan ?? Full, box, _ => { });

    [Fact]
    public void AnEntryThatCrashesWhileTheLoadBoxesAreBusyIsBisectedNotStopped()
    {
        // pr82's run on 2026-10-21: killed for memory above 188,500 users, and at 320,000 the five k6 boxes averaged
        // 87-89% CPU on refused connections. The crash decides: the search bisects down and scores the hold.
        var box = new FakeBox(probeMax: 188500, holdMax: 188250) { LgFrom = 320000 };
        var o = Search(box);
        Assert.Equal(
            ["warmup", "probe-2500", "probe-5000", "probe-10000", "probe-20000", "probe-40000", "probe-80000", "probe-160000",
             "probe-320000", "probe-240000", "probe-200000", "probe-180000", "probe-190000", "probe-185000", "probe-187500",
             "probe-188750", "probe-188000", "probe-188500", "hold-188500", "hold-188250"],
            box.Measured);
        Assert.Equal(188250, o.Score);
        Assert.Equal(188500, o.HighestPass);
        Assert.Equal(188750, o.LowestFail);
        Assert.Equal(0, o.LgLimitedAt);
    }

    [Fact]
    public void APassWhileTheLoadBoxesAreBusyStopsTheSearchWithNoScore()
    {
        var box = new FakeBox(probeMax: 1_000_000, holdMax: 1_000_000) { LgFrom = 40000 };
        var o = Search(box);
        Assert.Equal(["warmup", "probe-2500", "probe-5000", "probe-10000", "probe-20000", "probe-40000"], box.Ran);
        Assert.Equal(0, o.Score);
        Assert.Equal(40000, o.LgLimitedAt);
        Assert.Equal(20000, o.HighestPass);
    }

    [Fact]
    public void ALatencyFailWhileTheLoadBoxesAreBusyIsLoadLimited()
    {
        var box = new FakeBox(probeMax: 20000, holdMax: 20000) { CrashAbove = false, LgFrom = 40000 };
        var o = Search(box);
        Assert.Equal("probe-40000", box.Ran[^1]);
        Assert.Equal(40000, o.LgLimitedAt);
        Assert.Equal(0, o.Score);
    }

    [Fact]
    public void AnErrorFailWhileTheLoadBoxesAreBusyIsAFail()
    {
        var box = new FakeBox(probeMax: 1_000_000, holdMax: 30000) { LgFrom = 40000, ErrorsWhenLg = true };
        var o = Search(box);
        Assert.Equal(0, o.LgLimitedAt);
        Assert.Equal(40000, o.LowestFail);
        Assert.Equal(30000, o.Score);
    }

    [Fact]
    public void ProbesDoubleThenBisectToTheStepAndHoldsStepDownUntilOnePasses()
    {
        var box = new FakeBox(probeMax: 12340, holdMax: 11750) { CrashAbove = false };
        var o = Search(box);
        Assert.Equal(12250, o.HighestPass);
        Assert.Equal(12500, o.LowestFail);
        Assert.Equal(["hold-12250", "hold-12000", "hold-11750"], box.Ran.Where(n => n.StartsWith("hold-", StringComparison.Ordinal)));
        Assert.Equal(11750, o.Score);
        // the bisect never probes the same count twice, and every probe after the first double is a multiple of the step
        var probes = box.Ran.Where(n => n.StartsWith("probe-", StringComparison.Ordinal)).ToList();
        Assert.Equal(probes.Count, probes.Distinct().Count());
        Assert.All(probes, n => Assert.Equal(0, int.Parse(n["probe-".Length..], System.Globalization.CultureInfo.InvariantCulture) % 250));
    }

    [Fact]
    public void AnEntryThatFailsAtTheStartHalvesDownToOneStepAndScoresNothing()
    {
        var box = new FakeBox(probeMax: 0, holdMax: 0) { CrashAbove = false };
        var o = Search(box);
        Assert.Equal(["warmup", "probe-2500", "probe-1250", "probe-500", "probe-250"], box.Ran);
        Assert.Equal(0, o.Score);
        Assert.Equal(0, o.HighestPass);
        Assert.Equal(250, o.LowestFail);
    }

    [Fact]
    public void AnEntryThatPassesEverythingIsHeldAtTheCap()
    {
        var box = new FakeBox(probeMax: 1_000_000, holdMax: 1_000_000);
        var o = Search(box, Full with { Cap = 30000 });
        Assert.Equal(["warmup", "probe-2500", "probe-5000", "probe-10000", "probe-20000", "probe-30000", "hold-30000"], box.Ran);
        Assert.Equal(30000, o.Score);
    }

    [Fact]
    public void AFreshSearchWarmsUpOnceAndNotAgainBeforeTheFirstProbe()
    {
        var box = new FakeBox(probeMax: 5000, holdMax: 5000);
        Search(box);
        Assert.Equal("warmup", box.Ran[0]);
        Assert.Equal("probe-2500", box.Ran[1]);
    }

    [Fact]
    public void EveryPhaseAfterACrashIsPrecededByOneWarmUp()
    {
        var box = new FakeBox(probeMax: 188500, holdMax: 188250) { LgFrom = 320000 };
        Search(box);
        // each crash restarts the entry on a fresh database: the next phase measured warms it up first, once
        Assert.Equal(
            ["rewarm-probe-240000", "rewarm-probe-200000", "rewarm-probe-180000", "rewarm-probe-185000", "rewarm-probe-188000", "rewarm-hold-188250"],
            box.Ran.Where(n => n.StartsWith("rewarm-", StringComparison.Ordinal)));
        for (int i = 0; i < box.Ran.Count; i++)
            if (box.Ran[i].StartsWith("rewarm-", StringComparison.Ordinal))
                Assert.Equal(box.Ran[i]["rewarm-".Length..], box.Ran[i + 1]);
    }

    [Fact]
    public void AResumeSkipsWhatItHasAndWarmsUpBeforeTheFirstPhaseItRuns()
    {
        var box = new FakeBox(probeMax: 7000, holdMax: 7000) { CrashAbove = false };
        foreach (var (n, u) in new[] { ("warmup", 1000), ("probe-2500", 2500), ("probe-5000", 5000) })
            box.Cache[n] = new PhaseResult(n, u, true, false, 0, false);
        var o = Search(box);
        Assert.Equal("rewarm-probe-10000", box.Ran[0]);
        Assert.Equal("probe-10000", box.Ran[1]);
        Assert.DoesNotContain("warmup", box.Ran);
        Assert.DoesNotContain("probe-5000", box.Ran);
        Assert.Equal(7000, o.Score);
        Assert.Equal(["warmup", "probe-2500", "probe-5000"], o.Phases.Take(3).Select(p => p.Name));
    }

    [Fact]
    public void ASwitchBeforeAPhaseWarmsTheEntryUpAgain()
    {
        // warm and measuring, then another entry's search ran on the box before probe-5000: the switch back starts it cold
        var box = new FakeBox(probeMax: 5000, holdMax: 5000) { CrashAbove = false, SwitchOn = [3] };
        Search(box);
        Assert.Equal(["warmup", "probe-2500", "rewarm-probe-5000", "probe-5000"], box.Ran.Take(4));
        Assert.Single(box.Ran, n => n.StartsWith("rewarm-", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLiveEntryIsCheckedBeforeEveryPhaseThatRuns()
    {
        var box = new FakeBox(probeMax: 5000, holdMax: 5000) { CrashAbove = false };
        Search(box);
        Assert.Equal(box.Measured.Count, box.EnsureLiveCalls);
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(4750, 0)]
    public void AFixedSearchHoldsOnceAfterAWarmUpAndScoresOnlyAPass(int holdMax, int score)
    {
        var box = new FakeBox(probeMax: 0, holdMax: holdMax) { CrashAbove = false };
        var o = Search(box, Full with { Fixed = 5000 });
        Assert.Equal(["rewarm-hold-5000", "hold-5000"], box.Ran);
        Assert.Equal(score, o.Score);
    }

    [Theory]
    [InlineData(true, false, 0.0, true, "LgLimited")]
    [InlineData(true, false, 0.0, false, "Pass")]
    [InlineData(false, true, 0.0, true, "Fail")]
    [InlineData(false, false, 1.0, true, "Fail")]
    [InlineData(false, false, 0.99, true, "LgLimited")]
    [InlineData(false, false, 0.0, false, "Fail")]
    public void AVerdictPutsCrashesAndErrorsBeforeLoadLimits(bool pass, bool crashed, double errorPct, bool lg, string want)
    {
        Assert.Equal(Enum.Parse<Verdict>(want), new PhaseResult("probe-1", 1, pass, crashed, errorPct, lg).Verdict);
    }
}
