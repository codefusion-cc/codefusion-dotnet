// The TechEmpower-shaped rig on Google Cloud: an app VM, a Postgres VM and a load VM (wrk), as TFB's Citrine runs
// them, and the runs that compare CodeFusion with TFB's own entries built from TFB's repository at a pinned commit.
// benchmarks/CodeFusion.TechEmpower/README.md says why each shape, and how a run is read.
//   dotnet run scripts/TechEmpowerRig.cs -- create                     the VMs (labelled purpose=world-perf), from the images if there are any
//   dotnet run scripts/TechEmpowerRig.cs -- setup                      Docker, wrk, the .NET SDK, perf, TFB's repository; Postgres started
//   dotnet run scripts/TechEmpowerRig.cs -- push [<dir> <entry>]       this worktree's files (or another tree's) to the app VM, and
//                                                                      CodeFusion's image built (tfb/codefusion, or tfb/<entry>)
//   dotnet run scripts/TechEmpowerRig.cs -- build <entry>...           a TFB entry's image built on the app VM (see Entries below)
//   dotnet run scripts/TechEmpowerRig.cs -- bench <entry> <test>...    TFB's sequence for each test: primer, warmup, every level
//   dotnet run scripts/TechEmpowerRig.cs -- gate <entry> <seconds> <test>...  a CodeFusion entry under sustained load: GC counts and
//                                                                      allocation rate (TFB_ALLOC_TRACE=1: and what allocates, by type)
//   dotnet run scripts/TechEmpowerRig.cs -- profile <entry> <test> <connections>  perf (with .NET's perf map), system calls and
//                                                                      context switches per request, under steady load
//   dotnet run scripts/TechEmpowerRig.cs -- quick <entry> <test> <connections> [VAR=value...]  one level for 15 s after a
//                                                                      10 s warmup, with the server's environment set, for sweeps
//   dotnet run scripts/TechEmpowerRig.cs -- ssh <vm> <command>         a command on app, db or load
//   dotnet run scripts/TechEmpowerRig.cs -- images                     machine images of the set-up VMs, so the next create skips setup
//   dotnet run scripts/TechEmpowerRig.cs -- delete                     every VM of the rig (the images stay; `delete images` removes them)
//   dotnet run scripts/TechEmpowerRig.cs -- status                     the rig's VMs and images
// Results land in artifacts/techempower/<entry>/<test>.json and .txt (wrk's own output, every level).
#:property PublishAot=false
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// the GCP project: TFB_PROJECT, else gcloud's configured one (never written into the repository)
string Project = Environment.GetEnvironmentVariable("TFB_PROJECT") is { Length: > 0 } p ? p : Capture("gcloud", ["config", "get-value", "project"]).Trim();
if (Project.Length == 0) { Console.Error.WriteLine("set TFB_PROJECT or gcloud's project"); return 2; }
const string Zone = "europe-west1-b";
const string Labels = "purpose=world-perf,owner=codefusion";
// TechEmpower/FrameworkBenchmarks master on 2026-03-23, the newest commit when the rig was built
const string TfbCommit = "57d92fbec6f8fd7431bc77326dd0484e60c96e20";
// The SDK global.json pins
const string DotnetSdk = "10.0.402";

// The app VM is what is measured; wrk gets twice its cores, so it is never what limits a run. The project's quotas
// are 32 vCPUs in all regions and 24 of C4 per region: app C4 8 (4 Emerald Rapids cores with their hyperthreads),
// Postgres C4 8, and wrk on an Intel N2 of 16. With larger quotas, TFB_RIG=full makes it app 16, Postgres 32 and
// wrk 32, all C4, so the database is never the limit either (on Citrine all three are equal, and the database limits
// the top of queries and updates).
bool full = Environment.GetEnvironmentVariable("TFB_RIG") == "full";
var vms = new Dictionary<string, (string Name, string Shape)>
{
    ["app"] = ("tfb-app", full ? "c4-highcpu-16" : "c4-highcpu-8"),
    ["db"] = ("tfb-db", full ? "c4-highcpu-32" : "c4-highcpu-8"),
    ["load"] = ("tfb-load", full ? "c4-highcpu-32" : "n2-highcpu-16"),
};
// TFB_ROLES=app,load: only those VMs, for the tests that need no database (plaintext, json), when the quota is shared
if (Environment.GetEnvironmentVariable("TFB_ROLES") is { Length: > 0 } roles)
    foreach (string role in vms.Keys.ToList())
        if (!roles.Split(',').Contains(role)) vms.Remove(role);

