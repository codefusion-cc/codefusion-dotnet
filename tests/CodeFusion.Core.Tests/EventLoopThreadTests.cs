using System;
using System.Threading;
using Xunit;

namespace CodeFusion.Core.Tests;

/// <summary>
/// The current loop's index, and the request that a batch opened on a loop's thread gets its end in the same wake-up:
/// a no-op that allocates nothing off a loop, taken exactly once on one.
/// </summary>
public sealed class EventLoopThreadTests
{
    [Fact]
    public void OffALoopRequestingABatchEndDoesNothingAndAllocatesNothing()
    {
        Assert.Equal(-1, EventLoopThread.Current);
        EventLoopThread.RequestBatchEnd(); // warm: the first call may JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) EventLoopThread.RequestBatchEnd();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(EventLoopThread.TakeBatchEndRequest());
    }

    [Fact]
    public void OnALoopTheRequestIsTakenOnceAndOnlyOnThatThread()
    {
        bool first = false, second = true, other = true;
        var loop = new Thread(() =>
        {
            EventLoopThread.Enter(3);
            EventLoopThread.RequestBatchEnd();
            EventLoopThread.RequestBatchEnd(); // two requests are one batch end
            first = EventLoopThread.TakeBatchEndRequest();
            second = EventLoopThread.TakeBatchEndRequest();
            EventLoopThread.RequestBatchEnd();
            var off = new Thread(() => other = EventLoopThread.TakeBatchEndRequest());
            off.Start();
            off.Join();
        });
        loop.Start();
        loop.Join();
        Assert.True(first);
        Assert.False(second);
        Assert.False(other);
    }
}
