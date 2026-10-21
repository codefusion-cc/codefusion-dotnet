using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.ContestBench;

/// <summary>Durations of a search's phases; quick mode is for iterating, never for a score.</summary>
internal sealed record Timings(int WarmRamp, int Warm, int ProbeRamp, int Probe, int HoldRamp, int Hold, double Resolution)
{
    public static readonly Timings MulticoreFull = new(60, 120, 60, 60, 60, 300, 0.05);
    public static readonly Timings MulticoreQuick = new(20, 40, 30, 30, 30, 60, 0.10);
}

/// <summary>Search options from the command line.</summary>
internal sealed record SearchOptions(bool Quick, int? Start, int? Max, string? Label, bool NoSwitch, int? Fixed)
{
    /// <summary>The flags as typed, for the search log: what it takes to run the same search again.</summary>
    public string Flags() => string.Join(' ', new[]
    {
        Quick ? "--quick" : null, Start is int s ? $"--start {s}" : null, Max is int m ? $"--max {m}" : null,
        Fixed is int f ? $"--fixed {f}" : null, Label != null ? $"--label {Label}" : null, NoSwitch ? "--no-switch" : null,
    }.Where(x => x != null));
}

/// <summary>
/// What a deploy put on the target for an entry: its source (repository, commit and path) and its build, a hash of
/// the archive that was built. A redeploy from another commit under the same name has another build.
/// </summary>
internal sealed record EntryId(string Source, string Build)
{
    public static string Dir(string entry) => $"/opt/challenge/entries/{entry}";

    public static EntryId Read(string ip, string entry)
    {
        var r = Ssh.Run(ip, $"cat {Dir(entry)}/source && echo && cat {Dir(entry)}/build");
        var l = r.Out.Split('\n', StringSplitOptions.TrimEntries);
        if (!r.Ok || l.Length < 2 || l[1].Length == 0) throw new RigException($"{entry} has no build id on the target: deploy it again");
        return new(l[0], l[1]);
    }
}

internal static class Search
{
    /// <summary>Most users one load box drives: one connection each to the target's ip:port, ports 1024-65535.</summary>
    const int UsersPerLoadBox = 60000;

