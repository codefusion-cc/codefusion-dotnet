using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CodeFusion.ContestBench;

/// <summary>One contest: its repository, its benchmark box and its load boxes.</summary>
internal sealed record Contest(
    string Key, string Repo, string Ref, string TargetName, Shape Target, string TargetFamily,
    string LoadPrefix, Shape Load, int DefaultLoads, Shape Builder)
{
    /// <summary>
    /// The multicore box: 8 vCPU / 16 GB like the rules' box, as c4-highcpu-8, the shape PR #8's README measured on.
    /// The load boxes are n2-highcpu-4 spot machines, five of them: each drives up to 60k users (one connection each, so
    /// the local port range is the limit), and target + loads stay inside the project's 32-vCPU quota.
    /// </summary>
    public static readonly Contest Multicore = new(
        "multicore", "/Volumes/Dev/Projects/multicore-server-challenge", "origin/main",
        "cbench-mc-target", new Shape("c4-highcpu-8", 8, 16, 50, "hyperdisk-balanced", false), "cbench-multicore",
        "cbench-mc-load", new Shape("n2-highcpu-4", 4, 4, 20, "pd-balanced", true), 5,
        new Shape("c4-highcpu-8", 8, 16, 50, "hyperdisk-balanced", false));

    /// <summary>
    /// The $12 box: a DigitalOcean Basic droplet is 1 vCPU, 2 GB, no swap. The owner wants Intel C4 for every scored
    /// box, and C4's smallest shape is c4-highcpu-2 (2 vCPU, 4 GB), so the image's kernel command line holds it to
    /// one CPU and 2 GB (`nr_cpus=1 mem=2G`); the switch prints nproc and free before each search. The k6 boxes need
    /// memory (k6 keeps a JavaScript VM per user and every sample), so n2-highmem-4 spot.
    /// </summary>
    public static readonly Contest Twelve = new(
        "twelve", "/Volumes/Dev/Projects/twelve-dollar-codefusion", "origin/main",
        "cbench-tw-target", new Shape("c4-highcpu-2", 2, 4, 25, "hyperdisk-balanced", false), "cbench-twelve",
        "cbench-tw-load", new Shape("n2-highmem-4", 4, 32, 20, "pd-balanced", true), 1,
        new Shape("c4-highcpu-2", 2, 4, 25, "hyperdisk-balanced", false));

    /// <summary>The same $12 box on n1-custom-1-2048 (a real 1 vCPU / 2 GB VM), as an extra comparison only.</summary>
    public static readonly Shape TwelveN1 = new("n1-custom-1-2048", 1, 2, 25, "pd-balanced", false);

    public static Contest Get(string key) => key switch
    {
        "multicore" or "mc" => Multicore,
        "twelve" or "tw" or "12" => Twelve,
        _ => throw new RigException($"unknown contest {key} (multicore or twelve)"),
    };

    public string LoadName(int i) => $"{LoadPrefix}-{i}";
}

internal static class Rig
{
    public const string LoadFamily = "cbench-load";
    public const string K6Version = "v2.3.0";
    public const string GoVersion = "1.27.2";
    const string Ubuntu = "ubuntu-2404-lts-amd64";
    const string UbuntuProject = "ubuntu-os-cloud";

