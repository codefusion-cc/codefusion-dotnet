namespace CodeFusion.Http;

/// <summary>How a connection learns that its client sent bytes (<see cref="HttpServerOptions.Transport"/>). Either
/// way an idle connection holds no buffer.</summary>
public enum HttpTransport
{
    /// <summary>.NET's sockets, on every platform: a zero-byte receive waits for each request. On Linux and macOS
    /// that receive is tried at once, so every request costs a receive call that finds nothing before the one that
    /// reads it, and its completion goes through the runtime's own socket thread.</summary>
    Sockets,

    /// <summary>The server's own threads (<see cref="HttpServerOptions.EventLoops"/>), each waiting on epoll (Linux)
    /// or kqueue (macOS) for its share of the connections: one receive call per request, and the waits of all the
    /// connections ready at once shared. A handler runs on its loop's thread until it first waits, so one that blocks
    /// holds up every connection on that loop. Linux and macOS only.</summary>
    EventLoop,
}
