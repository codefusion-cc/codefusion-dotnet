using System;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace CodeFusion.Http;

/// <summary>A request's body, as the generated body binders read it.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct BodySource
{
    /// <summary>The <paramref name="length"/> bytes at <paramref name="start"/> in <paramref name="buffer"/>; escaped
    /// strings are unescaped into <paramref name="arena"/>. <paramref name="invalidBody"/> answers a body that isn't
    /// the JSON its struct describes, unless the struct names its own.</summary>
    public BodySource(byte[] buffer, int start, int length, IRequestArena arena, ErrorReply invalidBody)
        : this(buffer, start, length, arena, invalidBody, null, null) { }

    /// <summary><see cref="BodySource(byte[], int, int, IRequestArena, ErrorReply)"/>, with the app's replies to a body
    /// that isn't JSON (<paramref name="malformedBody"/>) and to no body (<paramref name="missingBody"/>); null
    /// leaves each to the next reply in line.</summary>
    public BodySource(byte[] buffer, int start, int length, IRequestArena arena, ErrorReply invalidBody, ErrorReply? malformedBody, ErrorReply? missingBody)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(invalidBody);
        if ((uint)start > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - start))
            throw new ArgumentOutOfRangeException(nameof(length));
        Buffer = buffer;
        Start = start;
        Length = length;
        Arena = arena;
        InvalidBody = invalidBody;
        MalformedBody = malformedBody;
        MissingBody = missingBody;
    }

    internal byte[] Buffer { get; }
    internal int Start { get; }
    internal int Length { get; }
    internal IRequestArena Arena { get; }

    /// <summary>The app's reply to a body that isn't the JSON its struct describes.</summary>
    public ErrorReply InvalidBody { get; }

    /// <summary>The app's reply to a body that isn't JSON at all, or null when it set none.</summary>
    public ErrorReply? MalformedBody { get; }

    /// <summary>The app's reply to a request without a body, or null when it set none.</summary>
    public ErrorReply? MissingBody { get; }

    /// <summary>The request has no body: none was sent, or it was empty.</summary>
    public bool IsEmpty => Length == 0;
}

/// <summary>What a body field held.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum BodyKind : byte
{
    /// <summary>Absent or null.</summary>
    Missing,
    /// <summary>A string that is valid text.</summary>
    Text,
    /// <summary>An array of non-empty strings, possibly empty.</summary>
    Array,
    /// <summary>A number, or true or false, that fits the field.</summary>
    Value,
    /// <summary>An object its struct accepted.</summary>
    Nested,
    /// <summary>The wrong JSON type, a string that isn't valid text, a number that doesn't fit; for an array, any
    /// bad element; for an object, any bad field.</summary>
    Invalid,
}

/// <summary>Where a body field's value is: in the body, or in the arena's scratch copy when it was escaped.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct BodyValue
{
    /// <summary>What the field held.</summary>
    public BodyKind Kind { get; }
    internal readonly bool InScratch;
    internal readonly int Start; // from the body's first byte
    internal readonly int Length;
    internal readonly int Count; // texts in an array
    internal readonly long Number; // a number's bits, a bool as 0 or 1, an array's text bytes

    internal BodyValue(BodyKind kind, int start = 0, int length = 0, bool inScratch = false, int count = 0, long number = 0)
    {
        Kind = kind;
        Start = start;
        Length = length;
        InScratch = inScratch;
        Count = count;
        Number = number;
    }

    internal static BodyValue Invalid => new(BodyKind.Invalid);

    /// <summary>A present, non-empty string.</summary>
    public bool IsRequiredText => Kind == BodyKind.Text && Length > 0;

    /// <summary>Absent, null, or a string (non-empty when <paramref name="nonEmpty"/>).</summary>
    public bool IsOptionalText(bool nonEmpty) => Kind == BodyKind.Missing || Kind == BodyKind.Text && (!nonEmpty || Length > 0);

    /// <summary>Absent or null.</summary>
    public bool IsMissing => Kind == BodyKind.Missing;

    /// <summary>The wrong type or a bad value.</summary>
    public bool IsInvalid => Kind == BodyKind.Invalid;

    /// <summary>A number of the field's type, or a bool.</summary>
    public bool IsValue => Kind == BodyKind.Value;

    /// <summary>An array of non-empty strings.</summary>
    public bool IsArray => Kind == BodyKind.Array;

    /// <summary>The number, read as an Int64 or an Int32.</summary>
    public long IntegerValue => Number;

    /// <summary>The number, read as a Double.</summary>
    public double FloatValue => BitConverter.Int64BitsToDouble(Number);

    /// <summary>The bool.</summary>
    public bool BoolValue => Number != 0;

    /// <summary>A field the binder of a nested struct read: <see cref="BodyKind.Nested"/> or <see cref="BodyKind.Invalid"/>.</summary>
    public static BodyValue FromNested(bool valid) => new(valid ? BodyKind.Nested : BodyKind.Invalid);
}

