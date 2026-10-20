using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.ContestBench;

/// <summary>
/// What runs on the rig's Linux boxes (`cbench agent ...`): the target's switch and monitor hooks that loadgen's
/// search calls, the disk prewarm, and the k6 sample summary on the $12 load boxes.
/// </summary>
internal static class Agent
{
    const string Entries = "/opt/challenge/entries";

    public static int Run(string[] args) => args switch
    {
        ["switch", var name] => Switch(name),
        ["monitor", var interval, var csv] => Monitor(double.Parse(interval, CultureInfo.InvariantCulture), csv),
        ["prewarm"] => Prewarm(),
        ["k6sum", var csv, ..] => K6Sum(csv),
        ["pgstat", var what] => PgStat(what),
        _ => Usage(),
    };

    static int Usage()
    {
        Console.Error.WriteLine("agent switch <entry> | monitor <seconds> <file.csv> | prewarm | k6sum <out.csv.gz> | pgstat on|off|reset|dump");
        return 2;
    }

    static string ContestOfBox() => File.Exists("/etc/cbench-contest") ? File.ReadAllText("/etc/cbench-contest").Trim() : "";

    static void Log(string s) => Console.WriteLine($"[switch {DateTime.Now:HH:mm:ss}] {s}");

    static string Sh(string file, params string[] args)
    {
        var r = Shell.Run(file, args, timeout: TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new RigException($"{file} {string.Join(' ', args)}: exit {r.Code}: {Shell.Tail(r.Err + r.Out, 2000)}");
        return r.Out;
    }

    /// <summary>
    /// The scorer's switch.sh: the entry on a fresh copy of the seed, caches dropped, started and healthy. Behind
    /// Nginx it listens on 127.0.0.1:3000, direct on 0.0.0.0:80; Nginx runs only for the first.
    /// </summary>
    static int Switch(string name)
    {
        var sw = Stopwatch.StartNew();
        string contest = ContestOfBox();
        string dir = Path.Combine(Entries, name);
        if (!Directory.Exists(Path.Combine(dir, "app"))) { Console.Error.WriteLine($"no entry {name}: deploy it first"); return 1; }
        string front = File.ReadAllText(Path.Combine(dir, "front")).Trim();
        bool direct = front == "direct";
        Log($"{name} ({front}) on the {contest} box: {File.ReadAllText(Path.Combine(dir, "source"))}");
        var mem = MemInfo();
        Log($"box: nproc {Environment.ProcessorCount}, MemTotal {mem.GetValueOrDefault("MemTotal") / 1024} MB, " +
            $"SwapTotal {mem.GetValueOrDefault("SwapTotal") / 1024} MB, kernel {File.ReadAllText("/proc/cmdline").Trim()}");
        if (contest == "twelve" && (Environment.ProcessorCount != 1 || mem.GetValueOrDefault("MemTotal") > 2100 * 1024 || mem.GetValueOrDefault("SwapTotal") > 0))
        {
            Console.Error.WriteLine("the $12 box must be 1 CPU, at most 2 GB, no swap");
            return 1;
        }
        Shell.Run("systemctl", ["stop", "challenge"]);
        string current = "/opt/challenge/current";
        if (File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget != null) File.Delete(current);
        File.CreateSymbolicLink(current, Path.Combine(dir, "app"));

        var env = new StringBuilder();
        if (contest == "multicore")
            env.Append("DATABASE_URL=postgres://conduit:conduit@127.0.0.1:5432/conduit\nJWT_SECRET=multicore-server-challenge\n");
        else
            env.Append("SQLITE_PATH=/var/lib/challenge/feed.db\nJWT_SECRET=twelve-dollar-challenge\n");
        env.Append(direct ? "HOST=0.0.0.0\nPORT=80\n" : "HOST=127.0.0.1\nPORT=3000\n");
        string extra = Path.Combine(dir, "env");
        if (File.Exists(extra)) env.Append(File.ReadAllText(extra));
        File.WriteAllText("/etc/challenge/env", env.ToString());
        Sh("chgrp", "challenge", "/etc/challenge/env");
        Sh("chmod", "640", "/etc/challenge/env");
        Sh("systemctl", direct ? "stop" : "restart", "nginx");

        if (contest == "multicore")
        {
            Log("fresh database conduit from conduit_seed");
            Sh("runuser", "-u", "postgres", "--", "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", "postgres",
                "-c", "DROP DATABASE IF EXISTS conduit WITH (FORCE)",
                "-c", "CREATE DATABASE conduit TEMPLATE conduit_seed STRATEGY FILE_COPY", "-c", "CHECKPOINT");
            Sh("systemctl", "restart", "postgresql@18-main");
            for (int i = 0; i < 60 && !Shell.Run("runuser", ["-u", "postgres", "--", "pg_isready", "-q"]).Ok; i++) Thread.Sleep(1000);
        }
        else
        {
            Log("fresh copy of seed/feed.db");
            foreach (string f in Directory.GetFiles("/var/lib/challenge", "feed.db*")) File.Delete(f);
            File.Copy("/opt/twelve/seed/feed.db", "/var/lib/challenge/feed.db");
            Sh("chown", "challenge:challenge", "/var/lib/challenge/feed.db");
        }
        Sh("sync");
        File.WriteAllText("/proc/sys/vm/drop_caches", "3");
        Log("caches dropped; starting");
        Sh("systemctl", "start", "challenge");
        string path = contest == "multicore" ? "/api/tags" : "/health";
        string url = $"http://127.0.0.1:{(direct ? 80 : 3000)}{path}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var start = Stopwatch.StartNew();
        while (start.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                using var resp = http.GetAsync(new Uri(url)).GetAwaiter().GetResult();
                if ((int)resp.StatusCode == 200)
                {
                    // Behind Nginx the load comes through port 80: check that path too.
                    if (!direct)
                    {
                        using var viaNginx = http.GetAsync(new Uri($"http://127.0.0.1{path}")).GetAwaiter().GetResult();
                        if ((int)viaNginx.StatusCode != 200) throw new HttpRequestException($"nginx answered {(int)viaNginx.StatusCode}");
                    }
                    Log($"healthy after {start.Elapsed.TotalSeconds:0.0} s ({url}); switch took {sw.Elapsed.TotalSeconds:0} s");
                    return 0;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            Thread.Sleep(500);
        }
        Console.Error.WriteLine($"{url} not 200 after 60 s");
        Console.Error.WriteLine(Shell.Run("journalctl", ["-u", "challenge", "-n", "60", "--no-pager"]).Out);
        return 1;
    }

    // ---------------------------------------------------------------- monitor

    sealed class Group(string name, string path)
    {
        public string Name = name;
        public string Path = path;
        public long LastUsec = -1;
    }

    /// <summary>
    /// One CSV row per interval until SIGTERM: box CPU (user, system, softirq, steal), the CPU of the server, Postgres
    /// and Nginx (their systemd cgroups, in % of the whole box), the server's RSS and threads, Postgres' memory
    /// (anon + shared buffers touched) and backends, Nginx's RSS, used memory and open TCP sockets.
    /// </summary>
    static int Monitor(double interval, string csvPath)
    {
        int ncpu = Environment.ProcessorCount;
        var groups = new[]
        {
            new Group("app", "/sys/fs/cgroup/system.slice/challenge.service"),
            new Group("db", "/sys/fs/cgroup/system.slice/system-postgresql.slice"),
            new Group("nginx", "/sys/fs/cgroup/system.slice/nginx.service"),
        };
        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; stop.Cancel(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        using var w = new StreamWriter(csvPath, append: false) { AutoFlush = true };
        w.WriteLine("time,cpu%,user%,sys%,softirq%,steal%,app%,db%,nginx%,app_rss_mb,app_anon_mb,app_threads,db_mem_mb,db_conns,nginx_rss_mb,mem_used_mb,tcp_inuse,load1");
        ulong[] last = CpuTimes();
        foreach (var g in groups) g.LastUsec = CgroupUsec(g.Path);
        var tick = Stopwatch.StartNew();
        long lastTicks = 0;
        int n = 0;
        while (!stop.IsCancellationRequested)
        {
            n++;
            var due = TimeSpan.FromSeconds(interval * n) - tick.Elapsed;
            if (due > TimeSpan.Zero) stop.Token.WaitHandle.WaitOne(due);
            if (stop.IsCancellationRequested) break;
            long nowTicks = tick.ElapsedTicks;
            double secs = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
            lastTicks = nowTicks;
            ulong[] cur = CpuTimes();
            ulong total = 0;
            for (int i = 0; i < 8; i++) total += cur[i] - last[i];
            double Pct(params int[] idx) => total == 0 ? 0 : 100.0 * idx.Sum(i => (double)(cur[i] - last[i])) / total;
            double busy = total == 0 ? 0 : 100.0 * (total - (cur[3] - last[3]) - (cur[4] - last[4])) / total;
            var row = new List<string>
            {
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                F(busy), F(Pct(0, 1)), F(Pct(2)), F(Pct(6)), F(Pct(7)),
            };
            last = cur;
            foreach (var g in groups)
            {
                long u = CgroupUsec(g.Path);
                row.Add(F(u < 0 || g.LastUsec < 0 ? 0 : 100.0 * (u - g.LastUsec) / (secs * 1e6 * ncpu)));
                g.LastUsec = u;
            }
            var (appRss, appThreads, _) = Procs(groups[0].Path);
            var (_, _, dbConns) = Procs(groups[1].Path, "conduit");
            var (nginxRss, _, _) = Procs(groups[2].Path);
            var appStat = MemStat(groups[0].Path);
            var dbStat = MemStat(groups[1].Path);
            row.Add(F(appRss / 1024.0));
            row.Add(F(appStat.GetValueOrDefault("anon") / 1048576.0));
            row.Add(appThreads.ToString(CultureInfo.InvariantCulture));
            row.Add(F((dbStat.GetValueOrDefault("anon") + dbStat.GetValueOrDefault("shmem")) / 1048576.0));
            row.Add(dbConns.ToString(CultureInfo.InvariantCulture));
            row.Add(F(nginxRss / 1024.0));
            var mem = MemInfo();
            row.Add(F((mem.GetValueOrDefault("MemTotal") - mem.GetValueOrDefault("MemAvailable")) / 1024.0));
            row.Add(SockstatTcp().ToString(CultureInfo.InvariantCulture));
            row.Add(File.ReadAllText("/proc/loadavg").Split(' ')[0]);
            w.WriteLine(string.Join(',', row));
        }
        return 0;
    }

    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static ulong[] CpuTimes()
    {
        string line = File.ReadLines("/proc/stat").First();
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var v = new ulong[8];
        for (int i = 0; i < 8 && i + 1 < f.Length; i++) v[i] = ulong.Parse(f[i + 1], CultureInfo.InvariantCulture);
        return v;
    }

    static long CgroupUsec(string path)
    {
        try
        {
            foreach (string l in File.ReadLines(Path.Combine(path, "cpu.stat")))
                if (l.StartsWith("usage_usec ", StringComparison.Ordinal)) return long.Parse(l[11..], CultureInfo.InvariantCulture);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return -1;
    }

    static Dictionary<string, long> MemStat(string path)
    {
        var d = new Dictionary<string, long>();
        try
        {
            foreach (string l in File.ReadLines(Path.Combine(path, "memory.stat")))
            {
                int sp = l.IndexOf(' ', StringComparison.Ordinal);
                if (sp > 0 && long.TryParse(l.AsSpan(sp + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) d[l[..sp]] = v;
            }
        }
        catch (IOException) { }
        return d;
    }

    /// <summary>Sum of VmRSS (kB) and threads of the cgroup's processes, and how many have <paramref name="cmdline"/> in their command line.</summary>
    static (long RssKb, int Threads, int Matching) Procs(string cgroup, string? cmdline = null)
    {
        long rss = 0;
        int threads = 0, matching = 0;
        IEnumerable<string> pids;
        try { pids = File.ReadAllLines(Path.Combine(cgroup, "cgroup.procs")); }
        catch (IOException) { return (0, 0, 0); }
        // A slice keeps its processes in its services' cgroups.
        if (!File.Exists(Path.Combine(cgroup, "cgroup.procs")) || !pids.Any())
            pids = Directory.Exists(cgroup) ? Directory.GetDirectories(cgroup).SelectMany(d =>
            {
                try { return File.ReadAllLines(Path.Combine(d, "cgroup.procs")); } catch (IOException) { return []; }
            }) : [];
        foreach (string pid in pids)
        {
            try
            {
                foreach (string l in File.ReadLines($"/proc/{pid}/status"))
                {
                    if (l.StartsWith("VmRSS:", StringComparison.Ordinal)) rss += long.Parse(l[6..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
                    else if (l.StartsWith("Threads:", StringComparison.Ordinal)) threads += int.Parse(l[8..].Trim(), CultureInfo.InvariantCulture);
                }
                if (cmdline != null && File.ReadAllText($"/proc/{pid}/cmdline").Contains(cmdline, StringComparison.Ordinal)) matching++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (rss, threads, matching);
    }

    static Dictionary<string, long> MemInfo()
    {
        var d = new Dictionary<string, long>();
        foreach (string l in File.ReadLines("/proc/meminfo"))
        {
            var f = l.Split(':', 2);
            if (f.Length == 2 && long.TryParse(f[1].Trim().Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) d[f[0]] = v;
        }
        return d;
    }

    static long SockstatTcp()
    {
        foreach (string l in File.ReadLines("/proc/net/sockstat"))
            if (l.StartsWith("TCP: inuse ", StringComparison.Ordinal)) return long.Parse(l[11..].Split(' ')[0], CultureInfo.InvariantCulture);
        return 0;
    }

    // ---------------------------------------------------------------- prewarm

    /// <summary>Reads the database files once, so a disk made from an image has every block fetched before a run.</summary>
    static int Prewarm()
    {
        string root = ContestOfBox() == "multicore" ? "/var/lib/postgresql" : "/opt/twelve/seed";
        var sw = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        long bytes = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
        {
            var buf = new byte[1 << 20];
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                int r;
                while ((r = s.Read(buf)) > 0) Interlocked.Add(ref bytes, r);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
        File.WriteAllText("/proc/sys/vm/drop_caches", "3");
        Console.WriteLine($"prewarm: read {bytes / 1048576} MB in {files.Count} files under {root} in {sw.Elapsed.TotalSeconds:0} s");
        return 0;
    }

    // ---------------------------------------------------------------- k6

    /// <summary>
    /// Summarizes one k6 run's `--out csv` file: request count, failures (http_req_failed) and http_req_duration as a
    /// histogram of 3 significant digits in µs, overall and per request name, so boxes merge exactly.
    /// </summary>
    static int K6Sum(string path)
    {
        using var fs = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        using var r = new StreamReader(s, Encoding.UTF8, false, 1 << 16);
        var head = r.ReadLine()!.Split(',');
        int iMetric = Array.IndexOf(head, "metric_name"), iValue = Array.IndexOf(head, "metric_value"), iName = Array.IndexOf(head, "name"),
            iTime = Array.IndexOf(head, "timestamp");
        var all = new Hist();
        var byName = new Dictionary<string, Hist>();
        long first = long.MaxValue, lastT = 0;
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            bool dur = line.StartsWith("http_req_duration,", StringComparison.Ordinal);
            bool failed = !dur && line.StartsWith("http_req_failed,", StringComparison.Ordinal);
            if (!dur && !failed) continue;
            var f = SplitCsv(line);
            string name = iName >= 0 && iName < f.Count ? f[iName] : "";
            if (!byName.TryGetValue(name, out var h)) byName[name] = h = new Hist();
            double v = double.Parse(f[iValue], CultureInfo.InvariantCulture);
            if (dur)
            {
                all.Add(v);
                h.Add(v);
                long t = long.Parse(f[iTime], CultureInfo.InvariantCulture);
                first = Math.Min(first, t);
                lastT = Math.Max(lastT, t);
            }
            else if (v != 0) { all.Failed++; h.Failed++; }
        }
        var o = all.ToJson();
        o["first_s"] = first == long.MaxValue ? 0 : first;
        o["last_s"] = lastT;
        var names = new JsonObject();
        foreach (var (k, h) in byName) names[k] = h.ToJson();
        o["by_name"] = names;
        Console.WriteLine(o.ToJsonString());
        _ = iMetric;
        return 0;
    }

    static List<string> SplitCsv(string line)
    {
        var f = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (char ch in line)
        {
            if (q) { if (ch == '"') q = false; else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { f.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        f.Add(sb.ToString());
        return f;
    }

    // ---------------------------------------------------------------- pg_stat_statements (profiling runs only)

    /// <summary>
    /// pg_stat_statements needs shared_preload_libraries, a server setting the rules don't give entries: it's for
    /// profiling runs only, never for a score. `on` adds it (Postgres restarts), `off` takes it out again.
    /// </summary>
    static int PgStat(string what)
    {
        const string Conf = "/etc/postgresql/18/main/conf.d/95-cbench-pgss.conf";
        string Psql(string db, string sql) => Sh("runuser", "-u", "postgres", "--", "psql", "-X", "-q", "-At", "-d", db, "-c", sql);
        switch (what)
        {
            case "on":
                File.WriteAllText(Conf, "shared_preload_libraries = 'pg_stat_statements'\npg_stat_statements.track = all\ntrack_io_timing = on\n");
                Sh("systemctl", "restart", "postgresql@18-main");
                Thread.Sleep(2000);
                Psql("conduit_seed", "CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
                return 0;
            case "off":
                if (File.Exists(Conf)) File.Delete(Conf);
                Sh("systemctl", "restart", "postgresql@18-main");
                return 0;
            case "reset":
                Psql("conduit", "CREATE EXTENSION IF NOT EXISTS pg_stat_statements; SELECT pg_stat_statements_reset()");
                return 0;
            case "dump":
                Console.WriteLine(Psql("conduit",
                    "SELECT round(total_exec_time::numeric, 0) AS total_ms, calls, round(mean_exec_time::numeric, 3) AS mean_ms, " +
                    "round((100 * total_exec_time / sum(total_exec_time) OVER ())::numeric, 1) AS pct, rows, shared_blks_hit, shared_blks_read, " +
                    "left(regexp_replace(query, '\\s+', ' ', 'g'), 160) FROM pg_stat_statements WHERE dbid = (SELECT oid FROM pg_database WHERE datname = 'conduit') " +
                    "ORDER BY total_exec_time DESC LIMIT 40"));
                return 0;
            default:
                return Usage();
        }
    }

    // ---------------------------------------------------------------- publish (on the Mac)

    /// <summary>This tool for linux-x64, self-contained in one file, for the boxes.</summary>
    public static string Publish()
    {
        string outDir = Path.Combine(Paths.State, "agent");
        string project = Path.Combine(Paths.RepoRoot(), "benchmarks", "CodeFusion.ContestBench");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dotnet = File.Exists(Path.Combine(home, ".local/dotnet/dotnet")) ? Path.Combine(home, ".local/dotnet/dotnet") : "dotnet";
        Shell.Check(dotnet, ["publish", project, "-c", "Release", "-r", "linux-x64", "--self-contained", "-p:PublishSingleFile=true",
            "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", outDir, "-v", "q", "-nologo", "-m:2"]);
        return Path.Combine(outDir, "cbench");
    }
}

/// <summary>Latencies in µs at 3 significant digits: exact enough for p95/p99 and merges by adding counts.</summary>
internal sealed class Hist
{
    public readonly SortedDictionary<long, long> Counts = [];
    public long Count;
    public long Failed;

    public void Add(double ms) => Add(Key((long)Math.Round(ms * 1000)), 1);

    public void Add(long key, long n)
    {
        Counts[key] = Counts.GetValueOrDefault(key) + n;
        Count += n;
    }

    static long Key(long us)
    {
        if (us < 1000) return us;
        long p = (long)Math.Pow(10, Math.Floor(Math.Log10(us)) - 2);
        return (long)Math.Round(us / (double)p) * p;
    }

    public double PercentileMs(double q)
    {
        if (Count == 0) return 0;
        long rank = (long)Math.Ceiling(q * Count), seen = 0;
        foreach (var (k, n) in Counts)
        {
            seen += n;
            if (seen >= rank) return k / 1000.0;
        }
        return Counts.Keys.Last() / 1000.0;
    }

    public JsonObject ToJson()
    {
        var h = new JsonObject();
        foreach (var (k, n) in Counts) h[k.ToString(CultureInfo.InvariantCulture)] = n;
        return new JsonObject { ["count"] = Count, ["failed"] = Failed, ["hist"] = h };
    }

    public static Hist FromJson(JsonNode n)
    {
        var x = new Hist { Failed = (long)n["failed"]! };
        foreach (var (k, v) in n["hist"]!.AsObject()) x.Add(long.Parse(k, CultureInfo.InvariantCulture), (long)v!);
        return x;
    }

    public void Merge(Hist o)
    {
        foreach (var (k, n) in o.Counts) Add(k, n);
        Failed += o.Failed;
    }
}