    /// <summary>
    /// The multicore score, exactly as the scorer runs it: bench/cmd/loadgen's own `search` (warm-up, doubling,
    /// bisecting to 5%, 5-minute holds stepping down 5%), started on the first load box, with this tool as the
    /// target's switch and monitor hooks. Only -max differs by default: the scorer's 120,000 is raised to what the
    /// load boxes can drive, so entries above it still get told apart (both numbers are reported).
    /// </summary>
    public static void Multicore(string entry, SearchOptions o)
    {
        Names.Check("entry", entry);
        if (o.Label != null) Names.Check("--label", o.Label);
        var c = Contest.Multicore;
        var target = Rig.Find(c.TargetName);
        var loads = Rig.Loads(c);
        if (loads.Count == 0) throw new RigException("no load boxes: `rig up multicore` first");
        var t = o.Quick ? Timings.MulticoreQuick : Timings.MulticoreFull;
        int capacity = UsersPerLoadBox * loads.Count;
        int max = Math.Min(o.Max ?? capacity, capacity);
        int start = o.Fixed ?? o.Start ?? (o.Quick ? 20000 : 500);
        if (o.Fixed is int f) max = f;
        string label = o.Label ?? $"{entry}-{(o.Fixed != null ? $"fixed{o.Fixed}" : o.Quick ? "quick" : "full")}-{DateTime.UtcNow:MMdd-HHmm}";
        string rdir = $"/root/results/{label}";
        var args = new List<string>
        {
            "/usr/local/bin/conduit-loadgen", "search", "-target-ssh", $"root@{target.InternalIp}", "-target-url", $"http://{target.InternalIp}",
            "-lang", entry, "-label", label, "-outdir", rdir,
            "-boxes", string.Join(',', loads.Select((l, i) => i == 0 ? "local=1" : $"root@{l.InternalIp}=1")),
            "-switch", "/opt/conduit-tools/cbench agent switch", "-monitor", "/opt/conduit-tools/cbench agent monitor",
            "-start", start.ToString(CultureInfo.InvariantCulture), "-max", max.ToString(CultureInfo.InvariantCulture),
            "-warmup-ramp", $"{t.WarmRamp}s", "-warmup", $"{(o.Fixed != null ? 0 : t.Warm)}s", "-probe-ramp", $"{t.ProbeRamp}s", "-probe-measure", $"{t.Probe}s",
            "-hold-ramp", $"{t.HoldRamp}s", "-hold-measure", $"{t.Hold}s", "-resolution", t.Resolution.ToString(CultureInfo.InvariantCulture),
        };
        if (o.Quick) args.AddRange(["-idle-max", "30s"]);
        if (o.NoSwitch) args.Add("-no-switch");
        string cmd = $"mkdir -p {rdir} && ulimit -n 1048576 && cd /root && ($(command -v setsid) nohup env CONDUIT_JWT_SECRET=multicore-server-challenge " +
            $"{string.Join(' ', args.Select(Shell.Q))} > {rdir}.out 2>&1 < /dev/null & echo $! > {rdir}.pid)";
        Console.WriteLine($"search {label}: {loads.Count} load boxes (up to {capacity:N0} users), -start {start} -max {max}" +
            (o.Quick ? " (quick: not a score)" : ""));
        string lg0 = loads[0].ExternalIp;
        // loadgen resumes a label's search from its outdir: one label is one build of the entry
        var id = EntryId.Read(target.ExternalIp, entry);
        string was = Ssh.Run(lg0, $"cat {rdir}/build 2>/dev/null").Out.Trim();
        if (was.Length > 0 && was != id.Build) throw new RigException($"{label} scored build {was} of {entry}; the entry is now {id.Build}: use a new --label");
        Ssh.Check(lg0, $"mkdir -p {rdir} && printf %s {Shell.Q(id.Build)} > {rdir}/build");
        Console.WriteLine($"  {entry} build {id.Build}: {id.Source}; options [{o.Flags()}]");
        Ssh.Check(lg0, cmd);
        Follow(lg0, $"{rdir}/search.log", $"{rdir}.pid", $"{rdir}.out");
        string local = Path.Combine(Paths.Results("multicore"), label);
        Directory.CreateDirectory(Paths.Results("multicore"));
        if (Directory.Exists(local)) Directory.Delete(local, true);
        Ssh.Download(lg0, rdir, Paths.Results("multicore"), recursive: true);
        Results.AddMulticore(entry, label, o.Quick ? "quick" : o.Fixed != null ? "fixed" : "full", local, id);
    }

    /// <summary>Prints a remote log as it grows until the process in <paramref name="pidFile"/> exits.</summary>
    static void Follow(string ip, string log, string pidFile, string outFile)
    {
        int shown = 0;
        while (true)
        {
            Thread.Sleep(20000);
            var r = Ssh.Run(ip, $"tail -n +{shown + 1} {log} 2>/dev/null; kill -0 $(cat {pidFile}) 2>/dev/null && echo @@RUNNING", timeout: TimeSpan.FromMinutes(2));
            if (!r.Ok && r.Code == 255) continue;
            var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            bool running = lines.Count > 0 && lines[^1] == "@@RUNNING";
            if (running) lines.RemoveAt(lines.Count - 1);
            foreach (string l in lines) Console.WriteLine(l);
            shown += lines.Count;
            if (!running)
            {
                Console.WriteLine(Ssh.Run(ip, $"tail -5 {outFile}").Out.Trim());
                return;
            }
        }
    }

    // ---------------------------------------------------------------- $12

