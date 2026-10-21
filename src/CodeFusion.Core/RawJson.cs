using System;

namespace CodeFusion;

/// <summary>
/// JSON text that is already JSON, such as a list a database built with <c>array_to_json</c>: written into a
/// response as it is, without being parsed or escaped. Like <see cref="Utf8"/>, it stays in its owner's buffer.
/// </summary>
public readonly struct RawJson : IEquatable<RawJson>
{
    /// <summary>The JSON's bytes in their buffer.</summary>
    public Utf8 Text { get; }

    /// <summary>JSON in <paramref name="text"/>'s bytes. The caller vouches that it is valid JSON.</summary>
    public RawJson(Utf8 text) => Text = text;

    /// <summary>The JSON's bytes.</summary>
    public ReadOnlySpan<byte> Span => Text.Span;

    /// <inheritdoc/>
    public bool Equals(RawJson other) => Text.Equals(other.Text);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RawJson other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Text.GetHashCode();

    /// <summary>Equal bytes.</summary>
    public static bool operator ==(RawJson left, RawJson right) => left.Equals(right);

    /// <summary>Different bytes.</summary>
    public static bool operator !=(RawJson left, RawJson right) => !left.Equals(right);

    /// <summary>The JSON as a new string. It allocates: for logs and tests.</summary>
    public override string ToString() => Text.ToString();
}
