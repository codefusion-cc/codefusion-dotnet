using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Data;

/// <summary>Reads a request starts together.</summary>
public static class DataContextExtensions
{
    /// <summary>
    /// Starts <paramref name="count"/> reads at once, the <c>i</c>th made by <paramref name="read"/><c>(context, i)</c>,
    /// so the provider sends them together instead of one round trip each, and gives back their rows in that order.
    /// A read that finds no row is left out; a failed read fails the whole, with its status and reply, reported
    /// once. The rows (and their text) are valid until the request ends: their list goes back to a pool every
    /// connection shares, so keep no <see cref="Rows{T}"/> past it (a Debug build of CodeFusion.Data throws on one). The reads finish on
    /// the provider's threads, each into a slot and an arena of its own, touching nothing else; they are gathered when
    /// the handler resumes, so the context can be used while an Each is under way (another Each, an awaited read).
    /// <code>var worlds = await db.Each(20, static (db, i) =&gt; db.WorldById(Random.Shared.Next(1, 10001)));</code>
    /// </summary>
    public static ValueTask<Rows<T>> Each<TContext, T>(this TContext context, int count, Func<TContext, int, One<T>> read)
        where TContext : DataContext where T : struct
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return context.RunEach(count, read);
    }

    /// <summary>
    /// Starts <paramref name="count"/> writes at once, the <c>i</c>th made by <paramref name="write"/><c>(context,
    /// state, i)</c>, so they queue together and commit in group commits instead of a group each. More writes than a
    /// group holds (Postgres: 128), or a provider with more than one writer loop, spread them over several groups on
    /// several connections, so writes to the same row in one Each may commit in either order: write each row once.
    /// The result is the first failed write's, in that order, or Ok when all committed; a failed write doesn't keep
    /// the others from committing.
    /// </summary>
    public static ValueTask<DataResult> Each<TContext, TState>(this TContext context, int count, TState state, Func<TContext, TState, int, Write> write)
        where TContext : DataContext
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return new ValueTask<DataResult>(new DataResult(DataStatus.Ok));
        return RunPool<WriteEachRun>.Rent().Start(context, count, state, write);
    }
}

/// <summary>A write <c>Each</c> in progress; pooled per context, as <see cref="EachRun{T}"/>.</summary>
sealed class WriteEachRun : IValueTaskSource<DataResult>, IPooledRun
{
#if DEBUG
    /// <summary>Makes each step throw before it waits. For tests.</summary>
    internal static Action? StepFault;
#endif
    readonly Action _step;
    Action? _drain;
    InlineValueTaskSourceCore<DataResult> _core;
    System.Runtime.CompilerServices.ValueTaskAwaiter<DataResult>[] _pending = [];
    DataContext _context = null!;
    int _count, _next;
    int _read;
    bool _faulted;
    Exception? _error;
    DataResult _failure;

    public WriteEachRun() => _step = Step;

    public ValueTask<DataResult> Start<TContext, TState>(TContext context, int count, TState state, Func<TContext, TState, int, Write> write) where TContext : DataContext
    {
        _context = context;
        _next = _count = 0;
        _error = null;
        _failure = new DataResult(DataStatus.Ok);
        _core.Reset();
        _read = 0; // after the reset: a stale read checks the old version and fails
        if (_pending.Length < count) _pending = new System.Runtime.CompilerServices.ValueTaskAwaiter<DataResult>[Math.Max(count, _pending.Length * 2)];
        try
        {
            for (int i = 0; i < count; i++)
            {
                _pending[i] = write(context, state, i).GetAwaiter();
                _count = i + 1;
            }
        }
        catch (Exception e)
        {
            _error = e;
        }
        var task = new ValueTask<DataResult>(this, _core.Version);
        Step();
        return task;
    }

    void Step()
    {
        try
        {
#if DEBUG
            StepFault?.Invoke();
#endif
            while (_next < _count)
            {
                ref var awaiter = ref _pending[_next];
                if (!awaiter.IsCompleted)
                {
                    awaiter.UnsafeOnCompleted(_step);
                    return;
                }
                try
                {
                    var r = awaiter.GetResult();
                    if (!r.IsOk && _failure.IsOk) _failure = r;
                }
                catch (Exception e)
                {
                    _error ??= e;
                }
                awaiter = default;
                _next++;
            }
            if (_error is not null) _core.SetException(_error);
            else _core.SetResult(_failure);
        }
        catch (Exception e)
        {
            // writes may still be in flight: the run is never used again, and waits for them only to let them go
            _faulted = true;
            Drain();
            _core.SetException(e);
        }
    }

