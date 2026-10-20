using System;
using System.Text;
using CodeFusion.Http;

namespace CodeFusion.Fuzz;

/// <summary>
/// RequestParser on a connection's bytes, whole and in pieces, with the server's default limits and with tiny ones
/// (so the limits' edges are a few bytes away). Invariants:
/// <list type="bullet">
/// <item>no exception: every refusal is ParseResult.Error with 400, 413, 414, 431 or 505;</item>
/// <item>every offset in RequestState stays inside the bytes parsed so far, in order;</item>
/// <item>pieces give what the whole gives: the same request, refusal or wait, ending at the same byte.</item>
/// </list>
/// </summary>
static class HttpParserTarget
{
    internal static readonly Limits Default = new(new HttpServerOptions());
    internal static readonly Limits Small = new(new HttpServerOptions
    {
        MaxRequestLineSize = 64, MaxRequestHeadersTotalSize = 128, MaxRequestHeaderCount = 4, MaxRequestBodySize = 10,
    });

    public static void Run(ReadOnlySpan<byte> input)
    {
        int[] cuts = Cuts(input);
        int[] every = input.Length <= 300 ? Every(input.Length) : [];
        foreach (bool small in (ReadOnlySpan<bool>)[false, true])
        {
            Limits limits = small ? Small : Default;
            string which = small ? "small limits" : "default limits";
            Outcome whole = Connection.Parse(input, limits, []);
            Outcome pieces = Connection.Parse(input, limits, cuts);
            if (whole != pieces)
                throw new FuzzFinding("http-parser: pieces differ from whole", $"{which}, cut at [{string.Join(",", cuts)}]: whole {whole}, pieces {pieces}");
            if (every.Length > 0 && Connection.Parse(input, limits, every) is var bytewise && whole != bytewise)
                throw new FuzzFinding("http-parser: a byte at a time differs from whole", $"{which}: whole {whole}, a byte at a time {bytewise}");
        }
    }

    /// <summary>Up to six cuts, from the input's own hash: the same input is always cut the same way.</summary>
    static int[] Cuts(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2) return [];
        var r = new Random(Hash(input));
        int n = 1 + r.Next(6);
        var cuts = new int[n];
        for (int i = 0; i < n; i++) cuts[i] = 1 + r.Next(input.Length - 1);
        Array.Sort(cuts);
        return cuts;
    }

    static int[] Every(int length)
    {
        var cuts = new int[Math.Max(0, length - 1)];
        for (int i = 0; i < cuts.Length; i++) cuts[i] = i + 1;
        return cuts;
    }

    /// <summary>FNV-1a: stable across processes, unlike string hashes.</summary>
    internal static int Hash(ReadOnlySpan<byte> b)
    {
        uint h = 2166136261;
        foreach (byte x in b) h = (h ^ x) * 16777619;
        return (int)h;
    }
}

/// <summary>What a connection makes of its bytes: the request the handler would see, a refusal, or a wait.</summary>
/// <param name="Status">0 for a request, -1 while incomplete, else the refusal's status.</param>
/// <param name="Consumed">The bytes of the stream the request took, framing included.</param>
sealed record Outcome(int Status, string Method, string Path, string Query, string? Auth, string Body, bool Persistent, bool ExpectContinue, int Consumed)
{
    public static readonly Outcome Incomplete = new(-1, "", "", "", null, "", false, false, 0);

    public static Outcome Error(int status) => new(status, "", "", "", null, "", false, false, 0);

    public byte[] BodyBytes => Encoding.Latin1.GetBytes(Body);

    public override string ToString() => Status switch
    {
        -1 => "incomplete",
        0 => $"{Method} {Findings.Show(Encoding.Latin1.GetBytes(Path))}?{Findings.Show(Encoding.Latin1.GetBytes(Query))} body {Findings.Show(BodyBytes)} consumed {Consumed}"
             + $" auth {(Auth is null ? "none" : Findings.Show(Encoding.Latin1.GetBytes(Auth)))} persistent {Persistent} continue {ExpectContinue}",
        _ => $"refused {Status}",
    };
}

/// <summary>
/// The parser driven as HttpExchange drives it: everything buffered so far after each receive, the head first, the
/// path normalized as soon as the head is done, then the body; between receives, the chunk framing already decoded
/// is dropped from the buffer (PrepareReceive), so later offsets move down.
/// </summary>
static class Connection
{
    static readonly int[] Refusals = [400, 413, 414, 431, 505];

