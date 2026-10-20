using System;
using System.Text;

namespace CodeFusion;

/// <summary>
/// UTF-8 text that stays in a buffer someone else owns: a request's bytes, or a row of a database reply. It is valid
/// as long as that buffer is: for a request, until its response has been sent; for a row, as its owner says. Nothing
/// is copied and no string is made, so passing one along costs a few words.
/// </summary>
/// <remarks>Equality compares the bytes. <see cref="ToString"/> makes a string, for logs and tests only.</remarks>
public readonly struct Utf8 : IEquatable<Utf8>
{
    readonly byte[]? _buffer;
    readonly int _start;
    readonly int _length;

    /// <summary>The <paramref name="length"/> bytes of <paramref name="buffer"/> from <paramref name="start"/>.</summary>
    public Utf8(byte[] buffer, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)start > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - start))
            throw new ArgumentOutOfRangeException(nameof(length), "the text must lie inside the buffer");
        _buffer = buffer;
        _start = start;
        _length = length;
    }

    /// <summary>No text: <see cref="IsEmpty"/>, and equal to <c>""u8</c>.</summary>
    public static Utf8 Empty => default;

    /// <summary>The text's bytes.</summary>
    public ReadOnlySpan<byte> Span => _buffer is null ? default : new ReadOnlySpan<byte>(_buffer, _start, _length);

    /// <summary>The length in bytes.</summary>
    public int Length => _length;

    /// <summary>Whether there are no bytes.</summary>
    public bool IsEmpty => _length == 0;

    /// <summary>The text's bytes, where a span is wanted.</summary>
    public static implicit operator ReadOnlySpan<byte>(Utf8 text) => text.Span;

    /// <summary>The text's bytes; for languages without implicit conversions.</summary>
    public ReadOnlySpan<byte> ToReadOnlySpan() => Span;

    /// <inheritdoc/>
    public bool Equals(Utf8 other) => Span.SequenceEqual(other.Span);

    /// <summary>Whether the bytes are <paramref name="other"/>'s, as in <c>slug.Equals("feed"u8)</c>.</summary>
    public bool Equals(ReadOnlySpan<byte> other) => Span.SequenceEqual(other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Utf8 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Span);
        return hash.ToHashCode();
    }

    /// <summary>Equal bytes.</summary>
    public static bool operator ==(Utf8 left, Utf8 right) => left.Equals(right);

    /// <summary>Different bytes.</summary>
    public static bool operator !=(Utf8 left, Utf8 right) => !left.Equals(right);

    /// <summary>Equal bytes, as in <c>slug == "feed"u8</c>.</summary>
    public static bool operator ==(Utf8 left, ReadOnlySpan<byte> right) => left.Equals(right);

    /// <summary>Different bytes.</summary>
    public static bool operator !=(Utf8 left, ReadOnlySpan<byte> right) => !left.Equals(right);

    /// <summary>The text as a new string (invalid UTF-8 becomes U+FFFD). It allocates: for logs and tests.</summary>
    public override string ToString() => Encoding.UTF8.GetString(Span);
}