// TFB's entries this rig builds: the directory under frameworks/, the dockerfile, the tests it implements, and its
// queries and updates URLs from its benchmark_config.json (TFB appends the count). All of them listen on 8080.
const string Q = "/queries?queries=", U = "/updates?queries=";
var entries = new Dictionary<string, (string Dir, string Dockerfile, string[] Tests, string Queries, string Updates)>
{
    ["aspnetcore"] = ("CSharp/aspnetcore", "aspnetcore.dockerfile", AllTests(), "/queries/", "/updates/"),
    ["aspnetcore-aot"] = ("CSharp/aspnetcore", "aspnetcore-aot.dockerfile", AllTests(), "/queries/", "/updates/"),
    ["aspnetcore-minimal"] = ("CSharp/aspnetcore", "aspnetcore-minimal.dockerfile", AllTests(), "/queries/", "/updates/"),
    ["appmpower-odbc-pg"] = ("CSharp/appmpower", "appmpower-odbc-pg.dockerfile", ["db", "queries", "fortunes", "updates"], "/queries?c=", "/updates?c="),
    ["may-minihttp"] = ("Rust/may-minihttp", "may-minihttp.dockerfile", AllTests(), "/queries?q=", "/updates?q="),
    ["xitca-web-barebone"] = ("Rust/xitca-web", "xitca-web-barebone.dockerfile", AllTests(), "/queries?q=", "/updates?q="),
    ["h2o"] = ("C/h2o", "h2o.dockerfile", AllTests(), Q, U),
    ["libreactor"] = ("C/libreactor", "libreactor.dockerfile", ["plaintext", "json"], "", ""),
    ["libreactor-server"] = ("C/libreactor", "libreactor-server.dockerfile", ["plaintext", "json"], "", ""),
    ["codefusion"] = ("", "", AllTests(), Q, U),
    // a CodeFusion tree with perf/http-transport merged in, on its event loop
    ["codefusion-eventloop"] = ("", "", AllTests(), Q, U),
    // a CodeFusion tree with perf/data-batched-updates merged in
    ["codefusion-batch"] = ("", "", AllTests(), Q, U),
    // perf/techempower with perf/data-batched-updates and perf/pg-pipelining merged in
    ["codefusion-pg"] = ("", "", AllTests(), Q, U),
    // that and perf/http-transport, on the event loop: every branch at once
    ["codefusion-all"] = ("", "", AllTests(), Q, U),
    // that, with a change under test: compared with codefusion-all in alternating rounds
    ["codefusion-cache"] = ("", "", AllTests(), Q, U),
    // HTTP comparisons: perf/techempower on a transport branch (A) and on a branch built on it (B), event loop
    ["cf-pool-a"] = ("", "", AllTests(), Q, U),
    ["cf-pool-b"] = ("", "", AllTests(), Q, U),
    ["cf-old"] = ("", "", AllTests(), Q, U),
    ["cf-new"] = ("", "", AllTests(), Q, U),
};
// what an entry's container gets besides TFB's own settings
var environment = new Dictionary<string, string>
{
    ["codefusion-eventloop"] = "-e CODEFUSION_TRANSPORT=eventloop",
    // the review fixes' cost: the data branches before and after them, each with the transport, four writers
    ["cf-old"] = "-e DB_WRITERS=4",
    ["cf-new"] = "-e DB_WRITERS=4",
};

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, ".."));
string results = Path.Combine(root, "artifacts", "techempower");
string KnownHosts = Path.Combine(results, "known_hosts");
Directory.CreateDirectory(results);

switch (args)
{
    case ["create"]:
        return Create();
    case ["setup"]:
        return Setup();
    case ["push"]:
        return Push(root, "codefusion");
    case ["push", var dir, var image]:
        return Push(Path.GetFullPath(dir), image);
    case ["build", .. var names] when names.Length > 0:
        // every one is tried; the exit code counts the failures
        return names.Count(n => Build(n) != 0);
    case ["bench", var entry, .. var tests] when tests.Length > 0:
        return tests.Count(t => Bench(entry, t) != 0);
    case ["gate", var entry, var seconds, .. var tests] when tests.Length > 0:
        return Gate(entry, int.Parse(seconds, CultureInfo.InvariantCulture), tests);
    case ["profile", var entry, var test, var connections]:
        return Profile(entry, test, int.Parse(connections, CultureInfo.InvariantCulture));
    case ["quick", var entry, var test, var connections, .. var vars]:
        return Quick(entry, test, int.Parse(connections, CultureInfo.InvariantCulture), vars);
    case ["ssh", var vm, .. var command] when command.Length > 0:
        return Ssh(vm, string.Join(' ', command));
    case ["images"]:
        return Images();
    case ["delete"]:
        return Delete(images: false);
    case ["delete", "images"]:
        return Delete(images: true);
    case ["status"]:
        return Status();
    default:
        Console.Error.WriteLine("usage: see the top of scripts/TechEmpowerRig.cs");
        return 2;
}

static string[] AllTests() => ["plaintext", "json", "db", "queries", "fortunes", "updates"];

// ---------- the VMs ----------

int Create()
{
    // one compact placement: the three VMs as near each other as the zone allows
    Gcloud(["compute", "resource-policies", "create", "group-placement", "tfb-compact", "--collocation=collocated", $"--region={Zone[..^2]}"], quiet: true);
    var running = new List<Process>();
    foreach (var (role, (name, shape)) in vms)
    {
        string? image = ImageOf(name);
        var a = new List<string>
        {
            "compute", "instances", "create", name, $"--machine-type={shape}", $"--labels={Labels},role={role}",
            "--resource-policies=tfb-compact", "--maintenance-policy=TERMINATE", "--network-interface=nic-type=GVNIC",
            $"--boot-disk-type={(shape.StartsWith("c4-", StringComparison.Ordinal) ? "hyperdisk-balanced" : "pd-balanced")}", "--boot-disk-size=60GB",
            // the backstop: GCP deletes the VM after this long, whatever happens to this run
            "--max-run-duration=8h", "--instance-termination-action=DELETE",
        };
        if (image is not null) a.Add($"--source-machine-image={image}");
        else a.AddRange(["--image-family=ubuntu-2404-lts-amd64", "--image-project=ubuntu-os-cloud"]);
        running.Add(Start("gcloud", [.. a, $"--project={Project}", $"--zone={Zone}", "--quiet"]));
    }
    int failed = 0;
    foreach (var proc in running)
    {
        proc.WaitForExit();
        if (proc.ExitCode != 0) failed++;
    }
    if (failed > 0)
    {
        // a rig with a VM missing is no rig: the ones made go too, rather than bill on
        Console.Error.WriteLine($"{failed} of {vms.Count} VMs failed to start: deleting the rest");
        Delete(images: false);
        return 1;
    }
    File.Delete(KnownHosts);
    // sshd needs a few seconds after the VM reports running
    foreach (string vm in vms.Keys)
        for (int i = 0; i < 30 && Ssh(vm, "true", quiet: true) != 0; i++) Thread.Sleep(5000);
    return 0;
}

