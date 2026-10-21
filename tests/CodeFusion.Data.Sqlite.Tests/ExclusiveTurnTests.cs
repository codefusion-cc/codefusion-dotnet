using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// <see cref="SqliteDatabase.Exclusive"/>'s turns under load: reads that never stop and the writer's groups each wait
/// for at most one of the other's. Timed, so it runs alone, with fewer threads than cores.
/// </summary>
[Collection(nameof(AllocationRuns))]
public sealed class ExclusiveTurnTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact(Timeout = 60_000)]
    public async Task ReadsAndWritesThatNeverStopTakeTurns()
    {
        var s = _db.Open(_db.Database(exclusive: true));
        Assert.True((await Kv.WriteMany(s, "seed", 20_000)).IsOk);
        var alone = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Assert.True((await Kv.Write(s, "a" + i, i)).IsOk);
        alone.Stop();
        long readsBeside = 0, readTicks = 0;
        int writing = 0;
        using var stop = new CancellationTokenSource();
        // fewer readers than cores: the claim is about turns, and waiters spin while no batch holds the connection, so
        // on a runner with fewer cores than threads the spinners would take the CPU the read holding it needs
        var readers = new Task[Math.Clamp(Environment.ProcessorCount - 2, 1, 4)];
        for (int t = 0; t < readers.Length; t++)
            readers[t] = Task.Run(() =>
            {
                // each read a scan of 20,000 rows, back to back
                while (!stop.IsCancellationRequested)
                {
                    long start = Stopwatch.GetTimestamp();
                    Kv.Count(s, "seed%");
                    if (Volatile.Read(ref writing) != 0)
                    {
                        Interlocked.Add(ref readTicks, Stopwatch.GetTimestamp() - start);
                        Interlocked.Increment(ref readsBeside);
                    }
                }
            }, TestContext.Current.CancellationToken);
        // the writer is first in line: a write waits for the read running, not for every read that starts after it
        await Task.Delay(100, TestContext.Current.CancellationToken); // the readers are reading
        Volatile.Write(ref writing, 1);
        var took = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Assert.True((await Kv.Write(s, "w" + i, i)).IsOk);
        took.Stop();
        Volatile.Write(ref writing, 0);
        stop.Cancel();
        await Task.WhenAll(readers);
        // Bounded by the same writes alone and the reads beside them rather than a fixed time, so a slow machine slows
        // both sides alike: a write waits about one read (its turn comes after the one running), while a writer that
        // every new read overtakes waits until the readers stop.
        double read = Stopwatch.GetElapsedTime(0, readTicks).TotalMilliseconds / Math.Max(1, readsBeside);
        Assert.True(took.Elapsed.TotalMilliseconds < 2 * alone.Elapsed.TotalMilliseconds + 200 * 3 * read + 200,
            $"200 writes took {took.Elapsed.TotalMilliseconds:F0} ms beside reads of {read:F2} ms each, {alone.Elapsed.TotalMilliseconds:F0} ms alone");
        // and the reads go on beside the writes: a group lets a waiting read through before the next one
        Assert.True(readsBeside >= 100, $"{readsBeside} reads beside 200 writes");
    }
}
