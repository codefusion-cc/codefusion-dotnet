using System;
using System.Threading;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// The commands waiting for a loop: a fixed ring (allocated once at its limit, so queueing allocates nothing) under
/// one lock. A new command wakes one idle loop, and only when no woken loop is on its way: that loop takes
/// everything queued by the time it runs, so a burst becomes one group instead of one wakeup and one group per
/// command. A loop that leaves commands behind wakes the next. The loop woken is the one that went idle last, so
/// under light load the work stays on one warm thread and Postgres backend instead of rotating through cold ones
/// (a lone 20-article page took 720 µs rotating through 16 loops, 420 µs on one).
/// </summary>
sealed class PgQueue
{
    /// <summary>One loop's own place to sleep, so the one to wake can be chosen.</summary>
    public sealed class Waiter
    {
        internal bool Woken;
    }

    readonly object _lock = new();
    readonly PgJob?[] _ring;
    int _head, _count, _waking;
    bool _stopped;
    Waiter?[] _idle = new Waiter?[8];
    int _idleCount;

    public PgQueue(int capacity) => _ring = new PgJob?[capacity];

    public int Count => Volatile.Read(ref _count);

    /// <summary>The loops asleep in <see cref="Take"/>, waiting for commands. For tests.</summary>
    public int Sleeping => Volatile.Read(ref _idleCount);

    public enum Added { Ok, Full, Stopped }

    public Added TryAdd(PgJob job)
    {
        Waiter? wake;
        lock (_lock)
        {
            if (_stopped) return Added.Stopped;
            if (_count == _ring.Length) return Added.Full;
            _ring[(_head + _count) % _ring.Length] = job;
            Volatile.Write(ref _count, _count + 1);
            wake = PickIdle();
        }
        Wake(wake);
        return Added.Ok;
    }

    /// <summary>Waits for commands and takes up to <paramref name="max"/> into <paramref name="into"/>, in queue
    /// order. 0 once the queue is stopped and empty. <paramref name="slept"/>: the loop had to wait for them.</summary>
    public int Take(Waiter me, PgJob?[] into, int max, out bool slept)
    {
        slept = false;
        while (true)
        {
            Waiter? wake = null;
            int n = 0;
            lock (_lock)
            {
                if (me.Woken)
                {
                    me.Woken = false;
                    _waking--;
                }
                if (_count > 0)
                {
                    n = Math.Min(max, _count);
                    for (int i = 0; i < n; i++)
                    {
                        int at = (_head + i) % _ring.Length;
                        into[i] = _ring[at];
                        _ring[at] = null;
                    }
                    _head = (_head + n) % _ring.Length;
                    Volatile.Write(ref _count, _count - n);
                    if (_count > 0) wake = PickIdle();
                }
                else if (_stopped) return 0;
                else
                {
                    if (_idleCount == _idle.Length) Array.Resize(ref _idle, _idleCount * 2);
                    _idle[_idleCount++] = me;
                }
            }
            if (n > 0)
            {
                Wake(wake);
                return n;
            }
            lock (me)
                while (!me.Woken) Monitor.Wait(me);
            slept = true;
        }
    }

    /// <summary>Takes no more; the loops take what is queued, then <see cref="Take"/> returns 0.</summary>
    public void Stop()
    {
        Waiter?[] all;
        lock (_lock)
        {
            _stopped = true;
            all = _idle[.._idleCount];
            _waking += _idleCount;
            Array.Clear(_idle, 0, _idleCount);
            _idleCount = 0;
        }
        foreach (var w in all) Wake(w);
    }

    /// <summary>Under the lock: the loop to wake, the last to go idle, unless one is already on its way.</summary>
    Waiter? PickIdle()
    {
        if (_waking > 0 || _idleCount == 0) return null;
        _waking++;
        var w = _idle[--_idleCount]!;
        _idle[_idleCount] = null;
        return w;
    }

    /// <summary>Outside the queue's lock, so the woken loop doesn't wake up only to wait for it.</summary>
    static void Wake(Waiter? w)
    {
        if (w is null) return;
        lock (w)
        {
            w.Woken = true;
            Monitor.Pulse(w);
        }
    }
}
