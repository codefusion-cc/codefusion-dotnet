using System;
using System.Buffers.Text;
using System.IO;
using System.Threading;

namespace CodeFusion.TechEmpower;

/// <summary>
/// With CODEFUSION_GC_REPORT set, a line on stderr every 10 s: <c>gc &lt;unix ms&gt; &lt;bytes allocated&gt;
/// &lt;gen0&gt; &lt;gen1&gt; &lt;gen2&gt;</c>, the process's totals so far. Written from one buffer by a thread of its
/// own, so the report allocates nothing itself and needs no diagnostics session in the process (dotnet-counters'
/// own allocates): the allocation gate reads the difference between two lines.
/// </summary>
sealed class GcReport : IDisposable
{
    readonly Stream _err = Console.OpenStandardError();
    readonly byte[] _line = new byte[160];
    readonly Thread _thread;
    volatile bool _stopped;

    public static GcReport? StartIfAsked() =>
        Environment.GetEnvironmentVariable("CODEFUSION_GC_REPORT") is { Length: > 0 } ? new GcReport() : null;

    GcReport()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "gc report" };
        _thread.Start();
    }

    void Run()
    {
        while (!_stopped)
        {
            Write();
            Thread.Sleep(10_000);
        }
    }

    void Write()
    {
        var b = _line.AsSpan();
        int n = Put(b, 0, "gc "u8);
        n = Number(b, n, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        n = Number(b, n, GC.GetTotalAllocatedBytes(precise: true));
        n = Number(b, n, GC.CollectionCount(0));
        n = Number(b, n, GC.CollectionCount(1));
        n = Number(b, n, GC.CollectionCount(2));
        b[n - 1] = (byte)'\n';
        _err.Write(_line, 0, n);
        _err.Flush();
    }

    static int Put(Span<byte> b, int at, ReadOnlySpan<byte> s)
    {
        s.CopyTo(b[at..]);
        return at + s.Length;
    }

    static int Number(Span<byte> b, int at, long value)
    {
        Utf8Formatter.TryFormat(value, b[at..], out int w);
        b[at + w] = (byte)' ';
        return at + w + 1;
    }

    public void Dispose() => _stopped = true;
}
