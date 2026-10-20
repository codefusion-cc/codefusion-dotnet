using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodeFusion.Fuzz;

/// <summary>
/// libFuzzer's kinds of mutation, from a seed: the same seed gives the same inputs on every machine and runtime
/// (seeded <see cref="Random"/> keeps its algorithm). Stacks one to five mutations on an input from the corpus.
/// </summary>
sealed class Mutator(int seed, IReadOnlyList<byte[]> dictionary)
{
    readonly Random _r = new(seed);

    static readonly long[] Interesting =
        [0, 1, 2, 4, 9, 10, 15, 16, 127, 128, 255, 256, 4096, 32767, 32768, 65535, 65536, int.MaxValue, 1L << 31, 1L << 32, long.MaxValue];

    static readonly byte[] InterestingBytes = [0, 0xFF, 0x7F, 0x80, (byte)'\r', (byte)'\n', (byte)' ', (byte)'\t', (byte)':', (byte)';', (byte)'%', (byte)'"', (byte)'\\'];

    public byte[] Next(IReadOnlyList<byte[]> corpus, int maxLength)
    {
        var b = new List<byte>(corpus[_r.Next(corpus.Count)]);
        int n = 1 + _r.Next(_r.Next(2) == 0 ? 2 : 5);
        for (int i = 0; i < n; i++) Mutate(b, corpus);
        if (b.Count > maxLength) b.RemoveRange(maxLength, b.Count - maxLength);
        return [.. b];
    }

    void Mutate(List<byte> b, IReadOnlyList<byte[]> corpus)
    {
        if (b.Count == 0)
        {
            InsertWord(b);
            return;
        }
        switch (_r.Next(13))
        {
            case 0: Erase(b); break;
            case 1: b.Insert(_r.Next(b.Count + 1), (byte)_r.Next(256)); break;
            case 2:
            {
                byte v = _r.Next(2) == 0 ? b[_r.Next(b.Count)] : InterestingBytes[_r.Next(InterestingBytes.Length)];
                b.InsertRange(_r.Next(b.Count + 1), Repeat(v, 1 + _r.Next(_r.Next(4) == 0 ? 300 : 16)));
                break;
            }
            case 3: b[_r.Next(b.Count)] = (byte)_r.Next(256); break;
            case 4: b[_r.Next(b.Count)] ^= (byte)(1 << _r.Next(8)); break;
            case 5: Shuffle(b); break;
            case 6: AsciiInteger(b); break;
            case 7: BinaryInteger(b); break;
            case 8: CopyPart(b); break;
            case 9: CrossOver(b, corpus[_r.Next(corpus.Count)]); break;
            case 10: InsertWord(b); break;
            case 11: OverwriteWord(b); break;
            default: b[_r.Next(b.Count)] = InterestingBytes[_r.Next(InterestingBytes.Length)]; break;
        }
    }

    static byte[] Repeat(byte v, int n)
    {
        var a = new byte[n];
        a.AsSpan().Fill(v);
        return a;
    }

    void Erase(List<byte> b)
    {
        int at = _r.Next(b.Count);
        int n = 1 + _r.Next(Math.Min(b.Count - at, _r.Next(4) == 0 ? 64 : 4));
        b.RemoveRange(at, n);
    }

    void Shuffle(List<byte> b)
    {
        int at = _r.Next(b.Count);
        int n = Math.Min(b.Count - at, 1 + _r.Next(8));
        for (int i = n - 1; i > 0; i--)
        {
            int j = _r.Next(i + 1);
            (b[at + i], b[at + j]) = (b[at + j], b[at + i]);
        }
    }