    /// <summary>After a failed step: the writes still in flight are waited for and their results dropped, so their
    /// provider can reuse what it holds for them. If even that can't wait, they are left to the GC.</summary>
    void Drain()
    {
        try
        {
            while (_next < _count)
            {
                ref var awaiter = ref _pending[_next];
                if (!awaiter.IsCompleted)
                {
                    awaiter.UnsafeOnCompleted(_drain ??= Drain);
                    return;
                }
                try { awaiter.GetResult(); }
                catch (Exception) { } // the whole failed already
                awaiter = default;
                _next++;
            }
        }
        catch (Exception) { }
    }

    DataResult IValueTaskSource<DataResult>.GetResult(short token)
    {
        // read once, when done, as EachRun's
        if (token != _core.Version || _core.GetStatus(token) == ValueTaskSourceStatus.Pending || Interlocked.Exchange(ref _read, 1) != 0)
            throw new InvalidOperationException("an Each's result is read once, after it finished");
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            _context = null!;
            _error = null;
            _failure = default;
            if (!_faulted) RunPool<WriteEachRun>.Return(this);
        }
    }

    public int LargeEntries => _pending.Length > RunPool<WriteEachRun>.SmallArray ? _pending.Length : 0;

    public void LetGoOfLargeArrays()
    {
        if (_pending.Length > RunPool<WriteEachRun>.SmallArray) _pending = [];
    }

    ValueTaskSourceStatus IValueTaskSource<DataResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<DataResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}

/// <summary>
/// An <c>Each</c> in progress: the started reads' awaiters, taken in order as each
/// completes. Pooled per context and row type, with its array, so a run allocates nothing once warm.
/// </summary>
sealed class EachRun<T> : IValueTaskSource<Rows<T>>, IPooledRun where T : struct
{
#if DEBUG
    /// <summary>Makes each step throw before it waits. For tests.</summary>
    internal static Action? StepFault;
#endif
    readonly Action _step;
    Action? _drain;
    InlineValueTaskSourceCore<Rows<T>> _core;
    RowAwaiter<T>[] _pending = [];
    DataContext _context = null!;
    RowList<T> _list = null!;
    EachArena _arena = null!;
    // the reads' slots, taken from the Each pool and given back together
    RowSlot<T>?[] _slots = [];
    int _rented;
    int _count, _next;
    int _read;
    bool _faulted;
    Exception? _error;
    DataStatus _status;
    ErrorReply? _failure;

    public EachRun() => _step = Step;

    /// <summary>The slots of <paramref name="count"/> reads, before <see cref="Start"/> starts them.</summary>
    internal RowSlot<T>?[] RentSlots(int count)
    {
        if (_slots.Length < count) _slots = new RowSlot<T>?[Math.Max(count, _slots.Length * 2)];
        EachPool<T>.RentSlots(_slots, count);
        _rented = count;
        return _slots;
    }

    public ValueTask<Rows<T>> Start<TContext>(TContext context, RowList<T> list, EachArena arena, int count, Func<TContext, int, One<T>> read) where TContext : DataContext
    {
        _context = context;
        _list = list;
        _arena = arena;
        _next = _count = 0;
        _error = null;
        _status = DataStatus.Ok;
        _failure = null;
        _core.Reset();
        _read = 0; // after the reset: a stale read checks the old version and fails
        if (_pending.Length < count) _pending = new RowAwaiter<T>[Math.Max(count, _pending.Length * 2)];
        try
        {
            // every read is under way before the first is waited for: the provider's loop finds them all queued
            for (int i = 0; i < count; i++)
            {
                _pending[i] = read(context, i).GetAwaiter();
                _count = i + 1;
            }
        }
        catch (Exception e)
        {
            // the ones started still run: they are waited for, then the whole fails with this
            _error = e;
        }
        var task = new ValueTask<Rows<T>>(this, _core.Version);
        Step();
        return task;
    }

