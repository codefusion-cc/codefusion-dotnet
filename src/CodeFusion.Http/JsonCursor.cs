using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace CodeFusion.Http;

/// <summary>
/// The body binders' JSON reader: <c>Utf8JsonReader</c>'s tokens for a final block with no comments, no trailing
/// commas and a depth limit, read in one pass, reporting a syntax error as <see cref="Malformed"/> instead of throwing,
/// so a malformed body costs no exception. It takes what the reader takes and refuses what it refuses (JsonSyntaxTests
/// and the json-syntax fuzz target hold it to that, token by token); like the reader it doesn't check strings for
/// UTF-8, which the binders do. Once malformed, every read is false.
/// </summary>
internal ref struct JsonCursor
{
    // what ends a run of plain string bytes: the quote, an escape, a control character
    static readonly SearchValues<byte> StringStops = SearchValues.Create(
        [(byte)'"', (byte)'\\', 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31]);

    // what Read looks for next
    const byte RootValue = 0, ValueOrEnd = 1, NameOrEnd = 2, AfterName = 3, CommaOrEnd = 4, Done = 5;

    readonly ReadOnlySpan<byte> _s;
    readonly int _maxDepth;
    int _i, _depth, _valueStart, _valueLength;
    ulong _objects; // bit d is set when the container at depth d + 1 is an object
    byte _expect;

    /// <summary>A cursor before the first token of <paramref name="json"/>, nesting at most <paramref name="maxDepth"/>
    /// (at most 64) containers.</summary>
    public JsonCursor(ReadOnlySpan<byte> json, int maxDepth)
    {
        if ((uint)maxDepth > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        _s = json;
        _maxDepth = maxDepth;
        _i = _depth = _valueStart = _valueLength = 0;
        _objects = 0;
        _expect = RootValue;
        TokenType = JsonTokenType.None;
        TokenStart = 0;
        ValueIsEscaped = false;
        Malformed = false;
    }

    /// <summary>The token at hand.</summary>
    public JsonTokenType TokenType { get; private set; }

    /// <summary>Where the token at hand starts: its quote, for a string or a property name.</summary>
    public int TokenStart { get; private set; }

    /// <summary>The token's bytes: a string's or a name's between its quotes, as they are (escapes and all).</summary>
    public readonly ReadOnlySpan<byte> ValueSpan => _s.Slice(_valueStart, _valueLength);

    /// <summary>Whether the string or name at hand has an escape.</summary>
    public bool ValueIsEscaped { get; private set; }

    /// <summary>The bytes aren't JSON: a read found a syntax error, an end too early, or bytes after the value.</summary>
    public bool Malformed { get; private set; }

    readonly bool InObject => (_objects >> (_depth - 1) & 1) != 0;

    /// <summary>The next token: false at the end of the value (only whitespace after it), or on a syntax error, which
    /// sets <see cref="Malformed"/>.</summary>
    public bool Read()
    {
        if (Malformed) return false;
        int i = Whitespace(_i);
        switch (_expect)
        {
            case Done:
                _i = i;
                return i < _s.Length && Fail();
            case CommaOrEnd:
                if (i >= _s.Length) return Fail();
                byte c = _s[i];
                if (c == (InObject ? '}' : ']')) return Close(i);
                if (c != ',') return Fail();
                i = Whitespace(i + 1);
                return InObject ? Name(i) : Value(i);
            case NameOrEnd:
                if (i < _s.Length && _s[i] == '}') return Close(i);
                return Name(i);
            case ValueOrEnd:
                if (i < _s.Length && _s[i] == ']') return Close(i);
                return Value(i);
            case AfterName:
                if (i >= _s.Length || _s[i] != ':') return Fail();
                return Value(Whitespace(i + 1));
            default:
                return Value(i);
        }
    }

    /// <summary>Skips what the token at hand opens: a property's value, or a container to its end. False on a syntax
    /// error.</summary>
    public bool Skip()
    {
        if (TokenType == JsonTokenType.PropertyName && !Read()) return false;
        if (TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            int depth = _depth - 1;
            while (true)
            {
                if (!Read()) return false;
                if (TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray && _depth == depth) return true;
            }
        }
        return !Malformed;
    }

    /// <summary>The number at hand as an Int64, as <c>Utf8JsonReader.TryGetInt64</c>: whole and in range.</summary>
    public readonly bool TryGetInt64(out long value)
    {
        ReadOnlySpan<byte> n = ValueSpan;
        value = 0;
        return TokenType == JsonTokenType.Number && n.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0
            && long.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The number at hand as an Int32, as <c>Utf8JsonReader.TryGetInt32</c>.</summary>
    public readonly bool TryGetInt32(out int value)
    {
        ReadOnlySpan<byte> n = ValueSpan;
        value = 0;
        return TokenType == JsonTokenType.Number && n.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0
            && int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The number at hand as a Double, as <c>Utf8JsonReader.TryGetDouble</c>: one too large for a Double is an
    /// infinity, which the binders refuse themselves.</summary>
    public readonly bool TryGetDouble(out double value)
    {
        value = 0;
        return TokenType == JsonTokenType.Number && double.TryParse(ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    bool Fail()
    {
        Malformed = true;
        TokenType = JsonTokenType.None;
        return false;
    }

    bool Close(int i)
    {
        TokenType = InObject ? JsonTokenType.EndObject : JsonTokenType.EndArray;
        ValueIsEscaped = false;
        _depth--;
        Token(i, i, 1);
        _expect = _depth == 0 ? Done : CommaOrEnd;
        return true;
    }

    void Token(int start, int value, int length)
    {
        TokenStart = start;
        _valueStart = value;
        _valueLength = length;
        _i = value + length;
    }

    bool Name(int i)
    {
        if (i >= _s.Length || _s[i] != '"' || !String(i)) return Fail();
        TokenType = JsonTokenType.PropertyName;
        _expect = AfterName;
        return true;
    }

    bool Value(int i)
    {
        if (i >= _s.Length) return Fail();
        byte c = _s[i];
        if (c is (byte)'{' or (byte)'[')
        {
            if (_depth == _maxDepth) return Fail();
            bool isObject = c == '{';
            _objects = isObject ? _objects | 1UL << _depth : _objects & ~(1UL << _depth);
            _depth++;
            TokenType = isObject ? JsonTokenType.StartObject : JsonTokenType.StartArray;
            ValueIsEscaped = false;
            Token(i, i, 1);
            _expect = isObject ? NameOrEnd : ValueOrEnd;
            return true;
        }
        if (c == '"')
        {
            if (!String(i)) return Fail();
            TokenType = JsonTokenType.String;
        }
        else
        {
            ValueIsEscaped = false;
            int end = c switch
            {
                (byte)'t' => Word(i, "true"u8),
                (byte)'f' => Word(i, "false"u8),
                (byte)'n' => Word(i, "null"u8),
                _ => Number(i),
            };
            if (end < 0) return Fail();
            TokenType = c switch { (byte)'t' => JsonTokenType.True, (byte)'f' => JsonTokenType.False, (byte)'n' => JsonTokenType.Null, _ => JsonTokenType.Number };
            Token(i, i, end - i);
        }
        _expect = _depth == 0 ? Done : CommaOrEnd;
        return true;
    }

    readonly int Whitespace(int i)
    {
        while (i < _s.Length && _s[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') i++;
        return i;
    }

    /// <summary>A string from its opening quote at <paramref name="i"/>: the token is its content, the cursor past its
    /// closing quote.</summary>
    bool String(int i)
    {
        int at = i + 1;
        bool escaped = false;
        while (true)
        {
            int stop = _s[at..].IndexOfAny(StringStops);
            if (stop < 0) return false;
            at += stop;
            byte c = _s[at];
            if (c == '"')
            {
                ValueIsEscaped = escaped;
                Token(i, i + 1, at - i - 1);
                _i = at + 1;
                return true;
            }
            if (c != '\\' || at + 1 >= _s.Length) return false; // a control character, or an escape cut short
            escaped = true;
            byte e = _s[at + 1];
            if (e is (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t')
            {
                at += 2;
                continue;
            }
            if (e != 'u' || at + 6 > _s.Length) return false;
            for (int h = at + 2; h < at + 6; h++)
                if (!char.IsAsciiHexDigit((char)_s[h])) return false;
            at += 6;
        }
    }

    readonly int Word(int i, ReadOnlySpan<byte> word) => _s[i..].StartsWith(word) ? i + word.Length : -1;

    /// <summary>Where <c>-? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?</c> from <paramref name="i"/> ends, or -1;
    /// what follows is the next read's to check.</summary>
    readonly int Number(int i)
    {
        if (_s[i] == '-') i++;
        if (i >= _s.Length || !char.IsAsciiDigit((char)_s[i])) return -1;
        if (_s[i++] != '0')
            while (i < _s.Length && char.IsAsciiDigit((char)_s[i])) i++;
        if (i < _s.Length && _s[i] == '.')
        {
            if (++i >= _s.Length || !char.IsAsciiDigit((char)_s[i])) return -1;
            while (i < _s.Length && char.IsAsciiDigit((char)_s[i])) i++;
        }
        if (i < _s.Length && _s[i] is (byte)'e' or (byte)'E')
        {
            if (++i < _s.Length && _s[i] is (byte)'+' or (byte)'-') i++;
            if (i >= _s.Length || !char.IsAsciiDigit((char)_s[i])) return -1;
            while (i < _s.Length && char.IsAsciiDigit((char)_s[i])) i++;
        }
        return i;
    }
}