    /// <summary>
    /// The $12 score (<see cref="TwelveSearch"/>). Probes use a 2-minute load.js run (the README doesn't give their
    /// length); holds use load.js's defaults (60 s ramp, 5 minutes). With several k6 boxes the users are split and the
    /// samples merged exactly. A label is one build of one entry: resuming it on another build is refused.
    /// </summary>
    public static void Twelve(string entry, SearchOptions o)
    {
        Names.Check("entry", entry);
        if (o.Label != null) Names.Check("--label", o.Label);
        var c = Contest.Twelve;
        var target = Rig.Find(c.TargetName);
        var loads = Rig.Loads(c);
        if (loads.Count == 0) throw new RigException("no k6 boxes: `rig up twelve` first");
        string label = o.Label ?? $"{entry}-{(o.Fixed != null ? $"fixed{o.Fixed}" : o.Quick ? "quick" : "full")}-{DateTime.UtcNow:MMdd-HHmm}";
        string dir = Path.Combine(Paths.Results("twelve"), label);
        Directory.CreateDirectory(dir);
        using var log = new StreamWriter(Path.Combine(dir, "search.log"), append: true) { AutoFlush = true };
        void Log(string s)
        {
            string l = $"[{DateTime.UtcNow:HH:mm:ss}] {s}";
            Console.WriteLine(l);
            log.WriteLine(l);
        }
        var id = EntryId.Read(target.ExternalIp, entry);
        string buildFile = Path.Combine(dir, "build");
        if (File.Exists(buildFile) && File.ReadAllText(buildFile).Trim() is var was && was != id.Build)
            throw new RigException($"{label} scored build {was} of {entry}; the entry is now {id.Build}: use a new --label");
        File.WriteAllText(buildFile, id.Build);
        Log($"=== search {label}: {entry} build {id.Build} on {target.InternalIp}, k6 boxes {string.Join(", ", loads.Select(l => l.Name))}, " +
            $"options [{o.Flags()}]" + (o.Quick ? " (quick: not a score)" : ""));
        Log($"    source {id.Source}");
        var box = new TwelveBox(target, loads, dir, label, entry, id.Build, o.NoSwitch, Log);
        var r = TwelveSearch.Run(TwelvePlan.For(o), box, Log);
        Log(r.LgLimitedAt > 0 ? $"RESULT: LG-limited at {r.LgLimitedAt} users (add k6 boxes); highest clean pass {r.HighestPass}"
            : r.Score > 0 ? $"RESULT: score {r.Score} users (5-minute hold passed)" : "RESULT: no passing hold");
        Results.AddTwelve(entry, label, o.Fixed != null ? "fixed" : o.Quick ? "quick" : "full", dir, r.Phases.Select(p => p.Raw!).ToList(),
            r.Score, r.HighestPass, r.LowestFail, r.LgLimitedAt, id);
    }

    /// <summary>The $12 box for <see cref="TwelveSearch"/>: phases cached in the label's folder, run on the k6 boxes.</summary>
    sealed class TwelveBox(Vm target, List<Vm> loads, string dir, string label, string entry, string build, bool noSwitch, Action<string> log) : IPhases
    {
        bool _ranAny;
        bool _checked;

        public PhaseResult? Cached(string name)
        {
            string mpath = Path.Combine(dir, name, "merged.json");
            if (!File.Exists(mpath)) return null;
            var cached = JsonNode.Parse(File.ReadAllText(mpath))!.AsObject();
            if (Builds.CacheConflict(cached, build) is { } why) throw new RigException(why);
            log($"{name,-12} {OneLine(cached)} (cached)");
            return PhaseResult.From(cached);
        }

        /// <summary>
        /// Before each phase run: the entry wasn't deployed again, and the box runs it. The first check switches when
        /// this label never switched; later ones when another entry's search ran on the box in between.
        /// </summary>
        public bool EnsureLive()
        {
            var live = Ssh.Check(target.ExternalIp, $"basename \"$(dirname \"$(readlink -f /opt/challenge/current)\")\"; cat {EntryId.Dir(entry)}/build 2>/dev/null; true")
                .Split('\n', StringSplitOptions.TrimEntries);
            string liveEntry = live[0], entryBuild = live.Length > 1 ? live[1] : "";
            if (entryBuild != build) throw new RigException($"{entry} was deployed again during the search (build {entryBuild}, was {build}): use a new --label");
            bool first = !_checked;
            _checked = true;
            if (noSwitch)
            {
                if (liveEntry != entry) throw new RigException($"--no-switch, but the box runs {liveEntry}, not {entry}");
                return false;
            }
            if (liveEntry == entry && !(first && !File.Exists(Path.Combine(dir, "switch.ok")))) return false;
            var sw = Ssh.Run(target.ExternalIp, $"/opt/conduit-tools/cbench agent switch {Shell.Q(entry)} 2>&1", timeout: TimeSpan.FromMinutes(5));
            File.AppendAllText(Path.Combine(dir, "switch.log"), sw.Out);
            if (!sw.Ok) throw new RigException($"switch failed: {Shell.Tail(sw.Out, 1500)}");
            File.WriteAllText(Path.Combine(dir, "switch.ok"), DateTime.UtcNow.ToString("o"));
            log(liveEntry == entry ? "switch ok" : $"switch ok (the box ran {liveEntry})");
            return true;
        }