    // On whichever provider thread finished a read: only waits for the next, touching neither the context nor the
    // request's arena (each read's row is in its own slot's arena). The gathering is done where the handler resumes.
    void Step()
    {
        try
        {
#if DEBUG
            StepFault?.Invoke();
#endif
            while (_next < _count)
            {
                if (!_pending[_next].IsCompleted)
                {
                    _pending[_next].UnsafeOnCompleted(_step);
                    return;
                }
                _next++;
            }
            _core.SetResult(default);
        }
        catch (Exception e)
        {
            // nothing here should throw; if something does, the request fails instead of waiting forever, and the run,
            // whose reads may still be in flight, is never used again
            _faulted = true;
            Drain();
            _core.SetException(e);
        }
    }

    /// <summary>After a failed step: the reads still in flight are waited for and their rows dropped, so their
    /// provider can reuse what it holds for them. If even that can't wait, they are left to the GC.</summary>
    void Drain()
    {
        try
        {
            while (_next < _count)
            {
                ref var awaiter = ref _pending[_next];
                if (!awaiter.IsCompleted)
                {
                    awaiter.UnsafeOnCompleted(_drain ??= Drain);
                    return;
                }
                try { awaiter.GetResult(out _); }
                catch (Exception) { } // the whole failed already
                awaiter = default;
                _next++;
            }
        }
        catch (Exception) { }
    }

    /// <summary>Gathers the rows in order, where the handler resumes: every read has finished, so nothing else touches
    /// the context or the list. The first failure, in that order, is the whole's, and is reported once.</summary>
    Rows<T> Gather()
    {
        DataResult first = default;
        string? name = null;
        for (int i = 0; i < _count; i++)
        {
            ref var awaiter = ref _pending[i];
            Row<T> row = default;
            DataResult r = default;
            try
            {
                row = awaiter.GetResult(out r);
            }
            catch (Exception e)
            {
                _error ??= e;
            }
            if (row.Failed && _failure is null)
            {
                _status = row.Status;
                _failure = row.Error;
                first = r;
                name = awaiter.Name;
            }
            else if (row.Found && _failure is null) _list.Add(row.Value);
            awaiter = default;
        }
        // the provider adds nothing more: the list and the arena can be used again after this request, and the slots
        // now
        _list.Taken = true;
        _arena.Gathered = true;
        EachPool<T>.ReturnSlots(_slots, _rented);
        _rented = 0;
        if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_error);
        if (_failure is not null)
        {
            var context = _context;
            if (DataSource.Reports(_status) && context.Response?.Aborted.IsCancellationRequested != true)
                context.Source.OnFailure?.Invoke(name!, first);
            return new Rows<T>(_status, false, null, 0, _failure);
        }
        return new Rows<T>(_list);
    }

    /// <summary>The entries of its arrays the pool counts against its budget: those past a small size.</summary>
    public int LargeEntries =>
        (_pending.Length > RunPool<EachRun<T>>.SmallArray ? _pending.Length : 0) + (_slots.Length > RunPool<EachRun<T>>.SmallArray ? _slots.Length : 0);

    public void LetGoOfLargeArrays()
    {
        if (_pending.Length > RunPool<EachRun<T>>.SmallArray) _pending = [];
        if (_slots.Length > RunPool<EachRun<T>>.SmallArray) _slots = [];
    }

    Rows<T> IValueTaskSource<Rows<T>>.GetResult(short token)
    {
        // read once, when done: a second read (or a stale token, the run being another Each's now) must not give the
        // run back again, or two Eaches would share it
        if (token != _core.Version || _core.GetStatus(token) == ValueTaskSourceStatus.Pending || Interlocked.Exchange(ref _read, 1) != 0)
            throw new InvalidOperationException("an Each's result is read once, after it finished");
        try
        {
            _core.GetResult(token);
            return Gather();
        }
        finally
        {
            _context = null!;
            _list = null!;
            _arena = null!;
            _error = null;
            _failure = null;
            if (!_faulted) RunPool<EachRun<T>>.Return(this);
        }
    }

    ValueTaskSourceStatus IValueTaskSource<Rows<T>>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<Rows<T>>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
