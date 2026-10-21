using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CodeFusion.Http;

/// <summary>Handles one request. Read the request from the exchange, write the body into it, set Status; the
/// response goes out when the returned ValueTask completes. Flush any writer over the exchange before returning.</summary>
public delegate ValueTask HttpHandler(HttpExchange exchange);

/// <summary>
/// An HTTP/1.1 server over sockets: keep-alive, pipelining, Content-Length and chunked request bodies, and
/// whole buffered responses with a Content-Length. Idle connections hold no buffers. A heartbeat refreshes the
/// Date header and enforces the timeouts once per <see cref="HttpServerOptions.HeartbeatInterval"/>.
/// </summary>
public sealed class HttpServer : IAsyncDisposable
{
    internal readonly HttpServerOptions Options;
    internal readonly HttpHandler Handler;
    internal readonly BufferPool RequestBuffers, ResponseBuffers;

    /// <summary>Called on each exchange once its handler has completed (or failed), before the response is finished:
    /// the app flushes its JSON writer and resets the request's arena there.</summary>
    internal Action<HttpExchange>? HandlerDone;

    /// <summary>Called when a connection has no request left and lets go of its buffers, and when it closes: the
    /// app gives back the arena chunk it kept for the connection's requests.</summary>
    internal Action<HttpExchange>? ConnectionIdle;

    /// <summary>Connections whose loop threw instead of closing as it should: for tests.</summary>
    internal int FailedConnections;

    /// <summary>Exceptions from a handler's code that the server could only drop: a callback registered on
    /// <see cref="HttpExchange.Aborted"/> that threw when the client left, or <see cref="HttpServerOptions.OnHandlerError"/>
    /// or <see cref="HttpServerOptions.OnError"/> itself. For tests.</summary>
    internal int CallbackFailures;

    /// <summary>A batch action threw: counted every time, reported the first time for each action.</summary>
    internal void BatchActionFailed(int action, Exception e)
    {
        Interlocked.Increment(ref CallbackFailures);
        if (Interlocked.Exchange(ref _batchReported[action], 1) == 0) Report("http event loop hook failed", e);
    }

    /// <summary>Hands a failure to <see cref="HttpServerOptions.OnError"/>. What the hook throws is counted in
    /// <see cref="CallbackFailures"/> and dropped, so it can't take down the loop that reports.</summary>
    internal void Report(string what, Exception? error)
    {
        try
        {
            Options.OnError?.Invoke(what, error);
        }
        catch
        {
            Interlocked.Increment(ref CallbackFailures);
        }
    }

    /// <summary>Debug builds count here (Release ones never touch them): connection runs and handler calls started
    /// and ended. Equal once the server has stopped: every run's and every handler's ValueTask was consumed exactly
    /// once. For tests.</summary>
    internal int RunsStarted, RunsEnded, HandlersStarted, HandlersEnded;

    /// <summary>Debug builds count here every receive call on a connection's socket: reads, peeks and the zero-byte
    /// receive that waits for a request. For tests.</summary>
    internal int SocketReceives;

    /// <summary>The app's <see cref="IServiceRegistry.OnEventLoopBatch"/> actions, called after
    /// <see cref="HttpServerOptions.OnEventLoopBatch"/> in their order. Set before Start.</summary>
    internal Action<int>[] BatchActions = [];

    // Which batch actions (0: the options' hook, then BatchActions) have reported a failure: once each, since one that
    // throws on every wake-up would fill the log at the server's request rate.
    int[] _batchReported = [0];

    /// <summary>Debug builds count here every recv and send an event loop's connection made itself, past Socket.
    /// For tests.</summary>
    internal int DirectCalls;

    /// <summary>Debug builds count here the sends that waited for room: on the runtime's SendAsync, or on an event
    /// loop. For tests.</summary>
    internal int RuntimeSendWaits, LoopSendWaits;

    /// <summary>Debug builds call it on a connection's own thread as it reaches each <see cref="ExchangePhase"/>,
    /// so a test can close, reset or stop exactly there. Release builds make no such calls.</summary>
    internal Action<HttpExchange, ExchangePhase>? PhaseHook;

