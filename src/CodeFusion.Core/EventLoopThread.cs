using System;

namespace CodeFusion;

/// <summary>
/// Which event loop the current thread runs. A server's event-loop transport (CodeFusion.Http's
/// <c>HttpTransport.EventLoop</c>) marks each of its loop threads, so code a handler calls can keep state per loop (a
/// connection, an open transaction) and use it without locks, and an <see cref="IServiceRegistry.OnEventLoopBatch"/>
/// action knows which loop's state to finish.
/// </summary>
public static class EventLoopThread
{
    [ThreadStatic] static int t_index; // the index + 1: 0 on every other thread
    [ThreadStatic] static bool t_batchEnd;

    /// <summary>The index of the loop this thread runs, from 0 to the server's loop count - 1; -1 on any other thread.</summary>
    public static int Current => t_index - 1;

    /// <summary>
    /// On a loop's thread: asks for the batch's actions (<see cref="IServiceRegistry.OnEventLoopBatch"/>) to run once
    /// more before the loop waits again. A provider calls it whenever it opens a batch, a write it will commit in its
    /// action, from code the loop didn't start as a handler: a continuation that resumes while the batch's answers are
    /// sent, after the actions ran. The loop runs another round for it, or, past its rounds, its next wait doesn't
    /// block, so the batch ends in the wake-up that opened it rather than at the next unrelated event. Off a loop it
    /// does nothing (a write there doesn't wait for a batch), and it never allocates.
    /// </summary>
    public static void RequestBatchEnd()
    {
        if (t_index != 0) t_batchEnd = true;
    }

    /// <summary>The loop takes the request: true if one came since it last looked.</summary>
    internal static bool TakeBatchEndRequest()
    {
        bool requested = t_batchEnd;
        t_batchEnd = false;
        return requested;
    }

    /// <summary>The loop looks without taking.</summary>
    internal static bool BatchEndRequested => t_batchEnd;

    /// <summary>Marks this thread as loop <paramref name="index"/>'s, for the thread's life.</summary>
    internal static void Enter(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        t_index = index + 1;
    }
}
