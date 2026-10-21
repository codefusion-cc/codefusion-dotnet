using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace CodeFusion.ContestBench;

/// <summary>A machine shape: what gcloud gets, and what it costs.</summary>
internal sealed record Shape(string MachineType, int Cpus, double MemGb, int DiskGb, string DiskType, bool Spot)
{
    /// <summary>n1-custom-1-2048 is not a predefined type: gcloud takes it as --custom-* flags.</summary>
    public IEnumerable<string> MachineArgs()
    {
        if (MachineType.StartsWith("n1-custom-", StringComparison.Ordinal))
            return ["--custom-vm-type=n1", $"--custom-cpu={Cpus}", $"--custom-memory={MemGb.ToString(CultureInfo.InvariantCulture)}GB"];
        return [$"--machine-type={MachineType}"];
    }

    /// <summary>USD per hour, from the Cloud Billing catalog's europe-west1 list prices (2026-10-21).</summary>
    public double PricePerHour()
    {
        (double core, double ram) = (MachineType.Split('-')[0], Spot) switch
        {
            ("c4", false) => (0.03815312, 0.004336132),
            ("c4", true) => (0.01374, 0.001561),
            ("n2", false) => (0.034773, 0.004661),
            ("n2", true) => (0.00788, 0.001054),
            ("n1", false) when MachineType.Contains("custom", StringComparison.Ordinal) => (0.03651165, 0.00489405),
            ("n1", true) when MachineType.Contains("custom", StringComparison.Ordinal) => (0.01878, 0.002518),
            ("n1", false) => (0.034773, 0.004661),
            _ => (0.04, 0.005),
        };
        // Hyperdisk Balanced 0.088, pd-balanced 0.11 per GB-month (730 h); the baseline IOPS and throughput are free.
        double disk = DiskGb * (DiskType == "hyperdisk-balanced" ? 0.088 : 0.11) / 730;
        return Cpus * core + MemGb * ram + disk;
    }
}

/// <summary>A rig VM as gcloud lists it.</summary>
internal sealed record Vm(string Name, string Status, string InternalIp, string ExternalIp, string Role, string MachineType);

/// <summary>gcloud calls. Every VM gets the labels purpose=contest-bench,owner=codefusion and a role.</summary>
internal static class Gcp
{
    public const string Zone = "europe-west1-b";
    public const string Labels = "purpose=contest-bench,owner=codefusion";
    public const string KeyPath = "~/.ssh/cbench_ed25519";

