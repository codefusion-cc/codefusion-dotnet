using System;
using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace CodeFusion.Http;

internal enum ParseResult : byte { Incomplete, Done, Error }

/// <summary>
/// What is known so far about the request at the front of a connection's buffer. Offsets are from the request's
/// first byte, so moving the buffer's contents keeps them valid. Input that arrives in pieces is scanned once:
/// <see cref="Scanned"/> and the chunk fields say where scanning stopped.
/// </summary>
internal struct RequestState
{
    public int Scanned;      // first byte of the next line not yet parsed
    public int LineEnd;      // first byte after the request line's LF, 0 until it is parsed
    public int HeadEnd;      // first byte after the blank line, 0 until it is parsed
    public int HeaderCount;
    public int Status;       // the response status on ParseResult.Error

    public RequestMethod Method;
    public int MethodLength;
    public int PathStart, PathLength, QueryStart, QueryLength;
    public int AuthStart, AuthLength; // AuthStart < 0: no Authorization header
    public long ContentLength;        // < 0: none
    public bool Http11, Chunked, Close, KeepAlive, ExpectContinue, SeenTransferEncoding;
    public int HostCount;

    // The body: [HeadEnd, BodyEnd) once decoded; Raw is where the request's next undecoded byte is.
    public int BodyEnd, Raw;
    public byte Chunk;       // ChunkState
    public bool ChunkDigit;  // a hex digit was read on this chunk-size line
    public long ChunkLeft;   // chunk-size, then the data bytes still to copy
    public int ChunkLine;    // bytes on this chunk-size line
    public int TrailerBytes;

    public void Reset()
    {
        this = default;
        AuthStart = -1;
        ContentLength = -1;
    }

    /// <summary>Whether the connection stays open after this request (before the server's own reasons to close).</summary>
    public readonly bool Persistent => Http11 ? !Close : KeepAlive && !Close;

    /// <summary>The request's length in the buffer, framing included, once it is Done.</summary>
    public readonly int Total => Raw;
}

internal readonly struct Limits(HttpServerOptions o)
{
    public readonly int RequestLine = o.MaxRequestLineSize, Headers = o.MaxRequestHeadersTotalSize,
        HeaderCount = o.MaxRequestHeaderCount, Body = o.MaxRequestBodySize;
}

