using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks.Sources;
using Xunit;

#pragma warning disable xUnit1031

namespace CodeFusion.Core.Tests;

public class InlineValueTaskSourceCoreTests
{
    sealed class Holder { public InlineValueTaskSourceCore<int> C; }

    [Fact]
    public void AContinuationRegisteredBeforeTheResultRunsWhenItArrives()
    {
        var h = new Holder();
        int seen = -1;
        h.C.OnCompleted(s => seen = ((Holder)s!).C.GetResult(h.C.Version), h, h.C.Version, ValueTaskSourceOnCompletedFlags.None);
        Assert.Equal(-1, seen);
        Assert.Equal(ValueTaskSourceStatus.Pending, h.C.GetStatus(h.C.Version));
        h.C.SetResult(42);
        Assert.Equal(42, seen); // inline, on the completing thread
    }

    [Fact]
    public void AContinuationRegisteredAfterTheResultRunsInlineAndAllocatesNothing()
    {
        var h = new Holder();
        int runs = 0;
        int thread = -1;
        Action<object?> continuation = _ => { runs++; thread = Environment.CurrentManagedThreadId; };
        for (int i = 0; i < 100; i++)
        {
            h.C.SetResult(i);
            h.C.OnCompleted(continuation, null, h.C.Version, ValueTaskSourceOnCompletedFlags.None);
            h.C.Reset();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            h.C.SetResult(i);
            h.C.OnCompleted(continuation, null, h.C.Version, ValueTaskSourceOnCompletedFlags.None);
            h.C.Reset();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); // the runtime's core queues a work item here
        Assert.Equal(1100, runs);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }

