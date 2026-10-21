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

    /// <summary>The index of the loop this thread runs, from 0 to the server's loop count - 1; -1 on any other thread.</summary>
    public static int Current => t_index - 1;

    /// <summary>Marks this thread as loop <paramref name="index"/>'s, for the thread's life.</summary>
    internal static void Enter(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        t_index = index + 1;
    }
}
