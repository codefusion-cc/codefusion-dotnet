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
internal sealed record SearchOptions(bool Quick, int? Start, int? Max, string? Label, bool NoSwitch, int? Fixed);

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
        Ssh.Check(lg0, cmd);
        Follow(lg0, $"{rdir}/search.log", $"{rdir}.pid", $"{rdir}.out");
        string local = Path.Combine(Paths.Results("multicore"), label);
        Directory.CreateDirectory(Paths.Results("multicore"));
        if (Directory.Exists(local)) Directory.Delete(local, true);
        Ssh.Download(lg0, rdir, Paths.Results("multicore"), recursive: true);
        Results.AddMulticore(entry, label, o.Quick ? "quick" : o.Fixed != null ? "fixed" : "full", local);
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
    /// The $12 score as its README describes it: a warm-up at 1,000 users for 2 minutes, bench/load.js from 2,500
    /// users doubling until a run fails, narrowing to within 250 users, then 5-minute holds stepping down 250 until
    /// one passes. A run passes when k6's thresholds would (p95 &lt; 500 ms, p99 &lt; 1 s, under 1% failed, over the
    /// whole run). Probes use a 2-minute load.js run (the README doesn't give their length); holds use load.js's
    /// defaults (60 s ramp, 5 minutes). With several k6 boxes the users are split and the samples merged exactly.
    /// </summary>
    public static void Twelve(string entry, SearchOptions o)
    {
        var c = Contest.Twelve;
        var target = Rig.Find(c.TargetName);
        var loads = Rig.Loads(c);
        if (loads.Count == 0) throw new RigException("no k6 boxes: `rig up twelve` first");
        string label = o.Label ?? $"{entry}-{(o.Fixed != null ? $"fixed{o.Fixed}" : o.Quick ? "quick" : "full")}-{DateTime.UtcNow:MMdd-HHmm}";
        string dir = Path.Combine(Paths.Results("twelve"), label);
        Directory.CreateDirectory(dir);
        var log = new StreamWriter(Path.Combine(dir, "search.log"), append: true) { AutoFlush = true };
        void Log(string s)
        {
            string l = $"[{DateTime.UtcNow:HH:mm:ss}] {s}";
            Console.WriteLine(l);
            log.WriteLine(l);
        }
        (int warmRamp, int warm, int probeRamp, int probe, int holdRamp, int hold, int step) = o.Quick
            ? (20, 40, 20, 40, 20, 60, 500) : (60, 120, 60, 120, 60, 300, 250);
        Log($"=== search {label}: {entry} on {target.InternalIp}, k6 boxes {string.Join(", ", loads.Select(l => l.Name))}" + (o.Quick ? " (quick: not a score)" : ""));
        // a search resumed after another entry's ran on the box switches back, though its first switch is done
        string live = Ssh.Run(target.ExternalIp, "basename $(dirname $(readlink -f /opt/challenge/current))").Out.Trim();
        if (!o.NoSwitch && (!File.Exists(Path.Combine(dir, "switch.ok")) || live != entry))
        {
            var sw = Ssh.Run(target.ExternalIp, $"/opt/conduit-tools/cbench agent switch {Shell.Q(entry)} 2>&1", timeout: TimeSpan.FromMinutes(5));
            File.WriteAllText(Path.Combine(dir, "switch.log"), sw.Out);
            if (!sw.Ok) throw new RigException($"switch failed: {Shell.Tail(sw.Out, 1500)}");
            File.WriteAllText(Path.Combine(dir, "switch.ok"), DateTime.UtcNow.ToString("o"));
            Log("switch ok");
        }
        var phases = new List<JsonObject>();
        bool first = true;
        JsonObject Phase(string name, int users, int ramp, int duration)
        {
            var r = TwelvePhase(target, loads, dir, name, users, ramp, duration, label, waitIdle: !first, Log);
            first = false;
            phases.Add(r);
            return r;
        }
        bool Pass(JsonObject r) => (bool)r["pass"]!;
        bool Lg(JsonObject r) => (bool)r["lg_limited"]!;

        if (o.Fixed is int fixedUsers)
        {
            Phase($"hold-{fixedUsers}", fixedUsers, holdRamp, hold);
            Results.AddTwelve(entry, label, "fixed", dir, phases, fixedUsers);
            return;
        }
        Phase("warmup", 1000, warmRamp, warm);
        int lo = 0, hi = 0, lgAt = 0;
        int u = o.Start ?? 2500;
        int cap = o.Max ?? 400000;
        while (true)
        {
            var r = Phase($"probe-{u}", u, probeRamp, probe);
            if (Lg(r)) { lgAt = u; break; }
            if (Pass(r))
            {
                lo = u;
                if (hi != 0 || u >= cap) break;
                u = Math.Min(u * 2, cap);
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
            var r = Phase($"probe-{m}", m, probeRamp, probe);
            if (Lg(r)) { lgAt = m; break; }
            if (Pass(r)) lo = m; else hi = m;
        }
        int score = 0;
        for (int h = lo; lgAt == 0 && h > 0; h -= step)
        {
            var r = Phase($"hold-{h}", h, holdRamp, hold);
            if (Lg(r)) { lgAt = h; break; }
            if (Pass(r)) { score = h; break; }
        }
        Log(lgAt > 0 ? $"RESULT: LG-limited at {lgAt} users (add k6 boxes); highest clean pass {lo}"
            : score > 0 ? $"RESULT: score {score} users (5-minute hold passed)" : "RESULT: no passing hold");
        Results.AddTwelve(entry, label, o.Quick ? "quick" : "full", dir, phases, score, lo, hi, lgAt);
    }

    static JsonObject TwelvePhase(Vm target, List<Vm> loads, string dir, string name, int users, int ramp, int duration,
        string label, bool waitIdle, Action<string> log)
    {
        string pdir = Path.Combine(dir, name);
        string mpath = Path.Combine(pdir, "merged.json");
        if (File.Exists(mpath))
        {
            var cached = JsonNode.Parse(File.ReadAllText(mpath))!.AsObject();
            log($"{name,-12} {OneLine(cached)} (cached)");
            return cached;
        }
        Directory.CreateDirectory(pdir);
        if (waitIdle) WaitIdle(target.ExternalIp, log);
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
            string why = Ssh.Run(target.ExternalIp, "systemctl show challenge -p Result -p ExecMainStatus -p ExecMainCode --value | tr '\\n' ' '; " +
                "journalctl -u challenge --no-pager -n 60").Out;
            File.WriteAllText(Path.Combine(pdir, "crash.log"), why);
            log($"  CRASHED during {name}: {Shell.Tail(why.Split('\n')[0], 200)}");
            string entry = Ssh.Check(target.ExternalIp, "basename $(dirname $(readlink -f /opt/challenge/current))").Trim();
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
            ["phase"] = name, ["users"] = users, ["pass"] = reasons.Count == 0, ["lg_limited"] = lgReasons.Count > 0 && name != "warmup",
            ["fail_reasons"] = new JsonArray(reasons.Select(x => (JsonNode)x).ToArray()),
            ["lg_reasons"] = new JsonArray(lgReasons.Select(x => (JsonNode)x!).ToArray()),
            ["requests"] = all.Count, ["rps"] = secs > 0 ? all.Count / secs : 0, ["p50_ms"] = all.PercentileMs(0.5), ["p95_ms"] = p95, ["p99_ms"] = p99,
            ["max_ms"] = all.Counts.Count > 0 ? all.Counts.Keys.Last() / 1000.0 : 0, ["error_pct"] = 100 * errRate,
            ["k6_rcs"] = new JsonArray(boxes.Select(b => (JsonNode)(int)b["k6_rc"]!).ToArray()),
            ["start_s"] = t0, ["ramp_s"] = ramp, ["measure_s"] = duration,
            ["target"] = target1.ToJson(), ["lg_cpu_max_avg"] = boxCpu.Count > 0 ? boxCpu.Max(m => m.Avg("cpu%")) : 0,
        };
        File.WriteAllText(mpath, r.ToJsonString(Paths.Json));
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