string? ImageOf(string vm)
{
    string list = Capture("gcloud", ["compute", "machine-images", "list", $"--filter=name={vm}-image", "--format=value(name)", $"--project={Project}"]);
    return list.Trim().Length > 0 ? list.Trim() : null;
}

int Images()
{
    // stopped for a consistent disk; started again afterwards
    foreach (var (_, (name, _)) in vms)
    {
        Gcloud(["compute", "machine-images", "delete", $"{name}-image", "--quiet"], quiet: true, zonal: false);
        if (Gcloud(["compute", "instances", "stop", name]) != 0) return 1;
        // machine images take no labels: they are found by name (<vm>-image)
        if (Gcloud(["compute", "machine-images", "create", $"{name}-image", $"--source-instance={name}", $"--source-instance-zone={Zone}"], zonal: false) != 0) return 1;
        if (Gcloud(["compute", "instances", "start", name]) != 0) return 1;
    }
    return 0;
}

int Delete(bool images)
{
    int failed = 0;
    if (images)
    {
        foreach (var (_, (name, _)) in vms)
            if (ImageOf(name) is { } image && Gcloud(["compute", "machine-images", "delete", image, "--quiet"], zonal: false) != 0) failed++;
        return failed;
    }
    var running = vms.Values.Select(v => Start("gcloud", ["compute", "instances", "delete", v.Name, "--quiet", $"--project={Project}", $"--zone={Zone}"])).ToList();
    foreach (var p in running)
    {
        p.WaitForExit();
        if (p.ExitCode != 0) failed++;
    }
    Gcloud(["compute", "resource-policies", "delete", "tfb-compact", $"--region={Zone[..^2]}", "--quiet"], quiet: true, zonal: false);
    return failed;
}

int Status()
{
    Gcloud(["compute", "instances", "list", "--filter=labels.purpose=world-perf", "--format=table(name,machineType.basename(),status,creationTimestamp)"], zonal: false);
    Gcloud(["compute", "machine-images", "list", "--filter=name~^tfb-", "--format=table(name,totalStorageBytes,creationTimestamp)"], zonal: false);
    return 0;
}

// ---------- setup ----------

int Setup()
{
    // what every VM needs: the kernel's limits as TFB's hosts raise them, Docker, perf and sysstat
    const string Common =
        "set -e; export DEBIAN_FRONTEND=noninteractive; " +
        "sudo apt-get -qq update && sudo apt-get -qq install -y docker.io git sysstat linux-tools-common linux-tools-$(uname -r) jq >/dev/null; " +
        "sudo usermod -aG docker $USER; " +
        "printf 'net.core.somaxconn=65535\\nnet.ipv4.tcp_max_syn_backlog=65535\\nnet.ipv4.ip_local_port_range=1024 65535\\nnet.ipv4.tcp_tw_reuse=1\\nnet.core.netdev_max_backlog=65535\\nkernel.perf_event_paranoid=-1\\nkernel.kptr_restrict=0\\n' | sudo tee /etc/sysctl.d/90-tfb.conf >/dev/null && sudo sysctl -q --system; " +
        "printf '* soft nofile 1048576\\n* hard nofile 1048576\\n' | sudo tee /etc/security/limits.d/90-tfb.conf >/dev/null";
    string tfb =
        $"if [ ! -d ~/tfb ]; then git init -q ~/tfb && cd ~/tfb && git remote add origin https://github.com/TechEmpower/FrameworkBenchmarks && " +
        $"git fetch -q --depth 1 origin {TfbCommit} && git checkout -q FETCH_HEAD; fi";
    var steps = new List<(string Vm, string Command)>
    {
        ("app", Common + "; " + tfb + "; " +
            // the .NET SDK, for CodeFusion's build and the diagnostics tools; dotnet-counters and dotnet-trace as global tools
            $"curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && bash /tmp/dotnet-install.sh --version {DotnetSdk} --install-dir ~/.dotnet >/dev/null && " +
            "~/.dotnet/dotnet tool update -g dotnet-counters >/dev/null && ~/.dotnet/dotnet tool update -g dotnet-trace >/dev/null && " +
            "grep -q DOTNET_ROOT ~/.bashrc || echo 'export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1' >> ~/.bashrc"),
        ("db", Common + "; " + tfb + "; " +
            // TFB's own Postgres image, run on the host's network
            "cd ~/tfb/toolset/databases/postgres && sudo docker build --network host -q -t tfb/postgres -f postgres.dockerfile . >/dev/null && " +
            "(sudo docker rm -f tfb-database >/dev/null 2>&1 || true) && " +
            "sudo docker run -d --name tfb-database --network host --shm-size=2g --restart unless-stopped tfb/postgres >/dev/null"),
        ("load", Common + "; sudo apt-get -qq install -y wrk >/dev/null; " +
            // TFB's pipeline script for plaintext
            $"curl -sSL https://raw.githubusercontent.com/TechEmpower/FrameworkBenchmarks/{TfbCommit}/toolset/wrk/pipeline.lua -o ~/pipeline.lua"),
    };
    var running = steps.Where(s => vms.ContainsKey(s.Vm)).Select(s => (s.Vm, Proc: Start("gcloud", SshArgs(s.Vm, s.Command)))).ToList();
    int failed = 0;
    foreach (var (vm, p) in running)
    {
        p.WaitForExit();
        Console.WriteLine($"setup {vm}: {(p.ExitCode == 0 ? "done" : "failed " + p.ExitCode)}");
        if (p.ExitCode != 0) failed++;
    }
    return failed;
}

// ---------- CodeFusion and the entries ----------

