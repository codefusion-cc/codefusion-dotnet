using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodeFusion.ContestBench;

// The contest rig. On the Mac:
//   image multicore|twelve|load                    build an image on a fresh VM (the VM is deleted afterwards)
//   rig up multicore|twelve [--load N]             create the target and N load boxes from the images, ready them
//   rig down [multicore|twelve]                    delete the rig's VMs (all of them without an argument)
//   rig status                                     the VMs, and the cost so far
//   deploy multicore|twelve <entry> <source> [--front nginx|direct] [--env K=V]...
//                                                  <source> is repo:rev:path or a folder; built on the target
//   search multicore|twelve <entry> [--quick] [--start N] [--max N] [--fixed N] [--label L] [--no-switch]
//   results                                        the results table (Markdown)
//   ssh <vm> <command>                             a command as root on a rig VM
//   push-agent                                     publish this tool for linux-x64 and copy it to every rig VM
// On a rig VM (called by loadgen's search and by this tool):
//   agent switch <entry> | agent monitor <s> <csv> | agent prewarm | agent k6sum <csv.gz> | agent pgstat on|off|reset|dump
try
{
    switch (args)
    {
        case ["agent", .. var rest]:
            return Agent.Run(rest);
        case ["image", var kind]:
            Rig.Image(kind);
            return 0;
        case ["rig", "up", var contest, .. var rest]:
            var c = Contest.Get(contest);
            Rig.Up(c, Int(rest, "--load") ?? c.DefaultLoads);
            return 0;
        case ["rig", "down", .. var rest]:
            Rig.Down(rest.FirstOrDefault());
            return 0;
        case ["rig", "status"]:
            Rig.Status();
            return 0;
        case ["deploy", var contest, var entry, var source, .. var rest]:
            Deploy.Run(Contest.Get(contest), entry, source, Str(rest, "--front"), All(rest, "--env"));
            return 0;
        case ["search", var contest, var entry, .. var rest]:
            var o = new SearchOptions(rest.Contains("--quick"), Int(rest, "--start"), Int(rest, "--max"), Str(rest, "--label"),
                rest.Contains("--no-switch"), Int(rest, "--fixed"));
            if (Contest.Get(contest) == Contest.Multicore) Search.Multicore(entry, o);
            else Search.Twelve(entry, o);
            return 0;
        case ["results"]:
            Console.Write(Results.Table());
            return 0;
        case ["ssh", var vm, .. var cmd]:
            var r = Ssh.Run(Rig.Find(vm).ExternalIp, string.Join(' ', cmd), echo: true);
            return r.Code;
        case ["push-agent"]:
            string bin = Agent.Publish();
            foreach (var v in Gcp.List().Where(v => v.Status == "RUNNING"))
            {
                Ssh.Upload(v.ExternalIp, bin, "/opt/conduit-tools/cbench.new");
                Ssh.Check(v.ExternalIp, "chmod 755 /opt/conduit-tools/cbench.new && mv /opt/conduit-tools/cbench.new /opt/conduit-tools/cbench");
                Console.WriteLine($"agent on {v.Name}");
            }
            return 0;
        default:
            Console.Error.WriteLine("usage: see the comment at the top of Program.cs, or README.md");
            return 2;
    }
}
catch (RigException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static string? Str(string[] a, string flag)
{
    int i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static int? Int(string[] a, string flag) => Str(a, flag) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

static List<string> All(string[] a, string flag) =>
    a.Select((x, i) => (x, i)).Where(p => p.x == flag && p.i + 1 < a.Length).Select(p => a[p.i + 1]).ToList();
