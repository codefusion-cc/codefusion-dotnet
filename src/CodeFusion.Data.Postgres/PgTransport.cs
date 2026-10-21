using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// A connection's byte stream, used by one loop thread at a time with blocking calls: the thread sleeps in the
/// kernel until Postgres answers, so a reply costs one wakeup and one receive, with no thread-pool hop. Tests put a
/// scripted server behind it.
/// </summary>
// An abstract Dispose is the whole contract: a transport has no finalizer and nothing to free but its stream, so a
// Dispose(bool) would be a second method every transport overrides the same way.
#pragma warning disable CA1063
public abstract class PgTransport : IDisposable
{
    /// <summary>Waits for at least one byte; 0 when the peer closed the connection.</summary>
    public abstract int Receive(Span<byte> buffer);
    /// <summary>The bytes already waiting, without waiting for more: 0 when there are none. Throws when the peer
    /// closed the connection.</summary>
    public abstract int ReceiveWaiting(Span<byte> buffer);
    /// <summary>Sends all of <paramref name="data"/>. Whenever the peer takes no more, <paramref name="whileBlocked"/>
    /// (if set) takes in what the peer sent meanwhile: a peer blocked on its own replies stops reading, so a
    /// blocking send of a large batch could wait for it forever.</summary>
    public abstract void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null);
    /// <summary>The startup exchange is over.</summary>
    public virtual void Started() { }
    /// <summary>Closes the stream.</summary>
    public abstract void Dispose();
}
#pragma warning restore CA1063

/// <summary>
/// TCP. On Linux and macOS the calls go straight to recv, send and poll on a blocking descriptor: .NET runs a
/// blocking Socket call through its asynchronous engine and allocates an operation each time a receive has to
/// wait, which is every reply here. Elsewhere the Socket methods are used.
/// </summary>
sealed unsafe class SocketTransport : PgTransport
{
    readonly Socket _socket;
    readonly int _fd = -1;
    /// <summary>The descriptor was made blocking (the runtime's shim is there to switch it).</summary>
    readonly bool _switchable;
    /// <summary>When the startup must be over (Stopwatch ticks).</summary>
    readonly long _startupDeadline;
    bool _started;

    static readonly bool Raw = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
    static readonly int DontWait = OperatingSystem.IsLinux() ? 0x40 : 0x80;     // MSG_DONTWAIT
    static readonly int NoSignal = OperatingSystem.IsLinux() ? 0x4000 : 0;       // MSG_NOSIGNAL; on macOS .NET sets SO_NOSIGPIPE
    static readonly int Again = OperatingSystem.IsLinux() ? 11 : 35;             // EAGAIN
    const int Interrupted = 4;                                                   // EINTR
    const short PollIn = 1, PollOut = 4;

    SocketTransport(Socket socket, long startupDeadline)
    {
        _socket = socket;
        _startupDeadline = startupDeadline;
        if (!Raw) return;
        _fd = (int)socket.SafeHandle.DangerousGetHandle();
        // .NET leaves the descriptor non-blocking once it was; a blocking one sleeps in recv itself, one call per
        // reply instead of recv, poll and recv again. Should the runtime's shim be missing, poll does the waiting.
        try
        {
            _switchable = SystemNative_FcntlSetIsNonBlocking(_fd, 0) == 0;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    public static SocketTransport Connect(PgSettings s)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(s.ConnectTimeout.TotalSeconds * Stopwatch.Frequency);
        IPAddress[] addresses = IPAddress.TryParse(s.Host, out var ip) ? [ip] : Lookup(s.Host, s.ConnectTimeout);
        if (addresses.Length == 0) throw new PgConnectionException($"{s.Host} has no address");
        Exception? last = null;
        foreach (var address in addresses)
        {
            // disposed below when this address fails; once connected, the transport owns it
#pragma warning disable CA2000
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
#pragma warning restore CA2000
            try
            {
                socket.NoDelay = true; // every send is a whole group: it goes out now
                // a server that vanished without a reset is noticed in about a minute, not never
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 6);
                Connect(socket, new IPEndPoint(address, s.Port), Left(deadline));
                return new SocketTransport(socket, deadline);
            }
            catch (Exception e)
            {
                socket.Dispose();
                last = e;
            }
        }
        throw new PgConnectionException($"could not connect to {s.Host}:{s.Port}: {last!.Message}", last);
    }

