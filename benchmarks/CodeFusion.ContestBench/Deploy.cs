using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodeFusion.ContestBench;

/// <summary>
/// Puts an entry on the target box and builds it there, as the scorer does: install.sh as root, build.sh as the
/// user challenge, from source. Each entry gets its own folder under /opt/challenge/entries; `agent switch` picks one.
/// </summary>
internal static partial class Deploy
{
    /// <param name="source">
    /// repo:rev:path (a commit of a git repository), or a folder inside a git checkout with nothing uncommitted or
    /// untracked in it (its HEAD). Only tracked files are packed, so the build can be made again from the commit.
    /// </param>
    public static void Run(Contest c, string name, string source, string? front, List<string> env)
    {
        Names.Check("entry", name);
        string tgz = Path.Combine(Paths.State, "entries", name + ".tgz");
        Directory.CreateDirectory(Path.GetDirectoryName(tgz)!);
        string[] parts = source.Split(':');
        (string repo, string rev, string path) = parts.Length == 3 ? (parts[0], parts[1], parts[2].Trim('/'))
            : Directory.Exists(source) ? Folder(source)
            : throw new RigException($"source {source}: want repo:rev:path or a folder in a git checkout");
        string full = Shell.Check("git", ["-C", repo, "rev-parse", "--verify", rev + "^{commit}"]).Trim();
        string tree = path.Length == 0 ? full : $"{full}:{path}";
        Shell.Check("git", ["-C", repo, "archive", "--format=tar.gz", "-o", tgz, tree]);
        string readme = Shell.Check("git", ["-C", repo, "show", path.Length == 0 ? $"{full}:README.md" : $"{full}:{path}/README.md"]);
        string described = $"{Path.GetFullPath(repo)} {full} {(path.Length == 0 ? "." : path)}";
        string build = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(tgz)))[..16];

        front ??= Front(c, readme);
        var vm = Rig.Find(c.TargetName);
        string dest = $"/opt/challenge/entries/{name}";
        Console.WriteLine($"deploying {name} ({front}), build {build}, from {described}");
        Ssh.Check(vm.ExternalIp, "install -d -m 755 -o challenge -g challenge /opt/challenge/entries");
        Ssh.Upload(vm.ExternalIp, tgz, $"/tmp/{name}.tgz");
        string envFile = string.Join("\n", env) + (env.Count > 0 ? "\n" : "");
        Ssh.Check(vm.ExternalIp,
            $"rm -rf {dest} && install -d -o challenge -g challenge {dest} {dest}/app && tar -xzf /tmp/{name}.tgz -C {dest}/app && " +
            $"chown -R challenge:challenge {dest} && printf %s {Shell.Q(front)} > {dest}/front && printf %s {Shell.Q(described)} > {dest}/source && " +
            $"printf %s {Shell.Q(envFile)} > {dest}/env");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] install.sh (root)");
        Ssh.Check(vm.ExternalIp, $"cd {dest}/app && bash install.sh > {dest}/install.log 2>&1 || {{ tail -40 {dest}/install.log; exit 1; }}",
            timeout: TimeSpan.FromMinutes(30));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] build.sh (user challenge)");
        Ssh.Check(vm.ExternalIp, $"cd {dest}/app && runuser -u challenge -- env -u XDG_RUNTIME_DIR HOME=/home/challenge bash build.sh > {dest}/build.log 2>&1 || {{ tail -60 {dest}/build.log; exit 1; }}",
            timeout: TimeSpan.FromMinutes(40));
        // the build id goes on last: an entry whose build failed has none, and a search refuses it
        Ssh.Check(vm.ExternalIp, $"printf %s {Shell.Q(build)} > {dest}/build");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] built {name}; `search {c.Key} {name}` scores it");
    }

    /// <summary>A folder's checkout, its HEAD and its path in it; refused with anything uncommitted or untracked in it.</summary>
    static (string Repo, string Rev, string Path) Folder(string folder)
    {
        string dir = Path.GetFullPath(folder);
        var top = Shell.Run("git", ["-C", dir, "rev-parse", "--show-toplevel"]);
        if (!top.Ok) throw new RigException($"{dir} is not in a git checkout: commit the entry, then deploy it (only committed files are deployed)");
        string dirty = Shell.Check("git", ["-C", dir, "status", "--porcelain", "--untracked-files=all", "--", "."]).Trim();
        if (dirty.Length > 0) throw new RigException($"{dir} has uncommitted or untracked files; commit them or deploy repo:rev:path:\n{Shell.Tail(dirty, 800)}");
        return (top.Out.Trim(), "HEAD", Shell.Check("git", ["-C", dir, "rev-parse", "--show-prefix"]).Trim().Trim('/'));
    }

    /// <summary>The README's front: multicore asks for a line `Front: nginx|direct`; the $12 README's table says "Nginx or direct".</summary>
    internal static string Front(Contest c, string readme)
    {
        var m = (c == Contest.Multicore ? MulticoreFrontRe() : TwelveFrontRe()).Match(readme);
        if (!m.Success) throw new RigException("the README doesn't say nginx or direct: pass --front");
        return m.Groups[1].Value.ToLowerInvariant();
    }

    [GeneratedRegex(@"(?im)^[-*_ ]*front[*_ ]*:[*_ ]*(nginx|direct)")]
    private static partial Regex MulticoreFrontRe();

    [GeneratedRegex(@"(?i)nginx or direct[^\n]*?\b(nginx|direct)\b")]
    private static partial Regex TwelveFrontRe();
}
