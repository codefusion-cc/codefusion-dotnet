using System;
using System.Collections.Generic;
using System.Text;

namespace CodeFusion.Fuzz;

/// <summary>
/// Which of RequestParser's strict rules a refused request breaks, read off the bytes, so that the differential's
/// "only ours refuses" disagreements group by rule. The rules are the ones RequestParser documents (RFC 9112 shapes
/// two parsers may frame differently); "other" is a refusal none of them explains, which is worth a look.
/// </summary>
static class Why
{
    public static string Refused(ReadOnlySpan<byte> input)
    {
        if (input.StartsWith("\r\n"u8) || input.StartsWith("\n"u8)) return "an empty line before the request line";
        int blank = input.IndexOf("\r\n\r\n"u8);
        ReadOnlySpan<byte> head = blank < 0 ? input : input[..(blank + 2)];
        for (int i = 0; i < head.Length; i++)
        {
            if (head[i] == '\n' && (i == 0 || head[i - 1] != '\r')) return "a bare LF";
            if (head[i] == '\r' && i + 1 < head.Length && head[i + 1] != '\n') return "a bare CR";
        }

        var lines = new List<byte[]>();
        foreach (Range r in head.Split("\r\n"u8)) lines.Add(head[r].ToArray());
        string? line = RequestLine(lines[0]);
        if (line is not null) return line;
        bool http11 = lines[0].AsSpan().EndsWith("HTTP/1.1"u8);

        int hosts = 0, lengths = 0, codings = 0;
        bool badLength = false, notChunked = false;
        for (int i = 1; i < lines.Count; i++)
        {
            byte[] h = lines[i];
            if (h.Length == 0) continue;
            if (h[0] is (byte)' ' or (byte)'\t') return "obs-fold";
            int colon = Array.IndexOf(h, (byte)':');
            if (colon < 0) return "a header line without a colon";
            if (colon == 0) return "an empty header name";
            if (h[colon - 1] is (byte)' ' or (byte)'\t') return "whitespace before the colon";
            foreach (byte b in h.AsSpan(0, colon))
                if (!IsToken(b)) return "a header name byte outside tchar";
            string name = Encoding.ASCII.GetString(h, 0, colon).ToUpperInvariant();
            ReadOnlySpan<byte> value = h.AsSpan(colon + 1).Trim(" \t"u8);
            foreach (byte b in value)
                if (b is < 0x20 and not (byte)'\t' or 0x7F) return "a control byte in a header value";
            switch (name)
            {
                case "HOST": hosts++; break;
                case "CONTENT-LENGTH":
                    lengths++;
                    badLength |= value.IsEmpty || value.ContainsAnyExceptInRange((byte)'0', (byte)'9') || value.Length > 18;
                    break;
                case "TRANSFER-ENCODING":
                    codings++;
                    notChunked |= !System.Text.Ascii.EqualsIgnoreCase(value, "chunked"u8);
                    break;
            }
        }
        if (hosts > 1) return "a repeated Host";
        if (hosts == 0 && http11 && blank >= 0) return "no Host in HTTP/1.1";
        if (lengths > 1) return "a repeated Content-Length";
        if (badLength) return "a Content-Length that isn't 1*DIGIT (or overflows)";
        if (codings > 1) return "a repeated Transfer-Encoding";
        if (notChunked) return "a Transfer-Encoding other than chunked";
        if (codings > 0 && lengths > 0) return "Content-Length with Transfer-Encoding";
        if (codings > 0 && !http11) return "chunked in HTTP/1.0";
        if (blank >= 0 && codings > 0) return "chunk framing";
        return "other";
    }

    static string? RequestLine(byte[] line)
    {
        string[] parts = Encoding.Latin1.GetString(line).Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0) return "a request line that isn't method SP target SP version";
        foreach (char c in parts[0])
            if (c > 0x7F || !IsToken((byte)c)) return "a method byte outside tchar";
        if (parts[2] is not ("HTTP/1.1" or "HTTP/1.0")) return parts[2].StartsWith("HTTP/", StringComparison.Ordinal) ? "another HTTP version" : "a malformed version";
        string target = parts[1];
        if (target[0] != '/') return target == "*" ? "the asterisk form" : "a target not in origin form";
        foreach (char c in target)
            if (c is < (char)0x21 or > (char)0x7E) return "a target byte outside visible ASCII";
        byte[] path = Encoding.Latin1.GetBytes(target.Split('?')[0]);
        return CodeFusion.Http.RequestParser.NormalizePath(path) < 0 ? "NUL or invalid UTF-8 in the decoded path" : null;
    }

    static bool IsToken(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
        || "!#$%&'*+-.^_`|~".Contains((char)b, StringComparison.Ordinal);
}
