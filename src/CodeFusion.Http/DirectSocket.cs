using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CodeFusion.Http;

/// <summary>
/// recv and send on a non-blocking socket, called directly for <see cref="HttpTransport.EventLoop"/> (Linux and
/// macOS): no GC transition, since neither call can block, and none of Socket's checks, error mapping and telemetry.
/// The handle is held for the call, as Socket holds it: a socket closed under the connection is an
/// ObjectDisposedException, never a call on a descriptor number the kernel has given to another socket since.
/// </summary>
internal static unsafe partial class DirectSocket
{
    static readonly int Again = OperatingSystem.IsLinux() ? 11 : 35;         // EAGAIN (and EWOULDBLOCK)
    static readonly int NoSignal = OperatingSystem.IsLinux() ? 0x4000 : 0;   // MSG_NOSIGNAL; on macOS .NET sets SO_NOSIGPIPE
    const int Interrupted = 4;                                               // EINTR
    const int ConnectionReset = 54, LinuxConnectionReset = 104;              // ECONNRESET on macOS and on Linux

    public static int Receive(SafeSocketHandle socket, Span<byte> buffer, out SocketError error)
    {
        bool held = false;
        try
        {
            socket.DangerousAddRef(ref held);
            int fd = (int)socket.DangerousGetHandle();
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    nint n = recv(fd, p, buffer.Length, 0);
                    if (n >= 0)
                    {
                        error = SocketError.Success;
                        return (int)n;
                    }
                    int errno = Marshal.GetLastSystemError();
                    if (errno != Interrupted) return Failed(errno, out error);
                }
            }
        }
        finally
        {
            if (held) socket.DangerousRelease();
        }
    }

    public static int Send(SafeSocketHandle socket, ReadOnlySpan<byte> buffer, out SocketError error)
    {
        bool held = false;
        try
        {
            socket.DangerousAddRef(ref held);
            int fd = (int)socket.DangerousGetHandle();
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    nint n = send(fd, p, buffer.Length, NoSignal);
                    if (n >= 0)
                    {
                        error = SocketError.Success;
                        return (int)n;
                    }
                    int errno = Marshal.GetLastSystemError();
                    if (errno != Interrupted) return Failed(errno, out error);
                }
            }
        }
        finally
        {
            if (held) socket.DangerousRelease();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Failed(int errno, out SocketError error)
    {
        error = errno == Again ? SocketError.WouldBlock
            : errno is ConnectionReset or LinuxConnectionReset ? SocketError.ConnectionReset
            : SocketError.SocketError;
        return 0;
    }

    // SuppressGCTransition: the socket is non-blocking, so each call returns in microseconds. SafeDirectories: never a
    // library planted next to the app; libc comes from the system. errno is read straight after the call, with
    // nothing in between that could change it.
    [LibraryImport("libc"), SuppressGCTransition, DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint recv(int fd, byte* buffer, nint length, int flags);

    [LibraryImport("libc"), SuppressGCTransition, DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint send(int fd, byte* buffer, nint length, int flags);
}
