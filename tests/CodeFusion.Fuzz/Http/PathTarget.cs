using System;
using System.Collections.Generic;
using System.Text;
using CodeFusion.Http;

namespace CodeFusion.Fuzz;

/// <summary>
/// RequestParser.NormalizePath on what the parser hands it (a path of visible ASCII starting with '/'; other bytes
/// are folded into that range), against a plain reading of its rules: one left-to-right pass decoding %XX except
/// %2F, NUL or invalid UTF-8 refused, then RFC 3986 5.2.4's remove_dot_segments as the RFC writes it. Also: no "."
/// or ".." segment survives, and a result without '%' normalizes to itself.
/// </summary>
static class PathTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        byte[] path = Path(input);
        byte[] ours = (byte[])path.Clone();
        int len = RequestParser.NormalizePath(ours);
        byte[]? reference = Reference(path);
        if ((reference is null) != (len < 0) || (reference is not null && !ours.AsSpan(0, len).SequenceEqual(reference)))
            throw new FuzzFinding("http-path: differs from RFC 3986", $"{Findings.Show(path)}: {(len < 0 ? "refused" : Findings.Show(ours.AsSpan(0, len)))}, the RFC gives {(reference is null ? "refused" : Findings.Show(reference))}");
        if (len < 0) return;
        ReadOnlySpan<byte> result = ours.AsSpan(0, len);
        if (result.IsEmpty || result[0] != '/' || result.Contains((byte)0) || !System.Text.Unicode.Utf8.IsValid(result))
            throw new FuzzFinding("http-path: not a path", Findings.Show(result));
        foreach (Range r in result[1..].Split((byte)'/'))
        {
            ReadOnlySpan<byte> seg = result[1..][r];
            if (seg.SequenceEqual("."u8) || seg.SequenceEqual(".."u8)) throw new FuzzFinding("http-path: a dot segment survives", Findings.Show(result));
        }
        if (result.IndexOf((byte)'%') < 0)
        {
            byte[] again = result.ToArray();
            int len2 = RequestParser.NormalizePath(again);
            if (len2 != len || !again.AsSpan(0, len2).SequenceEqual(result))
                throw new FuzzFinding("http-path: normalizing twice changes it", $"{Findings.Show(result)} -> {(len2 < 0 ? "refused" : Findings.Show(again.AsSpan(0, len2)))}");
        }
    }

    /// <summary>The bytes as a request target's path: visible ASCII, no '?', a leading '/'.</summary>
    static byte[] Path(ReadOnlySpan<byte> input)
    {
        var p = new byte[input.Length + 1];
        p[0] = (byte)'/';
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b is < 0x21 or > 0x7E) b = (byte)(0x21 + b % 94);
            p[i + 1] = b == '?' ? (byte)'/' : b;
        }
        return p;
    }

    static byte[]? Reference(byte[] path)
    {
        var decoded = new List<byte>();
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && i + 2 < path.Length && IsHex(path[i + 1]) && IsHex(path[i + 2]))
            {
                byte d = Convert.FromHexString(Encoding.ASCII.GetString(path, i + 1, 2))[0];
                if (d == 0) return null;
                if (d != '/')
                {
                    decoded.Add(d);
                    i += 2;
                    continue;
                }
            }
            decoded.Add(path[i]);
        }
        if (!System.Text.Unicode.Utf8.IsValid(decoded.ToArray())) return null;
        return Encoding.Latin1.GetBytes(RemoveDotSegments(Encoding.Latin1.GetString([.. decoded])));
    }

    static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    /// <summary>RFC 3986 section 5.2.4, step by step.</summary>
    static string RemoveDotSegments(string input)
    {
        var output = new StringBuilder();
        while (input.Length > 0)
        {
            if (input.StartsWith("../", StringComparison.Ordinal)) input = input[3..];
            else if (input.StartsWith("./", StringComparison.Ordinal)) input = input[2..];
            else if (input.StartsWith("/./", StringComparison.Ordinal)) input = input[2..];
            else if (input == "/.") input = "/";
            else if (input.StartsWith("/../", StringComparison.Ordinal) || input == "/..")
            {
                input = "/" + input[(input == "/.." ? 3 : 4)..];
                string o = output.ToString();
                int slash = o.LastIndexOf('/');
                output.Length = slash < 0 ? 0 : slash;
            }
            else if (input is "." or "..") input = "";
            else
            {
                int next = input.IndexOf('/', 1);
                if (next < 0) next = input.Length;
                output.Append(input.AsSpan(0, next));
                input = input[next..];
            }
        }
        return output.Length == 0 ? "/" : output.ToString();
    }
}