    public static string Key => KeyPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);

    static string Gcloud => Shell.Which("gcloud");

    public static List<Vm> List()
    {
        string json = Shell.Retry("gcloud list", 3, () => Shell.Check(Gcloud, ["compute", "instances", "list",
            "--filter=labels.purpose=contest-bench", "--format=json"]));
        var vms = new List<Vm>();
        foreach (var n in JsonNode.Parse(json)!.AsArray())
        {
            var nic = n!["networkInterfaces"]?[0];
            vms.Add(new Vm(
                (string)n["name"]!,
                (string)n["status"]!,
                (string?)nic?["networkIP"] ?? "",
                (string?)nic?["accessConfigs"]?[0]?["natIP"] ?? "",
                (string?)n["labels"]?["role"] ?? "",
                ((string)n["machineType"]!).Split('/')[^1]));
        }
        return vms;
    }

    /// <summary>Hours after which Google deletes a VM whatever happens here (`--max-run-duration`); `--hours N` raises it.</summary>
    public static int MaxRunHours { get; set; } = 8;

    /// <summary>
    /// Creates a VM from an image family (ours, or Ubuntu's when <paramref name="imageProject"/> is given). Google
    /// deletes it after <see cref="MaxRunHours"/>; Ctrl-C deletes it (see <see cref="Cleanup"/>); a create that fails
    /// after the VM exists deletes it.
    /// </summary>
    public static void Create(string name, string role, Shape shape, string imageFamily, string? imageProject = null)
    {
        string pub = File.ReadAllText(Key + ".pub").Trim();
        // At every boot: root takes the rig's key (search drives the boxes as root over ssh, as the scorer does).
        string startup = string.Join('\n',
            "#!/bin/bash",
            "install -d -m 700 /root/.ssh",
            $"grep -qF '{pub}' /root/.ssh/authorized_keys 2>/dev/null || echo '{pub}' >> /root/.ssh/authorized_keys",
            "chmod 600 /root/.ssh/authorized_keys",
            "echo 'PermitRootLogin prohibit-password' > /etc/ssh/sshd_config.d/00-cbench.conf",
            "systemctl reload ssh || true",
            "");
        string tmp = Path.Combine(Path.GetTempPath(), $"cbench-startup-{name}.sh");
        File.WriteAllText(tmp, startup);
        var args = new List<string> { "compute", "instances", "create", name, $"--zone={Zone}",
            $"--labels={Labels},role={role}", $"--boot-disk-size={shape.DiskGb}GB", $"--boot-disk-type={shape.DiskType}",
            $"--metadata-from-file=startup-script={tmp}", $"--max-run-duration={MaxRunHours}h", "--instance-termination-action=DELETE", "--quiet" };
        args.AddRange(shape.MachineArgs());
        if (imageProject != null) { args.Add($"--image-family={imageFamily}"); args.Add($"--image-project={imageProject}"); }
        else args.Add($"--image-family={imageFamily}");
        if (shape.MachineType.StartsWith("c4", StringComparison.Ordinal)) args.Add("--network-interface=nic-type=GVNIC");
        if (shape.Spot) args.Add("--provisioning-model=SPOT");
        Cleanup.Track(name);
        try
        {
            Shell.Check(Gcloud, args);
        }
        catch (RigException)
        {
            // gcloud can fail after the VM exists (a client timeout, a partial error): delete what it made
            if (List().Any(v => v.Name == name))
            {
                Ledger.Created(name, role, shape);
                Delete([name]);
            }
            Cleanup.Untrack(name);
            throw;
        }
        finally
        {
            File.Delete(tmp);
        }
        Ledger.Created(name, role, shape);
        // a new VM may get an address an old one had: its host key is new
        if (List().FirstOrDefault(v => v.Name == name) is { ExternalIp.Length: > 0 } vm) Ssh.Forget(vm.ExternalIp);
        Console.WriteLine($"created {name} ({shape.MachineType}{(shape.Spot ? ", spot" : "")}, {shape.PricePerHour():0.000} USD/h, deleted by Google after {MaxRunHours} h)");
    }

    /// <summary>Deletes the VMs of these names that exist; one already gone doesn't stop the others.</summary>
    public static void Delete(IEnumerable<string> names)
    {
        var want = names.Distinct().ToList();
        if (want.Count == 0) return;
        var live = List().Select(v => v.Name).ToHashSet();
        foreach (string n in want.Where(n => !live.Contains(n))) Cleanup.Untrack(n);
        var list = want.Where(live.Contains).ToList();
        if (list.Count == 0) return;
        var r = Shell.Run(Gcloud, ["compute", "instances", "delete", .. list, $"--zone={Zone}", "--quiet"]);
        var left = r.Ok ? [] : List().Select(v => v.Name).Where(list.Contains).ToHashSet();
        var done = list.Where(n => !left.Contains(n)).ToList();
        foreach (string n in done)
        {
            Ledger.Deleted(n);
            Cleanup.Untrack(n);
        }
        if (done.Count > 0) Console.WriteLine($"deleted {string.Join(", ", done)}");
        if (left.Count > 0) throw new RigException($"could not delete {string.Join(", ", left)}: {Shell.Tail(r.Err, 800)}");
    }

    /// <summary>
    /// When a VM the rig no longer sees went, and why, from the zone's operations: preempted, deleted at its
    /// max run duration or deleted elsewhere. The first such operation after <paramref name="since"/>; null when none is listed.
    /// </summary>
    public static (DateTime At, string Why)? GoneAt(string name, DateTime since)
    {
        var r = Shell.Run(Gcloud, ["compute", "operations", "list", $"--zones={Zone}", $"--filter=targetLink~/instances/{name}$",
            "--format=json(operationType,insertTime,endTime)"]);
        if (!r.Ok) return null;
        return JsonNode.Parse(r.Out)!.AsArray()
            .Select(o => ((string)o!["operationType"]!, (string?)o["endTime"] ?? (string)o["insertTime"]!))
            .Where(o => o.Item1 != "insert" && o.Item1 != "start" && !o.Item1.Contains("setMetadata", StringComparison.Ordinal))
            .Select(o => (At: DateTimeOffset.Parse(o.Item2, CultureInfo.InvariantCulture).UtcDateTime, Why: o.Item1))
            .Where(o => o.At >= since && (o.Why == "delete" || o.Why.Contains("preempt", StringComparison.OrdinalIgnoreCase) ||
                o.Why.Contains("terminat", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => o.At).Select(o => ((DateTime, string)?)o).FirstOrDefault();
    }

    public static void Stop(string name) =>
        Shell.Check(Gcloud, ["compute", "instances", "stop", name, $"--zone={Zone}", "--quiet"]);

    /// <summary>Makes an image of a stopped VM's boot disk into a family; older images of the family stay until pruned.</summary>
    public static string CreateImage(string vmName, string family)
    {
        string image = $"{family}-{DateTime.UtcNow:yyyyMMdd-HHmm}";
        Shell.Check(Gcloud, ["compute", "images", "create", image, $"--source-disk={vmName}", $"--source-disk-zone={Zone}",
            $"--family={family}", $"--labels={Labels}", "--storage-location=europe-west1", "--quiet"], echo: true);
        // Keep only the newest image of the family.
        string json = Shell.Check(Gcloud, ["compute", "images", "list", $"--filter=family={family}", "--format=json(name,creationTimestamp)"]);
        var old = JsonNode.Parse(json)!.AsArray().Select(n => (string)n!["name"]!).Where(n => n != image).ToList();
        if (old.Count > 0) Shell.Check(Gcloud, ["compute", "images", "delete", .. old, "--quiet"]);
        return image;
    }

    public static bool ImageExists(string family) =>
        Shell.Run(Gcloud, ["compute", "images", "describe-from-family", family, "--format=value(name)"]).Ok;

    /// <summary>Waits until root can ssh in (the startup script has run).</summary>
    public static void WaitSsh(string ip, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (true)
        {
            var r = Ssh.Run(ip, "true", timeout: TimeSpan.FromSeconds(20));
            if (r.Ok) return;
            if (DateTime.UtcNow > until) throw new RigException($"no ssh to root@{ip} after {max}: {Shell.Tail(r.Err, 300)}");
            Thread.Sleep(5000);
        }
    }
}

/// <summary>
/// ssh and scp to the rig's VMs as root with the rig key. Host keys go in the rig's own known_hosts, taken on first
/// contact (accept-new) and dropped when a new VM gets the address.
/// </summary>
internal static class Ssh
{
    static readonly string KnownHosts = Path.Combine(Paths.State, "known_hosts");
    static readonly Lock KnownHostsGate = new();
    static readonly string[] Opts = ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", $"UserKnownHostsFile={KnownHosts}",
        "-o", "LogLevel=ERROR", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=8"];
    static readonly string[] Recursive = ["-r"];

    static Ssh() => Directory.CreateDirectory(Paths.State);

    /// <summary>Drops an address's host key: a new VM has it now.</summary>
    public static void Forget(string ip)
    {
        lock (KnownHostsGate)
            if (File.Exists(KnownHosts)) Shell.Run("ssh-keygen", ["-q", "-R", ip, "-f", KnownHosts]);
    }

    public static ProcessResult Run(string ip, string command, bool echo = false, TimeSpan? timeout = null, string? stdin = null) =>
        Shell.Run("ssh", [.. Opts, "-i", Gcp.Key, $"root@{ip}", command], echo: echo, timeout: timeout, stdin: stdin);

    /// <summary>Runs a command and throws when it fails. ssh's own failures (exit 255) are retried.</summary>
    public static string Check(string ip, string command, bool echo = false, TimeSpan? timeout = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            var r = Run(ip, command, echo, timeout);
            if (r.Ok) return r.Out;
            if (r.Code == 255 && attempt < 3) { Thread.Sleep(3000 * attempt); continue; }
            throw new RigException($"ssh {ip} '{Shell.Tail(command, 200)}' exited {r.Code}: {Shell.Tail(r.Err + r.Out, 1500)}");
        }
    }

    public static void Upload(string ip, string local, string remote) =>
        Shell.Retry("scp", 3, () => Shell.Check("scp", [.. Opts, "-i", Gcp.Key, "-q", local, $"root@{ip}:{remote}"]));

    public static void Download(string ip, string remote, string local, bool recursive = false) =>
        Shell.Retry("scp", 3, () => Shell.Check("scp", [.. Opts, "-i", Gcp.Key, "-q", .. (recursive ? Recursive : []), $"root@{ip}:{remote}", local]));
}

