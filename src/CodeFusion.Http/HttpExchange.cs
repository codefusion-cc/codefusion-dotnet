using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Http;

// Pooled for the server's lifetime: its token source registers no timer and its socket args are reused, so neither
// needs disposing.
#pragma warning disable CA1001
/// <summary>
/// One connection and the request on it that is being handled. The handler reads the request through it and
/// writes the response body into it (it is the <see cref="IBufferWriter{T}"/>); the server adds the head and sends.
/// The same object serves every request on its connection and is pooled after it closes, so nothing here is
/// allocated per request. Request spans stay valid until the handler's ValueTask completes, across its awaits.
/// </summary>
public sealed class HttpExchange : IBufferWriter<byte>, IValueTaskSource, IThreadPoolWorkItem
#pragma warning restore CA1001
{
    readonly HttpServer _server;
    readonly Limits _limits;
    readonly long _keepAliveMs, _requestMs;
    // The minimum data rates as milliseconds a byte pays for (0: no rate), after their grace periods; and how far
    // ahead bytes may pay, the request timeout or the grace period if longer, so bytes sent early can't buy a crawl.
    readonly double _bodyMsPerByte, _sendMsPerByte;
    readonly long _bodyGraceMs, _sendGraceMs, _bodyAheadMs, _sendAheadMs;
    // What a send call on a full socket hands over: 64 KB, or with a minimum response rate what the rate takes in its
    // grace period, since on macOS the heartbeat counts a slice still pending as sent (Acknowledged).
    const int SendSlice = 64 << 10;
    readonly int _sendSliceSize = SendSlice;
    readonly WatchArgs _watch;
    readonly Action _onRunCompleted;
    InlineValueTaskSourceCore<bool> _readable;
    AbortSource _cts;
    Socket? _socket;
    ValueTask _run;
    internal int Slot = -1;

    // Input: the request at the front is [_start, _end); later pipelined bytes follow it.
    byte[]? _in;
    int _start, _end;
    RequestState _req;

    // Output: finished responses not yet sent are [_sendStart, _sendEnd); the handler's body is [_bodyStart, _bodyEnd).
    byte[]? _out;
    int _sendStart, _sendEnd, _bodyStart, _bodyEnd;
    int _status, _retryAfter;

    // The zero-byte receive that says the socket is readable: Idle, Pending, Waiting (the loop awaits it) or Signaled.
    // With an event loop it is the loop's next edge instead (Arm).
    const int Idle = 0, Pending = 1, Waiting = 2, Signaled = 3;
    int _rs;

    // HttpTransport.EventLoop: the loop that watches this socket (null: the socket's own zero-byte receive does) and
    // this connection's id in its events. The loop's edges are not repeated, so one that comes while no wait is armed
    // is kept in _edge for the next wait (Edge), and a receive that fills the buffer sets it too, since the kernel
    // won't announce the bytes it left. An edge that said the client closed or reset stays (Hangup): the receive that
    // took the bytes that came with it doesn't see the end, so every later wait reads at once until one does; a
    // receive that finds nothing clears it, since after a FIN or an error recv never says EAGAIN, so a made-up or
    // stale one (a timeout's Kick that lands on the connection object's next socket) costs one read. The bits above
    // the state count the edges, so that clear can't undo an edge that came after the receive began.
    // Whoever turns _armed from 1 to 0 completes the wait, exactly once.
    internal EventLoop? Loop;
    internal uint ConnectionId;
    const int NoEdge = 0, Edge = 1, Hangup = 2, EdgeState = 3, EdgeCount = 4;
    int _armed, _edge;
    // A connection always readable again could hold its loop's thread: the reads it starts there in one wake-up.
    const int ReadsPerWake = 16;
    int _reads, _readsWake;
    readonly Kicker _kicker;
    readonly SendTurn _sendTurn = new();
    // A send waiting for room on its event loop: armed until the loop's writable edge (or a Kick) takes it.
    readonly SendTurn _room = new();
    int _roomArmed;

    // The handler, in the low two bits: none, Running, Abandoned (running, and the client reset or the connection
    // failed: the response is dropped) or HalfClosed (running, and the client sent its FIN: Aborted is canceled, the
    // response still goes out). The bits above count the handlers started, and only the value a finder read changes,
    // so a client found gone for one request (by the watch, or by the heartbeat on another thread) can't abort a
    // later one. The loop takes the state back to none with one atomic and, so it sees a change up to that moment.
    const int NoHandler = 0, Running = 1, Abandoned = 2, HalfClosed = 3, HandlerState = 3, NextHandler = 4;
    int _handler;
    // The watch found the socket readable while the handler ran, and the client not reset: its next request or its
    // FIN. The watch has nothing more to tell then, and a peek finds those bytes even after a reset, so the
    // heartbeat asks the socket for its error, and behind a request for its FIN, instead (CheckGone) until the
    // handler ends.
    volatile bool _checkGone;
    bool _running; // Debug builds only: between Execute and RunCompleted

    // Before closing with a FIN: how long in all, from the last response, and how much to read and drop.
    const long LingerMs = 2000, LingerBytes = 4 << 20;

    // A TickCount64 deadline the heartbeat enforces (0: none, -1: it fired). _idleWait: waiting for a new request.
    // A send wait's is long.MaxValue minus its number on this object instead, above SendWaitDeadlines: never
    // reached, and never the same twice, so a check that read one wait can't end another.
    long _deadline;
    volatile bool _idleWait;
    long _headDeadline;
    const long SendWaitDeadlines = long.MaxValue / 2;
    long _sendWaits;
    // When the loop first waited for the request's body (0: not yet), the time its bytes have paid for, and how many
    // of them have paid.
    long _bodyAt, _bodyPaid, _bodyCounted;
    // The responses sent since the loop last waited for the client (a run): what the socket has taken of them, and
    // what it had taken when it first filled (-1: not yet). And what it has taken on this connection in all.
    long _sendTaken, _sendFill = -1, _sentTotal;
    // Waiting for the socket to take a slice: since when, how long the run waited before, the slice, and which run.
    // Set before the wait's deadline and kept until it is cleared, so the heartbeat (CheckSend) reads them as one.
    volatile bool _sendWait;
    long _sendWaitAt, _sendWaited;
    int _sendSlice, _sendRun;
    // The heartbeat's own: the run it last checked, the most of it the client had acknowledged and since when, and
    // the waiting time (the run's clock) what it acknowledged has paid for.
    int _checkedRun = -1;
    long _ackedMost, _ackedAt, _sendPaid;

    internal HttpExchange(HttpServer server)
    {
        _server = server;
        var o = server.Options;
        _limits = new Limits(o);
        _keepAliveMs = (long)o.KeepAliveTimeout.TotalMilliseconds;
        _requestMs = (long)o.RequestTimeout.TotalMilliseconds;
        if (o.MinRequestBodyDataRate is { } body)
        {
            _bodyMsPerByte = 1000 / body.BytesPerSecond;
            _bodyGraceMs = (long)body.GracePeriod.TotalMilliseconds;
            _bodyAheadMs = Math.Max(_bodyGraceMs, _requestMs);
        }
        if (o.MinResponseDataRate is { } send)
        {
            _sendMsPerByte = 1000 / send.BytesPerSecond;
            _sendGraceMs = (long)send.GracePeriod.TotalMilliseconds;
            _sendAheadMs = Math.Max(_sendGraceMs, _requestMs);
            _sendSliceSize = (int)Math.Clamp(send.BytesPerSecond * send.GracePeriod.TotalSeconds, 1, SendSlice);
        }
        _cts = new AbortSource(server);
        _watch = new WatchArgs(this);
        _watch.SetBuffer(Memory<byte>.Empty);
        _kicker = new Kicker(this);
        _onRunCompleted = RunCompleted;
        _req.Reset();
    }

    // ---- The request ----

    /// <summary>The request's method; <see cref="RequestMethod.Other"/> for the rest, read from <see cref="MethodBytes"/>.</summary>
    public RequestMethod Method => _req.Method;
    /// <summary>The method as it came, for one <see cref="Method"/> doesn't name.</summary>
    public ReadOnlySpan<byte> MethodBytes => RequestIn.AsSpan(_start, _req.MethodLength);

    /// <summary>The path, percent-decoded except %2F and with dot segments removed, as Kestrel gives it.</summary>
    public ReadOnlySpan<byte> Path => RequestIn.AsSpan(_start + _req.PathStart, _req.PathLength);

    /// <summary>The query after '?', as sent (not decoded); empty when there is none.</summary>
    public ReadOnlySpan<byte> Query => RequestIn.AsSpan(_start + _req.QueryStart, _req.QueryLength);

    /// <summary>HTTP/1.1, rather than 1.0.</summary>
    public bool IsHttp11 => _req.Http11;

    /// <summary>Whether an Authorization header was sent, even an empty one.</summary>
    public bool HasAuthorization => _req.AuthStart >= 0;

    /// <summary>The Authorization header's value without surrounding spaces; empty when absent. A request with
    /// two is refused (400), so a proxy that reads the last one can't see another identity.</summary>
    public ReadOnlySpan<byte> Authorization => _req.AuthStart < 0 ? default : RequestIn.AsSpan(_start + _req.AuthStart, _req.AuthLength);

    /// <summary>The whole body, chunked framing removed. Valid until the handler completes.</summary>
    public ReadOnlyMemory<byte> Body => RequestIn.AsMemory(_start + _req.HeadEnd, _req.BodyEnd - _req.HeadEnd);

    /// <summary>Canceled when the client leaves while the handler runs, as in Kestrel: it closes the connection (its
    /// FIN, as a browser, an HttpClient that timed out or a proxy whose own client left sends), resets it, or the
    /// connection fails. At once, or within <see cref="HttpServerOptions.HeartbeatInterval"/> when the client had sent
    /// its next request first (a FIN behind it is seen on Linux and macOS only). After a FIN the response is still
    /// sent if the handler finishes, since the client may only have closed its sending side; after a reset or a
    /// failure it is dropped (<see cref="IsAborted"/>). Callbacks registered on it run on the thread pool or, with
    /// <see cref="HttpTransport.Sockets"/>, the connection's own thread; never on the server's heartbeat or on an
    /// event loop, which other connections share. The source is the connection's own, renewed between
    /// requests.</summary>
    public CancellationToken Aborted => _cts.Token;

    /// <summary>The connection reset or failed while the handler ran: the response will not be sent. A client that
    /// only closed its side (a FIN) cancels <see cref="Aborted"/> without this.</summary>
    public bool IsAborted => (Volatile.Read(ref _handler) & HandlerState) == Abandoned;

    /// <summary>The handler's own per-connection state (a JSON writer, say); kept when the connection object is
    /// pooled and reused for another socket.</summary>
    public object? State { get; set; }

    // Where the request's parts lie in the request buffer, for Utf8 views of them. The buffer is neither moved nor
    // returned to its pool before the handler has completed (and with it any reply that points into it).
    internal byte[] RequestBuffer => RequestIn!;
    internal int PathOffset => _start + _req.PathStart;
    internal int PathLength => _req.PathLength;
    internal int QueryOffset => _start + _req.QueryStart;
    internal int QueryLength => _req.QueryLength;
    internal int RequestBodyOffset => _start + _req.HeadEnd;
    internal int RequestBodyLength => _req.BodyEnd - _req.HeadEnd;
    internal int AuthorizationOffset => _start + _req.AuthStart;

    /// <summary>The length of the request buffer held now (0: none), read from any thread. For tests.</summary>
    internal int RequestBufferLength => Volatile.Read(ref _in)?.Length ?? 0;

    // ---- The response ----

    /// <summary>200 unless set. 204 and 304 go out without a body.</summary>
    public int Status
    {
        get => _status;
        set => _status = value is >= 200 and <= 999 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Seconds for a Retry-After header; 0 sends none.</summary>
    public int RetryAfter
    {
        get => _retryAfter;
        set => _retryAfter = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>How many body bytes the handler has written so far.</summary>
    public int BodyLength => _bodyEnd - _bodyStart;

    /// <summary>Drops what was written and sets the status back to 200: nothing is sent before the handler returns.</summary>
    public void ResetResponse()
    {
        CheckWritable();
        _bodyEnd = _bodyStart;
        _status = 200;
        _retryAfter = 0;
    }

    /// <summary>Commits <paramref name="count"/> bytes written to the last <see cref="GetSpan"/> or <see cref="GetMemory"/> to the body.</summary>
    public void Advance(int count)
    {
        CheckWritable();
        if ((uint)count > (uint)(_out!.Length - _bodyEnd)) throw new ArgumentOutOfRangeException(nameof(count));
        _bodyEnd += count;
        CheckInvariants();
    }

    /// <summary>Room for at least <paramref name="sizeHint"/> more body bytes, in the connection's own buffer.</summary>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _out.AsMemory(_bodyEnd);
    }

    /// <summary>Room for at least <paramref name="sizeHint"/> more body bytes, in the connection's own buffer.</summary>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _out.AsSpan(_bodyEnd);
    }

    void Reserve(int sizeHint)
    {
        CheckWritable();
        if (sizeHint < 1) sizeHint = 1;
        if (_out!.Length - _bodyEnd < sizeHint) GrowOut(_bodyEnd + sizeHint);
    }

    void GrowOut(int need)
    {
        byte[] old = _out!;
        byte[] next = _server.ResponseBuffers.RentLarger(Math.Max(need, old.Length * 2));
        old.AsSpan(0, _bodyEnd).CopyTo(next);
        _out = next;
        _server.ResponseBuffers.Return(old);
    }

    // ---- The connection loop ----

    internal void Attach(Socket socket)
    {
        // bytes may have come before the loop watches the socket: the first wait reads at once
        _edge = Edge; // its count starts again: only Kick and the loop's own events read it
        Volatile.Write(ref _socket, socket);
    }

    /// <summary>Runs the connection on this thread until its first wait: a loop that accepted it.</summary>
    internal void StartRun() => ((IThreadPoolWorkItem)this).Execute();

    void IThreadPoolWorkItem.Execute()
    {
        RunStarted();
        // Kept to be consumed exactly once, in RunCompleted.
#pragma warning disable CA2012
        _run = RunAsync();
#pragma warning restore CA2012
        if (_run.IsCompleted) RunCompleted();
        else _run.GetAwaiter().UnsafeOnCompleted(_onRunCompleted);
    }

    void RunCompleted()
    {
        // Consuming the ValueTask hands its pooled state machine back; only then may the object be reused.
        try
        {
            _run.GetAwaiter().GetResult();
            CheckPooled(); // Debug: it goes back holding nothing, or the connection counts as failed
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _server.FailedConnections);
            _server.Report("http connection failed", e);
        }
        _run = default;
        Volatile.Write(ref _socket, null);
        RunEnded();
        _server.Release(this);
    }

    // The runtime's builder on purpose, here and on FlushAsync. They run once per connection and keep their box for
    // its life, so its cache misses cost per connection, not per request. And its box is a thread-pool work item
    // itself: a handler's source that completes before the await registers (a reply from another thread) queues it
    // without allocating, where PooledValueTaskMethodBuilder's allocates a work item (0.4 to 1.5 B/request against
    // under 0.1 in KeepAliveRequestsAllocateNothing with async handlers).