    public static Outcome Parse(ReadOnlySpan<byte> input, in Limits limits, ReadOnlySpan<int> cuts)
    {
        var buf = new byte[input.Length];
        int end = 0, taken = 0, dropped = 0, cut = 0;
        var s = new RequestState();
        s.Reset();
        while (true)
        {
            int next = input.Length;
            while (cut < cuts.Length)
            {
                int c = cuts[cut++];
                if (c > taken && c < input.Length)
                {
                    next = c;
                    break;
                }
            }
            input[taken..next].CopyTo(buf.AsSpan(end));
            end += next - taken;
            taken = next;

            ParseResult r = Step(buf.AsSpan(0, end), ref s, limits);
            Check(s, r, end, limits);
            if (r == ParseResult.Error) return Outcome.Error(s.Status);
            if (r == ParseResult.Done)
                return new Outcome(0, Encoding.Latin1.GetString(buf, 0, s.MethodLength), Encoding.Latin1.GetString(buf, s.PathStart, s.PathLength),
                    Encoding.Latin1.GetString(buf, s.QueryStart, s.QueryLength), s.AuthStart < 0 ? null : Encoding.Latin1.GetString(buf, s.AuthStart, s.AuthLength),
                    Encoding.Latin1.GetString(buf, s.HeadEnd, s.BodyEnd - s.HeadEnd), s.Persistent, s.ExpectContinue, s.Total + dropped);
            if (taken == input.Length) return Outcome.Incomplete;
            if (s.Chunked && s.HeadEnd > 0 && s.Raw > s.BodyEnd)
            {
                int gap = s.Raw - s.BodyEnd;
                buf.AsSpan(s.Raw, end - s.Raw).CopyTo(buf.AsSpan(s.BodyEnd));
                end -= gap;
                dropped += gap;
                s.Raw = s.BodyEnd;
            }
        }
    }

    /// <summary>HttpExchange.Parse.</summary>
    static ParseResult Step(Span<byte> req, ref RequestState s, in Limits limits)
    {
        if (s.HeadEnd == 0)
        {
            ParseResult r = RequestParser.ParseHead(req, ref s, limits);
            if (r != ParseResult.Done) return r;
            int len = RequestParser.NormalizePath(req.Slice(s.PathStart, s.PathLength));
            if (len < 0)
            {
                s.Status = 400;
                return ParseResult.Error;
            }
            s.PathLength = len;
        }
        return RequestParser.ParseBody(req, ref s, limits);
    }

    static void Check(in RequestState s, ParseResult r, int end, in Limits limits)
    {
        if (r == ParseResult.Error)
        {
            if (Array.IndexOf(Refusals, s.Status) < 0) Fail($"refused with status {s.Status}");
            return;
        }
        if (s.Scanned < 0 || s.Scanned > end) Fail($"Scanned {s.Scanned} outside [0, {end}]");
        if (s.LineEnd == 0) return;
        if (s.LineEnd > s.Scanned) Fail($"LineEnd {s.LineEnd} past Scanned {s.Scanned}");
        if (s.MethodLength <= 0 || s.PathStart != s.MethodLength + 1) Fail($"method {s.MethodLength}, path at {s.PathStart}");
        if (s.PathLength < 1 || s.PathStart + s.PathLength > s.LineEnd) Fail($"path [{s.PathStart}, +{s.PathLength}) outside the request line ({s.LineEnd})");
        if (s.QueryStart < s.PathStart || s.QueryLength < 0 || s.QueryStart + s.QueryLength > s.LineEnd - 2)
            Fail($"query [{s.QueryStart}, +{s.QueryLength}) outside the request line ({s.LineEnd})");
        if (s.HeadEnd == 0) return;
        if (s.HeadEnd < s.LineEnd || s.HeadEnd > s.BodyEnd || s.BodyEnd > s.Raw || s.Raw > end)
            Fail($"LineEnd {s.LineEnd}, HeadEnd {s.HeadEnd}, BodyEnd {s.BodyEnd}, Raw {s.Raw} out of order or past {end}");
        if (s.AuthStart >= 0 && (s.AuthStart < s.LineEnd || s.AuthLength < 0 || s.AuthStart + s.AuthLength > s.HeadEnd - 2))
            Fail($"Authorization [{s.AuthStart}, +{s.AuthLength}) outside the headers [{s.LineEnd}, {s.HeadEnd})");
        if (s.BodyEnd - s.HeadEnd > limits.Body) Fail($"a body of {s.BodyEnd - s.HeadEnd} bytes past the limit {limits.Body}");
        if (r == ParseResult.Done && !s.Chunked && s.BodyEnd - s.HeadEnd != Math.Max(0, s.ContentLength))
            Fail($"a body of {s.BodyEnd - s.HeadEnd} bytes for Content-Length {s.ContentLength}");
    }

    static void Fail(string what) => throw new FuzzFinding("http-parser: offset out of range", what);
}