int Push(string root, string image)
{
    // the files git tracks, as they are in the worktree now (uncommitted edits included): nothing git doesn't track
    // leaves the machine (a .env, a key), so a new file has to be added to git (git add) before it goes
    string list = Capture("git", ["-C", root, "ls-files", "--cached"]);
    string untracked = Capture("git", ["-C", root, "ls-files", "--others", "--exclude-standard"]).Trim();
    if (untracked.Length > 0)
        Console.Error.WriteLine($"push: left out, not added to git: {untracked.Replace('\n', ' ')}");
    string listFile = Path.Combine(Path.GetTempPath(), "tfb-push-files.txt");
    File.WriteAllText(listFile, list);
    string tar = Path.Combine(Path.GetTempPath(), "codefusion-src.tgz");
    // --no-mac-metadata: no ._ files beside each file from macOS's extended attributes
    if (Run("tar", [.. OperatingSystem.IsMacOS() ? ["--no-mac-metadata"] : Array.Empty<string>(), "-czf", tar, "-C", root, "-T", listFile]) != 0) return 1;
    if (Gcloud(["compute", "scp", tar, $"{vms["app"].Name}:/tmp/codefusion-src.tgz"]) != 0) return 1;
    return Ssh("app", "rm -rf ~/codefusion && mkdir -p ~/codefusion && tar -xzf /tmp/codefusion-src.tgz -C ~/codefusion && " +
        $"cd ~/codefusion && sudo docker build --network host -q -t tfb/{image} -f benchmarks/CodeFusion.TechEmpower/codefusion.dockerfile . >/dev/null && echo built");
}

int Build(string name)
{
    if (name == "codefusion") return Push(root, name);
    if (!entries.TryGetValue(name, out var e))
    {
        Console.Error.WriteLine($"no entry {name}: one of {string.Join(", ", entries.Keys)}");
        return 2;
    }
    var sw = Stopwatch.StartNew();
    int code = Ssh("app", $"cd ~/tfb/frameworks/{e.Dir} && sudo docker build --network host -q -t tfb/{name} -f {e.Dockerfile} . >/tmp/build-{name}.log 2>&1 || (tail -30 /tmp/build-{name}.log; exit 1)");
    Console.WriteLine($"build {name}: {(code == 0 ? "done" : "failed")} in {sw.Elapsed.TotalMinutes:F1} min");
    return code;
}

/// <summary>The Postgres VM's address, or the app's own loopback when the rig has no database VM.</summary>
string DbIp() => vms.ContainsKey("db") ? InternalIp("db") : "127.0.0.1";

string InternalIp(string vm) => Capture("gcloud", ["compute", "instances", "describe", vms[vm].Name, "--format=value(networkInterfaces[0].networkIP)", $"--project={Project}", $"--zone={Zone}"]).Trim();

/// <summary>The entry's container on the app VM, on the host's network, with tfb-database naming the db VM as in TFB.</summary>
int StartEntry(string name, string dbIp)
{
    Ssh("app", "sudo docker rm -f tfb-server >/dev/null 2>&1 || true", quiet: true);
    // --pid=host and /tmp shared: perf and dotnet-counters on the host see the server's process and its perf map
    string run = $"sudo docker run -d --name tfb-server --network host --pid host -v /tmp:/tmp --add-host tfb-database:{dbIp} " +
        $"--ulimit nofile=1048576:1048576 --security-opt seccomp=unconfined -e DOTNET_PerfMapEnabled=1 {environment.GetValueOrDefault(name, "")} tfb/{name} >/dev/null";
    if (Ssh("app", run) != 0) return 1;
    // ready when it answers on 8080
    return Ssh("app", "for i in $(seq 1 120); do curl -s -o /dev/null http://127.0.0.1:8080/plaintext && exit 0; " +
        "curl -s -o /dev/null http://127.0.0.1:8080/json && exit 0; curl -s -o /dev/null http://127.0.0.1:8080/db && exit 0; sleep 1; done; " +
        "sudo docker logs --tail 30 tfb-server; exit 1");
}

string Path_(string name, string test) => test switch
{
    "plaintext" => "/plaintext",
    "json" => "/json",
    "db" => "/db",
    "queries" => entries[name].Queries,
    "fortunes" => "/fortunes",
    "updates" => entries[name].Updates,
    _ => throw new ArgumentException("no test " + test),
};