    static IPAddress[] Lookup(string host, TimeSpan timeout)
    {
        var lookup = Dns.GetHostAddressesAsync(host);
        if (!lookup.Wait(timeout)) throw new PgConnectionException($"looking up {host} timed out");
        return lookup.Result;
    }

    static TimeSpan Left(long deadline) =>
        TimeSpan.FromSeconds(Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

    /// <summary>Connects within <paramref name="timeout"/> without ever using the socket asynchronously, which
    /// would leave it non-blocking for good.</summary>
    static void Connect(Socket socket, IPEndPoint endpoint, TimeSpan timeout)
    {
        socket.Blocking = false;
        try
        {
            socket.Connect(endpoint);
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
        {
            if (!socket.Poll(timeout, SelectMode.SelectWrite)) throw new TimeoutException($"connecting to {endpoint} timed out");
            int error = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
            if (error != 0) throw new SocketException(error);
        }
        socket.Blocking = true;
    }

    /// <summary>Ends the startup's deadline: afterwards a receive waits as long as a statement runs.</summary>
    public override void Started() => _started = true;

    public override int Receive(Span<byte> buffer)
    {
        if (!_started && !Wait(PollIn, Left(_startupDeadline))) throw new TimeoutException("Postgres didn't finish the startup in time");
        if (!Raw) return _socket.Receive(buffer, SocketFlags.None);
        fixed (byte* p = buffer)
            while (true)
            {
                nint n = recv(_fd, p, buffer.Length, 0);
                if (n >= 0) return (int)n;
                int errno = Marshal.GetLastPInvokeError();
                // the descriptor is blocking, so recv sleeps until data comes; should it not be, poll does the waiting
                if (errno == Again) Wait(PollIn, Timeout.InfiniteTimeSpan);
                else if (errno != Interrupted) throw Errno("recv");
            }
    }

    public override int ReceiveWaiting(Span<byte> buffer)
    {
        if (!Raw) return ReceiveWaiting(new Waiting(_socket), buffer);
        fixed (byte* p = buffer)
            while (true)
            {
                nint n = recv(_fd, p, buffer.Length, DontWait);
                if (n > 0) return (int)n;
                if (n == 0) throw new PgConnectionException("Postgres closed the connection");
                int errno = Marshal.GetLastPInvokeError();
                if (errno == Again) return 0;
                if (errno != Interrupted) throw Errno("recv");
            }
    }

    /// <summary>What <see cref="ReceiveWaiting{TSocket}"/> asks of a socket it can't call recv on.</summary>
    internal interface IWaitingSocket
    {
        /// <summary>The bytes waiting.</summary>
        int Available { get; }
        /// <summary>Whether a receive wouldn't wait: bytes are waiting, or the peer closed the connection.</summary>
        bool Readable();
        /// <summary>Receives what is waiting.</summary>
        int Receive(Span<byte> buffer);
    }

    readonly struct Waiting(Socket socket) : IWaitingSocket
    {
        public int Available => socket.Available;
        public bool Readable() => socket.Poll(0, SelectMode.SelectRead);
        public int Receive(Span<byte> buffer) => socket.Receive(buffer, SocketFlags.None);
    }

    /// <summary>The bytes waiting on <paramref name="socket"/>, without a recv that doesn't wait: readable with
    /// nothing to read is the peer's close. A reply that comes between the first look and the poll makes it readable
    /// too, so it looks again before it calls it a close.</summary>
    internal static int ReceiveWaiting<TSocket>(TSocket socket, Span<byte> buffer) where TSocket : IWaitingSocket
    {
        if (socket.Available > 0) return socket.Receive(buffer);
        if (!socket.Readable()) return 0;
        if (socket.Available > 0) return socket.Receive(buffer);
        throw new PgConnectionException("Postgres closed the connection");
    }

    public override void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null)
    {
        if (!Raw)
        {
            SendSocket(data, whileBlocked);
            return;
        }
        // A send that may meet a full socket goes out non-blocking, so it can take replies in instead of waiting:
        // on Linux with MSG_DONTWAIT, which costs no system call; macOS ignores that flag on send, so there the
        // socket is switched for the send.
        bool dontWait = whileBlocked is not null && OperatingSystem.IsLinux();
        bool switched = whileBlocked is not null && !dontWait && _switchable && SystemNative_FcntlSetIsNonBlocking(_fd, 1) == 0;
        int flags = dontWait ? NoSignal | DontWait : NoSignal;
        try
        {
            fixed (byte* start = data)
            {
                byte* p = start;
                nint left = data.Length;
                while (left > 0)
                {
                    nint n = send(_fd, p, left, flags);
                    if (n > 0)
                    {
                        p += n;
                        left -= n;
                        continue;
                    }
                    int errno = Marshal.GetLastPInvokeError();
                    if (n < 0 && errno == Interrupted) continue;
                    if (n >= 0 || errno != Again) throw Errno("send");
                    // the socket is full: sleep until it takes more, or Postgres has something for us
                    var fds = new PollFd { Fd = _fd, Events = whileBlocked is null ? PollOut : (short)(PollIn | PollOut) };
                    if (poll(&fds, 1, -1) < 0 && Marshal.GetLastPInvokeError() != Interrupted) throw Errno("poll");
                    if ((fds.Revents & PollIn) != 0) whileBlocked!();
                }
            }
        }
        finally
        {
            // Restoring blocking mode is best effort; the contest code never checked its result either.
#pragma warning disable CA1806
            if (switched) SystemNative_FcntlSetIsNonBlocking(_fd, 0);
#pragma warning restore CA1806
        }
    }

    void SendSocket(ReadOnlySpan<byte> data, Action? whileBlocked)
    {
        if (whileBlocked is not null) _socket.Blocking = false;
        try
        {
            while (data.Length > 0)
            {
                int n = _socket.Send(data, SocketFlags.None, out var error);
                if (error == SocketError.Success) data = data[n..];
                else if (error != SocketError.WouldBlock) throw new SocketException((int)error);
                else if (!_socket.Poll(1000, SelectMode.SelectWrite)) whileBlocked!();
            }
        }
        finally
        {
            if (whileBlocked is not null) _socket.Blocking = true;
        }
    }

    bool Wait(short events, TimeSpan timeout)
    {
        if (!Raw) return _socket.Poll(timeout, events == PollIn ? SelectMode.SelectRead : SelectMode.SelectWrite);
        var fds = new PollFd { Fd = _fd, Events = events };
        int ms = timeout == Timeout.InfiniteTimeSpan ? -1 : (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
        while (true)
        {
            int n = poll(&fds, 1, ms);
            if (n >= 0) return n > 0;
            if (Marshal.GetLastPInvokeError() != Interrupted) throw Errno("poll");
        }
    }

    static PgConnectionException Errno(string call)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new PgConnectionException($"{call} failed: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");
    }

    public override void Dispose() => _socket.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    // SafeDirectories: never a library planted next to the app. On Linux and macOS that only skips the assembly's
    // directory; libc comes from the system and libSystem.Native from the runtime's own directory, as before.
    [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    static extern nint recv(int fd, byte* buffer, nint length, int flags);

    [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    static extern nint send(int fd, byte* buffer, nint length, int flags);

    [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    static extern int poll(PollFd* fds, nuint count, int timeout);

    /// <summary>The runtime's own fcntl(F_SETFL) wrapper: fcntl itself is variadic, which P/Invoke can't call
    /// correctly on Apple arm64.</summary>
    [DllImport("libSystem.Native"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    static extern int SystemNative_FcntlSetIsNonBlocking(nint fd, int isNonBlocking);
}