    /// <summary>Debug builds call it on the watch receive's completion while a handler runs, with what its peek
    /// found (true: the client reset, or the connection failed), and take its answer instead; it may also hold the
    /// watch there. Release builds make no such calls.</summary>
    internal Func<HttpExchange, bool, bool>? WatchHook;

    /// <summary>Debug builds call it on the finder's thread once a client found gone has changed its request's state,
    /// just before <see cref="HttpExchange.Aborted"/> is canceled; it may hold the finder there. Release builds make
    /// no such calls.</summary>
    internal Action<HttpExchange>? GoneHook;

    /// <summary>Debug builds call it on the heartbeat as soon as it has read a send wait's deadline; it may hold the
    /// heartbeat there, and true makes the check find that wait too slow. Release builds make no such calls.</summary>
    internal Func<HttpExchange, bool>? SendCheckHook;

    /// <summary>Debug builds call it on an event loop's thread, with its index, before each wait; true makes that wait
    /// fail as a broken descriptor would. Release builds make no such calls.</summary>
    internal Func<int, bool>? LoopWaitHook;

    /// <summary>Debug builds call it on an event loop's thread, with its index, before each accept4 on its own
    /// listener (Linux); a non-zero errno is taken as that accept's failure. Release builds make no such calls.</summary>
    internal Func<int, int>? AcceptHook;

    /// <summary>Debug builds call it on a loop's thread once it has turned its listener off for lack of descriptors,
    /// before it says so: where a heartbeat's resume could land. Release builds make no such calls.</summary>
    internal Action<EventLoop>? PauseHook;

    /// <summary>Debug builds call it as a connection is admitted, before its connection object is rented: where
    /// another loop's admission could land. Release builds make no such calls.</summary>
    internal Action<EventLoop?>? AdmitHook;

    readonly Lock _lock = new();
    HttpExchange?[] _slots = new HttpExchange?[256];
    int _slotCount;            // slots in use or freed, high-water mark
    int[] _free = new int[256];
    int _freeCount;
    readonly HttpExchange[] _pool;
    int _poolCount;
    int _active;
    readonly int _maxConnections;
    uint _connectionIds;
    EventLoop[]? _loops;

    Socket? _listener;
    // The runtime's accept loops: the one listener's, and those of loops that failed and handed theirs over.
    readonly List<Task> _accepting = [];
    Thread? _heartbeat;
    readonly ManualResetEventSlim _wake = new();
    volatile bool _stopping, _stopped;
    Task? _stop;
    int _acceptFailing;
    Socket[]? _loopListeners;

    /// <summary>A server that answers every request with <paramref name="handler"/>; <see cref="Start"/> starts listening.</summary>
    public HttpServer(HttpServerOptions options, HttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        options.Validate();
        Options = options;
        Handler = handler;
        RequestBuffers = new BufferPool(options.RequestBufferSize, options.MaxPooledBuffers);
        ResponseBuffers = new BufferPool(options.ResponseBufferSize, options.MaxPooledBuffers);
        _pool = new HttpExchange[options.MaxPooledConnections];
        _maxConnections = options.MaxConnections ?? int.MaxValue;
    }

    internal bool Stopping => _stopping;

    /// <summary>The bytes of the request buffers the open connections hold now. For tests.</summary>
    internal long RequestBufferBytes
    {
        get
        {
            long n = 0;
            lock (_lock)
                foreach (HttpExchange? c in _slots.AsSpan(0, _slotCount)) n += c?.RequestBufferLength ?? 0;
            return n;
        }
    }

    /// <summary>Event loops started and not yet ended. For tests.</summary>
    internal int EventLoopsRunning
    {
        get
        {
            int n = 0;
            foreach (EventLoop l in _loops ?? []) if (l.Running) n++;
            return n;
        }
    }

    /// <summary>Open connections.</summary>
    public int Connections => Volatile.Read(ref _active);

    /// <summary>Where it listens, with the real port when the options asked for port 0.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)(_listener ?? throw new InvalidOperationException("not started")).LocalEndPoint!;

