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

    /// <summary>Creates a VM from an image family (ours, or Ubuntu's when <paramref name="imageProject"/> is given).</summary>
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
            $"--metadata-from-file=startup-script={tmp}", "--quiet" };
        args.AddRange(shape.MachineArgs());
        if (imageProject != null) { args.Add($"--image-family={imageFamily}"); args.Add($"--image-project={imageProject}"); }
        else args.Add($"--image-family={imageFamily}");
        if (shape.MachineType.StartsWith("c4", StringComparison.Ordinal)) args.Add("--network-interface=nic-type=GVNIC");
        if (shape.Spot) { args.Add("--provisioning-model=SPOT"); args.Add("--instance-termination-action=DELETE"); }
        try
        {
            Shell.Check(Gcloud, args);
        }
        finally
        {
            File.Delete(tmp);
        }
        Ledger.Created(name, role, shape);
        Console.WriteLine($"created {name} ({shape.MachineType}{(shape.Spot ? ", spot" : "")}, {shape.PricePerHour():0.000} USD/h)");
    }

    public static void Delete(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count == 0) return;
        Shell.Check(Gcloud, ["compute", "instances", "delete", .. list, $"--zone={Zone}", "--quiet"], echo: false);
        foreach (string n in list) Ledger.Deleted(n);
        Console.WriteLine($"deleted {string.Join(", ", list)}");
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

/// <summary>ssh and scp to the rig's VMs as root with the rig key. Host keys change with every VM, so none are kept.</summary>
internal static class Ssh
{
    static readonly string[] Opts = ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null",
        "-o", "LogLevel=ERROR", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=8"];
    static readonly string[] Recursive = ["-r"];

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

/// <summary>Every VM created or deleted, with its price, so the rig's cost can be added up.</summary>
internal static class Ledger
{
    static string Path => System.IO.Path.Combine(Paths.State, "ledger.jsonl");

    public static void Created(string name, string role, Shape s) => Append(new JsonObject
    {
        ["t"] = DateTime.UtcNow.ToString("o"), ["ev"] = "create", ["name"] = name, ["role"] = role,
        ["type"] = s.MachineType, ["spot"] = s.Spot, ["usd_h"] = s.PricePerHour(),
    });

    public static void Deleted(string name) => Append(new JsonObject
    {
        ["t"] = DateTime.UtcNow.ToString("o"), ["ev"] = "delete", ["name"] = name,
    });

    static void Append(JsonObject o)
    {
        Directory.CreateDirectory(Paths.State);
        File.AppendAllText(Path, o.ToJsonString() + "\n");
    }

    /// <summary>VM hours and cost so far; VMs still running count up to now.</summary>
    public static (double Usd, double Hours, List<string> Running) Cost()
    {
        if (!File.Exists(Path)) return (0, 0, []);
        var open = new Dictionary<string, (DateTime T, double Usd)>();
        double usd = 0, hours = 0;
        foreach (string line in File.ReadLines(Path))
        {
            var o = JsonNode.Parse(line)!;
            var t = DateTime.Parse((string)o["t"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            string name = (string)o["name"]!;
            if ((string)o["ev"]! == "create") open[name] = (t, (double)o["usd_h"]!);
            else if (open.Remove(name, out var c))
            {
                double h = (t - c.T).TotalHours;
                hours += h;
                usd += h * c.Usd;
            }
        }
        foreach (var (_, c) in open)
        {
            double h = (DateTime.UtcNow - c.T).TotalHours;
            hours += h;
            usd += h * c.Usd;
        }
        return (usd, hours, open.Keys.ToList());
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
