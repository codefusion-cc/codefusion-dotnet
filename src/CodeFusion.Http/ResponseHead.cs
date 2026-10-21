using System;
using System.Buffers.Text;

namespace CodeFusion.Http;

/// <summary>The status line and headers of a response, written from constants and the cached Date line.</summary>
internal static class ResponseHead
{
    /// <summary>Room left in front of each body for its head: the longest head below is about 300 bytes, with the
    /// longest media type (<see cref="MediaType.MaxLength"/>) and server name (<see cref="HttpServerOptions.MaxServerNameLength"/>).</summary>
    public const int Reserve = 320;

    /// <summary>The head; <paramref name="contentType"/> and <paramref name="server"/> are whole header lines, or empty for none.</summary>
    public static int Write(Span<byte> d, int status, int contentLength, ReadOnlySpan<byte> contentType, ReadOnlySpan<byte> server,
        int retryAfter, bool close, bool keepAlive)
    {
        int n = StatusLine(d, status);
        n += Copy(contentType, d[n..]);
        if (contentLength >= 0)
        {
            n += Copy("Content-Length: "u8, d[n..]);
            Utf8Formatter.TryFormat(contentLength, d[n..], out int w);
            n += w;
            n += Copy("\r\n"u8, d[n..]);
        }
        if (retryAfter > 0)
        {
            n += Copy("Retry-After: "u8, d[n..]);
            Utf8Formatter.TryFormat(retryAfter, d[n..], out int w);
            n += w;
            n += Copy("\r\n"u8, d[n..]);
        }
        if (close) n += Copy("Connection: close\r\n"u8, d[n..]);
        else if (keepAlive) n += Copy("Connection: keep-alive\r\n"u8, d[n..]);
        n += Copy(server, d[n..]);
        n += Copy(HttpDate.Line, d[n..]);
        n += Copy("\r\n"u8, d[n..]);
        return n;
    }

    public static ReadOnlySpan<byte> Continue => "HTTP/1.1 100 Continue\r\n\r\n"u8;

    static int Copy(ReadOnlySpan<byte> s, Span<byte> d)
    {
        s.CopyTo(d);
        return s.Length;
    }

    static int StatusLine(Span<byte> d, int status)
    {
        ReadOnlySpan<byte> line = status switch
        {
            200 => "HTTP/1.1 200 OK\r\n"u8,
            201 => "HTTP/1.1 201 Created\r\n"u8,
            204 => "HTTP/1.1 204 No Content\r\n"u8,
            304 => "HTTP/1.1 304 Not Modified\r\n"u8,
            400 => "HTTP/1.1 400 Bad Request\r\n"u8,
            401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
            403 => "HTTP/1.1 403 Forbidden\r\n"u8,
            404 => "HTTP/1.1 404 Not Found\r\n"u8,
            405 => "HTTP/1.1 405 Method Not Allowed\r\n"u8,
            409 => "HTTP/1.1 409 Conflict\r\n"u8,
            413 => "HTTP/1.1 413 Payload Too Large\r\n"u8,
            414 => "HTTP/1.1 414 URI Too Long\r\n"u8,
            422 => "HTTP/1.1 422 Unprocessable Entity\r\n"u8,
            429 => "HTTP/1.1 429 Too Many Requests\r\n"u8,
            431 => "HTTP/1.1 431 Request Header Fields Too Large\r\n"u8,
            500 => "HTTP/1.1 500 Internal Server Error\r\n"u8,
            503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
            505 => "HTTP/1.1 505 HTTP Version Not Supported\r\n"u8,
            _ => default,
        };
        if (!line.IsEmpty) return Copy(line, d);
        // Any other code goes out with an empty reason phrase, which RFC 9112 allows.
        int n = Copy("HTTP/1.1 "u8, d);
        Utf8Formatter.TryFormat(status, d[n..], out int w);
        n += w;
        return n + Copy(" \r\n"u8, d[n..]);
    }
}
