using System;

namespace CodeFusion;

/// <summary>
/// Where a package registers what handlers may ask for: CodeFusion.Http's app builder implements it, and an
/// extension such as CodeFusion.Data's <c>AddDatabase</c> adds to it. Everything is known when the app is built, so
/// the endpoint generator passes each handler its services directly: there is no container lookup per request.
/// </summary>
public interface IServiceRegistry
{
    /// <summary>One <paramref name="instance"/> for the app's life, handed to every handler that takes a
    /// <typeparamref name="TService"/>.</summary>
    void AddSingleton<TService>(TService instance) where TService : class;

    /// <summary>
    /// One instance per connection, made by <paramref name="factory"/> the first time a request on it needs one and
    /// kept with the connection (and reused by the next connection when it is pooled). Before each handler call it is
    /// bound to that request (<see cref="IRequestScoped.Bind"/>).
    /// </summary>
    void AddPerConnection<TService>(Func<TService> factory) where TService : class, IRequestScoped;

    /// <summary>Runs <paramref name="onStopping"/> once the app has stopped taking requests, in the reverse order of
    /// registration: to drain queues and close connections.</summary>
    void OnStopping(Action onStopping);

    /// <summary>
    /// Runs <paramref name="onBatch"/> with the loop's index (<see cref="EventLoopThread.Current"/>) each time an event
    /// loop has handled the connections one wake-up found ready, before it sends their answers: a database commits the
    /// batch's writes here, and a handler waiting for that commit, completed here, answers in the same batch. Actions
    /// run in the order they were registered, on the loop's thread, which every connection on that loop shares: one
    /// must not block. One that throws is counted and reported once, and the loop goes on. Only a server on event
    /// loops calls them; with sockets they never run.
    /// </summary>
    void OnEventLoopBatch(Action<int> onBatch);
}

/// <summary>
/// A per-connection service that works on behalf of the request in progress: a data context copies the rows a
/// handler awaits into the request's arena, and drops work whose client has gone.
/// </summary>
public interface IRequestScoped
{
    /// <summary>Binds the service to the request <paramref name="response"/> answers, until the next call.</summary>
    void Bind(IResponse response);
}