    /// <summary>A run of decimal digits becomes another number: one off, doubled, halved, a limit or past one.</summary>
    void AsciiInteger(List<byte> b)
    {
        var runs = new List<(int Start, int Length)>();
        for (int i = 0; i < b.Count;)
        {
            if (b[i] is < (byte)'0' or > (byte)'9') { i++; continue; }
            int s = i;
            while (i < b.Count && b[i] is >= (byte)'0' and <= (byte)'9') i++;
            runs.Add((s, i - s));
        }
        if (runs.Count == 0)
        {
            b.InsertRange(_r.Next(b.Count + 1), Encoding.ASCII.GetBytes(Interesting[_r.Next(Interesting.Length)].ToString(CultureInfo.InvariantCulture)));
            return;
        }
        var (start, length) = runs[_r.Next(runs.Count)];
        string text = Encoding.ASCII.GetString([.. b.GetRange(start, length)]);
        long v = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed : long.MaxValue;
        string next = _r.Next(9) switch
        {
            0 => (v + 1).ToString(CultureInfo.InvariantCulture),
            1 => (v - 1).ToString(CultureInfo.InvariantCulture),
            2 => (v * 2).ToString(CultureInfo.InvariantCulture),
            3 => (v / 2).ToString(CultureInfo.InvariantCulture),
            4 => "99999999999999999999",
            5 => "0" + text,
            6 => _r.Next(1000).ToString(CultureInfo.InvariantCulture),
            _ => Interesting[_r.Next(Interesting.Length)].ToString(CultureInfo.InvariantCulture),
        };
        b.RemoveRange(start, length);
        b.InsertRange(start, Encoding.ASCII.GetBytes(next));
    }

    /// <summary>A big-endian integer of 1, 2, 4 or 8 bytes is overwritten: a length field one off, a limit, the
    /// input's own size. Postgres's framing is all big-endian lengths.</summary>
    void BinaryInteger(List<byte> b)
    {
        int width = 1 << _r.Next(4);
        if (b.Count < width) width = 1;
        int at = _r.Next(b.Count - width + 1);
        Span<byte> cur = stackalloc byte[8];
        for (int i = 0; i < width; i++) cur[8 - width + i] = b[at + i];
        long old = BinaryPrimitives.ReadInt64BigEndian(cur);
        long v = _r.Next(5) switch
        {
            0 => old + 1 + _r.Next(8),
            1 => old - 1 - _r.Next(8),
            2 => b.Count - at + _r.Next(3) - 1,
            3 => -1,
            _ => Interesting[_r.Next(Interesting.Length)],
        };
        BinaryPrimitives.WriteInt64BigEndian(cur, v);
        for (int i = 0; i < width; i++) b[at + i] = cur[8 - width + i];
    }

    void CopyPart(List<byte> b)
    {
        int from = _r.Next(b.Count);
        int n = 1 + _r.Next(Math.Min(b.Count - from, 128));
        byte[] part = [.. b.GetRange(from, n)];
        int to = _r.Next(b.Count + 1);
        if (_r.Next(2) == 0) b.InsertRange(to, part);
        else
            for (int i = 0; i < part.Length && to + i < b.Count; i++) b[to + i] = part[i];
    }

    void CrossOver(List<byte> b, byte[] other)
    {
        if (other.Length == 0) return;
        int cut = _r.Next(b.Count + 1), from = _r.Next(other.Length);
        int n = 1 + _r.Next(other.Length - from);
        if (_r.Next(2) == 0) b.RemoveRange(cut, b.Count - cut);
        b.InsertRange(Math.Min(cut, b.Count), other.AsSpan(from, n).ToArray());
    }

    void InsertWord(List<byte> b)
    {
        if (dictionary.Count == 0)
        {
            b.Insert(_r.Next(b.Count + 1), (byte)_r.Next(256));
            return;
        }
        b.InsertRange(_r.Next(b.Count + 1), dictionary[_r.Next(dictionary.Count)]);
    }

    void OverwriteWord(List<byte> b)
    {
        if (dictionary.Count == 0) return;
        byte[] w = dictionary[_r.Next(dictionary.Count)];
        int at = _r.Next(b.Count);
        for (int i = 0; i < w.Length; i++)
        {
            if (at + i < b.Count) b[at + i] = w[i];
            else b.Add(w[i]);
        }
    }

    /// <summary>A libFuzzer dictionary: one quoted word per line (name="word" or "word"), \\, \" and \xNN escapes,
    /// # comments.</summary>
    public static List<byte[]> LoadDictionary(string path)
    {
        var words = new List<byte[]>();
        if (!File.Exists(path)) return words;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int open = line.IndexOf('"', StringComparison.Ordinal), close = line.LastIndexOf('"');
            if (open < 0 || close <= open) throw new InvalidDataException($"{path}: not a dictionary line: {raw}");
            words.Add(Unescape(line[(open + 1)..close]));
        }
        return words;
    }

    static byte[] Unescape(string s)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { bytes.Add((byte)s[i]); continue; }
            char c = s[++i];
            if (c == 'x')
            {
                bytes.Add(byte.Parse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else bytes.Add((byte)c);
        }
        return [.. bytes];
    }
}
