using System;
using System.Buffers.Binary;

namespace CodeFusion;

/// <summary>
/// A list of <see cref="Utf8"/> texts in one buffer, such as a request body's <c>"tagList": ["a", "b"]</c>: each
/// text is a 4-byte little-endian length and its bytes, one after another. Like <see cref="Utf8"/>, it stays valid as
/// long as its buffer does (for a request, until the response has been sent), and enumerating it allocates nothing.
/// Written into JSON as an array of strings.
/// </summary>
public readonly struct Utf8List : IEquatable<Utf8List>
{
    readonly byte[]? _buffer;
    readonly int _start;
    readonly int _length;

    /// <summary>The texts of <paramref name="length"/> bytes of <paramref name="buffer"/> from <paramref name="start"/>,
    /// which <see cref="Write"/> filled with <paramref name="count"/> of them.</summary>
    public Utf8List(byte[] buffer, int start, int length, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)start > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - start))
            throw new ArgumentOutOfRangeException(nameof(length), "the list must lie inside the buffer");
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        // the records must add up, or enumerating could read past the list
        var e = new Enumerator(buffer, start, length);
        int n = 0;
        while (e.MoveNext()) n++;
        if (n != count || !e.AtEnd) throw new ArgumentException("the bytes don't hold that many texts", nameof(count));
        _buffer = buffer;
        _start = start;
        _length = length;
        Count = count;
    }

    // For CodeFusion.Http's body binder, which wrote the records itself (InternalsVisibleTo).
    internal Utf8List(byte[] buffer, int start, int length, int count, bool trusted)
    {
        _ = trusted;
        _buffer = buffer;
        _start = start;
        _length = length;
        Count = count;
    }

    /// <summary>An empty list.</summary>
    public static Utf8List Empty => default;

    /// <summary>How many texts there are.</summary>
    public int Count { get; }

    /// <summary>Whether there are none.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>The bytes <see cref="Write"/> needs for texts of <paramref name="textBytes"/> bytes in all, <paramref name="count"/> of them.</summary>
    public static int RecordBytes(int textBytes, int count) => checked(textBytes + 4 * count);

    /// <summary>Writes <paramref name="text"/> as the next record at <paramref name="destination"/>; returns the bytes written.</summary>
    public static int Write(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, text.Length);
        text.CopyTo(destination[4..]);
        return 4 + text.Length;
    }

    /// <summary>For <c>foreach</c>.</summary>
    public Enumerator GetEnumerator() => new(_buffer, _start, _length);

    /// <summary>Walks the texts.</summary>
    // Nested, as List<T>.Enumerator is: foreach finds it through GetEnumerator, and nobody names it.
#pragma warning disable CA1034
    public struct Enumerator
#pragma warning restore CA1034
    {
        readonly byte[]? _buffer;
        readonly int _end;
        int _next;

        internal Enumerator(byte[]? buffer, int start, int length)
        {
            _buffer = buffer;
            _next = start;
            _end = start + length;
            Current = default;
        }

        /// <summary>The text at hand.</summary>
        public Utf8 Current { get; private set; }

        internal readonly bool AtEnd => _next == _end;

        /// <summary>Moves to the next text; false after the last.</summary>
        public bool MoveNext()
        {
            if (_buffer is null || _end - _next < 4) return false;
            int n = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_next));
            if (n < 0 || n > _end - _next - 4) return false;
            Current = new Utf8(_buffer, _next + 4, n);
            _next += 4 + n;
            return true;
        }
    }

    /// <inheritdoc/>
    public bool Equals(Utf8List other)
    {
        if (Count != other.Count) return false;
        var a = GetEnumerator();
        var b = other.GetEnumerator();
        while (a.MoveNext() && b.MoveNext())
            if (a.Current != b.Current) return false;
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Utf8List other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (Utf8 t in this) hash.Add(t);
        return hash.ToHashCode();
    }

    /// <summary>The same texts in the same order.</summary>
    public static bool operator ==(Utf8List left, Utf8List right) => left.Equals(right);

    /// <summary>Different texts or order.</summary>
    public static bool operator !=(Utf8List left, Utf8List right) => !left.Equals(right);

    /// <summary>The texts as a JSON-like list of strings. It allocates: for logs and tests.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("[");
        foreach (Utf8 t in this)
        {
            if (sb.Length > 1) sb.Append(", ");
            sb.Append('"').Append(t.ToString()).Append('"');
        }
        return sb.Append(']').ToString();
    }
}
