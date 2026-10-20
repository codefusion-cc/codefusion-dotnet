using System;
using System.ComponentModel;
using System.Text.Unicode;

namespace CodeFusion.Http;

/// <summary>
/// Marks a struct whose fields are read from the query string, as in <c>[FromQuery] partial struct ArticleQuery</c>:
/// a handler that takes one gets it bound with <see cref="Query"/>'s rules (Go's url.Values.Get: the first occurrence
/// wins, '+' is a space, a pair with a bad escape or a ';' is skipped). On a field or property, <see cref="Name"/>
/// gives the parameter's name; it is the member's name in camelCase otherwise.
/// <list type="bullet">
/// <item><see cref="Utf8"/>: the decoded value, empty when absent; <c>Utf8?</c>: null when absent. Bytes that aren't
/// UTF-8 after decoding become U+FFFD. A value with nothing to decode points into the request; one with escapes is
/// decoded into the request's arena.</item>
/// <item><c>long</c>, <c>int</c> and their nullables: ASCII digits only, escapes decoded; absent is the member's
/// <see cref="DefaultValueAttribute"/> (else 0, or null). A value that is present but not digits, too large, or
/// outside the member's <see cref="System.ComponentModel.DataAnnotations.RangeAttribute"/> answers the request with the
/// struct's <c>static ErrorReply Invalid</c>, which a struct with numbers must declare.</item>
/// </list>
/// The struct must be <c>partial</c>: the generator adds its binder to it.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class FromQueryAttribute : Attribute
{
    /// <summary>The query parameter's name, on a field or property.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// On a field of a request body struct: the reply when this field is invalid, named by a <c>static ErrorReply</c>
/// member of the struct (<c>[BodyError(nameof(BadTagList))]</c>), instead of the struct's <c>Invalid</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class BodyErrorAttribute(string member) : Attribute
{
    /// <summary>The name of the struct's static <see cref="ErrorReply"/> field or property.</summary>
    public string Member { get; } = member;
}

/// <summary>A request's query string, as the generated <c>[FromQuery]</c> binders read it.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct QuerySource
{
    readonly byte[]? _buffer;
    readonly int _start;
    readonly int _length;
    readonly IRequestArena? _arena;

    /// <summary>The query's <paramref name="length"/> bytes (what follows '?') at <paramref name="start"/> in
    /// <paramref name="buffer"/>; decoded values go into <paramref name="arena"/>.</summary>
    public QuerySource(byte[] buffer, int start, int length, IRequestArena arena)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(arena);
        if ((uint)start > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - start))
            throw new ArgumentOutOfRangeException(nameof(length));
        _buffer = buffer;
        _start = start;
        _length = length;
        _arena = arena;
    }

    ReadOnlySpan<byte> Span => _buffer is null ? default : _buffer.AsSpan(_start, _length);

    /// <summary>The first <paramref name="key"/> parameter's decoded value; false when there is none.</summary>
    public bool TryText(ReadOnlySpan<byte> key, out Utf8 value)
    {
        value = default;
        ReadOnlySpan<byte> query = Span;
        if (!Query.TryFind(query, key, out ReadOnlySpan<byte> raw)) return false;
        if (raw.IsEmpty) return true;
        if (raw.IndexOfAny((byte)'%', (byte)'+') < 0 && System.Text.Unicode.Utf8.IsValid(raw))
        {
            query.Overlaps(raw, out int offset);
            value = new Utf8(_buffer!, _start + offset, raw.Length);
            return true;
        }
        // decoding never grows the text, and replacing bad bytes needs three times the decoded length
        ArenaBytes dest = _arena!.Allocate(Query.MaxDecodedLength(raw.Length));
        Span<byte> d = dest.Span;
        int n = Query.Decode(raw, d);
        if (!System.Text.Unicode.Utf8.IsValid(d[..n])) n = Query.ReplaceInvalid(d, n);
        value = dest.Text(0, n);
        return true;
    }

    /// <summary>The first <paramref name="key"/> parameter as a number of ASCII digits within
    /// [<paramref name="min"/>, <paramref name="max"/>]: <see cref="QueryNumber.Absent"/> (and
    /// <paramref name="value"/> 0) when there is none, <see cref="QueryNumber.Invalid"/> when it is present (even
    /// empty) and isn't one.</summary>
    public QueryNumber TryNumber(ReadOnlySpan<byte> key, long min, long max, out long value)
    {
        value = 0;
        if (!Query.TryFind(Span, key, out ReadOnlySpan<byte> raw)) return QueryNumber.Absent;
        if (!Query.TryParseEscapedUint(raw, out value) || value < min || value > max)
        {
            value = 0;
            return QueryNumber.Invalid;
        }
        return QueryNumber.Valid;
    }
}

/// <summary>What <see cref="QuerySource.TryNumber"/> found.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum QueryNumber : byte
{
    /// <summary>No such parameter.</summary>
    Absent,
    /// <summary>A number in range.</summary>
    Valid,
    /// <summary>Present, and not a number in range.</summary>
    Invalid,
}
