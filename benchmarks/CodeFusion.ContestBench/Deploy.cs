using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CodeFusion.ContestBench;

/// <summary>
/// Puts an entry on the target box and builds it there, as the scorer does: install.sh as root, build.sh as the
/// user challenge, from source. Each entry gets its own folder under /opt/challenge/entries; `agent switch` picks one.
/// </summary>
internal static partial class Deploy
{
    /// <param name="source">repo:rev:path (a commit of a git repository) or a local folder (its files as they are now).</param>
    public static void Run(Contest c, string name, string source, string? front, List<string> env)
    {
        if (!NameRe().IsMatch(name)) throw new RigException($"entry name {name}: letters, digits, '.', '_' and '-' only");
        string tgz = Path.Combine(Paths.State, "entries", name + ".tgz");
        Directory.CreateDirectory(Path.GetDirectoryName(tgz)!);
        string described;
        string readme;
        string[] parts = source.Split(':');
        if (parts.Length == 3)
        {
            (string repo, string rev, string path) = (parts[0], parts[1], parts[2]);
            string full = Shell.Check("git", ["-C", repo, "rev-parse", rev]).Trim();
            Shell.Check("git", ["-C", repo, "archive", "--format=tar.gz", "-o", tgz, $"{full}:{path}"]);
            readme = Shell.Check("git", ["-C", repo, "show", $"{full}:{path}/README.md"]);
            described = $"{repo} {full} {path}";
        }
        else if (Directory.Exists(source))
        {
            string dir = Path.GetFullPath(source);
            // bsdtar on macOS: no AppleDouble files or extended attributes in the archive.
            Shell.Check("tar", ["--no-xattrs", "--exclude=./bin", "--exclude=./obj", "--exclude=*/bin", "--exclude=*/obj",
                "--exclude=TestResults", "--exclude=.git", "--exclude=BenchmarkDotNet.Artifacts", "-czf", tgz, "-C", dir, "."],
                env: new Dictionary<string, string> { ["COPYFILE_DISABLE"] = "1" });
            readme = File.ReadAllText(Path.Combine(dir, "README.md"));
            string? rev = Shell.Run("git", ["-C", dir, "rev-parse", "--short", "HEAD"]).Out.Trim();
            string dirty = Shell.Run("git", ["-C", dir, "status", "--porcelain", "."]).Out.Length > 0 ? " with uncommitted changes" : "";
            described = $"{dir} (folder at {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z, HEAD {rev}{dirty})";
        }
        else throw new RigException($"source {source}: want repo:rev:path or an existing folder");

        front ??= Front(c, readme);
        var vm = Rig.Find(c.TargetName);
        string dest = $"/opt/challenge/entries/{name}";
        Console.WriteLine($"deploying {name} ({front}) from {described}");
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
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] built {name}; `search {c.Key} {name}` scores it");
    }

    /// <summary>The README's front: multicore asks for a line `Front: nginx|direct`; the $12 README's table says "Nginx or direct".</summary>
    static string Front(Contest c, string readme)
    {
        var m = (c == Contest.Multicore ? MulticoreFrontRe() : TwelveFrontRe()).Match(readme);
        if (!m.Success) throw new RigException("the README doesn't say nginx or direct: pass --front");
        return m.Groups[1].Value.ToLowerInvariant();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex NameRe();

    [GeneratedRegex(@"(?im)^[*_ ]*front[*_ ]*:[*_ ]*(nginx|direct)")]
    private static partial Regex MulticoreFrontRe();

    [GeneratedRegex(@"(?i)nginx or direct[^\n]*?\b(nginx|direct)\b")]
    private static partial Regex TwelveFrontRe();
}
