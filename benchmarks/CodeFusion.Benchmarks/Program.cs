using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Running;

// Every *Benchmarks class in this assembly, with BenchConfig. Ours: --job short|full (default full) and --alloc-trace
// (an EventPipe allocation trace per benchmark); the rest goes to BenchmarkDotNet (--filter, --list flat, ...).
var rest = args.ToList();
bool shortJob = false, allocTrace = rest.Remove("--alloc-trace");
int job = rest.IndexOf("--job");
if (job >= 0 && job + 1 < rest.Count && rest[job + 1] is "short" or "full")
{
    shortJob = rest[job + 1] == "short";
    rest.RemoveRange(job, 2);
}
// a column must never show a number from an earlier run
if (Directory.Exists(CodeFusion.Benchmarks.Sidecar.Dir)) Directory.Delete(CodeFusion.Benchmarks.Sidecar.Dir, true);
BenchmarkSwitcher.FromAssembly(typeof(CodeFusion.Benchmarks.BenchConfig).Assembly)
    .Run(rest.ToArray(), CodeFusion.Benchmarks.BenchConfig.Create(shortJob, allocTrace));