    /// <summary>Binds the endpoint and starts accepting connections.</summary>
    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("already started");
        IPEndPoint ep = Options.EndPoint;
        // Linux event loops each listen on the port themselves (SO_REUSEPORT): the kernel spreads new connections
        // over them, and each loop accepts its own on its thread. Otherwise one listener and the runtime's accepts.
        bool own = Options.Transport == HttpTransport.EventLoop && Options.ListenerPerLoop && OperatingSystem.IsLinux();
        _batchReported = new int[BatchActions.Length + 1];
        var listeners = new Socket[own ? Options.EventLoops : 1];
        try
        {
            if (Options.Transport == HttpTransport.EventLoop) _loops = StartLoops(Options.EventLoops);
            for (int i = 0; i < listeners.Length; i++)
            {
                // the first binds what was asked; the others its port, the one port 0 picked
                listeners[i] = Listener(i == 0 ? ep : (IPEndPoint)listeners[0].LocalEndPoint!, own);
                if (own) _loops![i].Listen(listeners[i]);
            }
        }
        catch
        {
            foreach (Socket? l in listeners) l?.Dispose();
            foreach (EventLoop l in _loops ?? []) l.Stop();
            _loops = null;
            throw;
        }
        _listener = listeners[0];
        if (own) _loopListeners = listeners;
        _heartbeat = new Thread(Heartbeat) { IsBackground = true, Name = "http heartbeat" };
        _heartbeat.Start();
        // Disposing the listener is what ends the accept loop; the stop awaits the loop right after.
#pragma warning disable CA2025
        if (!own) _accepting.Add(AcceptAsync(_listener));
#pragma warning restore CA2025
    }

    Socket Listener(IPEndPoint ep, bool shared)
    {
        var listener = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (ep.Address.Equals(IPAddress.IPv6Any)) listener.DualMode = true;
            // A restart can bind while the last run's connections sit in TIME_WAIT. (On Windows this option
            // would let another process take the port, so it's left off there.)
            if (!OperatingSystem.IsWindows()) listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // Linux's SO_REUSEPORT: only sockets of the same user can join the port's group
            if (shared) listener.SetRawSocketOption(SolSocket, SoReusePort, [1, 0, 0, 0]);
            listener.Bind(ep);
            listener.Listen(Options.Backlog);
            if (shared) listener.Blocking = false;
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    const int SolSocket = 1, SoReusePort = 15; // Linux

    /// <summary>The event loops' own listeners (Linux). For tests.</summary>
    internal int LoopListeners => _loopListeners?.Length ?? 0;

    EventLoop[] StartLoops(int count)
    {
        var loops = new EventLoop[count];
        try
        {
            for (int i = 0; i < count; i++) loops[i] = EventLoop.Create(this, i);
        }
        catch
        {
            foreach (EventLoop? l in loops) l?.Discard();
            throw;
        }
        foreach (EventLoop l in loops) l.Start();
        return loops;
    }

    /// <summary>An event loop's edge for the connection in <paramref name="slot"/>, if it is still the one with
    /// <paramref name="id"/>: one in another slot, or a later socket's in this one, is left alone.</summary>
    internal void Ready(int slot, uint id, bool readable, bool writable, bool closed)
    {
        HttpExchange?[] slots = Volatile.Read(ref _slots);
        if ((uint)slot >= (uint)slots.Length || Volatile.Read(ref slots[slot]) is not { } c || c.ConnectionId != id) return;
        if (writable) c.Writable();
        if (readable) c.Readable(closed);
    }

    async Task AcceptAsync(Socket listener)
    {
        while (!_stopping)
        {
            Socket s;
            try
            {
                s = await listener.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (_stopping)
            {
                return;
            }
            catch (SocketException e)
            {
                // Out of file descriptors, or a connection reset before it was accepted: wait and go on.
                if (e.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.ConnectionAborted))
                {
                    AcceptFailed(e);
                    await Task.Delay(10).ConfigureAwait(false);
                }
                continue;
            }
            AcceptWorks();
            if (Admit(s, null) is not { } c) continue;
            // on its loop's thread, so its first request is handled in a batch as every later one is
            if (c.Loop is { } loop) loop.Post(c);
            else ThreadPool.UnsafeQueueUserWorkItem(c, preferLocal: false);
        }
    }

    /// <summary>One line per run of accept failures, not one per retry, from whichever thread accepts.</summary>
    internal void AcceptFailed(Exception e)
    {
        if (Interlocked.Exchange(ref _acceptFailing, 1) == 0) Report("http accept failing", e);
    }

    internal void AcceptWorks()
    {
        if (Volatile.Read(ref _acceptFailing) == 1 && Interlocked.Exchange(ref _acceptFailing, 0) == 1) Report("http accept works again", null);
    }

    /// <summary>A connection accepted: its connection object, attached, or null when it was closed at once (the
    /// server stops, or is at its limit). <paramref name="loop"/>: the event loop that accepted it.</summary>
    internal HttpExchange? Admit(Socket s, EventLoop? loop)
    {
        if (_stopping)
        {
            s.Dispose();
            return null;
        }
        try
        {
            s.NoDelay = true;
            s.Blocking = false; // receives and sends are tried directly first; the watch receive says when to retry
        }
        catch (SocketException)
        {
            s.Dispose();
            return null;
        }
        Admitting(loop);
        // the limit is checked where the count goes up, under the lock: loops admit at once
        if (Rent(loop) is not { } c)
        {
            s.Dispose(); // past the limit: closed at once, before it costs a connection object
            return null;
        }
        c.Attach(s);
        return c;
    }

    [Conditional("DEBUG")]
    void Admitting(EventLoop? loop) => AdmitHook?.Invoke(loop);

    /// <summary>A connection object for a new connection, or null at <see cref="HttpServerOptions.MaxConnections"/>.</summary>
    HttpExchange? Rent(EventLoop? loop)
    {
        lock (_lock)
        {
            if (_active >= _maxConnections) return null;
            HttpExchange c = _poolCount > 0 ? _pool[--_poolCount] : new HttpExchange(this);
            c.CheckPooled();
            int slot;
            if (_freeCount > 0) slot = _free[--_freeCount];
            else
            {
                if (_slotCount == _slots.Length)
                {
                    var bigger = new HttpExchange?[_slots.Length * 2];
                    _slots.AsSpan().CopyTo(bigger);
                    Volatile.Write(ref _slots, bigger);
                }
                slot = _slotCount++;
            }
            c.Slot = slot;
            c.ConnectionId = unchecked(++_connectionIds);
            c.Loop = loop ?? LoopFor(c.ConnectionId);
            Volatile.Write(ref _slots[slot], c);
            _active++;
            return c;
        }
    }

    /// <summary>The loop for a connection the runtime accepted: in turn, skipping any whose wait failed. With none
    /// left the connection waits on its socket as Sockets does.</summary>
    EventLoop? LoopFor(uint id)
    {
        if (_loops is not { } loops) return null;
        for (int i = 0; i < loops.Length; i++)
        {
            EventLoop l = loops[(int)((id + (uint)i) % (uint)loops.Length)];
            if (!l.Failed) return l;
        }
        return null;
    }

    /// <summary>A loop whose wait failed hands its listener over: closing it would reset the connections queued on it,
    /// and the kernel keeps sending it its share. The runtime accepts them from now on and gives them to the loops
    /// still running, or, with none left, to Sockets' waits.</summary>
    internal void TakeOver(Socket listener)
    {
        lock (_lock)
        {
            if (_stopping) return; // the stop closes it
#pragma warning disable CA2025 // the stop disposes the listener and awaits the loop
            _accepting.Add(AcceptAsync(listener));
#pragma warning restore CA2025
        }
    }

    /// <summary>A loop whose wait failed: nothing will wake its connections again, so they are closed, with a FIN as
    /// a timeout closes them: one it has just accepted may not have reached its client as connected yet, and a reset
    /// would fail the client's connect itself.</summary>
    internal void CloseConnectionsOf(EventLoop loop)
    {
        HttpExchange?[] left;
        lock (_lock) left = _slots[.._slotCount];
        foreach (HttpExchange? c in left)
            if (c is not null && c.Loop == loop) c.Close();
    }

    internal void Release(HttpExchange c)
    {
        lock (_lock)
        {
            CheckReleasable(c);
            _slots[c.Slot] = null;
            if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
            _free[_freeCount++] = c.Slot;
            c.Slot = -1;
            if (_poolCount < _pool.Length && !_stopped) _pool[_poolCount++] = c;
            _active--;
        }
    }

    /// <summary>Debug: a connection object goes back once per socket, from the slot it was given.</summary>
    [Conditional("DEBUG")]
    void CheckReleasable(HttpExchange c)
    {
        if (c.Slot < 0 || c.Slot >= _slotCount || !ReferenceEquals(_slots[c.Slot], c))
            throw new InvalidOperationException("a connection was released twice, or one this server didn't rent");
        if (Array.IndexOf(_pool, c, 0, _poolCount) >= 0) throw new InvalidOperationException("a pooled connection was released");
    }

    void Heartbeat()
    {
        int ms = (int)Math.Max(1, Options.HeartbeatInterval.TotalMilliseconds);
        while (!_stopped)
        {
            _wake.Wait(ms);
            _wake.Reset();
            HttpDate.Update(DateTime.UtcNow);
            foreach (EventLoop l in _loops ?? []) l.ResumeAccept();
            Sweep(heartbeat: true);
        }
    }

    /// <summary>Expires what is past its deadline and looks for resets the watch can't see. The heartbeat's own sweep
    /// also checks sends against what their clients acknowledge (CheckSend keeps state only that thread touches).</summary>
    void Sweep(bool heartbeat)
    {
        long now = Environment.TickCount64;
        bool stopping = _stopping;
        HttpExchange?[] slots = Volatile.Read(ref _slots);
        int n = Math.Min(Volatile.Read(ref _slotCount), slots.Length);
        for (int i = 0; i < n; i++)
        {
            HttpExchange? c = Volatile.Read(ref slots[i]);
            if (c is null) continue;
            c.Expire(now, stopping);
            c.CheckGone();
            if (heartbeat) c.CheckSend(now);
        }
    }

    /// <summary>
    /// Stops accepting, closes idle connections, lets requests in flight finish (each answered with
    /// Connection: close), and after <paramref name="timeout"/> closes whatever is left.
    /// </summary>
    public Task StopAsync(TimeSpan timeout)
    {
        // Every caller waits for the one stop, so nothing is disposed under a stop still running.
        lock (_lock) return _listener is null ? Task.CompletedTask : _stop ??= StopCoreAsync(timeout);
    }

    async Task StopCoreAsync(TimeSpan timeout)
    {
        _stopping = true;
        _listener!.Dispose();
        foreach (Socket l in _loopListeners ?? []) l.Dispose();
        Task[] accepting;
        lock (_lock) accepting = [.. _accepting];
        await Task.WhenAll(accepting).ConfigureAwait(false);
        long until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Connections > 0 && Environment.TickCount64 < until)
        {
            Sweep(heartbeat: false); // idle connections close at once; busy ones as soon as they turn idle
            await Task.Delay(10).ConfigureAwait(false);
        }
        if (Connections > 0)
        {
            // Closing a socket can run its connection's cleanup inline, which takes the lock: close outside it.
            HttpExchange?[] left;
            lock (_lock) left = _slots[.._slotCount];
            foreach (var c in left) c?.Abort();
            // A handler that never completes keeps its connection object; don't wait for it for long.
            long hard = Environment.TickCount64 + 1000;
            while (Connections > 0 && Environment.TickCount64 < hard) await Task.Delay(10).ConfigureAwait(false);
        }
        // A loop ends after its batch; one a handler blocks is left behind after the same second a handler that never
        // completes gets above, as a background thread that closes its descriptors when it ends.
        if (_loops is { } loops)
        {
            var exits = new Task[loops.Length];
            for (int i = 0; i < loops.Length; i++)
            {
                loops[i].Stop();
                exits[i] = loops[i].Exited;
            }
            await Task.WhenAny(Task.WhenAll(exits), Task.Delay(1000)).ConfigureAwait(false);
        }
        _stopped = true;
        _wake.Set();
        _heartbeat?.Join();
    }

    /// <summary>Starts if needed, runs until SIGTERM or SIGINT, then stops gracefully.</summary>
    public async Task RunAsync(TimeSpan shutdownTimeout)
    {
        if (_listener is null) Start();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(PosixSignalContext c)
        {
            c.Cancel = true; // keep the process alive until the connections are done
            signal.TrySetResult();
        }
        using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal))
        using (PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal))
            await signal.Task.ConfigureAwait(false);
        await StopAsync(shutdownTimeout).ConfigureAwait(false);
    }

    /// <summary>Stops the server as <see cref="StopAsync"/> does, giving requests in flight five seconds.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _wake.Dispose();
    }
}
