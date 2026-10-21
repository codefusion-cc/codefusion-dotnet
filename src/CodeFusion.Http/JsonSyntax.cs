using System;
using System.Buffers;

namespace CodeFusion.Http;

/// <summary>
/// Whether bytes are one JSON value (RFC 8259) as <c>Utf8JsonReader</c> reads a final block with no comments, no
/// trailing commas and a depth limit, checked without throwing, for what a binder must classify by itself. It is never
/// stricter than the reader, so what it refuses the reader would throw on; it doesn't check strings for UTF-8, which
/// the reader doesn't either.
/// </summary>
internal static class JsonSyntax
{
    // what ends a run of plain string bytes: the quote, an escape, a control character
    static readonly SearchValues<byte> StringStops = SearchValues.Create(
        [(byte)'"', (byte)'\\', 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31]);

    /// <summary>One value and only whitespace around it, nested at most <paramref name="maxDepth"/> (at most 64) deep.</summary>
    public static bool IsValid(ReadOnlySpan<byte> s, int maxDepth)
    {
        if ((uint)maxDepth > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        ulong objects = 0; // bit d is set when the container at depth d + 1 is an object
        int depth = 0;
        int i = Whitespace(s, 0);
        while (true)
        {
            // a value starts at i
            if (i >= s.Length) return false;
            byte c = s[i];
            if (c is (byte)'{' or (byte)'[')
            {
                if (depth == maxDepth) return false;
                bool isObject = c == '{';
                objects = isObject ? objects | 1UL << depth : objects & ~(1UL << depth);
                depth++;
                i = Whitespace(s, i + 1);
                if (i < s.Length && s[i] == (isObject ? '}' : ']'))
                {
                    depth--;
                    i++;
                }
                else
                {
                    if (isObject && !Name(s, ref i)) return false;
                    continue;
                }
            }
            else if (c == '"')
            {
                if (!String(s, ref i)) return false;
            }
            else if (c == 't')
            {
                if (!Word(s, ref i, "true"u8)) return false;
            }
            else if (c == 'f')
            {
                if (!Word(s, ref i, "false"u8)) return false;
            }
            else if (c == 'n')
            {
                if (!Word(s, ref i, "null"u8)) return false;
            }
            else if (!Number(s, ref i)) return false;

            // after a value: close containers, or take a comma and go on to the next value
            while (true)
            {
                i = Whitespace(s, i);
                if (depth == 0) return i == s.Length;
                if (i >= s.Length) return false;
                bool inObject = (objects >> (depth - 1) & 1) != 0;
                c = s[i];
                if (c == (inObject ? '}' : ']'))
                {
                    depth--;
                    i++;
                    continue;
                }
                if (c != ',') return false;
                i = Whitespace(s, i + 1);
                if (inObject && !Name(s, ref i)) return false;
                break;
            }
        }
    }

    static int Whitespace(ReadOnlySpan<byte> s, int i)
    {
        while (i < s.Length && s[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') i++;
        return i;
    }

    /// <summary>A property name and its colon; <paramref name="i"/> is then where its value starts.</summary>
    static bool Name(ReadOnlySpan<byte> s, ref int i)
    {
        if (i >= s.Length || s[i] != '"' || !String(s, ref i)) return false;
        i = Whitespace(s, i);
        if (i >= s.Length || s[i] != ':') return false;
        i = Whitespace(s, i + 1);
        return true;
    }

    /// <summary>A string from its opening quote at <paramref name="i"/> to just past its closing one.</summary>
    static bool String(ReadOnlySpan<byte> s, ref int i)
    {
        i++;
        while (true)
        {
            int stop = s[i..].IndexOfAny(StringStops);
            if (stop < 0) return false;
            i += stop;
            byte c = s[i];
            if (c == '"')
            {
                i++;
                return true;
            }
            if (c != '\\' || i + 1 >= s.Length) return false; // a control character, or an escape cut short
            byte e = s[i + 1];
            if (e is (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t')
            {
                i += 2;
                continue;
            }
            if (e != 'u' || i + 6 > s.Length) return false;
            for (int h = i + 2; h < i + 6; h++)
                if (!char.IsAsciiHexDigit((char)s[h])) return false;
            i += 6;
        }
    }

    static bool Word(ReadOnlySpan<byte> s, ref int i, ReadOnlySpan<byte> word)
    {
        if (!s[i..].StartsWith(word)) return false;
        i += word.Length;
        return true;
    }

    /// <summary><c>-? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?</c>; what follows is the caller's to check.</summary>
    static bool Number(ReadOnlySpan<byte> s, ref int i)
    {
        if (s[i] == '-') i++;
        if (i >= s.Length || !char.IsAsciiDigit((char)s[i])) return false;
        if (s[i++] != '0')
            while (i < s.Length && char.IsAsciiDigit((char)s[i])) i++;
        if (i < s.Length && s[i] == '.')
        {
            if (++i >= s.Length || !char.IsAsciiDigit((char)s[i])) return false;
            while (i < s.Length && char.IsAsciiDigit((char)s[i])) i++;
        }
        if (i < s.Length && s[i] is (byte)'e' or (byte)'E')
        {
            if (++i < s.Length && s[i] is (byte)'+' or (byte)'-') i++;
            if (i >= s.Length || !char.IsAsciiDigit((char)s[i])) return false;
            while (i < s.Length && char.IsAsciiDigit((char)s[i])) i++;
        }
        return true;
    }
}