/// <summary>
/// HTTP/1.1 request parsing (RFC 9112), strict where leniency lets two parsers disagree about where a request ends:
/// bare LF, obs-fold, whitespace before the colon, Content-Length with Transfer-Encoding, repeated framing headers
/// (and a repeated Authorization) and malformed chunk sizes are all 400. It never allocates.
/// </summary>
internal static class RequestParser
{
    // tchar (RFC 9110 5.6.2)
    static readonly SearchValues<byte> Token = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);
    // Control bytes other than HTAB, which a field value may not hold.
    static readonly SearchValues<byte> ValueCtl = SearchValues.Create(
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 127]);

    const byte CR = (byte)'\r', LF = (byte)'\n', SP = (byte)' ', HT = (byte)'\t';

    /// <summary>Parses the request line and headers in <paramref name="req"/> (the bytes buffered so far, from
    /// the request's first byte), carrying on from where the last call stopped.</summary>
    public static ParseResult ParseHead(ReadOnlySpan<byte> req, ref RequestState s, in Limits limits)
    {
        while (true)
        {
            int nl = req[s.Scanned..].IndexOf(LF);
            if (nl < 0)
            {
                // No complete line yet: refuse as soon as the line can no longer fit.
                if (s.LineEnd == 0) return req.Length >= limits.RequestLine ? Fail(ref s, 414) : ParseResult.Incomplete;
                return req.Length - s.LineEnd >= limits.Headers ? Fail(ref s, 431) : ParseResult.Incomplete;
            }
            int next = s.Scanned + nl + 1;
            // The limit before the line's syntax, as while its LF hasn't come: however the bytes are cut, a line
            // past the limit gets the same answer.
            if (s.LineEnd == 0 ? next > limits.RequestLine : next - s.LineEnd > limits.Headers)
                return Fail(ref s, s.LineEnd == 0 ? 414 : 431);
            if (nl == 0 || req[s.Scanned + nl - 1] != CR) return Fail(ref s, 400); // bare LF
            ReadOnlySpan<byte> line = req.Slice(s.Scanned, nl - 1);
            if (s.LineEnd == 0)
            {
                int status = RequestLine(line, ref s);
                if (status != 0) return Fail(ref s, status);
                s.LineEnd = next;
            }
            else
            {
                if (line.IsEmpty)
                {
                    s.HeadEnd = next;
                    s.Scanned = next;
                    return Finish(ref s, limits);
                }
                if (++s.HeaderCount > limits.HeaderCount) return Fail(ref s, 431);
                if (!Header(line, s.Scanned, ref s)) return Fail(ref s, 400);
            }
            s.Scanned = next;
        }
    }

    static ParseResult Fail(ref RequestState s, int status)
    {
        s.Status = status;
        return ParseResult.Error;
    }

    /// <summary>method SP request-target SP HTTP-version, origin-form targets only. 0 when valid, else the status.</summary>
    static int RequestLine(ReadOnlySpan<byte> line, ref RequestState s)
    {
        int sp1 = line.IndexOf(SP);
        if (sp1 <= 0 || line[..sp1].ContainsAnyExcept(Token)) return 400;
        ReadOnlySpan<byte> rest = line[(sp1 + 1)..];
        int sp2 = rest.IndexOf(SP);
        if (sp2 <= 0) return 400;
        ReadOnlySpan<byte> target = rest[..sp2], version = rest[(sp2 + 1)..];

        if (version.Length != 8 || !version.StartsWith("HTTP/"u8) || !IsDigit(version[5]) || version[6] != '.' || !IsDigit(version[7]))
            return 400;
        if (version[5] != '1' || version[7] > '1') return 505;
        s.Http11 = version[7] == '1';

        // Visible ASCII only; absolute-form, authority-form and '*' are for proxies and CONNECT/OPTIONS, which
        // this server doesn't serve.
        if (target[0] != '/' || target.ContainsAnyExceptInRange((byte)0x21, (byte)0x7E)) return 400;

        s.MethodLength = sp1;
        s.Method = Method(line[..sp1]);
        int q = target.IndexOf((byte)'?');
        s.PathStart = sp1 + 1;
        s.PathLength = q < 0 ? target.Length : q;
        s.QueryStart = q < 0 ? s.PathStart + target.Length : s.PathStart + q + 1;
        s.QueryLength = q < 0 ? 0 : target.Length - q - 1;
        return 0;
    }

    static RequestMethod Method(ReadOnlySpan<byte> m) => m.Length switch
    {
        3 when m.SequenceEqual("GET"u8) => RequestMethod.Get,
        3 when m.SequenceEqual("PUT"u8) => RequestMethod.Put,
        4 when m.SequenceEqual("POST"u8) => RequestMethod.Post,
        4 when m.SequenceEqual("HEAD"u8) => RequestMethod.Head,
        5 when m.SequenceEqual("PATCH"u8) => RequestMethod.Patch,
        6 when m.SequenceEqual("DELETE"u8) => RequestMethod.Delete,
        7 when m.SequenceEqual("OPTIONS"u8) => RequestMethod.Options,
        _ => RequestMethod.Other,
    };

    /// <summary>One field line at <paramref name="at"/>. Remembers the fields the server acts on.</summary>
    static bool Header(ReadOnlySpan<byte> line, int at, ref RequestState s)
    {
        int colon = line.IndexOf((byte)':');
        // A leading space or tab is obs-fold; a space before the colon is not a token byte either.
        if (colon <= 0 || line[..colon].ContainsAnyExcept(Token)) return false;
        ReadOnlySpan<byte> name = line[..colon];
        int start = colon + 1, end = line.Length;
        while (start < end && line[start] is SP or HT) start++;
        while (end > start && line[end - 1] is SP or HT) end--;
        ReadOnlySpan<byte> value = line[start..end];
        if (value.ContainsAny(ValueCtl)) return false;

        switch (name.Length)
        {
            case 4 when Ascii.EqualsIgnoreCase(name, "host"u8):
                return ++s.HostCount == 1;
            case 6 when Ascii.EqualsIgnoreCase(name, "expect"u8):
                if (Ascii.EqualsIgnoreCase(value, "100-continue"u8)) s.ExpectContinue = true;
                return true;
            case 10 when Ascii.EqualsIgnoreCase(name, "connection"u8):
                ConnectionTokens(value, ref s);
                return true;
            case 13 when Ascii.EqualsIgnoreCase(name, "authorization"u8):
                // A repeat: a proxy in front that reads the last one would see another identity than the app.
                if (s.AuthStart >= 0) return false;
                s.AuthStart = at + start;
                s.AuthLength = value.Length;
                return true;
            case 14 when Ascii.EqualsIgnoreCase(name, "content-length"u8):
                return s.ContentLength < 0 && TryContentLength(value, out s.ContentLength);
            case 17 when Ascii.EqualsIgnoreCase(name, "transfer-encoding"u8):
                // Only a single "chunked": any other coding, a list or a repeat could be framed differently by
                // whatever sits in front of this server.
                if (s.SeenTransferEncoding || !Ascii.EqualsIgnoreCase(value, "chunked"u8)) return false;
                s.SeenTransferEncoding = s.Chunked = true;
                return true;
            default:
                return true;
        }
    }

    static void ConnectionTokens(ReadOnlySpan<byte> value, ref RequestState s)
    {
        foreach (Range r in value.Split((byte)','))
        {
            ReadOnlySpan<byte> t = value[r].Trim(" \t"u8);
            if (Ascii.EqualsIgnoreCase(t, "close"u8)) s.Close = true;
            else if (Ascii.EqualsIgnoreCase(t, "keep-alive"u8)) s.KeepAlive = true;
        }
    }

    /// <summary>1*DIGIT, no sign, no list, no overflow (leading zeros allowed).</summary>
    static bool TryContentLength(ReadOnlySpan<byte> v, out long n)
    {
        n = 0;
        if (v.IsEmpty) return false;
        foreach (byte b in v)
        {
            if (!IsDigit(b) || n > (long.MaxValue - 9) / 10) return false;
            n = n * 10 + (b - '0');
        }
        return true;
    }

    static ParseResult Finish(ref RequestState s, in Limits limits)
    {
        if (s.Http11 && s.HostCount == 0) return Fail(ref s, 400);
        if (s.Chunked && (s.ContentLength >= 0 || !s.Http11)) return Fail(ref s, 400);
        if (s.ContentLength > limits.Body) return Fail(ref s, 413);
        s.BodyEnd = s.Raw = s.HeadEnd;
        return ParseResult.Done;
    }

    /// <summary>
    /// Brings the body forward: for Content-Length, whether all of it is here; for chunked, decodes what arrived
    /// in place, so the decoded body sits contiguous from HeadEnd and Raw moves on through the framing.
    /// </summary>
    public static ParseResult ParseBody(Span<byte> req, ref RequestState s, in Limits limits)
    {
        if (!s.Chunked)
        {
            long total = s.HeadEnd + Math.Max(0, s.ContentLength);
            if (req.Length < total) return ParseResult.Incomplete;
            s.BodyEnd = s.Raw = (int)total;
            return ParseResult.Done;
        }
        return Chunked(req, ref s, limits);
    }

    enum ChunkState : byte { Size, Ext, SizeLf, Data, DataCr, DataLf, TrailerStart, TrailerName, Trailer, TrailerLf, EndLf, SizeBws }

    const int MaxChunkLine = 4096; // chunk-size and extensions, which nothing here reads

    static ParseResult Chunked(Span<byte> req, ref RequestState s, in Limits limits)
    {
        while (s.Raw < req.Length)
        {
            byte b = req[s.Raw];
            switch ((ChunkState)s.Chunk)
            {
                case ChunkState.Size:
                {
                    int hex = Query.Hex(b);
                    if (hex >= 0)
                    {
                        // Refuse a size that, with what is decoded already, is past the limit. ChunkLeft never
                        // exceeds the room, so this can't overflow.
                        long size = s.ChunkLeft * 16 + hex;
                        if (size > limits.Body - (s.BodyEnd - s.HeadEnd)) return Fail(ref s, 413);
                        s.ChunkLeft = size;
                        s.ChunkDigit = true;
                    }
                    else if (!s.ChunkDigit) return Fail(ref s, 400);
                    else if (b == CR) s.Chunk = (byte)ChunkState.SizeLf;
                    else if (b == ';') s.Chunk = (byte)ChunkState.Ext;
                    else if (b is SP or HT) s.Chunk = (byte)ChunkState.SizeBws;
                    else return Fail(ref s, 400);
                    if (++s.ChunkLine > MaxChunkLine) return Fail(ref s, 400);
                    s.Raw++;
                    break;
                }
                case ChunkState.SizeBws:
                    // Whitespace after the size only before an extension (RFC 9112: BWS ";"): "1 0" is no size,
                    // and a reader that skips the space would take it for 0x10.
                    if (b == ';') s.Chunk = (byte)ChunkState.Ext;
                    else if (b is not (SP or HT)) return Fail(ref s, 400);
                    if (++s.ChunkLine > MaxChunkLine) return Fail(ref s, 400);
                    s.Raw++;
                    break;
                case ChunkState.Ext:
                    if (b == CR) s.Chunk = (byte)ChunkState.SizeLf;
                    else if (b == LF || (b < 0x20 && b != HT) || b == 0x7F) return Fail(ref s, 400);
                    if (++s.ChunkLine > MaxChunkLine) return Fail(ref s, 400);
                    s.Raw++;
                    break;
                case ChunkState.SizeLf:
                    if (b != LF) return Fail(ref s, 400);
                    s.Raw++;
                    s.ChunkDigit = false;
                    s.ChunkLine = 0;
                    s.Chunk = (byte)(s.ChunkLeft == 0 ? ChunkState.TrailerStart : ChunkState.Data);
                    break;
                case ChunkState.Data:
                {
                    int n = (int)Math.Min(s.ChunkLeft, req.Length - s.Raw);
                    if (s.Raw != s.BodyEnd) req.Slice(s.Raw, n).CopyTo(req[s.BodyEnd..]);
                    s.BodyEnd += n;
                    s.Raw += n;
                    s.ChunkLeft -= n;
                    if (s.ChunkLeft == 0) s.Chunk = (byte)ChunkState.DataCr;
                    break;
                }
                case ChunkState.DataCr:
                    if (b != CR) return Fail(ref s, 400);
                    s.Raw++;
                    s.Chunk = (byte)ChunkState.DataLf;
                    break;
                case ChunkState.DataLf:
                    if (b != LF) return Fail(ref s, 400);
                    s.Raw++;
                    s.Chunk = (byte)ChunkState.Size;
                    break;
                case ChunkState.TrailerStart:
                    // A trailer field line, held to the same syntax as a header; its value isn't used.
                    if (b == CR) s.Chunk = (byte)ChunkState.EndLf;
                    else if (Token.Contains(b)) s.Chunk = (byte)ChunkState.TrailerName;
                    else return Fail(ref s, 400);
                    if (++s.TrailerBytes > limits.Headers) return Fail(ref s, 431);
                    s.Raw++;
                    break;
                case ChunkState.TrailerName:
                    if (b == ':') s.Chunk = (byte)ChunkState.Trailer;
                    else if (!Token.Contains(b)) return Fail(ref s, 400);
                    if (++s.TrailerBytes > limits.Headers) return Fail(ref s, 431);
                    s.Raw++;
                    break;
                case ChunkState.Trailer:
                    if (b == CR) s.Chunk = (byte)ChunkState.TrailerLf;
                    else if (b == LF || (b < 0x20 && b != HT) || b == 0x7F) return Fail(ref s, 400);
                    if (++s.TrailerBytes > limits.Headers) return Fail(ref s, 431);
                    s.Raw++;
                    break;
                case ChunkState.TrailerLf:
                    if (b != LF) return Fail(ref s, 400);
                    s.Raw++;
                    s.Chunk = (byte)ChunkState.TrailerStart;
                    break;
                case ChunkState.EndLf:
                    if (b != LF) return Fail(ref s, 400);
                    s.Raw++;
                    return ParseResult.Done;
            }
        }
        return ParseResult.Incomplete;
    }

    /// <summary>
    /// Kestrel's path rules, in place: %XX is decoded except %2F (so an escaped slash can't make a new segment),
    /// then "." and ".." segments are removed. Returns the new length, or -1 for a NUL or invalid UTF-8 (400).
    /// </summary>
    public static int NormalizePath(Span<byte> path)
    {
        int len = path.Length;
        if (path.IndexOf((byte)'%') >= 0)
        {
            int w = 0;
            for (int r = 0; r < len; r++)
            {
                byte b = path[r];
                if (b == '%' && r + 2 < len && Query.Hex(path[r + 1]) is int hi and >= 0 && Query.Hex(path[r + 2]) is int lo and >= 0)
                {
                    byte d = (byte)(hi * 16 + lo);
                    if (d == 0) return -1;
                    if (d != '/')
                    {
                        path[w++] = d;
                        r += 2;
                        continue;
                    }
                }
                path[w++] = b;
            }
            len = w;
            if (!System.Text.Unicode.Utf8.IsValid(path[..len])) return -1;
        }
        if (path[..len].IndexOf("/."u8) >= 0) len = RemoveDotSegments(path[..len]);
        return len;
    }

    /// <summary>RFC 3986 5.2.4 on a path that starts with '/'.</summary>
    static int RemoveDotSegments(Span<byte> p)
    {
        int w = 0, r = 0, n = p.Length;
        while (r < n)
        {
            // p[r] is '/': the segment runs to the next '/' or the end.
            int segEnd = p[(r + 1)..].IndexOf((byte)'/');
            segEnd = segEnd < 0 ? n : r + 1 + segEnd;
            ReadOnlySpan<byte> seg = p[(r + 1)..segEnd];
            bool last = segEnd == n;
            if (seg.Length == 1 && seg[0] == '.')
            {
                if (last) p[w++] = (byte)'/';
            }
            else if (seg.Length == 2 && seg[0] == '.' && seg[1] == '.')
            {
                while (w > 0 && p[--w] != '/') { }
                if (last) p[w++] = (byte)'/';
            }
            else
            {
                p[r..segEnd].CopyTo(p[w..]);
                w += segEnd - r;
            }
            r = segEnd;
        }
        if (w == 0) p[w++] = (byte)'/';
        return w;
    }

    static bool IsDigit(byte b) => (uint)(b - '0') <= 9;
}
