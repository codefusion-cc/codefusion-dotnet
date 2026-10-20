using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Http;

/// <summary>
/// What an <c>async</c> handler returns. The Map overloads take <c>Func&lt;..., Pending&lt;TResult&gt;&gt;</c>, so an async
/// lambda passed to them gets <see cref="PendingMethodBuilder{T}"/> without any attribute: a handler that waits
/// borrows a box for its state from one pool per handler and gives it back when the endpoint has read its reply.
/// Await it exactly once, as every pooled ValueTask.
/// </summary>
/// <typeparam name="T">The reply.</typeparam>
[AsyncMethodBuilder(typeof(PendingMethodBuilder<>))]
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct Pending<T>
{
    // The pooled box while the method waits, the exception it threw before waiting, or null when it completed.
    readonly object? _source;
    readonly T _result;
    readonly short _token;

    internal Pending(PooledBox<T> box)
    {
        _source = box;
        _result = default!;
        _token = box.Version;
    }

    internal Pending(Exception error)
    {
        _source = error;
        _result = default!;
        _token = 0;
    }

    /// <summary>A completed reply, for a method that returns one without waiting.</summary>
    public Pending(T result)
    {
        _source = null;
        _result = result;
        _token = 0;
    }

    internal PooledBox<T>? Box => _source as PooledBox<T>;
    internal short Token => _token;

    /// <summary>Whether the reply is ready.</summary>
    public bool IsCompleted => _source is not PooledBox<T> box || box.GetStatus(_token) != ValueTaskSourceStatus.Pending;

    /// <summary>The reply, or what the method threw. Read it once, after it completed.</summary>
    public T GetResult()
    {
        if (_source is null) return _result;
        if (_source is PooledBox<T> box) return box.GetResult(_token);
        ExceptionDispatchInfo.Throw((Exception)_source);
        return default!;
    }

    /// <summary>Runs <paramref name="continuation"/> with <paramref name="state"/> when the reply is ready (at once
    /// when it is), without flowing the ExecutionContext.</summary>
    internal void OnCompleted(Action<object?> continuation, object state)
    {
        if (_source is PooledBox<T> box) box.OnCompleted(continuation, state, _token, ValueTaskSourceOnCompletedFlags.None);
        else continuation(state);
    }

    /// <summary>The same reply as a ValueTask, for code that awaits ValueTasks.</summary>
    public ValueTask<T> AsValueTask() =>
        _source switch
        {
            null => new ValueTask<T>(_result),
            PooledBox<T> box => new ValueTask<T>(box, _token),
            _ => ValueTask.FromException<T>((Exception)_source),
        };

    /// <summary>For <c>await</c>.</summary>
    public Awaiter GetAwaiter() => new(this);

    /// <summary>Awaits a <see cref="Pending{T}"/>.</summary>
    // Nested, as ValueTask<T>'s awaiters are: await finds it through GetAwaiter, and nobody names it.
#pragma warning disable CA1034
    public readonly struct Awaiter : ICriticalNotifyCompletion
#pragma warning restore CA1034
    {
        static readonly Action<object?> Invoke = static s => ((Action)s!)();
        readonly Pending<T> _pending;

        internal Awaiter(Pending<T> pending) => _pending = pending;

        /// <summary>Whether the reply is ready.</summary>
        public bool IsCompleted => _pending.IsCompleted;

        /// <summary>The reply.</summary>
        public T GetResult() => _pending.GetResult();

        /// <summary>Runs <paramref name="continuation"/> when the reply is ready, flowing the ExecutionContext.</summary>
        public void OnCompleted(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            if (_pending._source is PooledBox<T> box)
                box.OnCompleted(Invoke, continuation, _pending._token, ValueTaskSourceOnCompletedFlags.FlowExecutionContext);
            else continuation();
        }

        /// <summary>Runs <paramref name="continuation"/> when the reply is ready.</summary>
        public void UnsafeOnCompleted(Action continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            if (_pending._source is PooledBox<T> box)
                box.OnCompleted(Invoke, continuation, _pending._token, ValueTaskSourceOnCompletedFlags.None);
            else continuation();
        }
    }
}

/// <summary>
/// The builder of <see cref="Pending{T}"/> methods: <see cref="PooledValueTaskMethodBuilder{TResult}"/>'s pool per
/// method, behind the handler's own return type so that an async lambda needs no attribute.
/// </summary>
/// <typeparam name="T">The reply.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct PendingMethodBuilder<T>
{
    PooledBuilderCore<T> _core;

    /// <summary>Called by the compiler: one builder per call of the async method.</summary>
    // The compiler looks for a static Create on the builder.
#pragma warning disable CA1000
    public static PendingMethodBuilder<T> Create() => default;
#pragma warning restore CA1000

#pragma warning disable CA1822
    /// <summary>Runs the method up to its first wait.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        default(AsyncValueTaskMethodBuilder).Start(ref stateMachine);

    /// <summary>Called by the compiler; the state machine is boxed when the method first waits instead.</summary>
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }
#pragma warning restore CA1822

    /// <summary>Called by the compiler when the method returns.</summary>
    public void SetResult(T result) => _core.SetResult(result);

    /// <summary>Called by the compiler when the method throws.</summary>
    public void SetException(Exception exception) => _core.SetException(exception);

    /// <summary>The method's reply: backed by the pooled box once the method has waited, else already complete.</summary>
    public Pending<T> Task =>
        _core.Box is { } box ? new Pending<T>(box)
        : _core.Error is { } e ? new Pending<T>(e)
        : _core.Done ? new Pending<T>(_core.Result)
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