#pragma warning disable RS0030
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#pragma warning restore RS0030
    async ValueTask RunAsync()
    {
        bool graceful = false, first = true;
        try
        {
            Loop?.Add(_socket!, Slot, ConnectionId);
            while (true)
            {
                // _req is reset: by the last request's end, the close before this socket, or the constructor
                _headDeadline = Environment.TickCount64 + _requestMs; // for a pipelined request already here
                _bodyAt = 0;
                bool continueSent = false;
                ParseResult p;
                while ((p = Parse()) == ParseResult.Incomplete)
                {
                    if (_req.HeadEnd > 0 && _req.ExpectContinue && _req.Http11 && !continueSent)
                    {
                        AppendContinue();
                        continueSent = true;
                    }
                    if (_sendEnd > _sendStart)
                    {
                        await TurnToSend().ConfigureAwait(false);
                        if (!await FlushAsync().ConfigureAwait(false)) return;
                    }
                    _sendTaken = 0; // the client's turn: the next response starts a new run
                    _sendFill = -1;
                    bool idle = _start == _end, body = _req.HeadEnd > 0;
                    if (idle)
                    {
                        if (_server.Stopping) return;
                        ReleaseBuffers();
                        _server.ConnectionIdle?.Invoke(this);
                        // a new connection's first request must come within RequestTimeout, as a head's rest must
                        SetDeadline(first ? _headDeadline : Environment.TickCount64 + _keepAliveMs, true);
                    }
                    else SetDeadline(body ? BodyDeadline() : _headDeadline, false);
                    Phase(idle ? ExchangePhase.Idle : body ? ExchangePhase.ReadingBody : ExchangePhase.ReadingHead);
                    await WaitReadableAsync().ConfigureAwait(false);
                    if (!ClearDeadline()) return;
                    PrepareReceive();
                    CheckInvariants();
                    int n = Receive(_in.AsSpan(_end), out SocketError err);
                    if (err == SocketError.WouldBlock) continue;
                    if (err != SocketError.Success || n == 0) return;
                    if (n == _in!.Length - _end) MoreMayWait();
                    if (idle && !first) _headDeadline = Environment.TickCount64 + _requestMs;
                    _end += n;
                    CheckInvariants();
                }
                first = false;
                if (p == ParseResult.Error)
                {
                    BeginResponse();
                    _status = _req.Status;
                    if (_server.Options.RejectBody is { } rejected)
                    {
                        ReadOnlySpan<byte> b = rejected(_status);
                        b.CopyTo(GetSpan(b.Length));
                        Advance(b.Length);
                    }
                    FinishResponse(close: true);
                    await TurnToSend().ConfigureAwait(false);
                    graceful = await FlushAsync().ConfigureAwait(false);
                    if (!graceful) return;
                    break; // to the linger: the rest of the refused request is likely still coming
                }

                bool persistent = _req.Persistent;
                BeginResponse();
                Volatile.Write(ref _handler, unchecked(_handler + NextHandler) | Running);
                bool wentAsync = false;
                ValueTask vt;
                Count(ref _server.HandlersStarted);
                try
                {
                    vt = _server.Handler(this);
                }
                catch (Exception e)
                {
                    vt = default;
                    HandlerFailed(e);
                }
                if (!vt.IsCompleted)
                {
                    wentAsync = true;
                    WatchForClose();
                    Phase(ExchangePhase.HandlerWaiting);
                    try { await vt.ConfigureAwait(false); }
                    catch (Exception e) { HandlerFailed(e); }
                }
                else
                {
                    // it has completed: reading its result doesn't block
#pragma warning disable CA1849
                    try { vt.GetAwaiter().GetResult(); }
#pragma warning restore CA1849
                    catch (Exception e) { HandlerFailed(e); }
                }
                int ended = Interlocked.And(ref _handler, ~HandlerState) & HandlerState;
                _checkGone = false;
                // A client found gone has canceled the source, or will from the thread pool: the next request gets a
                // new one. Not disposed: that cancel may still run, and a source without a timer holds nothing that
                // needs disposing. Otherwise nothing canceled it, nor can now, and the reset drops the handler's
                // registrations.
                if (ended != Running || !_cts.TryReset()) _cts = new AbortSource(_server);
                Count(ref _server.HandlersEnded);
                _server.HandlerDone?.Invoke(this);
                if (ended == Abandoned) return;
                Phase(ExchangePhase.HandlerEnded);

                bool close = !persistent || _server.Stopping;
                FinishResponse(close);
                _start += _req.Total;
                if (_start == _end) _start = _end = 0;
                _req.Reset(); // the next request's parse starts from nothing
                CheckInvariants();
                if (close)
                {
                    await TurnToSend().ConfigureAwait(false);
                    graceful = await FlushAsync().ConfigureAwait(false);
                    // With nothing more from the client, the FIN and close can go now. With pipelined bytes, more
                    // likely follow: to the linger, so they don't make the close a reset.
                    if (!graceful || (_start == _end && _socket!.Available == 0)) return;
                    break;
                }
                // Responses to pipelined requests whose handlers finished at once go out together; after a handler
                // that waited, the next one likely waits too, so its response shouldn't wait behind it.
                if (wentAsync || _start == _end || _sendEnd - _sendStart >= _server.Options.ResponseBufferSize)
                {
                    await TurnToSend().ConfigureAwait(false);
                    if (!await FlushAsync().ConfigureAwait(false)) return;
                }
            }

            // The last response is sent and the connection closes while the client may still be sending. Closing
            // with request bytes unread makes the kernel send a reset, which can reach the client before it has read
            // that response. Send FIN, then read and drop what comes until the client's FIN, for LingerMs in all and
            // LingerBytes at most.
            _socket!.Shutdown(SocketShutdown.Send);
            graceful = false; // shut down already
            long until = Environment.TickCount64 + LingerMs;
            // what comes now is dropped: no request is being parsed, so PrepareReceive must not compact a refused
            // request's chunk framing at offsets the emptied buffer no longer has
            _start = _end = 0;
            _req.Reset();
            CheckInvariants();
            for (long left = LingerBytes; left > 0;)
            {
                SetDeadline(until, false);
                Phase(ExchangePhase.Lingering);
                await WaitReadableAsync().ConfigureAwait(false);
                if (!ClearDeadline()) return;
                PrepareReceive();
                CheckInvariants();
                int n = Receive(_in, out SocketError err);
                if (err == SocketError.WouldBlock) continue;
                if (err != SocketError.Success || n == 0) return;
                if (n == _in!.Length) MoreMayWait();
                left -= n;
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The socket failed or the heartbeat closed it: nothing to answer.
        }
        finally
        {
            await CloseAsync(graceful).ConfigureAwait(false);
        }
    }

    /// <summary>The wait for more of the body: RequestTimeout without progress, and once the minimum rate's grace
    /// period is over, the time its bytes have paid for. Each pays for 1/rate seconds from the body's first wait, but
    /// never more than RequestTimeout (or the grace period, if longer) past the moment it came, so bytes sent early
    /// can't buy a crawl later: a client above the rate between any two of its sends is never cut off. Only the
    /// body's own bytes count, not chunk framing: extensions and padding can't keep a connection.</summary>
    long BodyDeadline()
    {
        long now = Environment.TickCount64;
        long d = now + _requestMs;
        if (_bodyMsPerByte == 0) return d;
        if (_bodyAt == 0)
        {
            _bodyAt = _bodyPaid = now;
            _bodyCounted = 0;
        }
        // the body so far, the part that came with the head included; a chunked one as the parser has decoded it
        long body = _req.Chunked ? _req.BodyEnd - _req.HeadEnd : _end - _start - _req.HeadEnd;
        _bodyPaid = Paid(_bodyPaid, body - _bodyCounted, _bodyMsPerByte, now + _bodyAheadMs);
        _bodyCounted = body;
        return Math.Min(d, Math.Max(_bodyAt + _bodyGraceMs, _bodyPaid));
    }

    /// <summary>What <paramref name="bytes"/> more pay for at <paramref name="msPerByte"/>, past
    /// <paramref name="paid"/> but no later than <paramref name="cap"/>. In doubles, so no rate overflows it.</summary>
    static long Paid(long paid, long bytes, double msPerByte, long cap) =>
        bytes <= 0 ? paid : (long)Math.Min(paid + bytes * msPerByte, cap);

    ParseResult Parse()
    {
        if (_end == _start) return ParseResult.Incomplete;
        Span<byte> req = _in.AsSpan(_start, _end - _start);
        ParseResult r;
        if (_req.HeadEnd == 0)
        {
            r = RequestParser.ParseHead(req, ref _req, _limits);
            if (r != ParseResult.Done)
            {
                CheckInvariants();
                return r;
            }
            int len = RequestParser.NormalizePath(req.Slice(_req.PathStart, _req.PathLength));
            if (len < 0)
            {
                _req.Status = 400;
                CheckInvariants();
                return ParseResult.Error;
            }
            _req.PathLength = len;
        }
        r = RequestParser.ParseBody(req, ref _req, _limits);
        CheckInvariants();
        return r;
    }

    /// <summary>Makes room after _end for a receive: drops decoded chunk framing, moves the request to the front,
    /// or doubles the buffer. So what a connection holds follows what it has sent: a Content-Length only caps the
    /// growth, and a client that declares a large body and sends little of it holds little.</summary>
    void PrepareReceive()
    {
        if (_in is null)
        {
            _in = _server.RequestBuffers.Rent();
            return;
        }
        if (_req.Chunked && _req.HeadEnd > 0 && _req.Raw > _req.BodyEnd)
        {
            int gap = _req.Raw - _req.BodyEnd;
            _in.AsSpan(_start + _req.Raw, _end - _start - _req.Raw).CopyTo(_in.AsSpan(_start + _req.BodyEnd));
            _end -= gap;
            _req.Raw = _req.BodyEnd;
        }
        if (_in.Length - _end >= 512) return;
        if (_start > 0)
        {
            _in.AsSpan(_start, _end - _start).CopyTo(_in);
            _end -= _start;
            _start = 0;
            if (_in.Length - _end >= 512) return;
        }
        // No larger than the whole request when its length is known. Otherwise the parser refuses anything larger
        // than the limits allow, so that bound only stops a runaway.
        long max = _req.HeadEnd > 0 && !_req.Chunked
            ? _req.HeadEnd + _req.ContentLength
            : (long)_limits.RequestLine + 2L * _limits.Headers + _limits.Body + 8192;
        long want = Math.Min(_in.Length * 2L, Math.Min(max, Array.MaxLength));
        if (want <= _in.Length)
        {
            if (_in.Length > _end) return;
            throw new SocketException((int)SocketError.MessageSize);
        }
        byte[] next = _server.RequestBuffers.RentLarger((int)want);
        _in.AsSpan(0, _end).CopyTo(next);
        _server.RequestBuffers.Return(_in);
        _in = next;
    }

    void AppendContinue()
    {
        _out ??= _server.ResponseBuffers.Rent();
        ReadOnlySpan<byte> c = ResponseHead.Continue;
        if (_out.Length - _sendEnd < c.Length) { _bodyEnd = _sendEnd; GrowOut(_sendEnd + c.Length); }
        c.CopyTo(_out.AsSpan(_sendEnd));
        _sendEnd += c.Length;
        CheckInvariants();
    }

    void BeginResponse()
    {
        _out ??= _server.ResponseBuffers.Rent();
        _bodyStart = _bodyEnd = _sendEnd + ResponseHead.Reserve;
        if (_bodyStart > _out.Length) { _bodyEnd = _sendEnd; GrowOut(_bodyStart + 1024); _bodyEnd = _bodyStart; }
        _status = 200;
        _retryAfter = 0;
        CheckInvariants();
    }

    /// <summary>The response becomes a 500 with an empty body, which <see cref="HttpServerOptions.OnHandlerError"/>
    /// may fill in.</summary>
    void HandlerFailed(Exception e)
    {
        ResetResponse();
        _status = 500;
        // the client left: what Aborted's cancel made the handler throw isn't an error to report
        int state = Volatile.Read(ref _handler) & HandlerState;
        if (state == Abandoned || (state == HalfClosed && e is OperationCanceledException)) return;
        try
        {
            if (_server.Options.OnHandlerError is { } log) log(this, e);
            else _server.Report("http handler failed", e);
        }
        catch
        {
            // a failing logger mustn't take the connection down; the 500 goes out without its body
            Interlocked.Increment(ref _server.CallbackFailures);
            ResetResponse();
            _status = 500;
        }
    }

    /// <summary>Writes the head in front of the body and appends the whole response to what waits to be sent.</summary>
    void FinishResponse(bool close)
    {
        bool bodyless = _status is 204 or 304;
        int bodyLength = bodyless ? 0 : _bodyEnd - _bodyStart;
        Span<byte> head = stackalloc byte[ResponseHead.Reserve];
        int h = ResponseHead.Write(head, _status, bodyless ? -1 : bodyLength, bodyLength > 0, _retryAfter, close,
            keepAlive: !_req.Http11);
        int start = _bodyStart - h;
        head[..h].CopyTo(_out.AsSpan(start));
        int end = bodyless || _req.Method == RequestMethod.Head ? _bodyStart : _bodyEnd;
        if (_sendEnd == _sendStart)
        {
            _sendStart = start;
            _sendEnd = end;
        }
        else
        {
            _out.AsSpan(start, end - start).CopyTo(_out.AsSpan(_sendEnd));
            _sendEnd += end - start;
        }
        _bodyStart = _bodyEnd = 0;
        CheckInvariants();
    }

    /// <summary>Sends what waits: one send call when the socket takes it all, as it nearly always does. Once it is
    /// full, a slice at a time, each under a deadline.</summary>
    // The runtime's builder too: this waits only on a full send buffer, and then on the socket's own source, whose
    // completion from another thread queues the box without allocating.
#pragma warning disable RS0030
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#pragma warning restore RS0030
    async ValueTask<bool> FlushAsync()
    {
        Socket s = _socket!;
        while (_sendStart < _sendEnd)
        {
            int n = Send(s, _out.AsSpan(_sendStart, _sendEnd - _sendStart), out SocketError err);
            if (err == SocketError.WouldBlock)
            {
                // The socket is full: wait for it to take a slice, while the heartbeat watches what the client
                // acknowledges (CheckSend). The deadline is only this wait's own value for it to swap.
                int slice = (int)Math.Min(_sendEnd - _sendStart, _sendSliceSize);
                if (_sendFill < 0)
                {
                    _sendFill = _sendTaken;
                    _sendWaited = 0;
                    _sendRun++;
                }
                _sendSlice = Loop is null ? slice : 0; // a loop's wait hands the socket nothing until it has room
                _sendWaitAt = Environment.TickCount64;
                SetDeadline(long.MaxValue - ++_sendWaits, false);
                _sendWait = true;
                Phase(ExchangePhase.Sending);
                try
                {
                    if (Loop is { } loop)
                    {
                        Count(ref _server.LoopSendWaits);
                        await WaitForRoom(loop).ConfigureAwait(false);
                        n = 0; // the loop goes round and sends what the socket takes now
                    }
                    else
                    {
                        Count(ref _server.RuntimeSendWaits);
                        n = await s.SendAsync(_out.AsMemory(_sendStart, slice), SocketFlags.None).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    return false;
                }
                finally
                {
                    _sendWait = false;
                }
                if (!ClearDeadline()) return false;
                _sendWaited += Environment.TickCount64 - _sendWaitAt; // once the wait is over: CheckSend reads it
            }
            else if (err != SocketError.Success) return false;
            _sendStart += n;
            _sentTotal += n;
            _sendTaken += n;
            CheckInvariants();
        }
        _sendStart = _sendEnd = 0;
        CheckInvariants();
        return true;
    }

    /// <summary>A receive on the connection's socket: recv itself on an event loop, Socket's elsewhere.</summary>
    int Receive(Span<byte> buffer, out SocketError error)
    {
        Count(ref _server.SocketReceives);
        if (Loop is null) return _socket!.Receive(buffer, SocketFlags.None, out error);
        Count(ref _server.DirectCalls);
        int edge = Volatile.Read(ref _edge);
        int n = DirectSocket.Receive(_socket!.SafeHandle, buffer, out error);
        if (error == SocketError.WouldBlock) NothingWaited(edge);
        return n;
    }

    int Send(Socket s, ReadOnlySpan<byte> buffer, out SocketError error)
    {
        if (Loop is null) return s.Send(buffer, SocketFlags.None, out error);
        Count(ref _server.DirectCalls);
        return DirectSocket.Send(s.SafeHandle, buffer, out error);
    }

    void ReleaseBuffers()
    {
        if (_in is not null && _start == _end)
        {
            _server.RequestBuffers.Return(_in);
            _in = null;
            _start = _end = 0;
        }
        if (_out is not null && _sendStart == _sendEnd)
        {
            _server.ResponseBuffers.Return(_out);
            _out = null;
            _sendStart = _sendEnd = 0;
        }
        CheckInvariants();
    }

    async ValueTask CloseAsync(bool graceful)
    {
        Socket s = _socket!;
        if (graceful)
        {
            try { s.Shutdown(SocketShutdown.Send); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }
        s.Dispose();
        // Closing aborts a pending watch receive; the event args are only reusable once it has come back. A loop
        // drops a closed socket without an event: the wait is taken back here, unless the loop's edge has it.
        if (Disarm()) WatchCompleted();
        if (Volatile.Read(ref _rs) != Idle) await WaitReadableAsync().ConfigureAwait(false);
        _rs = Idle;
        Loop = null;
        _edge = NoEdge;
        _roomArmed = 0;
        _checkGone = false; // a watch that came back after the last handler ended may have set it
        Volatile.Write(ref _deadline, 0);
        _idleWait = false;
        _start = _end = 0;
        _req.Reset();
        _sendStart = _sendEnd = 0;
        _sendTaken = _sentTotal = 0;
        _sendFill = -1;
        _bodyStart = _bodyEnd = 0;
        ReleaseBuffers();
        _server.ConnectionIdle?.Invoke(this);
    }

    // ---- Readiness, and noticing a client that left ----

    ValueTask WaitReadableAsync()
    {
        if (_rs == Signaled)
        {
            _rs = Idle;
            return default;
        }
        if (_rs == Idle) StartWatch();
        _readable.Reset();
        if (Interlocked.CompareExchange(ref _rs, Waiting, Pending) == Pending) return new ValueTask(this, _readable.Version);
        _rs = Idle; // it completed already
        return default;
    }

    /// <summary>While a handler waits, a pending zero-byte receive tells us if the client closes or resets.</summary>
    void WatchForClose()
    {
        try
        {
            // Readable before the handler started: pipelined bytes, the FIN, or a reset.
            if (_rs == Signaled) Seen(_handler, Peek());
            else if (_rs == Idle) StartWatch();
        }
        catch (Exception)
        {
            // Disposed by the heartbeat or a stop, or the receive couldn't start: nothing would tell the loop that
            // the client left, so the request counts as gone. Its handler is still awaited before anything closes.
            Gone(_handler, reset: true, queue: Loop is not null);
        }
    }

    void StartWatch()
    {
        Volatile.Write(ref _rs, Pending);
        bool pending;
        try
        {
            Phase(ExchangePhase.Watching);
            if (Loop is null)
            {
                Count(ref _server.SocketReceives);
                pending = _socket!.ReceiveAsync(_watch);
            }
            else pending = Arm();
        }
        catch
        {
            Volatile.Write(ref _rs, Idle);
            throw;
        }
        if (!pending) WatchCompleted();
    }

    /// <summary>Event loop: waits for the loop's next edge, or takes back the wait at once (false) when an edge came
    /// since the last one.</summary>
    bool Arm()
    {
        Interlocked.Exchange(ref _armed, 1);
        if ((Volatile.Read(ref _edge) & EdgeState) == NoEdge) return true;
        // On its loop's thread, past its share of this wake-up, the connection waits for the loop to have served the
        // others: armed, with its edge kept, and handed to the loop's next round.
        if (Loop is { } loop && EventLoop.Current == loop && !TakeRead(loop))
        {
            loop.Later(this);
            return true;
        }
        return !Disarm();
    }

    bool TakeRead(EventLoop loop)
    {
        if (_readsWake != loop.Wakes)
        {
            _readsWake = loop.Wakes;
            _reads = 0;
        }
        return ++_reads <= ReadsPerWake;
    }

    /// <summary>Takes back the armed wait: true when this caller is the one to complete it.</summary>
    bool Disarm()
    {
        if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1) return false;
        // what the edge announced is read after this, by the receive or the peek the completion leads to; a hang-up
        // stays until a receive sees the end, or finds nothing
        SetEdge(Edge, NoEdge, count: false);
        return true;
    }

    /// <summary>Event loop: the socket turned readable, or <paramref name="closed"/>. Completes the armed wait, or
    /// keeps the edge for the next one.</summary>
    internal void Readable(bool closed)
    {
        if (closed) SetEdge(-1, Hangup, count: true);
        else SetEdge(NoEdge, Edge, count: true);
        if (Disarm()) WatchCompleted();
    }

    /// <summary>Sets the edge's state to <paramref name="to"/> if it is <paramref name="from"/> (-1: whatever it is),
    /// counting an edge if <paramref name="count"/>; a plain edge never replaces a hang-up.</summary>
    void SetEdge(int from, int to, bool count)
    {
        while (true)
        {
            int e = Volatile.Read(ref _edge);
            int state = e & EdgeState;
            if (from >= 0 && state != from)
            {
                if (!count) return;
                to = state; // the state stays; the edge is still counted
            }
            int next = ((count ? e + EdgeCount : e) & ~EdgeState) | to;
            if (Interlocked.CompareExchange(ref _edge, next, e) == e) return;
        }
    }

    /// <summary>A receive filled the buffer: more may wait in the kernel, which an edge-triggered loop won't announce.</summary>
    void MoreMayWait()
    {
        if (Loop is not null) SetEdge(NoEdge, Edge, count: false);
    }

    /// <summary>A receive found nothing: a hang-up seen before it began, and no edge since, was not the client's.</summary>
    void NothingWaited(int before)
    {
        if ((before & EdgeState) == Hangup) Interlocked.CompareExchange(ref _edge, before & ~EdgeState, before);
    }

    /// <summary>The socket was closed under the loop (a timeout, the stop's abort): no edge will tell the connection,
    /// so one is made, on the thread pool rather than the closer's own thread.</summary>
    void Kick()
    {
        if (Loop is null) return;
        _kicker.Id = ConnectionId;
        ThreadPool.UnsafeQueueUserWorkItem(_kicker, preferLocal: false);
    }

    /// <summary>A Kick, for the connection it was queued for: one that runs after the object went to the next socket
    /// does nothing.</summary>
    sealed class Kicker(HttpExchange owner) : IThreadPoolWorkItem
    {
        public volatile uint Id;

        public void Execute()
        {
            if (owner.ConnectionId != Id) return;
            owner.Writable();
            owner.Readable(closed: true);
        }
    }

    /// <summary>Event loop: waits for the socket to have room. Armed before the loop is asked, and the kernel reports
    /// room already there, so no edge falls between.</summary>
    ValueTask WaitForRoom(EventLoop loop)
    {
        ValueTask wait = _room.Wait();
        Interlocked.Exchange(ref _roomArmed, 1);
        try
        {
            loop.WaitWritable(_socket!, Slot, ConnectionId);
        }
        catch
        {
            Interlocked.Exchange(ref _roomArmed, 0); // the wait is never awaited: the send fails with this
            throw;
        }
        return wait;
    }

    /// <summary>Event loop: the socket has room to send, or failed. Completes a send's wait for it, if one is armed.</summary>
    internal void Writable()
    {
        if (Interlocked.CompareExchange(ref _roomArmed, 0, 1) == 1) _room.Complete();
    }

    /// <summary>Before a send: on its loop's thread in a wake-up, the answers wait until the batch's hook has run, then
    /// go out together. Awaited in RunAsync, whose box lives as long as the connection, so a held send allocates
    /// nothing (a FlushAsync suspended per answer took a new box whenever several were held at once).</summary>
    ValueTask TurnToSend() => Loop is { } loop && EventLoop.Batching == loop ? loop.WaitToSend(this) : default;

    internal ValueTask WaitForSendTurn() => _sendTurn.Wait();

    /// <summary>The loop's batch is over: the held send goes on, on the loop's thread.</summary>
    internal void TakeSendTurn() => _sendTurn.Complete();

    /// <summary>The wait of an answer held for its batch's end.</summary>
    sealed class SendTurn : IValueTaskSource
    {
        InlineValueTaskSourceCore<bool> _core;

        public ValueTask Wait()
        {
            _core.Reset();
            return new ValueTask(this, _core.Version);
        }

        public void Complete() => _core.SetResult(true);

        void IValueTaskSource.GetResult(short token) => _core.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }

    void WatchCompleted()
    {
        // Look before publishing the completion: once published, the loop may close and reuse this object.
        int h = Volatile.Read(ref _handler);
        if ((h & HandlerState) == Running)
        {
            Peer p = Peek();
            bool gone = p == Peer.Reset;
            Peeked(ref gone);
            Seen(h, gone ? Peer.Reset : p == Peer.Reset ? Peer.Sending : p);
        }
        while (true)
        {
            int s = Volatile.Read(ref _rs);
            if (s == Waiting)
            {
                if (Interlocked.CompareExchange(ref _rs, Idle, Waiting) == Waiting)
                {
                    _readable.SetResult(true);
                    return;
                }
            }
            else if (s == Pending)
            {
                if (Interlocked.CompareExchange(ref _rs, Signaled, Pending) == Pending) return;
            }
            else return;
        }
    }

    /// <summary>What the watch found while the handler whose state was <paramref name="h"/> runs. It has nothing
    /// more to tell after a request or a FIN, so the heartbeat looks from then on.</summary>
    void Seen(int h, Peer p)
    {
        // a loop's thread is every connection's on it: the cancel and what the handler does after it go to the pool
        if (p != Peer.Sending) Gone(h, reset: p == Peer.Reset, queue: Loop is not null);
        if (p != Peer.Reset) _checkGone = true;
    }

    /// <summary>What the client sent, as a one-byte peek finds it.</summary>
    enum Peer : byte
    {
        /// <summary>Bytes wait (its next request), or none yet.</summary>
        Sending,
        /// <summary>0: its FIN. It sends no more, but may still read the answer.</summary>
        Closed,
        /// <summary>An error: it reset, or the connection failed.</summary>
        Reset,
    }

    Peer Peek()
    {
        Span<byte> one = stackalloc byte[1];
        try
        {
            Count(ref _server.SocketReceives);
            int n = _socket!.Receive(one, SocketFlags.Peek, out SocketError err);
            return err is SocketError.Success ? (n == 0 ? Peer.Closed : Peer.Sending)
                : err is SocketError.WouldBlock ? Peer.Sending
                : Peer.Reset;
        }
        catch (ObjectDisposedException)
        {
            return Peer.Reset;
        }
    }

    /// <summary>Heartbeat, once the watch can't tell (<see cref="_checkGone"/>): a reset leaves its error on the socket
    /// even with bytes waiting in front of it, and a FIN its connection's state (on Linux and macOS). Reading the
    /// error clears it, so only the handler's own socket is asked: one read while that handler still ran.</summary>
    internal void CheckGone()
    {
        if (!_checkGone) return;
        int h = Volatile.Read(ref _handler);
        Socket? s = Volatile.Read(ref _socket);
        if ((h & HandlerState) is not (Running or HalfClosed) || s is null || Volatile.Read(ref _handler) != h) return;
        Span<byte> error = stackalloc byte[sizeof(int)];
        try
        {
            s.GetRawSocketOption(SolSocket, SoError, error);
            if (BitConverter.ToInt32(error) != 0) Gone(h, reset: true, queue: true);
            else if ((h & HandlerState) == Running && FinReceived(s)) Gone(h, reset: false, queue: true);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // closed under us: the loop finds that out itself
        }
    }

    /// <summary>Whether the connection is in CLOSE_WAIT: the client's FIN came, and the server hasn't closed. Linux's
    /// TCP_INFO and Darwin's TCP_CONNECTION_INFO both start with the state; elsewhere false.</summary>
    static bool FinReceived(Socket s)
    {
        Span<byte> state = stackalloc byte[1];
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            return s.GetRawSocketOption(IpProtoTcp, TcpInfo, state) == 1 && state[0] == LinuxCloseWait;
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
            return s.GetRawSocketOption(IpProtoTcp, TcpConnectionInfo, state) == 1 && state[0] == DarwinCloseWait;
        return false;
    }

    /// <summary>Heartbeat, on its own thread only: ends a send the client doesn't keep up with. Its TCP must
    /// acknowledge something new within RequestTimeout, and with a minimum response rate, what it acknowledged of the
    /// run must pay for the time the run waited on it, once the grace period is over: each byte 1/rate seconds, but
    /// never more than RequestTimeout (or the grace period, if longer) ahead, so a fast start can't buy a crawl. Only
    /// what the client's TCP acknowledged counts, not what the server's socket took: that takes hundreds of KB however
    /// slowly the client reads. A client reading faster than the rate between any two acknowledgments is never cut
    /// off by it.</summary>
    internal void CheckSend(long now)
    {
        if (!_sendWait) return;
        // The wait's values are set before its deadline and kept until it is cleared, and no two waits share a
        // deadline: read between two reads of the same deadline, they are that wait's.
        long d = Volatile.Read(ref _deadline);
        if (d < SendWaitDeadlines) return;
        bool forced = false;
        SendChecking(ref forced);
        Socket? s = Volatile.Read(ref _socket);
        long taken = Volatile.Read(ref _sendTaken), total = Volatile.Read(ref _sentTotal), fill = Volatile.Read(ref _sendFill);
        long waitAt = Volatile.Read(ref _sendWaitAt), waited = Volatile.Read(ref _sendWaited);
        int slice = Volatile.Read(ref _sendSlice), run = Volatile.Read(ref _sendRun);
        if (s is null || Volatile.Read(ref _deadline) != d) return;
        long acked = Acknowledged(s, taken, total, fill, slice), clock = waited + now - waitAt;
        if (run != _checkedRun)
        {
            _checkedRun = run;
            _ackedMost = _sendPaid = 0;
            _ackedAt = now;
        }
        if (acked > _ackedMost)
        {
            _sendPaid = Paid(_sendPaid, acked - _ackedMost, _sendMsPerByte, clock + _sendAheadMs);
            _ackedMost = acked;
            _ackedAt = now;
        }
        bool late = forced || now - Math.Max(_ackedAt, waitAt) > _requestMs
            || (_sendMsPerByte > 0 && clock > Math.Max(_sendGraceMs, _sendPaid));
        // a reset: a plain close would leave the kernel sending what it holds to a client that isn't reading
        if (late && Interlocked.CompareExchange(ref _deadline, -1, d) == d)
        {
            s.Close(0);
            Kick(); // a loop's wait for room hears nothing of a closed socket
        }
    }

    /// <summary>How much of the run the client's TCP has acknowledged. Linux says how much it acknowledged on the
    /// connection in all (TCP_INFO's tcpi_bytes_acked, from 4.1); macOS how much the socket still holds (SO_NWRITE),
    /// where a pending slice counts as sent. Elsewhere, or when the answer can't be right, what the socket took after
    /// it first filled: the first fill earns only the grace period.</summary>
    static long Acknowledged(Socket s, long taken, long total, long fill, int slice)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                Span<byte> info = stackalloc byte[TcpInfoBytesAcked + sizeof(ulong)];
                if (s.GetRawSocketOption(IpProtoTcp, TcpInfo, info) >= info.Length)
                {
                    long all = (long)BitConverter.ToUInt64(info[TcpInfoBytesAcked..]);
                    if (all >= 0 && all <= total + slice) return Math.Max(0, all - (total - taken));
                }
            }
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
            {
                Span<byte> held = stackalloc byte[sizeof(int)];
                s.GetRawSocketOption(0xFFFF, SoNWrite, held);
                int n = BitConverter.ToInt32(held);
                if (n >= 0 && n <= total + slice) return Math.Max(0, taken + slice - n);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // closing under us, or the option isn't there: the fallback
        }
        return Math.Max(0, taken - fill);
    }

    // Linux's IPPROTO_TCP, TCP_INFO, tcpi_bytes_acked's offset in struct tcp_info and TCP_CLOSE_WAIT; Darwin's
    // SO_NWRITE, TCP_CONNECTION_INFO and TCPS_CLOSE_WAIT.
    const int IpProtoTcp = 6, TcpInfo = 11, TcpInfoBytesAcked = 120, LinuxCloseWait = 8;
    const int SoNWrite = 0x1024, TcpConnectionInfo = 0x106, DarwinCloseWait = 5;

    // SO_ERROR's level and name, read raw since GetSocketOption boxes the value: Linux's, then those of macOS, the
    // BSDs and Windows.
    static readonly int SolSocket = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 1 : 0xFFFF;
    static readonly int SoError = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 4 : 0x1007;

    /// <summary>The client left while the handler whose state was <paramref name="seen"/> runs, and nothing once that
    /// handler has ended. A reset or a failed connection abandons the request: <see cref="Aborted"/> is canceled
    /// and the response dropped. A FIN only cancels it. The heartbeat <paramref name="queue"/>s the cancel to the
    /// thread pool, so the handler's callbacks and continuations never run on its thread.</summary>
    void Gone(int seen, bool reset, bool queue)
    {
        int state = seen & HandlerState;
        if (state != Running && !(reset && state == HalfClosed)) return;
        // Read before the compare-exchange: when it succeeds, this is that handler's source, and the loop, finding
        // the state changed, gives the next request a new one instead of resetting this one.
        AbortSource cts = Volatile.Read(ref _cts);
        if (Interlocked.CompareExchange(ref _handler, (seen & ~HandlerState) | (reset ? Abandoned : HalfClosed), seen) != seen
            || state == HalfClosed) // canceled already
            return;
        Canceling();
        if (queue) ThreadPool.UnsafeQueueUserWorkItem(cts, preferLocal: false);
        else cts.Execute();
    }

    /// <summary>The source of a connection's <see cref="Aborted"/> tokens, one per request a client left. It is its
    /// own thread-pool work item, so the heartbeat queues its cancel without allocating.</summary>
    sealed class AbortSource(HttpServer server) : CancellationTokenSource, IThreadPoolWorkItem
    {
        public void Execute()
        {
            try
            {
                Cancel();
            }
            catch (AggregateException)
            {
                // a handler's registration threw: counted, since nothing else would see it
                Interlocked.Increment(ref server.CallbackFailures);
            }
        }
    }

    void IValueTaskSource.GetResult(short token) => _readable.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _readable.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readable.OnCompleted(continuation, state, token, flags);

    sealed class WatchArgs(HttpExchange owner) : SocketAsyncEventArgs(unsafeSuppressExecutionContextFlow: true)
    {
        protected override void OnCompleted(SocketAsyncEventArgs e) => owner.WatchCompleted();
    }

    // ---- Timeouts ----

    void SetDeadline(long at, bool idle)
    {
        _idleWait = idle;
        Volatile.Write(ref _deadline, at);
    }

    /// <summary>False when the heartbeat closed the connection while it waited.</summary>
    bool ClearDeadline()
    {
        _idleWait = false;
        return Interlocked.Exchange(ref _deadline, 0) != -1;
    }

    /// <summary>Heartbeat: closes the socket if its wait is past its deadline, or if it is idle and the server stops.
    /// The compare-exchange makes it act at most once per wait, and only on a wait as late as the one read here: a
    /// later wait with the same deadline (the rest of the same head) is past it too, a later socket's waits start
    /// after it, and a send wait's deadline is never reached.</summary>
    internal void Expire(long now, bool stopping)
    {
        Socket? s = Volatile.Read(ref _socket);
        long d = Volatile.Read(ref _deadline);
        if (d <= 0 || (d > now && !(stopping && _idleWait))) return;
        if (Interlocked.CompareExchange(ref _deadline, -1, d) != d) return;
        s?.Dispose();
        Kick();
    }

    /// <summary>Shutdown's last step: closes the socket whatever the connection is doing, with a reset, so the kernel
    /// drops what it holds for the client. A plain close reset only when a receive or send was pending (.NET closes
    /// abortively when it cancels one), not under a handler that blocks its thread.</summary>
    internal void Abort()
    {
        Volatile.Read(ref _socket)?.Close(0);
        Kick();
    }

    // ---- Debug checks: each call compiles away in Release ----

    /// <summary>The request buffer, for the request's getters: in Debug builds reading the request outside its
    /// handler throws, since by then its bytes may be the next request's or a returned buffer's.</summary>
    byte[]? RequestIn
    {
        get
        {
            CheckRequestReadable();
            return _in;
        }
    }

    [Conditional("DEBUG")]
    void CheckRequestReadable()
    {
        if ((Volatile.Read(ref _handler) & HandlerState) == NoHandler) throw new InvalidOperationException("the request was read outside its handler");
    }

    /// <summary>Writes go to the response being built, from BeginResponse to FinishResponse.</summary>
    [Conditional("DEBUG")]
    void CheckWritable()
    {
        if (_bodyStart == 0) throw new InvalidOperationException("the response was written after it was finished");
    }

    /// <summary>The offsets agree with the buffers and with each other. Checked at each change of them, so a bug
    /// fails where it happens rather than at a later copy that trips over it.</summary>
    [Conditional("DEBUG")]
    void CheckInvariants()
    {
        int inLength = _in?.Length ?? 0;
        if (_start < 0 || _start > _end || _end > inLength) Broken($"input [{_start}, {_end}) in a buffer of {inLength}");
        // the parser's offsets count from the request's first byte, within what has been read of it
        int have = _end - _start;
        ref readonly RequestState r = ref _req;
        if ((uint)r.Scanned > (uint)have || (uint)r.LineEnd > (uint)have || (uint)r.HeadEnd > (uint)have
            || (uint)r.BodyEnd > (uint)have || (uint)r.Raw > (uint)have || r.BodyEnd > r.Raw
            || (r.Status == 0 && r.HeadEnd > 0 && (r.HeadEnd > r.BodyEnd || r.LineEnd > r.HeadEnd)))
            Broken($"request lines {r.Scanned}/{r.LineEnd}/{r.HeadEnd}, body to {r.BodyEnd}, raw {r.Raw} with {have} bytes read");
        if ((uint)r.MethodLength > (uint)have || !Within(r.PathStart, r.PathLength, have) || !Within(r.QueryStart, r.QueryLength, have)
            || (r.AuthStart >= 0 ? !Within(r.AuthStart, r.AuthLength, have) : r.AuthStart != -1))
            Broken($"method {r.MethodLength}, path {r.PathStart}+{r.PathLength}, query {r.QueryStart}+{r.QueryLength}, auth {r.AuthStart}+{r.AuthLength} with {have} bytes read");
        int outLength = _out?.Length ?? 0;
        if (_sendStart < 0 || _sendStart > _sendEnd || _sendEnd > outLength) Broken($"to send [{_sendStart}, {_sendEnd}) in a buffer of {outLength}");
        if (_bodyStart > _bodyEnd || _bodyEnd > outLength || (_bodyStart > 0 && _bodyStart < _sendEnd))
            Broken($"body [{_bodyStart}, {_bodyEnd}) after {_sendEnd} to send in a buffer of {outLength}");
    }

    static bool Within(int start, int length, int have) => start >= 0 && length >= 0 && start + length <= have;

    static void Broken(string what) => throw new InvalidOperationException("HttpExchange invariant broken: " + what);

    /// <summary>A connection going back to the pool, or taken from it, holds nothing and waits for nothing.</summary>
    [Conditional("DEBUG")]
    internal void CheckPooled()
    {
        if (_in is not null || _out is not null || _rs != Idle || _armed != 0 || _roomArmed != 0 || Loop is not null || (_handler & HandlerState) != NoHandler || _checkGone || Volatile.Read(ref _deadline) != 0
            || _req.HeadEnd != 0 || _req.Raw != 0 || _cts.IsCancellationRequested)
            throw new InvalidOperationException("a connection went back to the pool holding a buffer, a request or a canceled Aborted");
        CheckInvariants();
    }

    [Conditional("DEBUG")]
    void RunStarted()
    {
        if (_running) throw new InvalidOperationException("a connection was started while its last run hadn't ended");
        _running = true;
        Interlocked.Increment(ref _server.RunsStarted);
    }

    [Conditional("DEBUG")]
    void RunEnded()
    {
        if (!_running) throw new InvalidOperationException("a connection's run ended twice");
        _running = false;
        Interlocked.Increment(ref _server.RunsEnded);
    }

    [Conditional("DEBUG")]
    static void Count(ref int counter) => Interlocked.Increment(ref counter);

    [Conditional("DEBUG")]
    void Phase(ExchangePhase phase) => _server.PhaseHook?.Invoke(this, phase);

    [Conditional("DEBUG")]
    void Peeked(ref bool gone)
    {
        if (_server.WatchHook is { } hook) gone = hook(this, gone);
    }

    [Conditional("DEBUG")]
    void Canceling() => _server.GoneHook?.Invoke(this);

    [Conditional("DEBUG")]
    void SendChecking(ref bool late)
    {
        if (_server.SendCheckHook is { } hook) late = hook(this);
    }
}

/// <summary>Where a connection waits, for <see cref="HttpServer.PhaseHook"/> (Debug builds only).</summary>
internal enum ExchangePhase
{
    /// <summary>No request is buffered: waiting for the next one.</summary>
    Idle,
    /// <summary>Part of a request's head came: waiting for the rest.</summary>
    ReadingHead,
    /// <summary>The head is parsed: waiting for more of the body.</summary>
    ReadingBody,
    /// <summary>The handler's ValueTask hasn't completed: waiting for it while watching for the client to leave.</summary>
    HandlerWaiting,
    /// <summary>The socket took only part of the responses: waiting to send the rest.</summary>
    Sending,
    /// <summary>After the last response (an error, or before a close): reading and dropping what the client still sends.</summary>
    Lingering,
    /// <summary>Starting the zero-byte receive that says the socket is readable (a test can make it fail here).</summary>
    Watching,
    /// <summary>The handler has ended (and the client wasn't gone): its response is about to be finished.</summary>
    HandlerEnded,
}
