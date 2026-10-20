using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Http;

/// <summary>
/// The builder for <c>async ValueTask</c> handlers: <c>[AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]</c>.
/// A method that completes without waiting costs nothing; one that waits borrows a box for its state from one pool
/// per method, shared by every thread, and gives it back when its caller reads the result. The runtime's
/// PoolingAsyncValueTaskMethodBuilder keeps boxes per thread and per core instead, so a handler that starts on a
/// socket thread and finishes on a database loop's thread returns its box where the next request doesn't look: it
/// allocated a new box every few requests (15 to 30 B per request on average).
/// <para>
/// It doesn't flow the ExecutionContext across awaits: an AsyncLocal set before an await is gone after it. The
/// handlers use none. The ValueTask must be awaited exactly once, as every pooled ValueTask.
/// </para>
/// </summary>
public struct PooledValueTaskMethodBuilder
{
    PooledBuilderCore<VoidResult> _core;

    /// <summary>Called by the compiler: one builder per call of the async method.</summary>
    public static PooledValueTaskMethodBuilder Create() => default;

    // The compiler calls the builder's members on the instance, so they stay instance members.
#pragma warning disable CA1822
    /// <summary>Runs the method up to its first wait, keeping the caller's ExecutionContext and
    /// SynchronizationContext as they were, as every builder does.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        default(AsyncValueTaskMethodBuilder).Start(ref stateMachine);

    /// <summary>Called by the compiler; the state machine is boxed by <see cref="AwaitOnCompleted"/> instead.</summary>
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }
#pragma warning restore CA1822

    /// <summary>Called by the compiler when the method returns.</summary>
    public void SetResult() => _core.SetResult(default);

    /// <summary>Called by the compiler when the method throws.</summary>
    public void SetException(Exception exception) => _core.SetException(exception);

    /// <summary>The method's ValueTask: backed by the pooled box once the method has waited, else already complete.</summary>
    public ValueTask Task =>
        _core.Box is { } box ? new ValueTask(box, box.Version)
        : _core.Error is { } e ? ValueTask.FromException(e) // only a handler that failed before it waited
        : _core.Done ? default
        : throw new InvalidOperationException("the method has neither completed nor waited");

    /// <summary>Called by the compiler at an await that has to wait: moves the state machine into a pooled box.</summary>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine =>
        _core.AwaitOnCompleted(ref awaiter, ref stateMachine);

    /// <summary>Called by the compiler at an await that has to wait: moves the state machine into a pooled box.</summary>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine =>
        _core.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
}

/// <summary>
/// <see cref="PooledValueTaskMethodBuilder"/> for <c>async ValueTask&lt;TResult&gt;</c> methods:
/// <c>[AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder&lt;&gt;))]</c>. The same pool per method, the same rules.
/// </summary>
/// <typeparam name="TResult">The method's result.</typeparam>
public struct PooledValueTaskMethodBuilder<TResult>
{
    PooledBuilderCore<TResult> _core;

    /// <summary>Called by the compiler: one builder per call of the async method.</summary>
    // The compiler looks for a static Create on the builder.
#pragma warning disable CA1000
    public static PooledValueTaskMethodBuilder<TResult> Create() => default;
#pragma warning restore CA1000

#pragma warning disable CA1822
    /// <summary>Runs the method up to its first wait.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        default(AsyncValueTaskMethodBuilder).Start(ref stateMachine);

    /// <summary>Called by the compiler; the state machine is boxed when the method first waits instead.</summary>
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }
#pragma warning restore CA1822

    /// <summary>Called by the compiler when the method returns.</summary>
    public void SetResult(TResult result) => _core.SetResult(result);

    /// <summary>Called by the compiler when the method throws.</summary>
    public void SetException(Exception exception) => _core.SetException(exception);

    /// <summary>The method's ValueTask: backed by the pooled box once the method has waited, else already complete.</summary>
    public ValueTask<TResult> Task =>
        _core.Box is { } box ? new ValueTask<TResult>(box, box.Version)
        : _core.Error is { } e ? ValueTask.FromException<TResult>(e)
        : _core.Done ? new ValueTask<TResult>(_core.Result)
        : throw new InvalidOperationException("the method has neither completed nor waited");

    /// <summary>Called by the compiler at an await that has to wait: moves the state machine into a pooled box.</summary>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine =>
        _core.AwaitOnCompleted(ref awaiter, ref stateMachine);

    /// <summary>Called by the compiler at an await that has to wait: moves the state machine into a pooled box.</summary>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine =>
        _core.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
}

