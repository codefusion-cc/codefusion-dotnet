using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;

namespace CodeFusion.TechEmpower;

/// <summary>
/// With CODEFUSION_ALLOC_TRACE set, the runtime's AllocationTick events (one per about 100 KB a thread allocates,
/// naming the type that crossed it), summed by type and printed every 10 s: what still allocates under load, for the
/// allocation gate to name. Off by default; the listener itself allocates a little per event.
/// </summary>
sealed class AllocationTrace : EventListener
{
    readonly ConcurrentDictionary<string, long> _bytes = new();
    Timer? _timer;

    public static AllocationTrace? StartIfAsked() =>
        Environment.GetEnvironmentVariable("CODEFUSION_ALLOC_TRACE") is { Length: > 0 } ? new AllocationTrace() : null;

    AllocationTrace() => _timer = new Timer(_ => Print(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    protected override void OnEventSourceCreated(EventSource source)
    {
        // the GC keyword at Verbose level is what turns AllocationTick on
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.PayloadNames is null) return;
        int type = e.PayloadNames.IndexOf("TypeName"), amount = e.PayloadNames.IndexOf("AllocationAmount64");
        if (type < 0 || amount < 0) return;
        long bytes = Convert.ToInt64(e.Payload![amount], System.Globalization.CultureInfo.InvariantCulture);
        _bytes.AddOrUpdate(e.Payload[type] as string ?? "?", bytes, (_, v) => v + bytes);
    }

    void Print()
    {
        var top = _bytes.ToArray().OrderByDescending(kv => kv.Value).Take(12).ToList();
        _bytes.Clear();
        if (top.Count == 0) return;
        Console.Error.WriteLine("allocations in 10 s, by the type that crossed each 100 KB: " +
            string.Join("; ", top.Select(kv => $"{kv.Key} {kv.Value / 1024} KB")));
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        base.Dispose();
    }
}
