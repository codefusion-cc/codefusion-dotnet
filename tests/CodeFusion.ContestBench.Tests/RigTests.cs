using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.ContestBench.Tests;

public sealed class NameTests
{
    [Theory]
    [InlineData("pr82")]
    [InlineData("full1-pr82")]
    [InlineData("cf.tables_2")]
    [InlineData("A")]
    public void PlainNamesPass(string name) => Assert.True(Names.Valid(name));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData(".hidden")]
    [InlineData("-max")]
    [InlineData("a b")]
    [InlineData("a;rm -rf /")]
    [InlineData("a$(id)")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("zażółć")]
    [InlineData("a\n")]
    public void NamesThatAPathOrAShellWouldReadOtherwiseAreRefused(string name)
    {
        Assert.False(Names.Valid(name));
        var e = Assert.Throws<RigException>(() => Names.Check("--label", name));
        Assert.StartsWith("--label ", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAreAtMost64Characters()
    {
        Assert.True(Names.Valid(new string('a', 64)));
        Assert.False(Names.Valid(new string('a', 65)));
    }

    [Property]
    public bool AValidNameStaysInsideTheFolderItIsPutIn(string s) =>
        !Names.Valid(s) || Path.GetDirectoryName(Path.GetFullPath(Path.Combine("/r/results", s))) == "/r/results";
}

public sealed class CacheTests
{
    static JsonObject Phase(string? build) => build == null ? new JsonObject { ["phase"] = "probe-2500" } : new JsonObject { ["phase"] = "probe-2500", ["build"] = build };

    [Fact]
    public void APhaseOfTheSameBuildIsReused() => Assert.Null(Builds.CacheConflict(Phase("sha256:ab12"), "sha256:ab12"));

    [Fact]
    public void APhaseOfAnotherBuildIsRefused()
    {
        string? why = Builds.CacheConflict(Phase("sha256:ab12"), "sha256:cd34");
        Assert.NotNull(why);
        Assert.Contains("sha256:ab12", why, StringComparison.Ordinal);
        Assert.Contains("sha256:cd34", why, StringComparison.Ordinal);
    }

    [Fact]
    public void APhaseThatNamesNoBuildIsRefused() => Assert.NotNull(Builds.CacheConflict(Phase(null), "sha256:ab12"));
}

public sealed class LedgerTests
{
    static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    static string Create(string name, string t, double usd) =>
        new JsonObject { ["t"] = t, ["ev"] = "create", ["name"] = name, ["usd_h"] = usd }.ToJsonString();

    static string Ev(string ev, string name, string t) => new JsonObject { ["t"] = t, ["ev"] = ev, ["name"] = name }.ToJsonString();

    [Fact]
    public void ADeletedVmCostsItsHoursAtItsPrice()
    {
        var (usd, hours, running) = Ledger.Cost([Create("a", "2026-10-21T08:00:00Z", 0.5), Ev("delete", "a", "2026-10-21T10:00:00Z")], Now);
        Assert.Equal(1.0, usd, 9);
        Assert.Equal(2.0, hours, 9);
        Assert.Empty(running);
    }

    [Fact]
    public void AVmGoneOutsideTheRigStopsCostingWhenItWent()
    {
        var (usd, hours, running) = Ledger.Cost([Create("a", "2026-10-21T08:00:00Z", 1), Ev("gone", "a", "2026-10-21T09:30:00Z")], Now);
        Assert.Equal(1.5, hours, 9);
        Assert.Equal(1.5, usd, 9);
        Assert.Empty(running);
    }

    [Fact]
    public void AVmStillRunningCountsUpToNow()
    {
        var (usd, hours, running) = Ledger.Cost([Create("a", "2026-10-21T11:00:00Z", 2)], Now);
        Assert.Equal(1.0, hours, 9);
        Assert.Equal(2.0, usd, 9);
        Assert.Equal(["a"], running);
    }

    [Fact]
    public void AVmMadeAgainUnderItsNameIsCountedTwice()
    {
        var (_, hours, running) = Ledger.Cost([
            Create("a", "2026-10-21T08:00:00Z", 1), Ev("delete", "a", "2026-10-21T09:00:00Z"),
            Create("a", "2026-10-21T10:00:00Z", 1), Ev("delete", "a", "2026-10-21T10:30:00Z"),
        ], Now);
        Assert.Equal(1.5, hours, 9);
        Assert.Empty(running);
    }

    [Fact]
    public void ADeleteOfAVmTheLedgerNeverSawCountsNothing()
    {
        var (usd, hours, _) = Ledger.Cost([Ev("delete", "a", "2026-10-21T10:00:00Z")], Now);
        Assert.Equal(0, usd);
        Assert.Equal(0, hours);
    }

    [Fact]
    public void VmsTheLedgerHasOpenButGcloudDoesNotListAreMissing()
    {
        string[] lines = [Create("a", "2026-10-21T08:00:00Z", 1), Create("b", "2026-10-21T08:00:00Z", 1),
            Create("c", "2026-10-21T08:00:00Z", 1), Ev("delete", "c", "2026-10-21T09:00:00Z"),
            Create("d", "2026-10-21T08:00:00Z", 1), Ev("gone", "d", "2026-10-21T09:00:00Z")];
        Assert.Equal(["b"], Ledger.Missing(lines, new HashSet<string> { "a" }));
    }

    [Fact]
    public void ParallelAppendsLoseNoRow()
    {
        string dir = Directory.CreateTempSubdirectory("cbench-ledger-").FullName;
        try
        {
            string path = Path.Combine(dir, "ledger.jsonl");
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                Ledger.Append(path, new JsonObject { ["t"] = "2026-10-21T08:00:00Z", ["ev"] = "create", ["name"] = $"vm{i}", ["usd_h"] = 1.0 }));
            var lines = File.ReadAllLines(path);
            Assert.Equal(400, lines.Length);
            Assert.Equal(400, lines.Select(l => (string)JsonNode.Parse(l)!["name"]!).Distinct().Count());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class HistTests
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(999L, 999L)]
    [InlineData(1000L, 1000L)]
    [InlineData(1234L, 1230L)]
    [InlineData(1236L, 1240L)]
    [InlineData(99_949L, 99_900L)]
    [InlineData(99_951L, 100_000L)]
    [InlineData(1_000_000L, 1_000_000L)]
    [InlineData(123_456_789L, 123_000_000L)]
    public void KeysKeepThreeSignificantDigits(long us, long key) => Assert.Equal(key, Hist.Key(us));

    [Property]
    public bool AKeyIsWithinHalfAPercentAndStable(uint raw)
    {
        long us = raw;
        long k = Hist.Key(us);
        return Math.Abs(k - us) <= Math.Max(0.5, us * 0.005) && Hist.Key(k) == k;
    }

    [Fact]
    public void PercentilesAreTheNearestRank()
    {
        var h = new Hist();
        for (int ms = 100; ms >= 1; ms--) h.Add(ms);
        Assert.Equal(100, h.Count);
        Assert.Equal(50, h.PercentileMs(0.50));
        Assert.Equal(95, h.PercentileMs(0.95));
        Assert.Equal(99, h.PercentileMs(0.99));
        Assert.Equal(100, h.PercentileMs(1.0));
        Assert.Equal(1, h.PercentileMs(0.001));
    }

    [Fact]
    public void AnEmptyHistogramHasNoLatency()
    {
        var h = new Hist();
        Assert.Equal(0, h.PercentileMs(0.95));
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void OneSampleIsEveryPercentile()
    {
        var h = new Hist();
        h.Add(0.4);
        Assert.Equal(0.4, h.PercentileMs(0.01));
        Assert.Equal(0.4, h.PercentileMs(0.99));
    }

    [Property]
    public bool MergedBoxesEqualOneBoxWithEverySample(uint[] a, uint[] b, byte failedA, byte failedB)
    {
        var one = new Hist { Failed = failedA + failedB };
        var x = new Hist { Failed = failedA };
        var y = new Hist { Failed = failedB };
        foreach (uint v in a) { x.Add(v / 1000.0); one.Add(v / 1000.0); }
        foreach (uint v in b) { y.Add(v / 1000.0); one.Add(v / 1000.0); }
        // as the boxes send them: through their JSON
        var merged = new Hist();
        merged.Merge(Hist.FromJson(JsonNode.Parse(x.ToJson().ToJsonString())!));
        merged.Merge(Hist.FromJson(JsonNode.Parse(y.ToJson().ToJsonString())!));
        return merged.Count == one.Count && merged.Failed == one.Failed && merged.Counts.SequenceEqual(one.Counts) &&
            merged.PercentileMs(0.95) == one.PercentileMs(0.95) && merged.PercentileMs(0.99) == one.PercentileMs(0.99);
    }
}

public sealed class MonitorTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("cbench-monitor-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    string Csv(params string[] lines)
    {
        string p = Path.Combine(_dir, $"m{Guid.NewGuid():N}.csv");
        File.WriteAllLines(p, lines);
        return p;
    }

    [Fact]
    public void OnlyRowsInsideTheWindowCountBothEndsIncluded()
    {
        var m = Monitor.Window(Csv("t,cpu%,app_rss_mb", "99,90,10", "100,10,20", "101,20,40", "102,30,30", "103,99,99"), 100, 102);
        Assert.Equal(3, m.Rows);
        Assert.Equal(20, m.Avg("cpu%"), 9);
        Assert.Equal(30, m.Max("cpu%"), 9);
        Assert.Equal(40, m.Max("app_rss_mb"), 9);
    }

    [Fact]
    public void MillisecondTimestampsAreReadAsSeconds()
    {
        var m = Monitor.Window(Csv("t,cpu%", "1761033600000,10", "1761033601000,30", "1761033700000,99"), 1761033600, 1761033601);
        Assert.Equal(2, m.Rows);
        Assert.Equal(20, m.Avg("cpu%"), 9);
    }

    [Fact]
    public void RowsCutShortOrWithoutATimeAreSkipped()
    {
        var m = Monitor.Window(Csv("t,cpu%,app%", "100,10,1", "101,20", "x,30,3", "", "102,50,5"), 0, 1000);
        Assert.Equal(2, m.Rows);
        Assert.Equal(30, m.Avg("cpu%"), 9);
    }

    [Fact]
    public void AMissingOrEmptyFileHasNoRowsAndZeros()
    {
        var none = Monitor.Window(Path.Combine(_dir, "none.csv"), 0, 10);
        Assert.Equal(0, none.Rows);
        Assert.Equal(0, none.Avg("cpu%"));
        Assert.Equal(0, Monitor.Window(Csv("t,cpu%"), 0, 10).Rows);
        Assert.Equal(0, Monitor.Window(Csv(), 0, 10).Rows);
    }

    [Fact]
    public void TheJsonHasAveragesAndTheMaximaOfLoadAndMemory()
    {
        var o = Monitor.Window(Csv("t,cpu%,app_rss_mb,steal%", "1,10,100,1", "2,20,300,3"), 0, 10).ToJson();
        Assert.Equal(2, (int)o["rows"]!);
        Assert.Equal(15, (double)o["cpu%"]!);
        Assert.Equal(20, (double)o["cpu%_max"]!);
        Assert.Equal(300, (double)o["app_rss_mb_max"]!);
        Assert.Equal(2, (double)o["steal%"]!);
        Assert.Null(o["steal%_max"]);
    }
}

public sealed class CsvTests
{
    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("a,,c", new[] { "a", "", "c" })]
    [InlineData("a,b,", new[] { "a", "b", "" })]
    [InlineData("", new[] { "" })]
    [InlineData("http_req_duration,1761033600,12.5,\"GET /feed?a=1,2\",x", new[] { "http_req_duration", "1761033600", "12.5", "GET /feed?a=1,2", "x" })]
    [InlineData("\"say \"\"hi\"\"\",b", new[] { "say \"hi\"", "b" })]
    [InlineData("\"\",b", new[] { "", "b" })]
    public void FieldsSplitAsRfc4180(string line, string[] fields) => Assert.Equal(fields, Agent.SplitCsv(line));
}

public sealed class FrontTests
{
    [Theory]
    [InlineData("# Entry\n\nFront: nginx\n", "nginx")]
    [InlineData("**Front:** Direct\n", "direct")]
    [InlineData("- _front_ : NGINX behind it\n", "nginx")]
    public void AMulticoreReadmeNamesItsFrontOnALine(string readme, string front) =>
        Assert.Equal(front, Deploy.Front(Contest.Multicore, readme));

    [Theory]
    [InlineData("| Field | Value |\n|---|---|\n| Nginx or direct | direct |\n", "direct")]
    [InlineData("| Nginx or direct | **Nginx** |\n", "nginx")]
    [InlineData("Nginx or direct: direct (port 80)\n", "direct")]
    public void ATwelveReadmeNamesItsFrontInItsTable(string readme, string front) =>
        Assert.Equal(front, Deploy.Front(Contest.Twelve, readme));

    [Theory]
    [InlineData("multicore", "# Entry\nWe use nginx for static files.\n")]
    [InlineData("multicore", "Frontend: nginx\n")]
    [InlineData("twelve", "| Nginx or direct | |\n| Next | direct |\n")]
    [InlineData("twelve", "")]
    public void AReadmeThatDoesNotSayIsRefused(string contest, string readme) =>
        Assert.Throws<RigException>(() => Deploy.Front(Contest.Get(contest), readme));
}