        public PhaseResult Run(string name, int users, int ramp, int duration)
        {
            string pdir = Path.Combine(dir, name);
            if (Directory.Exists(pdir)) Directory.Delete(pdir, true);
            Directory.CreateDirectory(pdir);
            if (_ranAny) WaitIdle(target.ExternalIp, log);
            _ranAny = true;
            var r = TwelvePhase(target, loads, pdir, name, users, ramp, duration, label, entry, log);
            r["build"] = build;
            File.WriteAllText(Path.Combine(pdir, "merged.json"), r.ToJsonString(Paths.Json));
            return PhaseResult.From(r);
        }
    }

    static JsonObject TwelvePhase(Vm target, List<Vm> loads, string pdir, string name, int users, int ramp, int duration,
        string label, string entry, Action<string> log)
    {
        string run = $"{label}-{name}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var shares = Enumerable.Range(0, loads.Count).Select(i => users / loads.Count + (i < users % loads.Count ? 1 : 0)).ToArray();
        log($"{name,-12} {users} users [{string.Join(' ', shares)}], ramp {ramp}s + {duration}s");
        string mon = $"/root/mon/{run}";
        string StartMonitor() => $"mkdir -p /root/mon && ($(command -v setsid) nohup /opt/conduit-tools/cbench agent monitor 1 {mon}.csv > /dev/null 2>&1 < /dev/null & echo $! > {mon}.pid)";
        string StopMonitor() => $"kill -TERM $(cat {mon}.pid) 2>/dev/null; sleep 1; cat {mon}.csv";
        Ssh.Check(target.ExternalIp, StartMonitor());
        long t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var boxes = new JsonObject[loads.Count];
        Parallel.For(0, loads.Count, i =>
        {
            string ip = loads[i].ExternalIp;
            string rd = $"/root/k6/{run}";
            string k6 = $"cd /opt/twelve/bench && BASE_URL=http://{target.InternalIp} VUS={shares[i]} DURATION={duration}s RAMP_UP={ramp}s " +
                $"TOKENS=/opt/twelve/seed/tokens.json k6 run -q --no-color --summary-export={rd}/summary.json --out csv={rd}/out.csv.gz load.js > {rd}/k6.log 2>&1; echo $? > {rd}/rc";
            Ssh.Check(ip, StartMonitor() + $" && mkdir -p {rd} && ulimit -n 1048576 && ($(command -v setsid) nohup bash -c {Shell.Q(k6)} > /dev/null 2>&1 < /dev/null &)");
            var until = DateTime.UtcNow.AddSeconds(ramp + duration + 40 + 600);
            while (!Ssh.Run(ip, $"test -s {rd}/rc").Ok)
            {
                if (DateTime.UtcNow > until) throw new RigException($"k6 on {loads[i].Name} didn't finish");
                Thread.Sleep(10000);
            }
            string rc = Ssh.Check(ip, $"cat {rd}/rc").Trim();
            File.WriteAllText(Path.Combine(pdir, $"box{i}.csv"), Ssh.Check(ip, StopMonitor()));
            string sum = Ssh.Check(ip, $"/opt/conduit-tools/cbench agent k6sum {rd}/out.csv.gz", timeout: TimeSpan.FromMinutes(10));
            File.WriteAllText(Path.Combine(pdir, $"box{i}.sum.json"), sum);
            Ssh.Download(ip, $"{rd}/summary.json", Path.Combine(pdir, $"box{i}.k6.json"));
            Ssh.Download(ip, $"{rd}/k6.log", Path.Combine(pdir, $"box{i}.k6.log"));
            Ssh.Run(ip, $"rm -f {rd}/out.csv.gz");
            var o = JsonNode.Parse(sum)!.AsObject();
            o["k6_rc"] = int.Parse(rc, CultureInfo.InvariantCulture);
            o["users"] = shares[i];
            boxes[i] = o;
        });
        File.WriteAllText(Path.Combine(pdir, "monitor.csv"), Ssh.Check(target.ExternalIp, StopMonitor()));
        // An entry that died during the phase fails it, and is started again on a fresh copy of the database (the
        // switch), so the phases after it measure the entry rather than a dead port
        bool crashed = !Ssh.Run(target.ExternalIp, "systemctl is-active --quiet challenge").Ok;
        if (crashed)
        {
            // this phase's lines only (the unit's journal holds every entry that ran on the box), and the kernel's
            // OOM killer, which a kill from outside the unit's cgroup doesn't show in the unit's result
            string why = Ssh.Run(target.ExternalIp, "systemctl show challenge -p Result -p ExecMainStatus -p ExecMainCode --value | tr '\\n' ' '; echo; " +
                $"journalctl -k --no-pager -q --since @{t0} | grep -iE 'out of memory|oom-kill|killed process' | tail -5; " +
                $"journalctl -u challenge --no-pager -q --since @{t0} | tail -60").Out;
            File.WriteAllText(Path.Combine(pdir, "crash.log"), why);
            string kernel = why.Split('\n').FirstOrDefault(l => l.Contains("Killed process", StringComparison.OrdinalIgnoreCase) || l.Contains("out of memory", StringComparison.OrdinalIgnoreCase)) ?? "";
            log($"  CRASHED during {name}: {Shell.Tail(why.Split('\n')[0], 120)}{(kernel.Length > 0 ? $"; kernel: {Shell.Tail(kernel, 160)}" : "")}");
            var sw = Ssh.Run(target.ExternalIp, $"/opt/conduit-tools/cbench agent switch {Shell.Q(entry)} 2>&1", timeout: TimeSpan.FromMinutes(5));
            File.WriteAllText(Path.Combine(pdir, "restart.log"), sw.Out);
            log(sw.Ok ? "  restarted on a fresh database" : $"  restart failed: {Shell.Tail(sw.Out, 300)}");
        }

        var all = new Hist();
        foreach (var b in boxes) all.Merge(Hist.FromJson(b));
        double p95 = all.PercentileMs(0.95), p99 = all.PercentileMs(0.99);
        double errRate = all.Count == 0 ? 1 : (double)all.Failed / all.Count;
        // The steady part of the run (after the ramp, before the ramp-down) for the CPU and memory figures.
        long from = t0 + ramp + 5, to = t0 + ramp + duration;
        var boxCpu = boxes.Select((_, i) => Monitor.Window(Path.Combine(pdir, $"box{i}.csv"), from, to)).ToList();
        var lgReasons = boxCpu.Select((m, i) => m.Avg("cpu%") > 75 ? $"k6 box {i} CPU {m.Avg("cpu%"):0}%" : null).Where(x => x != null).ToList();
        var target1 = Monitor.Window(Path.Combine(pdir, "monitor.csv"), from, to);
        double secs = boxes.Max(b => (long)b["last_s"]!) - boxes.Min(b => (long)b["first_s"]!);
        var reasons = new List<string>();
        if (p95 >= 500) reasons.Add($"p95 {p95:0} ms");
        if (p99 >= 1000) reasons.Add($"p99 {p99:0} ms");
        if (errRate >= 0.01) reasons.Add($"errors {100 * errRate:0.00}%");
        if (crashed) reasons.Add("the entry crashed");
        var r = new JsonObject
        {
            ["crashed"] = crashed,
            ["phase"] = name, ["users"] = users, ["pass"] = reasons.Count == 0, ["lg_limited"] = lgReasons.Count > 0 && !name.EndsWith("warmup", StringComparison.Ordinal) && !name.StartsWith("rewarm-", StringComparison.Ordinal),
            ["fail_reasons"] = new JsonArray(reasons.Select(x => (JsonNode)x).ToArray()),
            ["lg_reasons"] = new JsonArray(lgReasons.Select(x => (JsonNode)x!).ToArray()),
            ["requests"] = all.Count, ["rps"] = secs > 0 ? all.Count / secs : 0, ["p50_ms"] = all.PercentileMs(0.5), ["p95_ms"] = p95, ["p99_ms"] = p99,
            ["max_ms"] = all.Counts.Count > 0 ? all.Counts.Keys.Last() / 1000.0 : 0, ["error_pct"] = 100 * errRate,
            ["k6_rcs"] = new JsonArray(boxes.Select(b => (JsonNode)(int)b["k6_rc"]!).ToArray()),
            ["start_s"] = t0, ["ramp_s"] = ramp, ["measure_s"] = duration,
            ["target"] = target1.ToJson(), ["lg_cpu_max_avg"] = boxCpu.Count > 0 ? boxCpu.Max(m => m.Avg("cpu%")) : 0,
        };
        log($"{name,-12} {OneLine(r)}");
        if (reasons.Count > 0) log($"  fail: {string.Join(", ", reasons)}");
        if (lgReasons.Count > 0) log($"  LG-limited: {string.Join("; ", lgReasons)}");
        return r;
    }

