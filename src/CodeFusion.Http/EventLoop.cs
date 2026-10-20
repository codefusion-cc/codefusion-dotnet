using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>The event of the loop's own listening socket.</summary>
    protected const ulong ListenToken = ulong.MaxValue - 1;
    /// <summary>Connections taken from the listener in one wake-up, so a flood of them can't starve the others.</summary>
    const int AcceptsPerWakeUp = 64;
    const int Interrupted = 4; // EINTR

    readonly HttpServer _server;
    readonly Thread _thread;
    readonly int _index;
    // This thread's loop index + 1 (0 on other threads), and the loop whose wake-up it is handling, if any: answers
    // finished then wait in _sends until the batch hook has run.
    [ThreadStatic] static int t_index;
    [ThreadStatic] static EventLoop? t_batching, t_loop;
    // Connections that used their share of a wake-up's reads (HttpExchange.Arm), each with the id it had then: the
    // next round, after a wait that doesn't block, takes them up once the others had their turn.
    (HttpExchange X, uint Id)[] _later = new (HttpExchange, uint)[16], _taking = new (HttpExchange, uint)[16];
    int _laterCount;
    readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _failed;
    HttpExchange?[] _sends = new HttpExchange?[64];
    int _sendCount;
    // Linux: the loop's own listening socket (SO_REUSEPORT), whose connections it accepts on its thread; null when the
    // server's one listener serves every loop. Accepting pauses when the process is out of descriptors (the listener
    // is level-triggered, so it would wake the loop for nothing until one closes); the heartbeat resumes it.
    Socket? _listener;
    volatile bool _acceptPaused;
    /// <summary>The epoll or kqueue descriptor; closed by the thread as it ends, once no Add holds it.</summary>
    protected readonly SafeFileHandle Queue;
    volatile bool _stopping;

    protected EventLoop(HttpServer server, int index, int queue)
    {
        _server = server;
        _index = index;
        Queue = new SafeFileHandle(queue, ownsHandle: true);
        _thread = new Thread(Run) { IsBackground = true, Name = $"http event loop {index}" };
    }

    public static EventLoop Create(HttpServer server, int index) =>
        OperatingSystem.IsLinux() ? new Epoll(server, index) : new Kqueue(server, index);

    public void Start() => _thread.Start();

    /// <summary>The index of the loop whose thread this is, or -1.</summary>
    public static int CurrentIndex => t_index - 1;

    /// <summary>The loop whose wake-up this thread is handling now, if any.</summary>
    public static EventLoop? Batching => t_batching;

    /// <summary>The loop whose thread this is, if any.</summary>
    public static EventLoop? Current => t_loop;

    /// <summary>The wake-ups so far: what a connection's share of reads is counted against.</summary>
    public int Wakes { get; private set; }

    /// <summary>Its wait failed: it takes no new connections.</summary>
    public bool Failed => _failed;

    /// <summary>Completes once the thread has ended (or at once if it never started).</summary>
    public Task Exited => _exited.Task;

    /// <summary>On this loop's thread: takes up <paramref name="x"/>'s armed wait in the next round.</summary>
    public void Later(HttpExchange x)
    {
        if (_laterCount == _later.Length) Array.Resize(ref _later, _later.Length * 2);
        _later[_laterCount++] = (x, x.ConnectionId);
    }

    /// <summary>Holds <paramref name="x"/>'s send until the batch's hook has run; the returned wait completes, on
    /// this thread, when its turn comes.</summary>
    public ValueTask WaitToSend(HttpExchange x)
    {
        if (_sendCount == _sends.Length) Array.Resize(ref _sends, _sends.Length * 2);
        _sends[_sendCount++] = x;
        return x.WaitForSendTurn();
    }

    /// <summary>Started and not yet ended.</summary>
    public bool Running => _thread.IsAlive;

    /// <summary>Closes a loop that was never started.</summary>
    public void Discard()
    {
        Closed();
        Queue.Dispose();
        _exited.TrySetResult();
    }

    /// <summary>Wakes the thread to end once its batch is done; <see cref="Exited"/> says when it has. A handler that
    /// blocks the thread delays that, so the stop waits for it only so long.</summary>
    public void Stop()
    {
        _stopping = true;
        if (!_thread.IsAlive) _exited.TrySetResult(); // never started, or ended
        try
        {
            Wake();
        }
        catch (ObjectDisposedException)
        {
            // it has ended already
        }
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
        t_index = _index + 1;
        t_loop = this;
        try
        {
            Queue.DangerousAddRef(ref held);
            int queue = (int)Queue.DangerousGetHandle();
            bool stop = false;
            while (!stop)
            {
                bool fail = false;
                WaitFails(ref fail);
                int n = fail ? -1 : Wait(queue, events, MaxEvents, block: _laterCount == 0);
                if (n < 0)
                {
                    if (!fail && Marshal.GetLastPInvokeError() == Interrupted) continue;
                    // Only a broken descriptor gets here. The loop takes no more connections (its listener closes, so
                    // the kernel sends them to the others), and the ones it had are closed: nothing would wake them.
                    _failed = true;
                    _server.Report("http event loop failed", fail ? new IOException("the test hook failed the wait")
                        : Errno(OperatingSystem.IsLinux() ? "epoll_wait" : "kevent"));
                    _listener?.Dispose();
                    _server.AbortConnectionsOf(this);
                    return;
                }
                Wakes++;
                // Every ready connection first, then the hook, then the answers: the batch ends whole even when the
                // stop's wake comes with it, so no connection is left waiting for its turn to send.
                t_batching = this;
                for (int i = 0; i < n; i++)
                {
                    ulong token = TokenAt(events, i, out bool readable, out bool writable, out bool closed);
                    if (token == WakeToken)
                    {
                        stop = _stopping;
                        continue;
                    }
                    if (token == ListenToken)
                    {
                        AcceptAll();
                        continue;
                    }
                    try
                    {
                        _server.Ready((int)(uint)token, (uint)(token >> 32), readable, writable, closed);
                    }
                    catch (Exception e)
                    {
                        // a connection's loop catches its own failures; this one would end every connection's
                        Interlocked.Increment(ref _server.FailedConnections);
                        _server.Report("http event loop failed", e);
                    }
                }
                TakeLater();
                if (_server.Options.OnEventLoopBatch is { } hook)
                {
                    try
                    {
                        hook(_index);
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref _server.CallbackFailures);
                        _server.Report("http event loop hook failed", e);
                    }
                }
                t_batching = null;
                SendAll();
            }
        }
        finally
        {
            NativeMemory.Free(events);
            if (held) Queue.DangerousRelease();
            Closed();
            Queue.Dispose();
            _exited.TrySetResult();
        }
    }

    /// <summary>The connections put off in the last round, each if it is still the one it was.</summary>
    void TakeLater()
    {
        (_taking, _later) = (_later, _taking);
        int n = _laterCount;
        _laterCount = 0;
        for (int i = 0; i < n; i++)
        {
            (HttpExchange x, uint id) = _taking[i];
            _taking[i] = default;
            try
            {
                if (x.ConnectionId == id) x.Readable(closed: false);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _server.FailedConnections);
                _server.Report("http event loop failed", e);
            }
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    void WaitFails(ref bool fail)
    {
        if (_server.LoopWaitHook is { } hook) fail = hook(_index);
    }

    /// <summary>Linux: makes <paramref name="listener"/> this loop's own, its connections accepted on this loop's
    /// thread. Level-triggered, so connections left after one wake-up's share wake it again.</summary>
    public void Listen(Socket listener)
    {
        _listener = listener;
        if (Watch(listener, add: true, on: true) < 0) throw Errno("epoll_ctl");
    }

    /// <summary>Heartbeat: accepting again after the process ran out of descriptors.</summary>
    public void ResumeAccept()
    {
        if (!_acceptPaused || _listener is not { } l) return;
        _acceptPaused = false;
        try
        {
            if (Watch(l, add: false, on: true) < 0) _acceptPaused = true;
        }
        catch (ObjectDisposedException)
        {
            // the server stopped
        }
    }

    int Watch(Socket listener, bool add, bool on)
    {
        SafeSocketHandle s = listener.SafeHandle;
        bool socketHeld = false, queueHeld = false;
        try
        {
            s.DangerousAddRef(ref socketHeld);
            Queue.DangerousAddRef(ref queueHeld);
            return Watch((int)Queue.DangerousGetHandle(), (int)s.DangerousGetHandle(), ListenToken, add, on);
        }
        finally
        {
            if (queueHeld) Queue.DangerousRelease();
            if (socketHeld) s.DangerousRelease();
        }
    }

    /// <summary>Takes what the listener holds, up to <see cref="AcceptsPerWakeUp"/>, and starts each connection here.</summary>
    void AcceptAll()
    {
        if (_listener is not { } l) return;
        SafeSocketHandle h = l.SafeHandle;
        for (int i = 0; i < AcceptsPerWakeUp; i++)
        {
            int fd, errno;
            bool held = false;
            try
            {
                h.DangerousAddRef(ref held);
                fd = Accept((int)h.DangerousGetHandle());
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            catch (ObjectDisposedException)
            {
                return; // the server stopped
            }
            finally
            {
                if (held) h.DangerousRelease();
            }
            if (fd < 0)
            {
                if (errno == Again) return;
                if (!OutOfResources(errno)) continue; // that connection failed before it was taken; the next may not
                _server.AcceptFailed(new SocketException(errno));
                _acceptPaused = true;
                _ = Watch(l, add: false, on: false);
                return;
            }
            _server.AcceptWorks();
            // Admit owns the socket: it closes it, or its connection does
#pragma warning disable CA2000
            if (Wrap(fd) is { } s) _server.Admit(s, this)?.StartRun();
#pragma warning restore CA2000
        }
    }

    /// <summary>A Socket over an accepted descriptor, which it owns from now on; null (and the descriptor closed) when
    /// the connection was reset before the runtime could look at it.</summary>
    static Socket? Wrap(int fd)
    {
        // the Socket owns the handle once made; until then the catch closes it
#pragma warning disable CA2000
        var handle = new SafeSocketHandle(fd, ownsHandle: true);
#pragma warning restore CA2000
        try
        {
            return new Socket(handle);
        }
        catch (SocketException)
        {
            handle.Dispose();
            return null;
        }
    }

    /// <summary>Gives each held answer its turn, in the order they were finished. Each connection sends and goes on to
    /// its next wait here, with the batch over, so nothing it does now is held.</summary>
    void SendAll()
    {
        for (int i = 0; i < _sendCount; i++)
        {
            HttpExchange x = _sends[i]!;
            _sends[i] = null;
            try
            {
                x.TakeSendTurn();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _server.FailedConnections);
                _server.Report("http event loop failed", e);
            }
        }
        _sendCount = 0;
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

    /// <summary>Waits for events (or, unless <paramref name="block"/>, takes those already there); their count, or -1
    /// and errno.</summary>
    protected abstract int Wait(int queue, byte* events, int max, bool block);

    /// <summary>The token of event <paramref name="i"/>; whether the socket turned readable or writable; and whether the
    /// client closed or reset: an edge that also brought bytes is all the kernel says of that, so the connection must
    /// not take a short read for the end of it. A close or an error makes it both readable and writable.</summary>
    protected abstract ulong TokenAt(byte* events, int i, out bool readable, out bool writable, out bool closed);

    /// <summary>Wakes the connection once socket <paramref name="fd"/>, already in the set, has room to send: on this
    /// loop rather than the runtime's own epoll. -1 and errno on failure.</summary>
    protected abstract int WatchWrite(int queue, int fd, ulong token);

    /// <summary>The connection in <paramref name="slot"/> (while its id is <paramref name="id"/>) waits to send:
    /// its next writable edge goes to it. Throws as <see cref="Add(Socket, int, uint)"/> does.</summary>
    public void WaitWritable(Socket socket, int slot, uint id)
    {
        SafeSocketHandle s = socket.SafeHandle;
        bool socketHeld = false, queueHeld = false;
        try
        {
            s.DangerousAddRef(ref socketHeld);
            Queue.DangerousAddRef(ref queueHeld);
            if (WatchWrite((int)Queue.DangerousGetHandle(), (int)s.DangerousGetHandle(), (uint)slot | (ulong)id << 32) < 0)
                throw Errno(OperatingSystem.IsLinux() ? "epoll_ctl" : "kevent");
        }
        finally
        {
            if (queueHeld) Queue.DangerousRelease();
            if (socketHeld) s.DangerousRelease();
        }
    }

    /// <summary>Adds the listener (<paramref name="add"/>) or changes its events: readable, level-triggered
    /// (<paramref name="on"/>), or none. -1 and errno on failure.</summary>
    protected virtual int Watch(int queue, int fd, ulong token, bool add, bool on) => throw new PlatformNotSupportedException();

    /// <summary>accept4, non-blocking and close-on-exec; -1 and errno on failure.</summary>
    protected virtual int Accept(int listener) => throw new PlatformNotSupportedException();

    /// <summary>EAGAIN.</summary>
    protected virtual int Again => 35;

    /// <summary>An accept error that the next accept would meet too: out of descriptors or memory.</summary>
    protected virtual bool OutOfResources(int errno) => false;

    /// <summary>Makes the thread's wait return a <see cref="WakeToken"/> event.</summary>
    protected abstract void Wake();

    /// <summary>The thread has ended: what the loop owns besides its queue goes.</summary>
    protected virtual void Closed() { }

    /// <summary>Linux: epoll, and an eventfd to wake it.</summary>
#pragma warning disable CA1001 // as the loop's queue
    sealed class Epoll : EventLoop
#pragma warning restore CA1001
    {
        const int EpollCtlAdd = 1, EpollCtlMod = 3, CloExec = 0x80000; // EPOLL_CLOEXEC and EFD_CLOEXEC are O_CLOEXEC
        const int SockNonBlock = 0x800, SockCloExec = 0x80000;
        const int EAgain = 11, ENFile = 23, EMFile = 24, ENoMem = 12, ENoBufs = 105;
        const uint EpollIn = 0x001, EpollOut = 0x004, EpollErr = 0x008, EpollHup = 0x010, EpollRdHup = 0x2000, EpollEt = 1u << 31;
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

        protected override int Wait(int queue, byte* events, int max, bool block) => epoll_wait(queue, events, max, block ? -1 : 0);

        protected override int Watch(int queue, int fd, ulong token, bool add, bool on)
        {
            byte* ev = stackalloc byte[16];
            Event(ev, on ? EpollIn : 0, token);
            return epoll_ctl(queue, add ? EpollCtlAdd : EpollCtlMod, fd, ev);
        }

        protected override int Accept(int listener) => accept4(listener, null, null, SockNonBlock | SockCloExec);

        protected override int Again => EAgain;

        protected override bool OutOfResources(int errno) => errno is EMFile or ENFile or ENoMem or ENoBufs;

        protected override ulong TokenAt(byte* events, int i, out bool readable, out bool writable, out bool closed)
        {
            byte* ev = events + i * EventSize;
            uint e = Unsafe.ReadUnaligned<uint>(ev);
            closed = (e & (EpollRdHup | EpollHup | EpollErr)) != 0;
            readable = closed || (e & EpollIn) != 0;
            writable = (e & (EpollOut | EpollHup | EpollErr)) != 0;
            return Unsafe.ReadUnaligned<ulong>(ev + (Packed ? 4 : 8));
        }

        // Edge-triggered EPOLLOUT stays in the set once added: it fires only when a full socket gets room again, which
        // costs nothing while the sends fit.
        protected override int WatchWrite(int queue, int fd, ulong token)
        {
            byte* ev = stackalloc byte[16];
            Event(ev, EpollIn | EpollRdHup | EpollEt | EpollOut, token);
            return epoll_ctl(queue, EpollCtlMod, fd, ev);
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
        static extern int accept4(int fd, void* address, void* addressLength, int flags);

        [DllImport("libc", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        static extern nint write(int fd, void* buffer, nint count);
    }

    /// <summary>macOS: kqueue, with EV_CLEAR for edges and a user event to wake it.</summary>
    sealed class Kqueue : EventLoop
    {
        const short EvfiltRead = -1, EvfiltWrite = -2, EvfiltUser = -10;
        const ushort EvAdd = 0x1, EvOneShot = 0x10, EvClear = 0x20, EvEof = 0x8000;
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

        protected override int Wait(int queue, byte* events, int max, bool block)
        {
            long* zero = stackalloc long[2]; // struct timespec { 0, 0 }
            zero[0] = zero[1] = 0;
            return kevent(queue, null, 0, (KEvent*)events, max, block ? null : zero);
        }

        protected override ulong TokenAt(byte* events, int i, out bool readable, out bool writable, out bool closed)
        {
            ref KEvent ev = ref ((KEvent*)events)[i];
            bool eof = (ev.Flags & EvEof) != 0;
            readable = ev.Filter == EvfiltRead;
            writable = ev.Filter == EvfiltWrite;
            closed = eof && readable;
            return ev.UData;
        }

        // One-shot: a write filter left on would fire on every acknowledgment that frees room.
        protected override int WatchWrite(int queue, int fd, ulong token)
        {
            KEvent ev = new() { Ident = (nuint)fd, Filter = EvfiltWrite, Flags = EvAdd | EvOneShot, UData = token };
            return kevent(queue, &ev, 1, null, 0, null);
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
