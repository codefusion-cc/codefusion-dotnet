using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace CodeFusion.Benchmarks;

/// <summary>
/// The config every benchmark runs with: memory, threading and exception diagnosers, JSON and GitHub markdown
/// output, the server's runtime settings, and the <see cref="Sidecar"/> columns. Program.cs picks the job:
/// <c>--job short</c> while developing, <c>--job full</c> (the default) for a comparison.
/// </summary>
public static class BenchConfig
{
    public static IConfig Create(bool shortJob, bool allocTrace)
    {
        var job = (shortJob ? Job.ShortRun : Job.Default)
            .WithId(shortJob ? "short" : "full")
            // what the server runs with: the contest server's gen0 budget, GC and thread-pool settings
            .WithGcServer(true)
            .WithGcConcurrent(false)
            .WithEnvironmentVariables(
                new EnvironmentVariable("DOTNET_GCgen0size", "0x4000000"),
                new EnvironmentVariable("DOTNET_TieredPGO", "1"),
                new EnvironmentVariable("DOTNET_ThreadPool_UnfairSemaphoreSpinLimit", "0"),
                new EnvironmentVariable(Sidecar.DirVariable, Sidecar.Dir));
        var config = ManualConfig.CreateEmpty()
            .AddJob(job)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddColumn(Sidecar.Columns)
            .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
            .AddValidator(DefaultConfig.Instance.GetValidators().ToArray())
            .AddAnalyser(DefaultConfig.Instance.GetAnalysers().ToArray())
            .AddDiagnoser(MemoryDiagnoser.Default, ThreadingDiagnoser.Default, ExceptionDiagnoser.Default)
            .AddExporter(JsonExporter.Full, MarkdownExporter.GitHub)
            .WithArtifactsPath(Sidecar.ArtifactsPath);
        // an EventPipe trace per benchmark with every allocation's type and stack (open the .nettrace in PerfView);
        // off by default because it slows the run and skews the times
        if (allocTrace) config.AddDiagnoser(new EventPipeProfiler(EventPipeProfile.GcVerbose));
        return config;
    }
}
