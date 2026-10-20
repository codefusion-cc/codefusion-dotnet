using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace CodeFusion.Benchmarks;

/// <summary>
/// Numbers MemoryDiagnoser can't split: when a server runs inside the benchmark process, its allocations and the
/// client's land in one Allocated column. A benchmark keeps a <see cref="Meter"/>, counts its client's bytes on its
/// own thread, and at GlobalCleanup writes the split to a file per case; the host's columns read those files back.
/// </summary>
public static class Sidecar
{
    public const string DirVariable = "CODEFUSION_BENCH_SIDECAR";

    public static string ArtifactsPath { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("BENCH_ARTIFACTS") is { Length: > 0 } a ? a : "BenchmarkDotNet.Artifacts");

    public static string Dir => Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } d ? d : Path.Combine(ArtifactsPath, "sidecar");

    public static readonly IColumn[] Columns =
    [
        new Column("Server B/op", "bytes allocated per operation by every thread but the client's: Allocated minus Client B/op", (r, allocated) => allocated - r.ClientBytesPerOp, "0"),
        new Column("Client B/op", "bytes the client thread allocated per operation", (r, _) => r.ClientBytesPerOp, "0"),
        new Column("CPU us/op", "process CPU time per operation, client and server, including the harness between iterations", (r, _) => r.CpuMicrosPerOp, "0.0"),
    ];

    /// <summary>What a case wrote. ProcessBytesPerOp counts every allocation from Start on, the harness's between
    /// iterations too, so the Server column uses MemoryDiagnoser's exact Allocated instead.</summary>
    public sealed record Result(long Ops, double ProcessBytesPerOp, double ClientBytesPerOp, double CpuMicrosPerOp);

    /// <summary>The file a case writes and its column reads: type, method and parameter values.</summary>
    public static string PathFor(string type, string method, IEnumerable<object?> parameters) =>
        Path.Combine(Dir, string.Join("_", new[] { type, method }.Concat(parameters.Select(p => $"{p}"))
            .Select(s => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-')))) + ".json");

    /// <summary>Counts from <see cref="Start"/> on. Call <see cref="Op"/> around each operation's client work.</summary>
    public sealed class Meter
    {
        long _ops, _client, _total0, _cpu0Ticks;
        long _start;

        public void Start()
        {
            _ops = 0; _client = 0;
            _total0 = GC.GetTotalAllocatedBytes(true);
            _cpu0Ticks = Environment.CpuUsage.TotalTime.Ticks;
            _start = Stopwatch.GetTimestamp();
        }

        /// <summary>Adds one operation whose client thread allocated <paramref name="clientBytes"/>.</summary>
        public void Op(long clientBytes) { _ops++; _client += clientBytes; }

        public void Write(string type, string method, params object?[] parameters)
        {
            long total = GC.GetTotalAllocatedBytes(true) - _total0;
            double cpuUs = (Environment.CpuUsage.TotalTime.Ticks - _cpu0Ticks) / 10.0;
            if (_ops == 0) return;
            var r = new Result(_ops, total / (double)_ops, _client / (double)_ops, cpuUs / _ops);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(type, method, parameters), JsonSerializer.Serialize(r));
        }
    }

    sealed class Column(string name, string legend, Func<Result, double, double> value, string format) : IColumn
    {
        public string Id => "Sidecar." + name;
        public string ColumnName => name;
        // a zero (the client's bytes) is the point, so the column stays even when every row shows it
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public string Legend => legend;

        public bool IsAvailable(Summary summary) => summary.BenchmarksCases.Any(c => Read(c) != null);
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
            Read(benchmarkCase) is { } r && summary[benchmarkCase] is { } report
                ? value(r, report.GcStats.GetBytesAllocatedPerOperation(benchmarkCase) ?? double.NaN).ToString(format, style.CultureInfo)
                : "-";

        static Result? Read(BenchmarkCase c)
        {
            string path = PathFor(c.Descriptor.Type.Name, c.Descriptor.WorkloadMethod.Name, c.Parameters.Items.Select(p => p.Value));
            return File.Exists(path) ? JsonSerializer.Deserialize<Result>(File.ReadAllText(path)) : null;
        }
    }
}