/// <summary>
/// Every VM created, deleted, or gone without the rig (preempted, or deleted at its max run duration), with its
/// price, so the rig's cost can be added up. Appends are whole lines under a lock, so parallel creates keep every row.
/// </summary>
internal static class Ledger
{
    static string File_ => System.IO.Path.Combine(Paths.State, "ledger.jsonl");
    static readonly Lock Gate = new();

    public static void Created(string name, string role, Shape s) => Append(File_, new JsonObject
    {
        ["t"] = DateTime.UtcNow.ToString("o"), ["ev"] = "create", ["name"] = name, ["role"] = role,
        ["type"] = s.MachineType, ["spot"] = s.Spot, ["usd_h"] = s.PricePerHour(),
    });

    public static void Deleted(string name) => Append(File_, new JsonObject
    {
        ["t"] = DateTime.UtcNow.ToString("o"), ["ev"] = "delete", ["name"] = name,
    });

    /// <summary>Adds a "gone" row for each VM the ledger has running that gcloud no longer lists, at the time it went.</summary>
    public static void Reconcile(ISet<string> listed)
    {
        if (!File.Exists(File_)) return;
        var open = Open(File.ReadLines(File_).ToList());
        foreach (var (name, created) in open.Where(o => !listed.Contains(o.Name)))
        {
            var gone = Gcp.GoneAt(name, created);
            Append(File_, new JsonObject
            {
                ["t"] = (gone?.At ?? DateTime.UtcNow).ToString("o"), ["ev"] = "gone", ["name"] = name,
                ["why"] = gone?.Why ?? "not listed by gcloud; when it went is unknown, so it counts to now",
            });
            Console.WriteLine($"ledger: {name} went without the rig ({gone?.Why ?? "unknown"}, {gone?.At:HH:mm}Z)");
        }
    }

