using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace CodeFusion.ContestBench;

/// <summary>A finished process: exit code and what it printed.</summary>
internal readonly record struct ProcessResult(int Code, string Out, string Err)
{
    public bool Ok => Code == 0;
}

/// <summary>Runs local programs (gcloud, ssh, scp, git, tar, go) with argument lists, never through a shell.</summary>
internal static class Shell
{
    public static bool Verbose { get; set; }

    /// <summary>Runs a program and captures its output. <paramref name="echo"/> streams stdout and stderr too.</summary>
    public static ProcessResult Run(string file, IEnumerable<string> args, string? cwd = null, bool echo = false,
        string? stdin = null, IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (cwd != null) psi.WorkingDirectory = cwd;
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        if (Verbose) Console.Error.WriteLine($"  $ {file} {string.Join(' ', psi.ArgumentList)}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var o = new StringBuilder();
        var e = new StringBuilder();
        p.OutputDataReceived += (_, d) =>
        {
            if (d.Data == null) return;
            lock (o) o.Append(d.Data).Append('\n');
            if (echo) Console.WriteLine(d.Data);
        };
        p.ErrorDataReceived += (_, d) =>
        {
            if (d.Data == null) return;
            lock (e) e.Append(d.Data).Append('\n');
            if (echo) Console.Error.WriteLine(d.Data);
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        if (timeout is { } t && !p.WaitForExit(t))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            return new ProcessResult(124, o.ToString(), e + $"\ntimed out after {t}");
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, o.ToString(), e.ToString());
    }

    /// <summary>Runs a program and throws with its stderr when it fails.</summary>
    public static string Check(string file, IEnumerable<string> args, string? cwd = null, bool echo = false,
        string? stdin = null, IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        var r = Run(file, args, cwd, echo, stdin, env, timeout);
        if (!r.Ok)
            throw new RigException($"{file} {string.Join(' ', args)} exited {r.Code}: {Tail(r.Err.Length > 0 ? r.Err : r.Out, 2000)}");
        return r.Out;
    }

    /// <summary>Retries <paramref name="action"/> on <see cref="RigException"/>, waiting longer each time.</summary>
    public static T Retry<T>(string what, int attempts, Func<T> action)
    {
        for (int i = 1; ; i++)
        {
            try { return action(); }
            catch (RigException ex) when (i < attempts)
            {
                Console.Error.WriteLine($"  {what} failed (attempt {i}/{attempts}): {Tail(ex.Message, 300)}");
                Thread.Sleep(TimeSpan.FromSeconds(5 * i));
            }
        }
    }

    public static string Tail(string s, int max) => s.Length <= max ? s.Trim() : "..." + s[^max..].Trim();

    /// <summary>Quotes a value for a POSIX shell on the remote side.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string Which(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string f = Path.Combine(dir, name);
            if (File.Exists(f)) return f;
        }
        throw new RigException($"{name} not found on PATH");
    }
}

/// <summary>An expected failure: printed without a stack trace.</summary>
internal sealed class RigException : Exception
{
    public RigException(string message) : base(message) { }
    public RigException(string message, Exception inner) : base(message, inner) { }
    public RigException() { }
}
