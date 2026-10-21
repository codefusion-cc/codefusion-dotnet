using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace CodeFusion;

/// <summary>
/// ManualResetValueTaskSourceCore without its one allocation, for the packages' pooled ValueTask sources (the method
/// builders, the HTTP connection, the Postgres jobs). When a continuation is registered on a source that completed
/// after the awaiter looked (the result came from another thread in between), the runtime's core queues the
/// continuation to the thread pool, which allocates a work item; this one runs it right there, on the registering
/// thread, unless the stack is deep.
/// <para>
/// So a continuation can run inside OnCompleted: whoever registers it must touch nothing of its own after
/// registering. The compiler's state machines and the packages' builders return straight after. (A Task queues a late
/// continuation instead.)
/// </para>
/// <para>
/// It claims the continuation slot before it marks the result ready and touches nothing after, so a reader that sees
/// the result may reset the core at once. (The runtime's core marks the result ready first, but its GetStatus waits for
/// the slot, so an awaiting reader can't reach that window either.) Completions run inline on the completing thread,
/// the ExecutionContext isn't captured, and the flags are ignored: the pooled builders don't flow the context either.
/// </para>
/// </summary>
internal struct InlineValueTaskSourceCore<TResult>
{
    static readonly Action<object?> Completed = static _ => throw new InvalidOperationException("the completion sentinel ran");

    Action<object?>? _continuation;
    object? _state;
    TResult _result;
    ExceptionDispatchInfo? _error;
    bool _completed;
    short _version;

    public readonly short Version => _version;

    /// <summary>Ready for the next operation: a new version, nothing completed or waiting.</summary>
    public void Reset()
    {
        _version++;
        _completed = false;
        _result = default!;
        _error = null;
        _state = null;
        _continuation = null;
    }

    public void SetResult(TResult result)
    {
        _result = result;
        Signal();
    }

    public void SetException(Exception error)
    {
        _error = ExceptionDispatchInfo.Capture(error);
        Signal();
    }

    public readonly ValueTaskSourceStatus GetStatus(short token)
    {
        Validate(token);
        return !Volatile.Read(ref Unsafe.AsRef(in _completed)) ? ValueTaskSourceStatus.Pending
            : _error is null ? ValueTaskSourceStatus.Succeeded
            : _error.SourceException is OperationCanceledException ? ValueTaskSourceStatus.Canceled
            : ValueTaskSourceStatus.Faulted;
    }

    public readonly TResult GetResult(short token)
    {
        Validate(token);
        if (!Volatile.Read(ref Unsafe.AsRef(in _completed))) throw new InvalidOperationException("the operation hasn't completed");
        _error?.Throw();
        return _result;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        _ = flags;
        Validate(token);
        // A second await is refused before it writes anything: it must not overwrite the first one's state.
        Action<object?>? current = Volatile.Read(ref _continuation);
        if (current is not null && !ReferenceEquals(current, Completed)) throw new InvalidOperationException("a pooled ValueTask was awaited twice");
        // The state goes in first: once the continuation is published, Signal may run it at once.
        _state = state;
        Action<object?>? previous = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (previous is null) return;
        if (!ReferenceEquals(previous, Completed)) throw new InvalidOperationException("a pooled ValueTask was awaited twice");
        // Completed between the awaiter's look and now. Signal may be a few instructions short of marking the result
        // ready; then run the continuation here, unless the stack is deep.
        SpinWait spin = default;
        while (!Volatile.Read(ref _completed)) spin.SpinOnce();
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack()) continuation(state);
        else ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: true);
    }

    /// <summary>
    /// Takes the waiting continuation (or leaves the sentinel for one that comes later) before marking the result
    /// ready, and touches nothing after: whoever sees the result ready may read it and reset the core for its next
    /// operation at once. (The runtime's core marks the result ready first, so a reader that resets in that window
    /// has the sentinel land in its next operation.)
    /// </summary>
    void Signal()
    {
        Action<object?>? waiting = Interlocked.Exchange(ref _continuation, Completed);
        if (ReferenceEquals(waiting, Completed)) throw new InvalidOperationException("the operation completed twice");
        object? state = _state;
        Volatile.Write(ref _completed, true); // after the result, and the last write here
        waiting?.Invoke(state);
    }

    readonly void Validate(short token)
    {
        if (token != _version) throw new InvalidOperationException("a pooled ValueTask was read after it was reused");
    }
}
