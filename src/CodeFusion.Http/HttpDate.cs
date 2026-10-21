using System;
using System.Buffers.Text;
using System.Threading;

namespace CodeFusion.Http;

/// <summary>The "Date: ...\r\n" header line, formatted once a second by the heartbeat instead of per response.</summary>
internal static class HttpDate
{
    public const int Length = 37; // "Date: " + 29 + CRLF

    // Two buffers: the heartbeat formats into the one no response is copying from, then publishes it.
    static readonly byte[] A = new byte[Length], B = new byte[Length];
    static byte[] _current = Format(A, DateTime.UtcNow);

    public static ReadOnlySpan<byte> Line => Volatile.Read(ref _current);

    public static void Update(DateTime utcNow)
    {
        byte[] next = Volatile.Read(ref _current) == A ? B : A;
        Volatile.Write(ref _current, Format(next, utcNow));
    }

    static byte[] Format(byte[] dst, DateTime utcNow)
    {
        "Date: "u8.CopyTo(dst);
        Utf8Formatter.TryFormat(utcNow, dst.AsSpan(6), out _, new System.Buffers.StandardFormat('R'));
        dst[Length - 2] = (byte)'\r';
        dst[Length - 1] = (byte)'\n';
        return dst;
    }
}
