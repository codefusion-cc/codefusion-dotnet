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

    /// <summary>The least a loop takes when it shares a burst with sleeping loops: fewer would cost a wakeup and a
    /// round trip for too little.</summary>
    public const int ShareAtLeast = 4;

    /// <summary>Waits for commands and takes up to <paramref name="max"/> into <paramref name="into"/>, in queue
    /// order. 0 once the queue is stopped and empty. <paramref name="slept"/>: the loop had to wait for them. With
    /// <paramref name="share"/>, a loop that finds others asleep takes only its share and wakes the next for the rest,
    /// so a burst runs on several connections at once instead of one after another on one.</summary>
    public int Take(Waiter me, PgJob?[] into, int max, out bool slept, bool share = false)
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
                    n = TakeLocked(into, max, share);
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

    /// <summary>Takes up to <paramref name="max"/> without waiting: 0 when nothing is queued. For a loop with a group
    /// in flight, which is never asleep.</summary>
    public int TryTake(PgJob?[] into, int max, bool share = false)
    {
        if (Volatile.Read(ref _count) == 0) return 0;
        Waiter? wake = null;
        int n;
        lock (_lock)
        {
            if (_count == 0) return 0;
            n = TakeLocked(into, max, share);
            if (_count > 0) wake = PickIdle();
        }
        Wake(wake);
        return n;
    }

    /// <summary>Under the lock: the commands at the head, as many as <see cref="Take"/> says.</summary>
    int TakeLocked(PgJob?[] into, int max, bool share)
    {
        int n = Math.Min(max, _count);
        // the loops asleep each get as much: this one its share, then one of them is woken for the rest
        if (share && _idleCount > 0) n = Math.Min(n, Math.Max(ShareAtLeast, (_count + _idleCount) / (_idleCount + 1)));
        for (int i = 0; i < n; i++)
        {
            int at = (_head + i) % _ring.Length;
            into[i] = _ring[at];
            _ring[at] = null;
        }
        _head = (_head + n) % _ring.Length;
        Volatile.Write(ref _count, _count - n);
        return n;
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
