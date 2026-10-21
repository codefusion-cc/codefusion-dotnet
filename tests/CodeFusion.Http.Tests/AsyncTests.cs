using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051 // the race test drives threads and blocking waits on purpose

namespace CodeFusion.Http.Tests;

/// <summary>Pending&lt;T&gt; and the pooled builders. The value-task core under them is tested in CodeFusion.Core.Tests.</summary>
public class AsyncTests
{
    /// <summary>A source completed by the test, from any thread.</summary>
    sealed class Gate : IValueTaskSource<int>
    {
        ManualResetValueTaskSourceCore<int> _core; // completed on the waiting thread or before the wait, in these tests
        public ValueTask<int> Wait()
        {
            _core.Reset();
            return new ValueTask<int>(this, _core.Version);
        }
        public void Open(int value) => _core.SetResult(value);
        public void Fail(Exception e) => _core.SetException(e);
        public int GetResult(short token) => _core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> c, object? s, short token, ValueTaskSourceOnCompletedFlags f) => _core.OnCompleted(c, s, token, f);
    }

    static async Pending<int> Twice(Gate gate)
    {
        int a = await gate.Wait();
        return 2 * a;
    }

    static async Pending<int> Immediate(int x)
    {
        await Task.CompletedTask;
        return x + 1;
    }

    static async Pending<int> ThrowsAtOnce(bool yes)
    {
        if (yes) throw new InvalidOperationException("at once");
        await Task.CompletedTask;
        return 0;
    }

    static async Pending<int> ThrowsLater(Gate gate)
    {
        await gate.Wait();
        throw new InvalidOperationException("later");
    }

    [Fact]
    public void ACompletedMethodNeedsNoBox()
    {
        Pending<int> p = Immediate(4);
        Assert.True(p.IsCompleted);
        Assert.Null(p.Box);
        Assert.Equal(5, p.GetResult());
        Assert.Equal(7, new Pending<int>(7).GetResult());
    }

    [Fact]
    public void AnExceptionBeforeTheFirstWaitIsKept()
    {
        Pending<int> p = ThrowsAtOnce(true);
        Assert.True(p.IsCompleted);
        var e = Assert.Throws<InvalidOperationException>(() => p.GetResult());
        Assert.Equal("at once", e.Message);
        Assert.Contains(nameof(ThrowsAtOnce), e.StackTrace, StringComparison.Ordinal); // the original trace survives
    }

    [Fact]
    public async Task AWaitingMethodRunsInAPooledBoxAndGivesItBack()
    {
        var gate = new Gate();
        Pending<int> p = Twice(gate);
        Assert.False(p.IsCompleted);
        PooledBox<int> box = p.Box!;
        await Task.Run(() => gate.Open(21));
        Assert.True(p.IsCompleted);
        Assert.Equal(42, p.GetResult());
        Assert.Throws<InvalidOperationException>(() => p.GetResult()); // read twice
        // the next call of the same method gets the same box back
        Pending<int> q = Twice(gate);
        Assert.Same(box, q.Box);
        gate.Open(1);
        Assert.Equal(2, q.GetResult());
    }

    [Fact]
    public async Task AnExceptionAfterAWaitComesThroughTheAwait()
    {
        var gate = new Gate();
        Pending<int> p = ThrowsLater(gate);
        gate.Open(1);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(async () => await p);
        Assert.Equal("later", e.Message);
    }

    [Fact]
    public async Task AwaitingAPendingFromAnotherMethod()
    {
        var gate = new Gate();
        static async Pending<int> Outer(Gate g) => await Twice(g) + 1;
        Pending<int> p = Outer(gate);
        _ = Task.Run(() => gate.Open(10));
        Assert.Equal(21, await p.AsValueTask());
    }

    [Fact]
    public void AWaitingMethodAllocatesNothingOnceItsBoxIsPooled()
    {
        Assert.SkipWhen(AllocationTests.Debugger(), "Debug builds make every async method's state machine a class: run in Release");
        var gate = new Gate();
        int Run()
        {
            Pending<int> p = Twice(gate);
            gate.Open(3);
            return p.GetResult();
        }
        for (int i = 0; i < 100; i++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