/// <summary>
/// Reads a request body over its bytes with <c>JsonDocument</c>'s outcomes, for the generated binders: one leading
/// UTF-8 BOM is skipped, the reader's depth limit is 64, the last of duplicate keys wins, keys match after
/// unescaping, and anything after the root value but whitespace makes the body malformed, which <see cref="ReadToEnd"/>
/// reports without an exception: one pass reads and checks it (<see cref="JsonCursor"/>). A value without escapes
/// stays where it is in the body; an escaped one is unescaped into a scratch copy of the body in the request's arena,
/// at its own offset (unescaping never grows a value), so values never overlap.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct JsonBody
{
    const int MaxDepth = 64; // JsonDocument's default

    JsonCursor _r;
    readonly byte[] _buffer;
    readonly int _start, _length, _bom;
    readonly IRequestArena _arena;
    byte[]? _scratch;
    int _scratchStart;

    /// <summary>A reader at the start of <paramref name="source"/>'s body.</summary>
    public JsonBody(in BodySource source)
    {
        _buffer = source.Buffer;
        _start = source.Start;
        _length = source.Length;
        _arena = source.Arena;
        ReadOnlySpan<byte> body = _buffer.AsSpan(_start, _length);
        _bom = body.StartsWith(Utf8Bom) ? 3 : 0; // JsonDocument.ParseAsync skips one
        _r = new JsonCursor(body[_bom..], MaxDepth);
        _scratch = null;
        _scratchStart = 0;
    }

    static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Reads the root value: true when it is an object.</summary>
    public bool ReadRootObject() => _r.Read() && _r.TokenType == JsonTokenType.StartObject;

    /// <summary>Moves to the next property of the object at hand: false at its end.</summary>
    public bool NextProperty() => _r.Read() && _r.TokenType == JsonTokenType.PropertyName;

    /// <summary>
    /// The property name at hand once unescaped, in <paramref name="buffer"/> when it was escaped. A name longer
    /// than <paramref name="buffer"/> once escaped, or one that doesn't unescape (a lone surrogate), is returned as a
    /// one-byte span that matches no field name: JsonDocument's lookup didn't match it either. The buffer must hold
    /// six bytes for each byte of the longest name looked for: an escape is at most six bytes per byte it stands for.
    /// </summary>
    public readonly ReadOnlySpan<byte> PropertyName(Span<byte> buffer)
    {
        ReadOnlySpan<byte> raw = _r.ValueSpan;
        if (!_r.ValueIsEscaped) return raw;
        if (raw.Length > buffer.Length || !TryUnescape(raw, buffer, out int n)) return NoName;
        return buffer[..n];
    }

    // A name no field has: field names are text, and a quote can't be part of one unescaped.
    static ReadOnlySpan<byte> NoName => "\""u8;

    /// <summary>Skips the value of the property at hand.</summary>
    public void SkipValue()
    {
        _r.Read();
        _r.Skip();
    }

    /// <summary>Reads the value of the property at hand: true when it is an object, whose properties
    /// <see cref="NextProperty"/> then walks; false (the value skipped) when it is anything else.</summary>
    public bool ReadObject()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.StartObject) return true;
        _r.Skip();
        return false;
    }

    /// <summary>Reads the value of the property at hand for a nested struct: <see cref="BodyKind.Missing"/> for null,
    /// <see cref="BodyKind.Nested"/> for an object (whose properties <see cref="NextProperty"/> then walks), else
    /// <see cref="BodyKind.Invalid"/> with the value skipped.</summary>
    public BodyKind ReadNested()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.Null) return BodyKind.Missing;
        if (_r.TokenType == JsonTokenType.StartObject) return BodyKind.Nested;
        _r.Skip();
        return BodyKind.Invalid;
    }

    /// <summary>Reads to the end of the body, as a full parse would: true when the whole body was JSON. Every read before
    /// it may have stopped early at a syntax error, which this reports; nothing throws.</summary>
    public bool ReadToEnd()
    {
        while (_r.Read()) { }
        return !_r.Malformed;
    }

    /// <summary>A string field: null is missing, a string is its text, anything else is invalid.</summary>
    public BodyValue ReadText()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.Null) return default;
        if (_r.TokenType != JsonTokenType.String)
        {
            _r.Skip();
            return BodyValue.Invalid;
        }
        int start = _bom + _r.TokenStart + 1;
        ReadOnlySpan<byte> raw = _r.ValueSpan;
        if (!_r.ValueIsEscaped)
            return System.Text.Unicode.Utf8.IsValid(raw) ? new BodyValue(BodyKind.Text, start, raw.Length) : BodyValue.Invalid;
        if (!TryUnescape(raw, Scratch().Slice(start, raw.Length), out int written)) return BodyValue.Invalid;
        return new BodyValue(BodyKind.Text, start, written, inScratch: true);
    }

    /// <summary>An array of non-empty strings, kept as the array's place in the body; null is missing.</summary>
    public BodyValue ReadTextArray()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.Null) return default;
        if (_r.TokenType != JsonTokenType.StartArray)
        {
            _r.Skip();
            return BodyValue.Invalid;
        }
        int start = _r.TokenStart;
        int count = 0;
        long bytes = 0;
        bool ok = true;
        while (_r.Read() && _r.TokenType != JsonTokenType.EndArray)
        {
            if (!ok || _r.TokenType != JsonTokenType.String)
            {
                ok = false;
                _r.Skip();
                continue;
            }
            // any escape unescapes to at least one byte, so non-empty is the raw length
            ok = _r.ValueSpan.Length > 0 && (_r.ValueIsEscaped ? IsValidEscaped(_r.ValueSpan) : System.Text.Unicode.Utf8.IsValid(_r.ValueSpan));
            bytes += _r.ValueSpan.Length;
            count++;
        }
        if (!ok) return BodyValue.Invalid;
        int end = _r.TokenStart + 1;
        return new BodyValue(BodyKind.Array, _bom + start, end - start, count: count, number: bytes);
    }

    /// <summary>A number that is an Int64; null is missing.</summary>
    public BodyValue ReadInt64()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.Null) return default;
        if (_r.TokenType == JsonTokenType.Number && _r.TryGetInt64(out long v)) return new BodyValue(BodyKind.Value, number: v);
        _r.Skip();
        return BodyValue.Invalid;
    }

    /// <summary>A number that is an Int32; null is missing.</summary>
    public BodyValue ReadInt32()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.Null) return default;
        if (_r.TokenType == JsonTokenType.Number && _r.TryGetInt32(out int v)) return new BodyValue(BodyKind.Value, number: v);
        _r.Skip();
        return BodyValue.Invalid;
    }

    /// <summary>A number as a Double; null is missing.</summary>
    public BodyValue ReadDouble()
    {
        _r.Read();
        if (_r.TokenType == JsonTokenType.Null) return default;
        if (_r.TokenType == JsonTokenType.Number && _r.TryGetDouble(out double v) && double.IsFinite(v))
            return new BodyValue(BodyKind.Value, number: BitConverter.DoubleToInt64Bits(v));
        _r.Skip();
        return BodyValue.Invalid;
    }

    /// <summary>true or false; null is missing.</summary>
    public BodyValue ReadBoolean()
    {
        _r.Read();
        switch (_r.TokenType)
        {
            case JsonTokenType.Null: return default;
            case JsonTokenType.True: return new BodyValue(BodyKind.Value, number: 1);
            case JsonTokenType.False: return new BodyValue(BodyKind.Value, number: 0);
            default:
                _r.Skip();
                return BodyValue.Invalid;
        }
    }

    /// <summary>A <see cref="BodyKind.Text"/> value's text: in the body, or in the scratch copy when it was escaped.</summary>
    public readonly Utf8 Text(in BodyValue value) =>
        value.Kind != BodyKind.Text ? default
        : value.InScratch ? new Utf8(_scratch!, _scratchStart + value.Start, value.Length)
        : new Utf8(_buffer, _start + value.Start, value.Length);

    /// <summary>A <see cref="BodyKind.Array"/> value's texts, copied (unescaped) into the request's arena.</summary>
    public readonly Utf8List TextList(in BodyValue value)
    {
        if (value.Kind != BodyKind.Array || value.Count == 0) return default;
        int size = Utf8List.RecordBytes(checked((int)value.Number), value.Count);
        ArenaBytes dest = _arena.Allocate(size);
        Span<byte> d = dest.Span;
        var r = new JsonCursor(_buffer.AsSpan(_start + value.Start, value.Length), MaxDepth);
        r.Read(); // [
        int at = 0;
        // ReadTextArray checked every element: each is a non-empty string that unescapes
        while (r.Read() && r.TokenType == JsonTokenType.String)
        {
            ReadOnlySpan<byte> raw = r.ValueSpan;
            if (!r.ValueIsEscaped) at += Utf8List.Write(raw, d[at..]);
            else
            {
                TryUnescape(raw, d[(at + 4)..], out int n);
                at += Utf8List.Write(d.Slice(at + 4, n), d[at..]);
            }
        }
        return new Utf8List(dest.Buffer, dest.Start, at, value.Count, trusted: true);
    }

    /// <summary>The scratch copy for unescaped values, made the first time one is needed.</summary>
    Span<byte> Scratch()
    {
        if (_scratch is null)
        {
            ArenaBytes s = _arena.Allocate(_length);
            _scratch = s.Buffer;
            _scratchStart = s.Start;
        }
        return _scratch.AsSpan(_scratchStart, _length);
    }

    /// <summary>Whether an escaped string is one JsonDocument could read; checked without writing it anywhere.</summary>
    static bool IsValidEscaped(ReadOnlySpan<byte> raw)
    {
        int i = 0;
        while (i < raw.Length)
        {
            int esc = raw[i..].IndexOf((byte)'\\');
            int run = esc < 0 ? raw.Length - i : esc;
            if (!System.Text.Unicode.Utf8.IsValid(raw.Slice(i, run))) return false;
            i += run;
            if (i < raw.Length && !TryEscape(raw, ref i, out _)) return false;
        }
        return true;
    }

    /// <summary>
    /// Unescapes a string the reader has already checked for escape syntax. False on what JsonDocument's GetString
    /// throws for: invalid UTF-8 and a \u escape of a lone surrogate.
    /// </summary>
    internal static bool TryUnescape(ReadOnlySpan<byte> raw, Span<byte> dest, out int written)
    {
        written = 0;
        int i = 0;
        while (i < raw.Length)
        {
            int esc = raw[i..].IndexOf((byte)'\\');
            int run = esc < 0 ? raw.Length - i : esc;
            // a literal run must be valid UTF-8 by itself: an escape only ever produces whole characters
            ReadOnlySpan<byte> lit = raw.Slice(i, run);
            if (!System.Text.Unicode.Utf8.IsValid(lit)) return false;
            lit.CopyTo(dest[written..]);
            written += run;
            i += run;
            if (i >= raw.Length) break;
            if (!TryEscape(raw, ref i, out int scalar)) return false;
            written += new Rune(scalar).EncodeToUtf8(dest[written..]);
        }
        return true;
    }

    /// <summary>The scalar of the escape at raw[i] ('\\'), advancing past it; false for a lone surrogate.</summary>
    static bool TryEscape(ReadOnlySpan<byte> raw, ref int i, out int scalar)
    {
        byte c = raw[i + 1];
        i += 2;
        switch (c)
        {
            case (byte)'"': scalar = '"'; return true;
            case (byte)'\\': scalar = '\\'; return true;
            case (byte)'/': scalar = '/'; return true;
            case (byte)'b': scalar = '\b'; return true;
            case (byte)'f': scalar = '\f'; return true;
            case (byte)'n': scalar = '\n'; return true;
            case (byte)'r': scalar = '\r'; return true;
            case (byte)'t': scalar = '\t'; return true;
        }
        // 'u': the reader guarantees four hex digits
        int u = Hex4(raw.Slice(i, 4));
        i += 4;
        scalar = u;
        if (u is < 0xD800 or > 0xDFFF) return true;
        if (u >= 0xDC00) return false; // a low surrogate first
        if (i + 6 > raw.Length || raw[i] != '\\' || raw[i + 1] != 'u') return false;
        int lo = Hex4(raw.Slice(i + 2, 4));
        if (lo is < 0xDC00 or > 0xDFFF) return false;
        i += 6;
        scalar = 0x10000 + ((u - 0xD800) << 10) + (lo - 0xDC00);
        return true;
    }

    static int Hex4(ReadOnlySpan<byte> h) => Query.Hex(h[0]) << 12 | Query.Hex(h[1]) << 8 | Query.Hex(h[2]) << 4 | Query.Hex(h[3]);

    /// <summary>
    /// Compares the string or property name the reader is on with <paramref name="text"/> (ASCII, at most 16
    /// bytes) once unescaped, without throwing: false when it is escaped and doesn't unescape (a lone surrogate).
    /// </summary>
    internal static bool TryTextEquals(ref Utf8JsonReader r, ReadOnlySpan<byte> text, out bool equal)
    {
        ReadOnlySpan<byte> raw = r.ValueSpan;
        equal = false;
        if (!r.ValueIsEscaped)
        {
            equal = raw.SequenceEqual(text);
            return true;
        }
        if (!IsValidEscaped(raw)) return false;
        // an escape takes at most 6 raw bytes per byte it stands for, so a longer raw name can't match
        if (raw.Length > 6 * text.Length) return true;
        Span<byte> buf = stackalloc byte[raw.Length]; // at most 6 per byte of text, as checked above
        TryUnescape(raw, buf, out int n);
        equal = buf[..n].SequenceEqual(text);
        return true;
    }
}
