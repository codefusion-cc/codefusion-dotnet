using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CodeFusion.Data;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// One DataRow, read in place (every column in binary format). It lives only during the handler's call: the bytes
/// are the receive buffer's. Each column's type comes from the statement's RowDescription, and a getter reads only the
/// types it reads exactly (an int4 is a double too; an int8 or a float isn't the other), so a column of another type
/// throws <see cref="InvalidCastException"/> instead of being misread. <see cref="GetBytes"/> takes any type. The
/// handler's exception fails its own command.
/// </summary>
public readonly ref struct PgRow : IDataRow
{
    /// <summary>2000-01-01T00:00:00Z, the epoch of binary timestamps, in DateTime ticks.</summary>
    const long EpochTicks = 630822816000000000;
    /// <summary>Microseconds from 1970-01-01 to 2000-01-01.</summary>
    const long UnixToPgMicros = 946684800000000;

    // pg_type OIDs
    const uint Bool = 16, Bytea = 17, Int8 = 20, Int2 = 21, Int4 = 23, Text = 25, Json = 114, Float4 = 700, Float8 = 701, Varchar = 1043,
        Timestamp_ = 1114, Timestamptz = 1184, Jsonb = 3802;

    readonly ReadOnlySpan<byte> _data;
    readonly ReadOnlySpan<int> _start, _len;
    readonly ReadOnlySpan<uint> _types;

    /// <summary>The row's place among its statement's rows, from 0.</summary>
    public readonly int Index;

    PgRow(ReadOnlySpan<byte> data, ReadOnlySpan<int> start, ReadOnlySpan<int> len, ReadOnlySpan<uint> types, int index)
    {
        _data = data;
        _start = start;
        _len = len;
        _types = types;
        Index = index;
    }

    /// <summary>Splits a DataRow body into columns, of the <paramref name="types"/> the statement's RowDescription
    /// gave. Throws <see cref="PgConnectionException"/> on a malformed one, or one with another number of columns:
    /// the stream can't be trusted after it.</summary>
    internal static PgRow Parse(ReadOnlySpan<byte> body, ref int[] start, ref int[] len, int index, ReadOnlySpan<uint> types)
    {
        if (body.Length < 2) throw Malformed();
        int n = BinaryPrimitives.ReadInt16BigEndian(body);
        if (n < 0) throw Malformed();
        if (n != types.Length) throw new PgConnectionException($"a DataRow of {n} columns where RowDescription said {types.Length}");
        if (n > start.Length)
        {
            start = new int[Math.Max(n, start.Length * 2)];
            len = new int[start.Length];
        }
        int p = 2;
        for (int i = 0; i < n; i++)
        {
            if (body.Length - p < 4) throw Malformed();
            int l = BinaryPrimitives.ReadInt32BigEndian(body[p..]);
            p += 4;
            start[i] = p;
            len[i] = l;
            if (l >= 0)
            {
                if (body.Length - p < l) throw Malformed();
                p += l;
            }
            else if (l != -1) throw Malformed();
        }
        if (p != body.Length) throw Malformed();
        return new PgRow(body, start.AsSpan(0, n), len.AsSpan(0, n), types, index);
    }

    static PgConnectionException Malformed() => new("malformed DataRow");

    /// <summary>How many columns the row has.</summary>
    public int FieldCount => _start.Length;

    /// <summary>Whether column <paramref name="c"/> is NULL.</summary>
    public bool IsNull(int c) => _len[c] < 0;

    /// <summary>The value's bytes: UTF-8 for text, the raw bytes for bytea. Throws for NULL.</summary>
    public ReadOnlySpan<byte> GetBytes(int c)
    {
        int l = _len[c];
        if (l < 0) throw new InvalidCastException($"column {c} is NULL");
        return _data.Slice(_start[c], l);
    }

    /// <summary>A json or jsonb column's text (binary jsonb's version byte left out), or the bytes of a text or bytea
    /// one holding JSON (<c>convert_to(x::text, 'UTF8')</c>), as they are.</summary>
    public ReadOnlySpan<byte> GetJson(int c)
    {
        uint type = _types[c];
        var b = GetBytes(c);
        if (type == Jsonb)
        {
            if (b.Length == 0 || b[0] != JsonbVersion) throw new InvalidCastException($"column {c}: jsonb of an unknown format version");
            return b[1..];
        }
        return type is Json or Text or Varchar or Bytea ? b : throw WrongType(c, "JSON");
    }

    const byte JsonbVersion = 1;

    /// <summary>An int8, int4 or int2 column.</summary>
    public long GetInt64(int c)
    {
        uint type = _types[c];
        var b = GetBytes(c);
        return (type, b.Length) switch
        {
            (Int8, 8) => BinaryPrimitives.ReadInt64BigEndian(b),
            (Int4, 4) => BinaryPrimitives.ReadInt32BigEndian(b),
            (Int2, 2) => BinaryPrimitives.ReadInt16BigEndian(b),
            (Int8 or Int4 or Int2, _) => throw WrongSize(c, b.Length),
            _ => throw WrongType(c, "an integer"),
        };
    }

    /// <summary>A bool column.</summary>
    public bool GetBoolean(int c)
    {
        if (_types[c] != Bool) throw WrongType(c, "a bool");
        var b = GetBytes(c);
        if (b.Length != 1) throw WrongSize(c, b.Length);
        return b[0] != 0;
    }

    /// <summary>A float8 or float4 column, or an int4 or int2 one (each of those is a double exactly; an int8 or a
    /// numeric isn't, so they throw).</summary>
    public double GetDouble(int c)
    {
        uint type = _types[c];
        var b = GetBytes(c);
        return (type, b.Length) switch
        {
            (Float8, 8) => BinaryPrimitives.ReadDoubleBigEndian(b),
            (Float4, 4) => BinaryPrimitives.ReadSingleBigEndian(b),
            (Int4, 4) => BinaryPrimitives.ReadInt32BigEndian(b),
            (Int2, 2) => BinaryPrimitives.ReadInt16BigEndian(b),
            (Float8 or Float4 or Int4 or Int2, _) => throw WrongSize(c, b.Length),
            _ => throw WrongType(c, "a float4, float8, int2 or int4"),
        };
    }

    /// <summary>A timestamptz column; ±infinity as the microseconds long.MaxValue and long.MinValue.</summary>
    public Timestamp GetTimestamp(int c) => new(GetUnixMicroseconds(c));

    /// <summary>A timestamptz as microseconds since 1970-01-01 UTC; ±infinity as long.MaxValue and long.MinValue.
    /// Throws for the last 30 years of Postgres's range (after 294247-01-10), which a long of microseconds since 1970
    /// can't hold apart from +infinity.</summary>
    public long GetUnixMicroseconds(int c)
    {
        long us = Timestamp(c);
        if (us is long.MaxValue or long.MinValue) return us;
        if (us >= long.MaxValue - UnixToPgMicros) throw new InvalidCastException($"column {c}: timestamp {us} µs from 2000 is past what microseconds since 1970 hold");
        return us + UnixToPgMicros;
    }

    /// <summary>A timestamptz as Postgres sends it: microseconds since 2000-01-01 UTC; ±infinity as long.MaxValue and long.MinValue.</summary>
    public long GetPgMicroseconds(int c) => Timestamp(c);

    /// <summary>A timestamptz as a UTC DateTime. Throws for values DateTime can't hold (infinity, before year 1).</summary>
    public DateTime GetDateTime(int c)
    {
        long us = Timestamp(c);
        const long MinUs = -EpochTicks / 10, MaxUs = (3155378975999999999 - EpochTicks) / 10;
        if (us is < MinUs or > MaxUs) throw new InvalidCastException($"column {c}: timestamp {us} µs from 2000 is outside DateTime's range");
        return new DateTime(EpochTicks + us * 10, DateTimeKind.Utc);
    }

    long Timestamp(int c)
    {
        if (_types[c] is not (Timestamptz or Timestamp_)) throw WrongType(c, "a timestamptz or timestamp");
        var b = GetBytes(c);
        if (b.Length != 8) throw WrongSize(c, b.Length);
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static InvalidCastException WrongSize(int c, int size) => new($"column {c} has {size} bytes, not the size of its type");

    [MethodImpl(MethodImplOptions.NoInlining)]
    InvalidCastException WrongType(int c, string wanted) => new($"column {c} is of type {_types[c]} (pg_type), not {wanted}");
}
