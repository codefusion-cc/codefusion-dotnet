// Linked into the Postgres and the data layer's tests.
global using CodeFusion.Tests;
using System;
using System.Collections.Generic;
using System.Threading;
using CodeFusion.Data.Postgres;
using Xunit;

namespace CodeFusion.Tests;

/// <summary>
/// What holds once a reader's or writer's loops have stopped, whatever the test did: no loop caught an exception that
/// no failing connection explains (the commands it hit end as Broken, so the test may well pass), nothing is left
/// queued, and in Debug builds every job rented for the loops is back in the pool (each command's task read once).
/// </summary>
internal static class LoopChecks
{
    /// <summary>Stops a data source's reader and writer, then checks them.</summary>
    public static void Stop(params IEnumerable<PgDataSource?> sources)
    {
        var loops = new List<PgLoops>();
        foreach (var s in sources)
            if (s is not null) loops.AddRange([s.Reader, s.Writer]);
        Stop(loops);
    }

    /// <summary>Takes no more commands, waits for the loops to end, then checks them.</summary>
    public static void Stop(params IEnumerable<PgLoops> loops)
    {
        foreach (var l in loops) l.Complete();
        var failures = new List<string>();
        foreach (var l in loops)
        {
            if (!l.WaitStopped(TimeSpan.FromSeconds(10))) failures.Add("a loop still runs 10 s after Complete");
            if (Volatile.Read(ref l.UnexpectedFailures) is int n and > 0)
                failures.Add($"{n} exception(s) in the loops that no failing connection explains, the first: {l.FirstUnexpected}");
            if (l.Queued > 0) failures.Add($"{l.Queued} command(s) left in the queue");
#if DEBUG
            if (l.JobsOut is int jobs and not 0) failures.Add($"{jobs} job(s) never back in the pool: a command's task not read, or read twice");
#endif
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>For loops a test shares and doesn't stop: no unexpected exception so far, and (Debug builds) no job out
    /// while the caller has nothing in flight.</summary>
    public static void AssertClean(PgLoops loops)
    {
        Assert.True(Volatile.Read(ref loops.UnexpectedFailures) == 0, $"exceptions in the loops that no failing connection explains, the first: {loops.FirstUnexpected}");
#if DEBUG
        Assert.True(loops.JobsOut == 0, $"{loops.JobsOut} job(s) out with nothing in flight");
#endif
    }
}