    static string OneLine(JsonObject r) => string.Create(CultureInfo.InvariantCulture,
        $"{((bool)r["pass"]! ? "PASS" : "FAIL")}{((bool)r["lg_limited"]! ? " LG" : "")} {(double)r["rps"]!:0} req/s p50 {(double)r["p50_ms"]!:0.0} p95 {(double)r["p95_ms"]!:0.0} p99 {(double)r["p99_ms"]!:0.0} ms err {(double)r["error_pct"]!:0.00}% " +
        // a crashed entry's phase may have no target samples: a missing figure is 0
        $"cpu {(double?)r["target"]?["cpu%"] ?? 0:0}% app {(double?)r["target"]?["app%"] ?? 0:0}% rss {(double?)r["target"]?["app_rss_mb_max"] ?? 0:0} MB");

    /// <summary>As loadgen's search does between phases: at least 5 s, then until the target's CPU is under 15% (90 s at most).</summary>
    static void WaitIdle(string ip, Action<string> log)
    {
        Thread.Sleep(5000);
        var until = DateTime.UtcNow.AddSeconds(90);
        double cpu = -1;
        while (DateTime.UtcNow < until)
        {
            var r = Ssh.Run(ip, "head -1 /proc/stat; sleep 2; head -1 /proc/stat");
            var l = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (l.Length == 2)
            {
                var (b0, t0) = Stat(l[0]);
                var (b1, t1) = Stat(l[1]);
                cpu = t1 > t0 ? 100.0 * (b1 - b0) / (t1 - t0) : 0;
                if (cpu < 15) return;
            }
        }
        log($"  target not idle after 90 s (CPU {cpu:0}%); continuing");
    }