/// <summary>The result of a method that has none.</summary>
internal readonly struct VoidResult;

/// <summary>What the pooled builders share: the method's result while it hasn't waited, its box once it has.</summary>
internal struct PooledBuilderCore<TResult>
{
    public PooledBox<TResult>? Box;
    public Exception? Error;
    public TResult Result;
    public bool Done;

    public void SetResult(TResult result)
    {
        if (Box is { } box) box.SetResult(result);
        else
        {
            Result = result;
            Done = true;
        }
    }

    public void SetException(Exception exception)
    {
        if (Box is { } box) box.SetException(exception);
        else Error = exception;
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine =>
        awaiter.OnCompleted(BoxFor(ref stateMachine).MoveNextAction);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine =>
        awaiter.UnsafeOnCompleted(BoxFor(ref stateMachine).MoveNextAction);

    /// <summary>The box the method runs in from its first wait on. The first time, the state machine (holding this
    /// builder) is still on the caller's stack: the box is noted here first, so the copy moved into it knows its box.</summary>
    PooledBox<TResult> BoxFor<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        if (Box is { } box) return box;
        var b = PooledBox<TResult, TStateMachine>.Rent();
        Box = b;
        b.StateMachine = stateMachine;
        return b;
    }
}

/// <summary>A waiting method's state and result, borrowed from its method's pool and given back when the result is read.</summary>
internal abstract class PooledBox<TResult> : IValueTaskSource<TResult>, IValueTaskSource
{
    // Continuations run inline: the caller goes on where the method finished, with no thread hop, and one registered
    // just after the method finished runs at once instead of from a work item.
    InlineValueTaskSourceCore<TResult> _core;
    public Action MoveNextAction = null!;
    bool _inPool; // Debug builds only: given back and not rented since

    public short Version => _core.Version;

    public void SetResult(TResult result)
    {
        CheckLive();
        _core.SetResult(result);
    }

    public void SetException(Exception e)
    {
        CheckLive();
        _core.SetException(e);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        CheckLive();
        return _core.GetStatus(token);
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        CheckLive();
        _core.OnCompleted(continuation, state, token, flags);
    }

    public TResult GetResult(short token)
    {
        CheckLive();
        // a stale token or an early read must not give back a box another call is using
        if (token != _core.Version || _core.GetStatus(token) == ValueTaskSourceStatus.Pending)
            throw new InvalidOperationException("a pooled ValueTask was read twice or before it completed");
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            _core.Reset();
            Return();
        }
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);

    protected abstract void Return();

    /// <summary>Debug: a box in the pool is no one's; touching it (a method that runs on after its result was read,
    /// or a ValueTask read after its box was reused and returned) throws.</summary>
    [Conditional("DEBUG")]
    protected void CheckLive()
    {
        if (_inPool) throw new InvalidOperationException("a pooled ValueTask box was used after its return");
    }

    /// <summary>Debug: marks the box as in the pool or rented; a second return throws.</summary>
    [Conditional("DEBUG")]
    protected void MarkPooled(bool pooled)
    {
        if (pooled && _inPool) throw new InvalidOperationException("a pooled ValueTask box was returned twice");
        _inPool = pooled;
    }
}

internal sealed class PooledBox<TResult, TStateMachine> : PooledBox<TResult> where TStateMachine : IAsyncStateMachine
{
    /// <summary>Boxes kept per method: about the requests of one kind in flight at the rig's load. A burst past it
    /// allocates boxes that are dropped when they come back.</summary>
    const int MaxPooled = 4096;

    static readonly PooledBox<TResult, TStateMachine>?[] Pool = new PooledBox<TResult, TStateMachine>?[MaxPooled];
    static int _pooled;
    static readonly Lock PoolLock = new();

    public TStateMachine StateMachine = default!;

    PooledBox() => MoveNextAction = MoveNext;

    void MoveNext()
    {
        CheckLive();
        StateMachine.MoveNext();
    }

    public static PooledBox<TResult, TStateMachine> Rent()
    {
        lock (PoolLock)
        {
            if (_pooled > 0)
            {
                var b = Pool[--_pooled]!;
                Pool[_pooled] = null;
                b.MarkPooled(false);
                return b;
            }
        }
        return new PooledBox<TResult, TStateMachine>();
    }

    protected override void Return()
    {
        MarkPooled(true);
        StateMachine = default!; // let go of what the method held: the exchange, its spans' arrays
        lock (PoolLock)
            if (_pooled < MaxPooled) Pool[_pooled++] = this;
    }
}
