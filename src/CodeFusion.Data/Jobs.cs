using System;
using System.Diagnostics;
using System.Threading;

namespace CodeFusion.Data;

/// <summary>Where a provider's pooled job (one command, its buffers and its completion) is in its life.</summary>
internal enum JobStep : byte { Pooled, Building, Held, Queued, Completed }

/// <summary>
/// The life of a provider's pooled job, the part every provider shares: the generation that keeps a stale handle off a
/// recycled job, the hold that one caller claims for the job's one run or discard, and in Debug builds each step
/// checked (a step out of order hands one command's rows or result to another) and the job counted out of its pool.
/// A field of the job; its calls compile away in Release where marked.
/// </summary>
internal struct JobLife(string kind, bool queuesUnheld)
{
    /// <summary>Changes each time the job goes back to the pool, so a stale command or held copy can't touch it.</summary>
    public int Generation;
    /// <summary>While held and not yet taken for its run or discard: the generation it was held at, plus one; else 0.
    /// Taken with one compare-exchange, so of two callers racing only one wins, and one holding a stale generation never.</summary>
    long _heldAt;
    JobStep _step;
    int _rentedGeneration;
    JobCounter? _owner;

    /// <summary>Debug: where the job is (Release builds never move it).</summary>
    public readonly JobStep Step => _step;

    /// <summary>Debug: the generation it was rented at.</summary>
    public readonly int RentedGeneration => _rentedGeneration;

    /// <summary>Built: held for one later run or discard.</summary>
    public void Hold()
    {
        To(JobStep.Held);
        Volatile.Write(ref _heldAt, (uint)Generation + 1L);
    }

    /// <summary>Takes the held job for its run or discard; false if another caller took it, or it is no longer the
    /// generation <paramref name="generation"/> held.</summary>
    public bool Claim(int generation)
    {
        long held = (uint)generation + 1L;
        return Interlocked.CompareExchange(ref _heldAt, 0, held) == held;
    }

    /// <summary>Back in the pool: a new generation, and in Debug counted back in. After <see cref="CheckReturnable"/>.</summary>
    public void Returned()
    {
        Generation++;
        Volatile.Write(ref _heldAt, 0);
        To(JobStep.Pooled);
        Uncount();
    }

    /// <summary>Moves the job to <paramref name="next"/>, if its life allows that step.</summary>
    [Conditional("DEBUG")]
    public void To(JobStep next)
    {
        bool allowed = (_step, next) switch
        {
            (JobStep.Pooled, JobStep.Building) => true,
            (JobStep.Building, JobStep.Held) or (JobStep.Held, JobStep.Queued) => true,
            (JobStep.Building, JobStep.Queued) => queuesUnheld,
            (JobStep.Queued, JobStep.Completed) => true,
            (not JobStep.Pooled, JobStep.Pooled) => true, // read (Completed), run inline (Held), or given back unrun
            _ => false,
        };
        if (!allowed)
            throw new InvalidOperationException(next == JobStep.Completed
                ? $"a {kind} job was completed while {_step} (twice, or after its return)"
                : $"a {kind} job went from {_step} to {next}");
        if (next == JobStep.Building) _rentedGeneration = Generation;
        _step = next;
    }

    /// <summary>The job is at <paramref name="step"/>: a provider touches a job's run only while it is queued, since after
    /// its completion it may be someone else's.</summary>
    [Conditional("DEBUG")]
    public readonly void CheckIs(JobStep step)
    {
        if (_step != step) throw new InvalidOperationException($"a {kind} job was used while {_step}, not {step}");
    }

    /// <summary>The job isn't in the pool already.</summary>
    [Conditional("DEBUG")]
    public readonly void CheckReturnable()
    {
        if (_step == JobStep.Pooled) throw new InvalidOperationException($"a {kind} job was returned twice");
    }

    /// <summary>Debug: counts the job out against <paramref name="owner"/> until it is back in the pool.</summary>
    [Conditional("DEBUG")]
    public void CountOut(JobCounter owner)
    {
        _owner = owner;
        Interlocked.Increment(ref owner.Out);
    }

    [Conditional("DEBUG")]
    void Uncount()
    {
        if (_owner is { } owner) Interlocked.Decrement(ref owner.Out);
        _owner = null;
    }

    /// <summary>Debug: fills a returned job's buffer with <see cref="JobPool.Returned"/>, so a reader that kept it sees garbage.</summary>
    [Conditional("DEBUG")]
    public static void Poison(byte[]? buffer) => buffer?.AsSpan().Fill(JobPool.Returned);

    /// <summary>Debug: a pooled job's buffer sat untouched; anything but <see cref="JobPool.Returned"/> was written after its return.</summary>
    [Conditional("DEBUG")]
    public readonly void CheckPoisoned(byte[]? buffer)
    {
        if (buffer is not null && buffer.AsSpan().IndexOfAnyExcept(JobPool.Returned) >= 0)
            throw new InvalidOperationException($"a pooled {kind} job's buffer was written to after its return");
    }
}

/// <summary>Debug: how many jobs a provider's source has out of the pool. A test that ends with commands in flight
/// sees it above 0: a command's task not read, or read twice.</summary>
internal sealed class JobCounter
{
    public int Out;
}

/// <summary>What every provider's job pool shares.</summary>
internal static class JobPool
{
    /// <summary>Buffers past this size go with a returned job instead of being pooled.</summary>
    public const int MaxPooledBytes = 64 << 10;

    /// <summary>What a returned job's buffers hold in Debug builds.</summary>
    public const byte Returned = 0xDD;
}

/// <summary>
/// A provider's pool of its jobs, with their buffers grown to what they carry. A stack, so the jobs in use are the few
/// warm ones; most of the jobs a burst left behind are dropped, since a workload has a few thousand commands in
/// flight. A provider whose jobs are rented and given back on one thread also keeps one per thread, no lock.
/// </summary>
internal static class JobPool<T> where T : class
{
    const int MaxPooled = 4096;
    static readonly T?[] Pool = new T?[MaxPooled];
    static readonly Lock PoolLock = new();
    static int _pooled;
    /// <summary>The job this thread gave back last. (A shared lock alone capped SQLite's reads at about 2 M/s whatever
    /// the thread count.)</summary>
    [ThreadStatic] static T? t_cached;

    /// <summary>A pooled job, or null: this thread's own first when <paramref name="local"/>.</summary>
    public static T? Take(bool local)
    {
        if (local && t_cached is { } mine)
        {
            t_cached = null;
            return mine;
        }
        lock (PoolLock)
        {
            if (_pooled == 0) return null;
            var job = Pool[--_pooled];
            Pool[_pooled] = null;
            return job;
        }
    }

    /// <summary>Gives <paramref name="job"/> back: to this thread when <paramref name="local"/> and it has none.</summary>
    public static void Give(T job, bool local)
    {
        if (local && t_cached is null)
        {
            t_cached = job;
            return;
        }
        lock (PoolLock)
            if (_pooled < MaxPooled) Pool[_pooled++] = job;
    }
}