    [Fact]
    public void ALateContinuationOnANearlyFullStackGoesToTheThreadPool()
    {
        var h = new Holder();
        h.C.SetResult(1);
        using var ran = new ManualResetEventSlim();
        int deepThread = -1, ranOn = -1;
        var t = new Thread(() =>
        {
            deepThread = Environment.CurrentManagedThreadId;
            NearTheStackLimit(() => h.C.OnCompleted(_ =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                ran.Set();
            }, null, h.C.Version, ValueTaskSourceOnCompletedFlags.None));
        }, maxStackSize: 1 << 20);
        t.Start();
        t.Join();
        Assert.True(ran.Wait(5000, TestContext.Current.CancellationToken));
        Assert.NotEqual(deepThread, ranOn); // not one frame deeper on a stack about to overflow
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NearTheStackLimit(Action then)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            then();
            return;
        }
        Span<byte> frame = stackalloc byte[1024];
        frame[0] = 1;
        NearTheStackLimit(then);
        GC.KeepAlive(frame[0]);
    }

    [Fact]
    public void CompletingTwiceIsRefusedAndRunsNoContinuationAgain()
    {
        var h = new Holder();
        int runs = 0;
        h.C.OnCompleted(_ => runs++, null, h.C.Version, ValueTaskSourceOnCompletedFlags.None);
        h.C.SetResult(1);
        Assert.Throws<InvalidOperationException>(() => h.C.SetResult(2));
        Assert.Throws<InvalidOperationException>(() => h.C.SetException(new InvalidCastException()));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void ASecondAwaitWhilePendingIsRefusedWithoutTouchingTheFirst()
    {
        var h = new Holder();
        object? seen = null;
        h.C.OnCompleted(s => seen = s, "first", h.C.Version, ValueTaskSourceOnCompletedFlags.None);
        Assert.Throws<InvalidOperationException>(() => h.C.OnCompleted(_ => { }, "second", h.C.Version, ValueTaskSourceOnCompletedFlags.None));
        h.C.SetResult(1);
        Assert.Equal("first", seen); // the first await still gets its own state
    }

    [Fact]
    public void ResultsAndErrorsReachTheReader()
    {
        var h = new Holder();
        h.C.SetResult(7);
        Assert.Equal(ValueTaskSourceStatus.Succeeded, h.C.GetStatus(h.C.Version));
        Assert.Equal(7, h.C.GetResult(h.C.Version));
        h.C.Reset();
        h.C.SetException(new InvalidCastException("x"));
        Assert.Equal(ValueTaskSourceStatus.Faulted, h.C.GetStatus(h.C.Version));
        Assert.Equal("x", Assert.Throws<InvalidCastException>(() => h.C.GetResult(h.C.Version)).Message);
        h.C.Reset();
        h.C.SetException(new OperationCanceledException());
        Assert.Equal(ValueTaskSourceStatus.Canceled, h.C.GetStatus(h.C.Version));
    }

    [Fact]
    public void MisuseThrowsInsteadOfMixingUpOperations()
    {
        var h = new Holder();
        short old = h.C.Version;
        Assert.Throws<InvalidOperationException>(() => h.C.GetResult(old)); // not completed
        h.C.OnCompleted(_ => { }, null, old, ValueTaskSourceOnCompletedFlags.None);
        Assert.Throws<InvalidOperationException>(() => h.C.OnCompleted(_ => { }, null, old, ValueTaskSourceOnCompletedFlags.None)); // awaited twice
        h.C.SetResult(1);
        Assert.Throws<InvalidOperationException>(() => h.C.SetResult(2)); // completed twice
        h.C.Reset();
        Assert.Throws<InvalidOperationException>(() => h.C.GetStatus(old)); // read after reuse
        Assert.Throws<InvalidOperationException>(() => h.C.GetResult(old));
        bool ran = false;
        Assert.Throws<InvalidOperationException>(() => h.C.OnCompleted(_ => ran = true, null, old, ValueTaskSourceOnCompletedFlags.None));
        h.C.SetResult(3);
        Assert.False(ran); // a stale await never gets this operation's result
    }

    /// <summary>
    /// The pattern the pooled sources follow: the producer completes on one thread; the consumer sees the result on
    /// another, reads it and resets at once, then registers the next operation's continuation, which nothing has
    /// completed and which must therefore never run. Oversubscribed cores preempt the producer at random points.
    /// </summary>
    [Fact]
    public void AResetRightAfterTheResultNeverRunsTheNextOperationsContinuation()
    {
        var h = new Holder();
        using var go = new Barrier(2);
        using var done = new Barrier(2);
        // CI oversubscribes every core for the full count; a shared dev machine runs a quarter, on half the cores.
        bool ci = Environment.GetEnvironmentVariable("CI") is "true";
        int early = 0, rounds = ci ? 200_000 : 50_000;
        bool stop = false;
        Action<object?> next = _ => Interlocked.Increment(ref early);
        var noise = new Thread[ci ? Environment.ProcessorCount : Math.Max(1, Environment.ProcessorCount / 2)];
        for (int t = 0; t < noise.Length; t++)
        {
            noise[t] = new Thread(() => { while (!Volatile.Read(ref stop)) Thread.SpinWait(50); }) { IsBackground = true };
            noise[t].Start();
        }
        var producer = new Thread(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                go.SignalAndWait(TestContext.Current.CancellationToken);
                h.C.SetResult(i);
                done.SignalAndWait(TestContext.Current.CancellationToken);
            }
        });
        producer.Start();
        try
        {
            for (int i = 0; i < rounds; i++)
            {
                go.SignalAndWait(TestContext.Current.CancellationToken);
                short v = h.C.Version;
                while (h.C.GetStatus(v) == ValueTaskSourceStatus.Pending) Thread.SpinWait(1);
                Assert.Equal(i, h.C.GetResult(v));
                h.C.Reset();
                h.C.OnCompleted(next, null, h.C.Version, ValueTaskSourceOnCompletedFlags.None);
                done.SignalAndWait(TestContext.Current.CancellationToken);
                h.C.Reset();
            }
        }
        finally
        {
            producer.Join();
            Volatile.Write(ref stop, true);
        }
        Assert.Equal(0, early);
    }
}