    internal static void Append(string path, JsonObject o)
    {
        byte[] line = System.Text.Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // another cbench process may hold the file for a moment
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    f.Write(line);
                    return;
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    /// <summary>The VMs the ledger has running: created, and neither deleted nor gone since.</summary>
    internal static List<(string Name, DateTime Created)> Open(IEnumerable<string> lines)
    {
        var open = new Dictionary<string, DateTime>();
        foreach (var (ev, name, t, _) in Rows(lines))
            if (ev == "create") open[name] = t;
            else open.Remove(name);
        return open.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    internal static List<string> Missing(IEnumerable<string> lines, ISet<string> listed) =>
        Open(lines).Where(o => !listed.Contains(o.Name)).Select(o => o.Name).ToList();

    static IEnumerable<(string Ev, string Name, DateTime T, double UsdH)> Rows(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            var o = JsonNode.Parse(line)!;
            yield return ((string)o["ev"]!, (string)o["name"]!,
                DateTime.Parse((string)o["t"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                (double?)o["usd_h"] ?? 0);
        }
    }

    public static (double Usd, double Hours, List<string> Running) Cost() =>
        File.Exists(File_) ? Cost(File.ReadLines(File_).ToList(), DateTime.UtcNow) : (0, 0, []);

    /// <summary>VM hours and cost; VMs still running count up to <paramref name="now"/>.</summary>
    internal static (double Usd, double Hours, List<string> Running) Cost(IEnumerable<string> lines, DateTime now)
    {
        var open = new Dictionary<string, (DateTime T, double Usd)>();
        double usd = 0, hours = 0;
        foreach (var (ev, name, t, price) in Rows(lines))
        {
            if (ev == "create") open[name] = (t, price);
            else if (open.Remove(name, out var c))
            {
                double h = (t - c.T).TotalHours;
                hours += h;
                usd += h * c.Usd;
            }
        }
        foreach (var (_, c) in open)
        {
            double h = (now - c.T).TotalHours;
            hours += h;
            usd += h * c.Usd;
        }
        return (usd, hours, open.Keys.ToList());
    }
}

/// <summary>
/// The VMs this process made and still owns. Ctrl-C deletes them before the process ends; a `rig up` that finished
/// hands its VMs over (they are meant to stay up), and a builder is deleted when its image is made or fails.
/// </summary>
internal static class Cleanup
{
    static readonly HashSet<string> Made = [];
    static int _stopping;

    public static void Track(string name) { lock (Made) Made.Add(name); }

    public static void Untrack(string name) { lock (Made) Made.Remove(name); }

    public static void Keep(IEnumerable<string> names) { lock (Made) foreach (string n in names) Made.Remove(n); }

    public static List<string> Tracked() { lock (Made) return Made.ToList(); }

    /// <summary>On Ctrl-C: deletes what this process made, then exits. A second Ctrl-C ends it at once.</summary>
    public static void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        e.Cancel = true;
        var made = Tracked();
        if (made.Count > 0)
        {
            Console.Error.WriteLine($"interrupted: deleting what this run made: {string.Join(", ", made)}");
            try { Gcp.Delete(made); }
            catch (RigException ex) { Console.Error.WriteLine($"error: {ex.Message}; `rig status` lists what is left"); }
        }
        Environment.Exit(130);
    }
}

internal static class Paths
{
    /// <summary>artifacts/contest in the repository (ignored by git): the ledger, built binaries and every result.</summary>
    public static string State { get; } = System.IO.Path.Combine(RepoRoot(), "artifacts", "contest");

    public static string Results(string contest) => System.IO.Path.Combine(State, "results", contest);

    public static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", ".."));

    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true };
}