static string Accept(string test) => test switch
{
    "plaintext" => "text/plain,text/html;q=0.9,application/xhtml+xml;q=0.9,application/xml;q=0.8,*/*;q=0.7",
    "fortunes" => "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    _ => "application/json,text/html;q=0.9,application/xhtml+xml;q=0.9,application/xml;q=0.8,*/*;q=0.7",
};

/// <summary>TFB's sequence for one test (toolset/wrk/*.sh): a primer, a warmup at 512, then each level for 15 s.</summary>
int Bench(string name, string test)
{
    if (!entries.TryGetValue(name, out var e) || !e.Tests.Contains(test))
    {
        Console.Error.WriteLine($"{name} has no {test}");
        return 2;
    }
    string appIp = InternalIp("app"), dbIp = DbIp();
    if (StartEntry(name, dbIp) != 0) return 1;
    string url = $"http://{appIp}:8080{Path_(name, test)}";
    string headers = $"-H 'Host: tfb-server' -H 'Accept: {Accept(test)}' -H 'Connection: keep-alive'";
    bool queries = test is "queries" or "updates";
    string first = queries ? url + "2" : url;
    var levels = test == "plaintext" ? new[] { 256, 1024, 4096, 16384 } : queries ? new[] { 1, 5, 10, 15, 20 } : new[] { 16, 32, 64, 128, 256, 512 };
    var script = new StringBuilder("set -e; T=$(nproc); ");
    script.Append(CultureInfo.InvariantCulture, $"wrk {headers} --latency -d 5 -c 8 --timeout 8 -t 8 '{first}' >/dev/null; sleep 5; ");
    script.Append(CultureInfo.InvariantCulture, $"wrk {headers} --latency -d 15 -c 512 --timeout 8 -t $T '{first}' >/dev/null; sleep 5; ");
    foreach (int level in levels)
    {
        string target = queries ? url + level : url;
        int c = queries ? 512 : level;
        string threads = queries ? "$T" : $"$(( {c} < T ? {c} : T ))";
        string pipeline = test == "plaintext" ? " -s ~/pipeline.lua -- 16" : "";
        script.Append(CultureInfo.InvariantCulture, $"echo '=== level {level}'; wrk {headers} --latency -d 15 -c {c} --timeout 8 -t {threads} '{target}'{pipeline}; sleep 2; ");
    }
    // every second while the levels run: the server's own CPU ticks and RSS on the app VM, and each VM's busy share
    // (vmstat), which shows whether wrk or Postgres was what limited the run
    string sampler = "rm -f /tmp/stat.txt; (while sleep 1; do T=0; R=0; for P in $(sudo docker top tfb-server -eo pid | tail -n +2); do " +
        "set -- $(sudo cat /proc/$P/stat 2>/dev/null | sed 's/.*) //'); T=$((T + ${12:-0} + ${13:-0})); " +
        "R=$((R + $(sudo awk '/VmRSS/ {print $2}' /proc/$P/status 2>/dev/null || echo 0))); done; echo $(date +%s) $T $R >> /tmp/stat.txt; done) >/dev/null 2>&1 & echo $! > /tmp/sampler.pid";
    Ssh("app", sampler, quiet: true);
    foreach (string vm in vms.Keys) Ssh(vm, "pkill -x vmstat; nohup vmstat -n 1 > /tmp/vmstat.txt 2>/dev/null & true", quiet: true);
    var dotnet = name.StartsWith("aspnetcore", StringComparison.Ordinal) || name.StartsWith("appmpower", StringComparison.Ordinal) || name.StartsWith("codefusion", StringComparison.Ordinal);
    if (dotnet) StartCounters("/tmp/counters.csv");
    string output = Capture("gcloud", SshArgs("load", script.ToString()));
    Ssh("app", "kill $(cat /tmp/sampler.pid) 2>/dev/null; sudo pkill -INT -f 'dotnet-counters collect'; sleep 1; true", quiet: true);
    var busy = new Dictionary<string, double>();
    foreach (string vm in vms.Keys)
    {
        Ssh(vm, "pkill -x vmstat; true", quiet: true);
        busy[vm] = PeakBusy(Capture("gcloud", SshArgs(vm, "cat /tmp/vmstat.txt")));
    }
    string stats = Capture("gcloud", SshArgs("app", "cat /tmp/stat.txt"));
    string counters = dotnet ? Capture("gcloud", SshArgs("app", "cat /tmp/counters.csv 2>/dev/null")) : "";
    Ssh("app", "sudo docker rm -f tfb-server >/dev/null 2>&1 || true", quiet: true);

    var parsed = ParseLevels(output);
    string dir = Path.Combine(results, name);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, test + ".txt"), output + "\n=== samples (unix time, cpu seconds, rss KB)\n" + stats);
    if (counters.Length > 0) File.WriteAllText(Path.Combine(dir, test + ".counters.csv"), counters);
    var best = queries ? parsed.LastOrDefault() : parsed.OrderByDescending(l => l.Rps).FirstOrDefault();
    var (cpu, rss) = CpuAndRss(stats);
    var gc = dotnet ? GateOf(counters) : default;
    var summary = new
    {
        entry = name, test, levels = parsed, best = best?.Concurrency, rps = best?.Rps ?? 0, cpuCores = cpu, rssMb = rss,
        appBusy = busy.GetValueOrDefault("app"), dbBusy = busy.GetValueOrDefault("db"), loadBusy = busy.GetValueOrDefault("load"),
        gen0 = gc.Gen0, gen1 = gc.Gen1, gen2 = gc.Gen2, allocatedBytes = gc.Allocated, counterSeconds = gc.Seconds,
        peakAllocationRate = gc.PeakRate, bytesPerRequest = best is { Rps: > 0 } ? gc.PeakRate / best.Rps : 0,
    };
    File.WriteAllText(Path.Combine(dir, test + ".json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{name} {test}: {summary.rps:N0} req/s at {best?.Concurrency}, p99 {best?.P99}, server {cpu:F1} cores {rss:N0} MB, " +
        $"busy app {busy.GetValueOrDefault("app"):P0} db {busy.GetValueOrDefault("db"):P0} load {busy.GetValueOrDefault("load"):P0}" + (dotnet ? $", gc {gc.Gen0}/{gc.Gen1}/{gc.Gen2}, peak {gc.PeakRate / 1e6:F1} MB/s, ~{summary.bytesPerRequest:F1} B/req" : ""));
    return parsed.Count == levels.Length ? 0 : 1;
}

static List<Level> ParseLevels(string output)
{
    var list = new List<Level>();
    foreach (var block in output.Split("=== level ").Skip(1))
    {
        int level = int.Parse(block[..block.IndexOf('\n')].Trim(), CultureInfo.InvariantCulture);
        double rps = Number(block, @"Requests/sec:\s+([0-9.]+)");
        long requests = (long)Number(block, @"([0-9]+) requests in");
        long non2xx = (long)Number(block, @"Non-2xx or 3xx responses: ([0-9]+)");
        var socket = Regex.Match(block, @"Socket errors: connect ([0-9]+), read ([0-9]+), write ([0-9]+), timeout ([0-9]+)");
        long errors = socket.Success ? long.Parse(socket.Groups[1].Value, CultureInfo.InvariantCulture) + long.Parse(socket.Groups[2].Value, CultureInfo.InvariantCulture) + long.Parse(socket.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        // TFB's score: requests that were answered with no error, over the 15 s
        double score = Math.Floor((requests - non2xx - errors) / 15.0);
        list.Add(new Level(level, score, rps, Text(block, @"Latency\s+([0-9.]+[a-z]+)"), Text(block, @"\s99%\s+([0-9.]+[a-z]+)"), non2xx, errors));
    }
    return list;
}

static double Number(string s, string pattern)
{
    var m = Regex.Match(s, pattern);
    return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
}

static string Text(string s, string pattern)
{
    var m = Regex.Match(s, pattern);
    return m.Success ? m.Groups[1].Value : "";
}

/// <summary>The server's busy cores (CPU ticks, 100 a second) at its busiest 15 s, and its peak RSS.</summary>
static (double Cores, double RssMb) CpuAndRss(string stats)
{
    var rows = stats.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Split(' ')).Where(p => p.Length == 3)
        .Select(p => (T: double.Parse(p[0], CultureInfo.InvariantCulture), Cpu: double.Parse(p[1], CultureInfo.InvariantCulture), Rss: double.Parse(p[2], CultureInfo.InvariantCulture)))
        .ToList();
    if (rows.Count < 2) return (0, 0);
    // the busiest stretch: the largest CPU-seconds per second between two samples 15 s apart
    double cores = 0;
    for (int i = 0; i + 15 < rows.Count; i++)
        cores = Math.Max(cores, (rows[i + 15].Cpu - rows[i].Cpu) / 100 / (rows[i + 15].T - rows[i].T));
    return (cores, rows.Max(r => r.Rss) / 1024);
}

/// <summary>
/// One entry's test under steady load at <paramref name="connections"/> (queries and updates at 20 per request): after
/// 15 s of warmup, 10 s of system call and context switch counts (perf stat), then 10 s of perf record at 999 Hz with
/// call graphs, whose report by symbol (JIT code named from the perf map) lands next to the bench results.
/// </summary>
int Profile(string name, string test, int connections)
{
    string appIp = InternalIp("app"), dbIp = DbIp();
    // .NET's W^X maps JIT code twice, and perf sees only the anonymous file: off while profiling, so the perf map names it
    string kept = environment.GetValueOrDefault(name, "");
    environment[name] = kept + " -e DOTNET_EnableWriteXorExecute=0";
    try
    {
        if (StartEntry(name, dbIp) != 0) return 1;
    }
    finally
    {
        environment[name] = kept;
    }
    bool queries = test is "queries" or "updates";
    string url = $"http://{appIp}:8080{Path_(name, test)}{(queries ? "20" : "")}";
    string headers = $"-H 'Host: tfb-server' -H 'Accept: {Accept(test)}' -H 'Connection: keep-alive'";
    string pipeline = test == "plaintext" ? " -s ~/pipeline.lua -- 16" : "";
    var load = Start("gcloud", SshArgs("load", $"wrk {headers} --latency -d 45 -c {connections} --timeout 8 -t $(nproc) '{url}'{pipeline} > /tmp/profile-wrk.txt"));
    Thread.Sleep(15_000);
    // the server's process: the newest one in the container
    const string Pid = "P=$(sudo docker top tfb-server -eo pid | tail -n +2 | sort -n | tail -1)";
    string stat = Capture("gcloud", SshArgs("app", $"{Pid}; sudo perf stat -e raw_syscalls:sys_enter,context-switches,cpu-migrations -p $P -- sleep 10 2>&1 | grep -E 'raw_syscalls|context|migrations|elapsed'"));
    string syscalls = Capture("gcloud", SshArgs("app", $"{Pid}; sudo perf trace -s -p $P -- sleep 2 2>&1 | grep -E '^ +[a-z_0-9]+ +[0-9]+' | sort -k2 -n -r | head -12"));
    string report = Capture("gcloud", SshArgs("app", $"{Pid}; sudo perf record -q -F 999 -g -p $P -o /tmp/perf.data -- sleep 10 2>/dev/null; " +
        "sudo perf report -i /tmp/perf.data --stdio --no-children -g none --sort dso,sym --percent-limit 0.3 2>/dev/null | grep -E '^ +[0-9]' | head -70; " +
        "echo '=== by dso'; sudo perf report -i /tmp/perf.data --stdio --no-children -g none --sort dso 2>/dev/null | grep -E '^ +[0-9]' | head -12"));
    load.WaitForExit();
    string wrk = Capture("gcloud", SshArgs("load", "cat /tmp/profile-wrk.txt"));
    Ssh("app", "sudo docker rm -f tfb-server >/dev/null 2>&1 || true", quiet: true);
    double rps = Number(wrk, @"Requests/sec:\s+([0-9.]+)");
    double sys = Number(stat.Replace(",", "", StringComparison.Ordinal), @"([0-9]+)\s+raw_syscalls:sys_enter");
    double cs = Number(stat.Replace(",", "", StringComparison.Ordinal), @"([0-9]+)\s+context-switches");
    string summary = $"{name} {test} c={connections}: {rps:N0} req/s, {sys / 10 / Math.Max(1, rps):F2} system calls and {cs / 10 / Math.Max(1, rps):F3} context switches per request";
    string dir = Path.Combine(results, name);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, $"profile-{test}.txt"), summary + "\n\n=== perf stat (10 s)\n" + stat + "\n=== system calls (perf trace, 2 s)\n" + syscalls + "\n=== perf report\n" + report + "\n=== wrk\n" + wrk);
    Console.WriteLine(summary);
    return 0;
}

/// <summary>One level of a test with the server's environment set (<paramref name="vars"/>, VAR=value), for sweeps:
/// a 10 s warmup, then 15 s measured. Appends a line to artifacts/techempower/quick.txt.</summary>
int Quick(string name, string test, int connections, string[] vars)
{
    string appIp = InternalIp("app"), dbIp = DbIp();
    string before = environment.GetValueOrDefault(name, "");
    environment[name] = before + string.Concat(vars.Select(v => $" -e {v}"));
    try
    {
        if (StartEntry(name, dbIp) != 0) return 1;
    }
    finally
    {
        environment[name] = before;
    }
    bool queries = test is "queries" or "updates";
    string url = $"http://{appIp}:8080{Path_(name, test)}{(queries ? "20" : "")}";
    string headers = $"-H 'Host: tfb-server' -H 'Accept: {Accept(test)}' -H 'Connection: keep-alive'";
    string pipeline = test == "plaintext" ? " -s ~/pipeline.lua -- 16" : "";
    string wrk = Capture("gcloud", SshArgs("load", $"wrk {headers} -d 10 -c {connections} --timeout 8 -t $(nproc) '{url}'{pipeline} >/dev/null; " +
        $"wrk {headers} --latency -d 15 -c {connections} --timeout 8 -t $(nproc) '{url}'{pipeline}"));
    Ssh("app", "sudo docker rm -f tfb-server >/dev/null 2>&1 || true", quiet: true);
    var level = ParseLevels("=== level " + connections + "\n" + wrk).FirstOrDefault();
    string line = $"{DateTime.UtcNow:u} {name} {test} c={connections} {string.Join(' ', vars)}: {level?.Rps ?? 0:N0} req/s, p99 {level?.P99}, errors {level?.SocketErrors ?? 0}, non-2xx {level?.Non2xx ?? 0}";
    Directory.CreateDirectory(results);
    File.AppendAllText(Path.Combine(results, "quick.txt"), line + "\n");
    Console.WriteLine(line);
    return 0;
}

/// <summary>A VM's busiest 15 s from vmstat's lines: 1 - idle, as a fraction.</summary>
static double PeakBusy(string vmstat)
{
    var idle = new List<double>();
    int column = -1;
    foreach (string line in vmstat.Split('\n'))
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // the header names the columns: procps 4 adds gu after st
        if (Array.IndexOf(p, "id") is >= 0 and var at) column = at;
        else if (column >= 0 && p.Length > column && double.TryParse(p[column], NumberStyles.Float, CultureInfo.InvariantCulture, out double id)) idle.Add(id);
    }
    double best = 0;
    for (int i = 1; i + 15 <= idle.Count; i++) best = Math.Max(best, 1 - idle.Skip(i).Take(15).Average() / 100);
    return best;
}

/// <summary>dotnet-counters on the server's process, as root (the container's process is root's), into <paramref name="file"/>.</summary>
void StartCounters(string file) =>
    Ssh("app", $"sudo rm -f {file}; P=$(pgrep -f -n 'dotnet .*[.]dll|^/app/[A-Za-z]+$'); " +
        $"sudo nohup env DOTNET_ROOT=$HOME/.dotnet $HOME/.dotnet/tools/dotnet-counters collect -p $P --refresh-interval 1 --format csv -o {file} --counters System.Runtime >/dev/null 2>&1 &", quiet: true);

/// <summary>
/// The allocation gate: a CodeFusion entry under each test's load for a 30 s warmup, then <paramref name="seconds"/>
/// measured, on the same connections. The app reports its own totals every 10 s (CODEFUSION_GC_REPORT: GC.GetTotalAllocatedBytes and the
/// collection counts, written without allocating), and the gate takes the difference between the first and the last
/// report inside the measured stretch, so no diagnostics session runs in the process to allocate on its own. Zero
/// collections and zero bytes pass. With TFB_ALLOC_TRACE=1 the app also names what allocates, by type.
/// </summary>
int Gate(string name, int seconds, string[] tests)
{
    bool trace = Environment.GetEnvironmentVariable("TFB_ALLOC_TRACE") == "1";
    string before = environment.GetValueOrDefault(name, "");
    environment[name] = before + " -e CODEFUSION_GC_REPORT=1" + (trace ? " -e CODEFUSION_ALLOC_TRACE=1" : "");
    string appIp = InternalIp("app"), dbIp = DbIp();
    int failed = 0;
    var lines = new List<string>();
    try
    {
        foreach (string test in tests)
        {
            if (StartEntry(name, dbIp) != 0) return 1;
            bool queries = test is "queries" or "updates";
            string url = $"http://{appIp}:8080{Path_(name, test)}{(queries ? "20" : "")}";
            string headers = $"-H 'Host: tfb-server' -H 'Accept: {Accept(test)}' -H 'Connection: keep-alive'";
            int c = test == "plaintext" ? 1024 : 512;
            string pipeline = test == "plaintext" ? " -s ~/pipeline.lua -- 16" : "";
            // one wrk run, so the measured stretch keeps the warmup's connections (a connection accepted costs a
            // Socket and its handle, about 360 B, which is the connection's, not a request's): 30 s of warmup, the
            // stretch, and 11 s more, so a report lands after it
            long started = long.Parse(Capture("gcloud", SshArgs("app", "date +%s%3N")).Trim(), CultureInfo.InvariantCulture);
            string wrk = Capture("gcloud", SshArgs("load", $"wrk {headers} --latency -d {30 + seconds + 11} -c {c} --timeout 8 -t $(nproc) '{url}'{pipeline}"));
            long from = started + 30_000, to = from + seconds * 1000L + 10_500;
            string logs = Capture("gcloud", SshArgs("app", "sudo docker logs tfb-server 2>&1 | grep -E '^gc |allocations in'"));
            Ssh("app", "sudo docker rm -f tfb-server >/dev/null 2>&1 || true", quiet: true);
            // gc <unix ms> <bytes> <gen0> <gen1> <gen2>, and in some builds more counters after
            var reports = logs.Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 6 && p[0] == "gc")
                .Select(p => p.Skip(1).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray()).Where(r => r[0] >= from && r[0] <= to).ToList();
            string dir = Path.Combine(results, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"gate-{test}.txt"), wrk + "\n=== reports (unix ms, bytes, gen0, gen1, gen2)\n" + logs);
            if (reports.Count < 2)
            {
                lines.Add($"{test}: NO DATA ({reports.Count} reports in the window)");
                Console.WriteLine(lines[^1]);
                failed++;
                continue;
            }
            var (first, last) = (reports[0], reports[^1]);
            double span = (last[0] - first[0]) / 1000.0;
            long bytes = last[1] - first[1], g0 = last[2] - first[2], g1 = last[3] - first[3], g2 = last[4] - first[4];
            double rps = Number(wrk, @"Requests/sec:\s+([0-9.]+)");
            bool pass = bytes == 0 && g0 == 0 && g1 == 0 && g2 == 0;
            if (!pass) failed++;
            string line = $"{test}: {(pass ? "PASS" : "FAIL")} over {span:F0} s at {rps:N0} req/s: gen0 {g0} gen1 {g1} gen2 {g2}, {bytes:N0} B allocated " +
                $"({bytes / Math.Max(1, span):N0} B/s, {bytes / Math.Max(1, span * rps):F4} B/request)" +
                (first.Length > 5 ? $", counter after the GC counts {last[5] - first[5]:N0}" : "");
            Console.WriteLine(line);
            lines.Add(line);
            if (trace) foreach (var t in logs.Split('\n').Where(l => l.StartsWith("allocations in", StringComparison.Ordinal)).TakeLast(3)) lines.Add("  " + t);
        }
    }
    finally
    {
        environment[name] = before;
    }
    File.WriteAllLines(Path.Combine(results, name, "gate.txt"), lines);
    return failed;
}

/// <summary>The GC counts and bytes allocated over the counters' file: last value minus first.</summary>
static (long Gen0, long Gen1, long Gen2, long Allocated, int Seconds, double PeakRate) GateOf(string csv)
{
    // dotnet-counters' CSV: Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
    var byCounter = new Dictionary<string, List<(DateTime At, double Value)>>();
    foreach (string line in csv.Split('\n').Skip(1))
    {
        var p = line.Split(',');
        if (p.Length < 5 || !double.TryParse(p[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
        if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
        string name = p[2];
        if (!byCounter.TryGetValue(name, out var l)) byCounter[name] = l = [];
        l.Add((at, v));
    }
    // .NET 9+ counters are increments per interval (Rate) for the GC counts and allocation bytes: their sum is the total
    long Sum(string counter, string? tag = null) => byCounter
        .Where(kv => kv.Key.StartsWith(counter, StringComparison.Ordinal) && (tag is null || kv.Key.Contains(tag, StringComparison.Ordinal)))
        .SelectMany(kv => kv.Value).Sum(x => (long)x.Value);
    long gen0 = Sum("dotnet.gc.collections", "gen0"), gen1 = Sum("dotnet.gc.collections", "gen1"), gen2 = Sum("dotnet.gc.collections", "gen2");
    long allocated = Sum("dotnet.gc.heap.total_allocated");
    int seconds = byCounter.Values.Select(l => l.Count).DefaultIfEmpty(0).Max();
    // the busiest 15 s of allocation, to set against the best level's requests per second
    var rate = byCounter.Where(kv => kv.Key.StartsWith("dotnet.gc.heap.total_allocated", StringComparison.Ordinal)).SelectMany(kv => kv.Value).Select(x => x.Value).ToList();
    double peak = 0;
    for (int i = 0; i + 15 <= rate.Count; i++) peak = Math.Max(peak, rate.Skip(i).Take(15).Average());
    return (gen0, gen1, gen2, allocated, seconds, peak);
}

// ---------- processes ----------

string[] SshArgs(string vm, string command) =>
    ["compute", "ssh", vms[vm].Name, $"--project={Project}", $"--zone={Zone}", "--quiet", "--ssh-flag=-oServerAliveInterval=30",
        // a VM's host key is taken on first contact and held to for the run: the file starts anew with each create
        $"--ssh-flag=-oUserKnownHostsFile={KnownHosts}", "--ssh-flag=-oStrictHostKeyChecking=accept-new", "--command", command];

int Ssh(string vm, string command, bool quiet = false) => quiet ? Silent("gcloud", SshArgs(vm, command)) : Run("gcloud", SshArgs(vm, command));

int Gcloud(string[] a, bool quiet = false, bool zonal = true)
{
    var all = new List<string>(a) { $"--project={Project}" };
    if (zonal && !a.Any(x => x.StartsWith("--region", StringComparison.Ordinal) || x.StartsWith("--source-instance-zone", StringComparison.Ordinal))) all.Add($"--zone={Zone}");
    return quiet ? Silent("gcloud", [.. all]) : Run("gcloud", [.. all]);
}

static Process Start(string file, string[] a)
{
    var psi = new ProcessStartInfo(file);
    foreach (string x in a) psi.ArgumentList.Add(x);
    return Process.Start(psi)!;
}

static int Run(string file, string[] a)
{
    using var p = Start(file, a);
    p.WaitForExit();
    return p.ExitCode;
}

static int Silent(string file, string[] a)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string x in a) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode;
}

static string Capture(string file, string[] a)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true };
    foreach (string x in a) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    string s = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return s;
}

static string Source([CallerFilePath] string path = "") => path;

/// <summary>One level of a test: wrk's concurrency (or queries per request), TFB's score, wrk's own rate, latency.</summary>
sealed record Level(int Concurrency, double Rps, double WrkRps, string Latency, string P99, long Non2xx, long SocketErrors);
