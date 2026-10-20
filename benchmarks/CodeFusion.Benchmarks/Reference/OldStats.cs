// The reader and writer counters of Stats.cs at 21abce6, which OldReader and OldWriter update.
using System.Threading;

namespace CodeFusion.Benchmarks.Reference;

public static class OldStats
{
    public static long WriteGroups, WritesGrouped, WriteGroupMax, CommitTicks, CommitTicksMax, WriteWaitTicksMax, WriteRetries, WritesRefused;
    public static long ReadGroups, ReadsGrouped, ReadGroupMax, ReadReruns, ReadsRefused;

    public static void Max(ref long field, long value)
    {
        for (long seen = Volatile.Read(ref field); value > seen; seen = Volatile.Read(ref field))
            if (Interlocked.CompareExchange(ref field, value, seen) == seen) return;
    }
}
