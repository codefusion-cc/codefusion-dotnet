using System;
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

    readonly Lock _lock = new();
    HttpExchange?[] _slots = new HttpExchange?[256];
    int _slotCount;            // slots in use or freed, high-water mark
    int[] _free = new int[256];
    int _freeCount;
    readonly HttpExchange[] _pool;
    int _poolCount;
    int _active;
    readonly int _maxConnections;

    Socket? _listener;
    Task? _accepting;
    Thread? _heartbeat;
    readonly ManualResetEventSlim _wake = new();
    volatile bool _stopping, _stopped;
    Task? _stop;
    bool _acceptFailing;

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

    /// <summary>Open connections.</summary>
    public int Connections => Volatile.Read(ref _active);

    /// <summary>Where it listens, with the real port when the options asked for port 0.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)(_listener ?? throw new InvalidOperationException("not started")).LocalEndPoint!;

    /// <summary>Binds the endpoint and starts accepting connections.</summary>
    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("already started");
        IPEndPoint ep = Options.EndPoint;
        var listener = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (ep.Address.Equals(IPAddress.IPv6Any)) listener.DualMode = true;
            // A restart can bind while the last run's connections sit in TIME_WAIT. (On Windows this option
            // would let another process take the port, so it's left off there.)
            if (!OperatingSystem.IsWindows()) listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(ep);
            listener.Listen(Options.Backlog);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        _heartbeat = new Thread(Heartbeat) { IsBackground = true, Name = "http heartbeat" };
        _heartbeat.Start();
        // Disposing the listener is what ends the accept loop; the stop awaits the loop right after.
#pragma warning disable CA2025
        _accepting = AcceptAsync(listener);
#pragma warning restore CA2025
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
                // Out of file descriptors, or a connection reset before it was accepted: wait and go on. One line
                // per run of failures, not one per retry.
                if (e.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.ConnectionAborted))
                {
                    if (!_acceptFailing) Report("http accept failing", e);
                    _acceptFailing = true;
                    await Task.Delay(10).ConfigureAwait(false);
                }
                continue;
            }
            if (_acceptFailing)
            {
                Report("http accept works again", null);
                _acceptFailing = false;
            }
            if (_stopping)
            {
                s.Dispose();
                return;
            }
            if (Connections >= _maxConnections)
            {
                s.Dispose(); // past the limit: closed at once, before it costs a connection object
                continue;
            }
            try
            {
                s.NoDelay = true;
                s.Blocking = false; // receives and sends are tried directly first; the watch receive says when to retry
            }
            catch (SocketException)
            {
                s.Dispose();
                continue;
            }
            HttpExchange c = Rent();
            c.Attach(s);
            ThreadPool.UnsafeQueueUserWorkItem(c, preferLocal: false);
        }
    }

    HttpExchange Rent()
    {
        lock (_lock)
        {
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
            Volatile.Write(ref _slots[slot], c);
            _active++;
            return c;
        }
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
        if (_accepting is not null) await _accepting.ConfigureAwait(false);
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