    /// <summary>Creates whatever of the contest's target and load boxes is missing, then readies them.</summary>
    public static void Up(Contest c, int loads)
    {
        if (!Gcp.ImageExists(c.TargetFamily)) throw new RigException($"no image {c.TargetFamily}: run `image {c.Key}` first");
        if (!Gcp.ImageExists(LoadFamily)) throw new RigException($"no image {LoadFamily}: run `image load` first");
        var have = Gcp.List().ToDictionary(v => v.Name);
        var want = new List<(string Name, string Role, Shape Shape, string Family)> { (c.TargetName, c.Key + "-target", c.Target, c.TargetFamily) };
        for (int i = 0; i < loads; i++) want.Add((c.LoadName(i), c.Key + "-load", c.Load, LoadFamily));
        Parallel.ForEach(want.Where(w => !have.ContainsKey(w.Name)), w => Gcp.Create(w.Name, w.Role, w.Shape, w.Family));
        var vms = Gcp.List().ToDictionary(v => v.Name);
        Parallel.ForEach(want, w => Gcp.WaitSsh(vms[w.Name].ExternalIp, TimeSpan.FromMinutes(4)));
        var target = vms[c.TargetName];
        var boxes = want.Skip(1).Select(w => vms[w.Name]).ToList();
        // The first load box runs the multicore search and drives the target and the other boxes over ssh as root.
        if (c == Contest.Multicore)
        {
            Ssh.Upload(boxes[0].ExternalIp, Gcp.Key, "/root/.ssh/id_ed25519");
            Ssh.Check(boxes[0].ExternalIp, "chmod 600 /root/.ssh/id_ed25519 && " + string.Join(" && ",
                boxes.Skip(1).Select(b => b.InternalIp).Append(target.InternalIp).Select(ip =>
                    $"ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new root@{ip} true")));
        }
        else
        {
            string tokens = Path.Combine(Paths.State, "twelve-tokens.json");
            foreach (var b in boxes) Ssh.Upload(b.ExternalIp, tokens, "/opt/twelve/seed/tokens.json");
        }
        string shape = Ssh.Check(target.ExternalIp, "echo nproc $(nproc); free -m | head -3; cat /proc/cmdline");
        Console.WriteLine($"{c.TargetName}: {shape.Trim()}");
        if (c == Contest.Twelve && !shape.StartsWith("nproc 1\n", StringComparison.Ordinal))
            throw new RigException("the $12 box must have one CPU (nr_cpus=1 on the kernel command line)");
        // A disk made from an image reads each block slowly the first time: read the data once.
        Ssh.Check(target.ExternalIp, "/opt/conduit-tools/cbench agent prewarm", echo: true, timeout: TimeSpan.FromMinutes(15));
        Status();
    }

    public static void Down(string? contest)
    {
        var vms = Gcp.List();
        var names = vms.Where(v => contest == null || v.Role.StartsWith(Contest.Get(contest).Key, StringComparison.Ordinal))
            .Select(v => v.Name).ToList();
        Gcp.Delete(names);
        Status();
    }

    public static void Status()
    {
        var vms = Gcp.List();
        if (vms.Count == 0) Console.WriteLine("no contest-bench VMs");
        foreach (var v in vms)
            Console.WriteLine($"{v.Name,-20} {v.Status,-10} {v.MachineType,-16} {v.InternalIp,-15} {v.ExternalIp,-15} {v.Role}");
        var (usd, hours, running) = Ledger.Cost();
        Console.WriteLine($"cost so far: {usd:0.00} USD over {hours:0.0} VM-hours (VMs, disks while running; images extra)" +
            (running.Count > 0 ? $"; in the ledger as running: {string.Join(", ", running)}" : ""));
    }

    public static Vm Find(string name) =>
        Gcp.List().FirstOrDefault(v => v.Name == name) ?? throw new RigException($"{name} is not running: `rig up` first");

