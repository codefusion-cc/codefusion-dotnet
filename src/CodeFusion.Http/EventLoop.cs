using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CodeFusion.Http;

/// <summary>
/// One thread of <see cref="HttpTransport.EventLoop"/>: it waits on epoll (Linux) or kqueue (macOS) for its
/// connections' sockets to turn readable, and hands each edge to its connection (<see cref="HttpExchange.Readable"/>),
/// whose loop then runs on this thread until it next waits. A socket is added once, edge-triggered and for reading
/// only, so a request costs no system call besides its own receive and send, and one wait serves every connection
/// ready at once. Closing a socket takes it out of the set without an event: whoever closes it wakes its connection.
/// </summary>
// Its thread closes the descriptors as it ends (Stop), and Discard closes those of a loop never started: nothing
// else may, while the thread can still wait on them.
#pragma warning disable CA1001
internal abstract unsafe class EventLoop
#pragma warning restore CA1001
{
    /// <summary>Linux and macOS.</summary>
    public static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    const int MaxEvents = 512;
    /// <summary>The event that only wakes the thread, to see the stop.</summary>
    protected const ulong WakeToken = ulong.MaxValue;
    const int Interrupted = 4; // EINTR

    readonly HttpServer _server;
    readonly Thread _thread;
    /// <summary>The epoll or kqueue descriptor; closed by the thread as it ends, once no Add holds it.</summary>
    protected readonly SafeFileHandle Queue;
    volatile bool _stopping;

    protected EventLoop(HttpServer server, int index, int queue)
    {
        _server = server;
        Queue = new SafeFileHandle(queue, ownsHandle: true);
        _thread = new Thread(Run) { IsBackground = true, Name = $"http event loop {index}" };
    }

    public static EventLoop Create(HttpServer server, int index) =>
        OperatingSystem.IsLinux() ? new Epoll(server, index) : new Kqueue(server, index);

    public void Start() => _thread.Start();

    /// <summary>Started and not yet ended.</summary>
    public bool Running => _thread.IsAlive;

    /// <summary>Closes a loop that was never started.</summary>
    public void Discard()
    {
        Closed();
        Queue.Dispose();
    }

    /// <summary>Wakes the thread to end, and waits for it unless this is that thread.</summary>
    public void Stop()
    {
        _stopping = true;
        try
        {
            Wake();
        }
        catch (ObjectDisposedException)
        {
            // it has ended already
        }
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join();
    }

    /// <summary>Watches <paramref name="socket"/> from now on: its edges go to the connection in
    /// <paramref name="slot"/> while that connection's id is <paramref name="id"/>, so a late edge of a closed socket
    /// can't reach the connection object's next one. Throws ObjectDisposedException when the socket or the loop has
    /// closed, IOException when the kernel refuses.</summary>
    public void Add(Socket socket, int slot, uint id)
    {
        SafeSocketHandle s = socket.SafeHandle;
        bool socketHeld = false, queueHeld = false;
        try
        {
            s.DangerousAddRef(ref socketHeld);
            Queue.DangerousAddRef(ref queueHeld);
            if (Add((int)Queue.DangerousGetHandle(), (int)s.DangerousGetHandle(), (uint)slot | (ulong)id << 32) < 0)
                throw Errno(OperatingSystem.IsLinux() ? "epoll_ctl" : "kevent");
        }
        finally
        {
            if (queueHeld) Queue.DangerousRelease();
            if (socketHeld) s.DangerousRelease();
        }
    }

    void Run()
    {
        byte* events = (byte*)NativeMemory.Alloc(MaxEvents, (nuint)EventSize);
        bool held = false;
        try
        {
            Queue.DangerousAddRef(ref held);
            int queue = (int)Queue.DangerousGetHandle();
            while (true)
            {
                int n = Wait(queue, events, MaxEvents);
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted) continue;
                    // only a broken descriptor gets here: its connections are left to their timeouts
                    _server.Report("http event loop failed", Errno(OperatingSystem.IsLinux() ? "epoll_wait" : "kevent"));
                    return;
                }
                for (int i = 0; i < n; i++)
                {
                    ulong token = TokenAt(events, i, out bool closed);
                    if (token == WakeToken)
                    {
                        if (_stopping) return;
                        continue;
                    }
                    try
                    {
                        _server.Readable((int)(uint)token, (uint)(token >> 32), closed);
                    }
                    catch (Exception e)
                    {
                        // a connection's loop catches its own failures; this one would end every connection's
                        Interlocked.Increment(ref _server.FailedConnections);
                        _server.Report("http event loop failed", e);
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(events);
            if (held) Queue.DangerousRelease();
            Closed();
            Queue.Dispose();
        }
    }

    static IOException Errno(string call)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new IOException($"{call} failed: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");
    }

    /// <summary>The size of one event in the wait's array.</summary>
    protected abstract int EventSize { get; }

    /// <summary>Adds <paramref name="fd"/>, edge-triggered for reading, with <paramref name="token"/>; -1 and errno
    /// on failure.</summary>
    protected abstract int Add(int queue, int fd, ulong token);

    /// <summary>Waits for events; their count, or -1 and errno.</summary>
    protected abstract int Wait(int queue, byte* events, int max);

    /// <summary>The token of event <paramref name="i"/>, and whether the client closed or reset: an edge that also
    /// brought bytes is all the kernel says of that, so the connection must not take a short read for the end of it.</summary>
    protected abstract ulong TokenAt(byte* events, int i, out bool closed);

    /// <summary>Makes the thread's wait return a <see cref="WakeToken"/> event.</summary>
    protected abstract void Wake();

    /// <summary>The thread has ended: what the loop owns besides its queue goes.</summary>
    protected virtual void Closed() { }

    /// <summary>Linux: epoll, and an eventfd to wake it.</summary>
#pragma warning disable CA1001 // as the loop's queue
    sealed class Epoll : EventLoop
#pragma warning restore CA1001
    {
        const int EpollCtlAdd = 1, CloExec = 0x80000; // EPOLL_CLOEXEC and EFD_CLOEXEC are O_CLOEXEC
        const uint EpollIn = 0x001, EpollErr = 0x008, EpollHup = 0x010, EpollRdHup = 0x2000, EpollEt = 1u << 31;
        // struct epoll_event is packed on x86 and x86-64: 12 bytes with the data at 4. Elsewhere it is aligned: 16, at 8.
        static readonly bool Packed = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

        readonly SafeFileHandle _wake;

        public Epoll(HttpServer server, int index) : base(server, index, Create())
        {
            int wake = eventfd(0, CloExec);
            if (wake < 0)
            {
                IOException e = Errno("eventfd");
                Queue.Dispose();
                throw e;
            }
            _wake = new SafeFileHandle(wake, ownsHandle: true);
            byte* ev = stackalloc byte[16];
            Event(ev, EpollIn, WakeToken); // level-triggered: it stays readable once written
            if (epoll_ctl((int)Queue.DangerousGetHandle(), EpollCtlAdd, wake, ev) < 0)
            {
                IOException e = Errno("epoll_ctl");
                _wake.Dispose();
                Queue.Dispose();
                throw e;
            }
        }

        static int Create()
        {
            int fd = epoll_create1(CloExec);
            return fd >= 0 ? fd : throw Errno("epoll_create1");
        }

        protected override int EventSize => Packed ? 12 : 16;

        static void Event(byte* ev, uint events, ulong token)
        {
            Unsafe.WriteUnaligned(ev, events);
            Unsafe.WriteUnaligned(ev + (Packed ? 4 : 8), token);
        }

        protected override int Add(int queue, int fd, ulong token)
        {
            byte* ev = stackalloc byte[16];
            Event(ev, EpollIn | EpollRdHup | EpollEt, token);
            return epoll_ctl(queue, EpollCtlAdd, fd, ev);
        }

        protected override int Wait(int queue, byte* events, int max) => epoll_wait(queue, events, max, -1);

        protected override ulong TokenAt(byte* events, int i, out bool closed)
        {
            byte* ev = events + i * EventSize;
            closed = (Unsafe.ReadUnaligned<uint>(ev) & (EpollRdHup | EpollHup | EpollErr)) != 0;
            return Unsafe.ReadUnaligned<ulong>(ev + (Packed ? 4 : 8));
        }

        protected override void Wake()
        {
            bool held = false;
            try
            {
                _wake.DangerousAddRef(ref held);
                ulong one = 1;
                _ = write((int)_wake.DangerousGetHandle(), &one, sizeof(ulong));
            }
            finally
            {
                if (held) _wake.DangerousRelease();
            }
        }

        protected override void Closed() => _wake.Dispose();

        // SafeDirectories: never a library planted next to the app; libc comes from the system.
        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern int epoll_create1(int flags);

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern int epoll_ctl(int epfd, int op, int fd, byte* ev);

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern int epoll_wait(int epfd, byte* events, int maxevents, int timeout);

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern int eventfd(uint initval, int flags);

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern nint write(int fd, void* buffer, nint count);
    }

    /// <summary>macOS: kqueue, with EV_CLEAR for edges and a user event to wake it.</summary>
    sealed class Kqueue : EventLoop
    {
        const short EvfiltRead = -1, EvfiltUser = -10;
        const ushort EvAdd = 0x1, EvClear = 0x20, EvEof = 0x8000;
        const uint NoteTrigger = 0x01000000;

        public Kqueue(HttpServer server, int index) : base(server, index, Create())
        {
            KEvent ev = new() { Ident = 0, Filter = EvfiltUser, Flags = EvAdd | EvClear, UData = WakeToken };
            if (kevent((int)Queue.DangerousGetHandle(), &ev, 1, null, 0, null) < 0)
            {
                IOException e = Errno("kevent");
                Queue.Dispose();
                throw e;
            }
        }

        static int Create()
        {
            int fd = kqueue();
            return fd >= 0 ? fd : throw Errno("kqueue");
        }

        protected override int EventSize => sizeof(KEvent);

        protected override int Add(int queue, int fd, ulong token)
        {
            KEvent ev = new() { Ident = (nuint)fd, Filter = EvfiltRead, Flags = EvAdd | EvClear, UData = token };
            return kevent(queue, &ev, 1, null, 0, null);
        }

        protected override int Wait(int queue, byte* events, int max) => kevent(queue, null, 0, (KEvent*)events, max, null);

        protected override ulong TokenAt(byte* events, int i, out bool closed)
        {
            ref KEvent ev = ref ((KEvent*)events)[i];
            closed = (ev.Flags & EvEof) != 0;
            return ev.UData;
        }

        protected override void Wake()
        {
            bool held = false;
            try
            {
                Queue.DangerousAddRef(ref held);
                KEvent ev = new() { Ident = 0, Filter = EvfiltUser, FFlags = NoteTrigger, UData = WakeToken };
                _ = kevent((int)Queue.DangerousGetHandle(), &ev, 1, null, 0, null);
            }
            finally
            {
                if (held) Queue.DangerousRelease();
            }
        }

        /// <summary>struct kevent on 64-bit Darwin.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct KEvent
        {
            public nuint Ident;
            public short Filter;
            public ushort Flags;
            public uint FFlags;
            public nint Data;
            public ulong UData;
        }

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern int kqueue();

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern int kevent(int kq, KEvent* changes, int nchanges, KEvent* events, int nevents, void* timeout);
    }
}
