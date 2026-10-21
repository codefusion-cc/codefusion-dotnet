using System;
using System.Net;

namespace CodeFusion.Http;

/// <summary>The body for a refused request's status, sent as application/json.</summary>
public delegate ReadOnlySpan<byte> HttpRejectBody(int status);

/// <summary>Where to listen and the limits. The defaults are Kestrel's, except the keep-alive timeout and the body
/// limit, which are what the Conduit server set on Kestrel.</summary>
public sealed class HttpServerOptions
{
    /// <summary>Where to listen; port 0 picks a free port, which <see cref="HttpServer.LocalEndPoint"/> then says.</summary>
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Loopback, 3000);

    /// <summary>The default 512 overflows while tens of thousands of clients connect at once; the kernel caps it
    /// at net.core.somaxconn.</summary>
    public int Backlog { get; set; } = 65535;

    /// <summary>How long a connection may wait for the first byte of its next request. Browsers keep an idle
    /// connection through a 70 s think time and Nginx's upstream keep-alive is 60 s.</summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a request may take from its first byte to its last header line (a new connection's first
    /// request, from the connection), how long a body read may go without a byte, and how long a response the socket
    /// couldn't take at once may go without the client's TCP acknowledging a byte more of it.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The slowest a request body may arrive once its grace period is over; slower closes the connection.
    /// Each byte pays for 1/rate seconds from the server's first wait for the body, but no more than
    /// <see cref="RequestTimeout"/> (or the grace period, if longer) past the moment it came, so bytes sent early
    /// can't buy a crawl later, and a client faster than the rate between any two of its sends is never cut off.
    /// Only the body's own bytes count, not chunk framing. Kestrel's default, 240 B/s after 5 s. Null turns it
    /// off.</summary>
    public MinDataRate? MinRequestBodyDataRate { get; set; } = MinDataRate.Default;

    /// <summary>The slowest a client may take responses the socket couldn't take at once, once its grace period is
    /// over; slower closes the connection. Each byte the client's TCP acknowledges (on Linux and macOS; elsewhere what
    /// the socket took after it first filled) pays for 1/rate seconds of the time the server waited on it since it
    /// last waited for the client's next request, but no more than <see cref="RequestTimeout"/> (or the grace period,
    /// if longer) ahead, so a fast start can't buy a crawl later, and a client reading faster than the rate between
    /// any two acknowledgments is never cut off. Kestrel's default, 240 B/s after 5 s. Null turns it off.</summary>
    public MinDataRate? MinResponseDataRate { get; set; } = MinDataRate.Default;

    /// <summary>The most connections open at once; past it a new connection is closed as soon as it is accepted.
    /// Null, the default as in Kestrel, sets no limit, so nothing bounds how many slow clients can each hold their
    /// connection and its buffers.</summary>
    public int? MaxConnections { get; set; }

    /// <summary>The request line with its CRLF, in bytes; longer gets 414.</summary>
    public int MaxRequestLineSize { get; set; } = 8 * 1024;

    /// <summary>The header lines and the blank line after them, in bytes; more gets 431. Trailers of a chunked
    /// body count against it separately.</summary>
    public int MaxRequestHeadersTotalSize { get; set; } = 32 * 1024;

    /// <summary>More header lines than this gets 431.</summary>
    public int MaxRequestHeaderCount { get; set; } = 100;

    /// <summary>The body after any chunked decoding, in bytes; more gets 413.</summary>
    public int MaxRequestBodySize { get; set; } = 1 << 20;

    /// <summary>What a request starts with. Typical requests, Authorization header included, fit in it; larger
    /// ones double it as their bytes come, up to the whole request, so a connection holds at most about twice what
    /// it has sent, whatever its Content-Length says.</summary>
    public int RequestBufferSize { get; set; } = 4 * 1024;

    /// <summary>What a response starts with; a larger body grows it.</summary>
    public int ResponseBufferSize { get; set; } = 16 * 1024;

    /// <summary>Request and response buffers kept for reuse, of each size. Only connections with a request in
    /// flight hold one.</summary>
    public int MaxPooledBuffers { get; set; } = 4096;

    /// <summary>Connection objects (with their socket event args and the handler's per-connection state) kept for
    /// reuse after their socket closes.</summary>
    public int MaxPooledConnections { get; set; } = 4096;

    /// <summary>How often the Date header is refreshed, timeouts are checked, and a reset is looked for that only the
    /// socket's error shows (<see cref="HttpExchange.Aborted"/>).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The body of the server's own answer to a request it refused (400, 413, 414, 431, 505); none when null.</summary>
    public HttpRejectBody? RejectBody { get; set; }

    /// <summary>Called with what a handler threw, after the response was reset to a 500 with no body: it may write
    /// the body. Null hands the exception to <see cref="OnError"/>.</summary>
    public Action<HttpExchange, Exception>? OnHandlerError { get; set; }

    /// <summary>Called with what the server caught outside a handler, as a short line and the exception: a
    /// connection that failed, accepting that fails (once per run of failures, and once when it works again), and a
    /// handler's exception when <see cref="OnHandlerError"/> is null. It may be called from any thread, and what it
    /// throws is dropped. By default a line on stderr; null drops them.</summary>
    public Action<string, Exception?>? OnError { get; set; } = Stderr;

    /// <summary>The error hooks' default, and the package's one way to the console: a server that fails quietly
    /// hides its bugs.</summary>
    internal static void Stderr(string what, Exception? error)
    {
#pragma warning disable RS0030 // the hooks' default: an app that sets its own hook writes nothing here
        Console.Error.WriteLine(error is null ? what : $"{what}: {error}");
#pragma warning restore RS0030
    }

    internal void Validate()
    {
        if (Backlog < 1 || MaxRequestLineSize < 16 || MaxRequestHeadersTotalSize < 2 || MaxRequestHeaderCount < 0
            || MaxRequestBodySize < 0 || RequestBufferSize < 256 || ResponseBufferSize < 1024 || MaxPooledBuffers < 0
            || MaxPooledConnections < 0 || KeepAliveTimeout <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero
            || HeartbeatInterval <= TimeSpan.Zero || MaxConnections < 1)
            throw new ArgumentException("an HttpServerOptions value is out of range");
    }
}

/// <summary>A minimum data rate, in bytes per second, that starts to count once <see cref="GracePeriod"/> is over.</summary>
public sealed class MinDataRate
{
    internal static readonly MinDataRate Default = new(240, TimeSpan.FromSeconds(5));

    /// <summary>A rate of <paramref name="bytesPerSecond"/> (more than 0; however small, the request timeout still
    /// applies), enforced after <paramref name="gracePeriod"/> (0 or more).</summary>
    public MinDataRate(double bytesPerSecond, TimeSpan gracePeriod)
    {
        if (!(bytesPerSecond > 0) || double.IsInfinity(bytesPerSecond)) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero);
        BytesPerSecond = bytesPerSecond;
        GracePeriod = gracePeriod;
    }

    /// <summary>The bytes per second the transfer must keep up.</summary>
    public double BytesPerSecond { get; }

    /// <summary>How long the transfer may go before the rate counts.</summary>
    public TimeSpan GracePeriod { get; }
}