    public static List<Vm> Loads(Contest c) =>
        Gcp.List().Where(v => v.Name.StartsWith(c.LoadPrefix + "-", StringComparison.Ordinal))
            .OrderBy(v => int.Parse(v.Name[(c.LoadPrefix.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture)).ToList();

    // ---------------------------------------------------------------- images

    /// <summary>
    /// Builds an image on a fresh Ubuntu 24.04 VM, then deletes the VM: the multicore box (box/setup-box.sh, Go for
    /// the seed, the full and dev seeds), the $12 box, or a load box (loadgen and k6).
    /// </summary>
    public static void Image(string kind)
    {
        string agent = Agent.Publish();
        (string name, Shape shape, string family) = kind switch
        {
            "multicore" or "mc" => ("cbench-img-mc", Contest.Multicore.Builder, Contest.Multicore.TargetFamily),
            "twelve" or "tw" => ("cbench-img-tw", Contest.Twelve.Builder, Contest.Twelve.TargetFamily),
            "load" => ("cbench-img-load", new Shape("n2-standard-4", 4, 16, 20, "pd-balanced", false), LoadFamily),
            _ => throw new RigException("image multicore|twelve|load"),
        };
        if (Gcp.List().Any(v => v.Name == name)) Gcp.Delete([name]);
        Gcp.Create(name, "builder", shape, Ubuntu, UbuntuProject);
        try
        {
            var vm = Find(name);
            Gcp.WaitSsh(vm.ExternalIp, TimeSpan.FromMinutes(4));
            string ip = vm.ExternalIp;
            // cloud-init's own apt run holds the lock for the first minute or so.
            Ssh.Check(ip, "cloud-init status --wait >/dev/null 2>&1 || true", timeout: TimeSpan.FromMinutes(10));
            Ssh.Check(ip, "install -d -m 755 /opt/conduit-tools");
            Ssh.Upload(ip, agent, "/opt/conduit-tools/cbench");
            Ssh.Check(ip, "chmod 755 /opt/conduit-tools/cbench");
            switch (family)
            {
                case "cbench-multicore": BuildMulticore(ip); break;
                case "cbench-twelve": BuildTwelve(ip); break;
                default: BuildLoad(ip); break;
            }
            Ssh.Check(ip, "apt-get clean; journalctl --rotate >/dev/null; journalctl --vacuum-time=1s >/dev/null; sync");
            Gcp.Stop(name);
            Console.WriteLine($"image {Gcp.CreateImage(name, family)} ready");
        }
        finally
        {
            Gcp.Delete([name]);
        }
    }

    const string Apt = "DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get -y -q -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold";

    static void Step(string ip, string what, string command, int minutes = 20)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {what}");
        Ssh.Check(ip, command, timeout: TimeSpan.FromMinutes(minutes));
    }

    static string Archive(Contest c)
    {
        Shell.Check("git", ["-C", c.Repo, "fetch", "-q", "origin"]);
        string rev = Shell.Check("git", ["-C", c.Repo, "rev-parse", c.Ref]).Trim();
        string tgz = Path.Combine(Paths.State, $"{c.Key}-{rev[..7]}.tgz");
        Directory.CreateDirectory(Paths.State);
        Shell.Check("git", ["-C", c.Repo, "archive", "--format=tar.gz", "-o", tgz, rev]);
        Console.WriteLine($"{c.Key} repository at {rev[..7]}");
        return tgz;
    }

    static void BuildMulticore(string ip)
    {
        string tgz = Archive(Contest.Multicore);
        Ssh.Upload(ip, tgz, "/tmp/repo.tgz");
        Step(ip, "repository to /opt/mcsc", "rm -rf /opt/mcsc && mkdir -p /opt/mcsc && tar -xzf /tmp/repo.tgz -C /opt/mcsc && echo multicore > /etc/cbench-contest");
        Step(ip, "box/setup-box.sh", "bash /opt/mcsc/box/setup-box.sh > /root/setup-box.log 2>&1 || { tail -50 /root/setup-box.log; exit 1; }", 40);
        Step(ip, $"Go {GoVersion} for the seed generator, perf for profiling",
            $"curl -fsSL https://go.dev/dl/go{GoVersion}.linux-amd64.tar.gz | tar -C /usr/local -xz && {Apt} install linux-tools-gcp linux-tools-$(uname -r) >/dev/null");
        const string Seed = "chown -R postgres:postgres /opt/mcsc && cd /opt/mcsc && runuser -u postgres -- env HOME=/var/lib/postgresql PATH=/usr/local/go/bin:/usr/bin:/bin";
        Step(ip, "dev seed", $"{Seed} bash seed/make-seed.sh --dev 2>&1 | tail -3", 20);
        Step(ip, "full seed (5-10 min)", $"{Seed} bash seed/make-seed.sh 2>&1 | tail -3", 60);
        Console.WriteLine(Ssh.Check(ip, "bash /opt/mcsc/box/verify-seed.sh conduit_seed", timeout: TimeSpan.FromMinutes(20)));
        Step(ip, "stop Postgres cleanly", "systemctl stop postgresql@18-main && rm -rf /opt/mcsc/seed/data-* && cat /root/versions.txt");
    }

    static void BuildTwelve(string ip)
    {
        string tgz = Archive(Contest.Twelve);
        Ssh.Upload(ip, tgz, "/tmp/repo.tgz");
        Step(ip, "repository to /opt/twelve-repo", "rm -rf /opt/twelve-repo && mkdir -p /opt/twelve-repo && tar -xzf /tmp/repo.tgz -C /opt/twelve-repo && echo twelve > /etc/cbench-contest");
        Step(ip, "apt: upgrade, nginx, sqlite3, node (seed only), perf",
            $"{Apt} update >/dev/null && {Apt} upgrade >/dev/null && {Apt} install nginx sqlite3 nodejs curl jq sysstat unzip linux-tools-gcp linux-tools-$(uname -r) >/dev/null", 30);
        Step(ip, "seed", "cd /opt/twelve-repo && bash seed/make-seed.sh | tail -2 && install -d /opt/twelve/seed && cp seed/feed.db seed/tokens.json /opt/twelve/seed/", 20);
        Ssh.Download(ip, "/opt/twelve/seed/tokens.json", Path.Combine(Paths.State, "twelve-tokens.json"));
        // bench/nginx.conf's header: nginx.conf also sets worker_rlimit_nofile 65535 and worker_connections 16384.
        Step(ip, "nginx as bench/nginx.conf says",
            "sed -i -E 's/^(\\s*)worker_connections\\s+[0-9]+;/\\1worker_connections 16384;/' /etc/nginx/nginx.conf && " +
            "grep -q '^worker_rlimit_nofile' /etc/nginx/nginx.conf || sed -i '/^pid /a worker_rlimit_nofile 65535;' /etc/nginx/nginx.conf; " +
            "rm -f /etc/nginx/sites-enabled/default && cp /opt/twelve-repo/bench/nginx.conf /etc/nginx/conf.d/bench.conf && nginx -t 2>&1 && systemctl restart nginx");
        // One CPU and 2 GB on any machine this image boots on (c4-highcpu-2 has 2 and 4); no swap (none on GCE images).
        Step(ip, "kernel command line: nr_cpus=1 mem=2G",
            "printf '%s\\n' 'GRUB_CMDLINE_LINUX_DEFAULT=\"$GRUB_CMDLINE_LINUX_DEFAULT nr_cpus=1 mem=2G\"' > /etc/default/grub.d/99-cbench-1cpu-2g.cfg && update-grub 2>&1 | tail -1 && " +
            "swapon --show | grep -q . && echo 'swap is on' && exit 1 || true");
        Step(ip, "user challenge and challenge.service",
            "id challenge >/dev/null 2>&1 || useradd --create-home --shell /bin/bash challenge; " +
            "install -d -m 755 -o challenge -g challenge /opt/challenge /opt/challenge/entries /var/lib/challenge; install -d -m 750 -o root -g challenge /etc/challenge; " +
            "printf '%s\\n' '[Unit]' 'Description=$12 server challenge submission' 'After=network.target nginx.service' '[Service]' 'Type=simple' 'User=challenge' 'Group=challenge' " +
            "'WorkingDirectory=/opt/challenge/current' 'EnvironmentFile=/etc/challenge/env' 'Environment=HOME=/home/challenge' 'ExecStart=/opt/challenge/current/start.sh' " +
            "'Restart=no' 'LimitNOFILE=65535' 'AmbientCapabilities=CAP_NET_BIND_SERVICE' 'TimeoutStopSec=20' 'KillMode=mixed' > /etc/systemd/system/challenge.service && systemctl daemon-reload");
    }

    static void BuildLoad(string ip)
    {
        // loadgen from the multicore repository's main, built here for linux/amd64.
        string src = Path.Combine(Paths.State, "src", "mcsc");
        if (Directory.Exists(src)) Directory.Delete(src, true);
        Directory.CreateDirectory(src);
        string tgz = Archive(Contest.Multicore);
        Shell.Check("tar", ["-xzf", tgz, "-C", src]);
        string bin = Path.Combine(Paths.State, "conduit-loadgen");
        Shell.Check("go", ["build", "-o", bin, "./cmd/loadgen"], cwd: Path.Combine(src, "bench"),
            env: new Dictionary<string, string> { ["GOOS"] = "linux", ["GOARCH"] = "amd64", ["CGO_ENABLED"] = "0" });
        Ssh.Upload(ip, bin, "/usr/local/bin/conduit-loadgen");
        string tw = Archive(Contest.Twelve);
        Ssh.Upload(ip, tw, "/tmp/twelve.tgz");
        Ssh.Upload(ip, Path.Combine(src, "box", "sysctl.conf"), "/etc/sysctl.d/90-challenge.conf");
        Step(ip, $"limits, k6 {K6Version}, the $12 load script",
            "chmod 755 /usr/local/bin/conduit-loadgen && sysctl -q --system && " +
            "printf 'root soft nofile 1048576\\nroot hard nofile 1048576\\n* soft nofile 1048576\\n* hard nofile 1048576\\n' > /etc/security/limits.d/90-nofile.conf && " +
            "install -d /etc/systemd/system.conf.d && printf '[Manager]\\nDefaultLimitNOFILE=1048576\\n' > /etc/systemd/system.conf.d/90-nofile.conf && " +
            $"curl -fsSL https://github.com/grafana/k6/releases/download/{K6Version}/k6-{K6Version}-linux-amd64.tar.gz | tar -xz -C /tmp && " +
            $"install -m 755 /tmp/k6-{K6Version}-linux-amd64/k6 /usr/local/bin/k6 && k6 version && " +
            "rm -rf /opt/twelve && install -d /opt/twelve/seed && tar -xzf /tmp/twelve.tgz -C /opt/twelve bench && echo load > /etc/cbench-contest && " +
            $"{Apt} update >/dev/null && {Apt} install jq sysstat >/dev/null");
    }
}
