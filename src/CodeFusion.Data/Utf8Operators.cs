using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeFusion.Data;

/// <summary>
/// Text functions of <see cref="Utf8"/>. In a query definition each is the SQL function its summary names, run by the
/// database; anywhere else it runs as C# with the same result as that SQL (a new <see cref="Utf8"/>, which allocates),
/// for text SQL can hold (no NUL: a definition refuses one) and of a deterministic collation (on a nondeterministic one
/// Postgres refuses <c>regexp_replace</c>, and <c>replace</c> before Postgres 18).
/// The spec's slug rule, for one:
/// <code>title.ReplaceMatches("[^A-Za-z0-9]+", "-").ToLowerAscii().Trim('-').Concat("-"u8).Concat(id)</code>
/// </summary>
public static class Utf8Operators
{
    /// <summary>The text with A to Z lowered and every other character as it is: <c>translate(x, 'A…Z', 'a…z')</c>,
    /// which keeps the text's collation for an order by it. A collation's own <c>lower</c> also lowers the letters its
    /// locale knows (Ä, and Σ as ς at a word's end), which no C# method does alike.</summary>
    public static Utf8 ToLowerAscii(this Utf8 text) => Ascii(text, (byte)'A', (byte)'Z');

    /// <summary>The text with a to z raised and every other character as it is: <c>translate(x, 'a…z', 'A…Z')</c>.</summary>
    public static Utf8 ToUpperAscii(this Utf8 text) => Ascii(text, (byte)'a', (byte)'z');

    /// <summary>The text without <paramref name="trimChar"/> at either end, however often it is there:
    /// <c>trim(both '-' from x)</c>. In a definition the character is a constant.</summary>
    public static Utf8 Trim(this Utf8 text, char trimChar) => Trimmed(text, trimChar, start: true, end: true);

    /// <summary>The text without <paramref name="trimChar"/> at its start: <c>trim(leading '-' from x)</c>.</summary>
    public static Utf8 TrimStart(this Utf8 text, char trimChar) => Trimmed(text, trimChar, start: true, end: false);

    /// <summary>The text without <paramref name="trimChar"/> at its end: <c>trim(trailing '-' from x)</c>.</summary>
    public static Utf8 TrimEnd(this Utf8 text, char trimChar) => Trimmed(text, trimChar, start: false, end: true);

    /// <summary>
    /// Each <paramref name="oldValue"/> in the text, from the left and without overlaps, replaced by
    /// <paramref name="newValue"/>: <c>replace(x, old, new)</c>. An empty <paramref name="oldValue"/> replaces nothing,
    /// as in SQL (string.Replace would throw).
    /// </summary>
    public static Utf8 Replace(this Utf8 text, ReadOnlySpan<byte> oldValue, ReadOnlySpan<byte> newValue)
    {
        var s = text.Span;
        if (oldValue.IsEmpty) return Copy(s);
        int count = 0;
        for (var rest = s; rest.IndexOf(oldValue) is int at and >= 0; count++) rest = rest[(at + oldValue.Length)..];
        if (count == 0) return Copy(s);
        var b = new byte[s.Length + count * (newValue.Length - oldValue.Length)];
        int written = 0;
        while (true)
        {
            int at = s.IndexOf(oldValue);
            if (at < 0) break;
            s[..at].CopyTo(b.AsSpan(written));
            written += at;
            newValue.CopyTo(b.AsSpan(written));
            written += newValue.Length;
            s = s[(at + oldValue.Length)..];
        }
        s.CopyTo(b.AsSpan(written));
        return new Utf8(b, 0, b.Length);
    }

    /// <summary>
    /// Each match of <paramref name="pattern"/> replaced by <paramref name="replacement"/>, as
    /// <see cref="Regex.Replace(string, string, string)"/> does: <c>regexp_replace(x, pattern, replacement, 'g')</c>.
    /// In a definition both are constants, and the pattern is what .NET and SQL read alike: literals, escaped
    /// punctuation and <c>[...]</c> classes of ASCII characters and ranges, each once or with <c>+</c>, and at most one
    /// <c>[^...]</c> class, which takes <c>+</c>. The replacement has no <c>$</c>, <c>\</c> or NUL. As C# it runs with
    /// <see cref="RegexOptions.NonBacktracking"/>, in time linear in the text (as SQL's engine does), so a pattern
    /// needs no construct that option lacks (back references, lookarounds).
    /// </summary>
    public static Utf8 ReplaceMatches(this Utf8 text, [StringSyntax(StringSyntaxAttribute.Regex)] string pattern, string replacement) =>
        Encode(Regex.Replace(text.ToString(), pattern, replacement, RegexOptions.NonBacktracking));

    /// <summary>The text followed by <paramref name="other"/>: <c>x || y</c>.</summary>
    public static Utf8 Concat(this Utf8 text, ReadOnlySpan<byte> other)
    {
        var b = new byte[text.Length + other.Length];
        text.Span.CopyTo(b);
        other.CopyTo(b.AsSpan(text.Length));
        return new Utf8(b, 0, b.Length);
    }

    /// <summary>The text followed by <paramref name="number"/>'s digits, a minus sign before a negative one, as SQL
    /// writes a number as text: <c>x || n</c>.</summary>
    public static Utf8 Concat(this Utf8 text, long number)
    {
        Span<byte> digits = stackalloc byte[20];
        Utf8Formatter.TryFormat(number, digits, out int n);
        return text.Concat(digits[..n]);
    }

    static Utf8 Ascii(Utf8 text, byte from, byte to)
    {
        var b = text.Span.ToArray();
        for (int i = 0; i < b.Length; i++)
            if (b[i] >= from && b[i] <= to) b[i] ^= 0x20;
        return new Utf8(b, 0, b.Length);
    }

    static Utf8 Trimmed(Utf8 text, char trimChar, bool start, bool end)
    {
        if (char.IsSurrogate(trimChar)) throw new ArgumentException("a surrogate is half a character: trim by a whole one", nameof(trimChar));
        Span<byte> c = stackalloc byte[4];
        c = c[..new Rune(trimChar).EncodeToUtf8(c)];
        var s = text.Span;
        // UTF-8 is self-synchronizing: a whole character's bytes at an end are that character
        if (start) while (s.StartsWith(c)) s = s[c.Length..];
        if (end) while (s.EndsWith(c)) s = s[..^c.Length];
        return Copy(s);
    }

    static Utf8 Copy(ReadOnlySpan<byte> s)
    {
        var b = s.ToArray();
        return new Utf8(b, 0, b.Length);
    }

    static Utf8 Encode(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }
}