    static (ulong Busy, ulong Total) Stat(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(x => ulong.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        ulong total = 0;
        foreach (ulong x in f) total += x;
        return (total - f[3] - f[4], total);
    }
}

/// <summary>A monitor CSV's rows inside a time window: averages and maxima per column.</summary>
internal sealed class Monitor
{
    readonly Dictionary<string, List<double>> _cols = [];

    public static Monitor Window(string path, long fromS, long toS)
    {
        var m = new Monitor();
        if (!File.Exists(path)) return m;
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return m;
        var head = lines[0].Split(',');
        foreach (string l in lines.Skip(1))
        {
            var f = l.Split(',');
            if (f.Length != head.Length || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
            double s = t > 1e11 ? t / 1000 : t;
            if (s < fromS || s > toS) continue;
            for (int i = 1; i < head.Length; i++)
                if (double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    if (!m._cols.TryGetValue(head[i], out var list)) m._cols[head[i]] = list = [];
                    list.Add(v);
                }
        }
        return m;
    }

    public int Rows => _cols.Count == 0 ? 0 : _cols.Values.First().Count;
    public double Avg(string col) => _cols.TryGetValue(col, out var l) && l.Count > 0 ? l.Average() : 0;
    public double Max(string col) => _cols.TryGetValue(col, out var l) && l.Count > 0 ? l.Max() : 0;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["rows"] = Rows };
        foreach (string c in _cols.Keys)
        {
            o[c] = Math.Round(Avg(c), 2);
            if (c.EndsWith("_mb", StringComparison.Ordinal) || c is "app_threads" or "db_conns" or "tcp_inuse" or "cpu%" or "app%" or "db%")
                o[c + "_max"] = Math.Round(Max(c), 2);
        }
        return o;
    }
}
