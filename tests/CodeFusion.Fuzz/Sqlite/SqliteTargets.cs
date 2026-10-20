using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeFusion.Data;
using CodeFusion.Data.Sqlite;

namespace CodeFusion.Fuzz;

/// <summary>
/// The SQLite provider's own encodings, run against SQLite itself: a text array bound as one JSON text (json_each must
/// give back exactly the elements bound, so no element breaks out of its string), and a write's rows, kept in the job
/// until its group commits (each must read as the row SQLite returned, as a read gets it in place).
/// </summary>
static class SqliteTargets
{
    static readonly Lazy<SqliteDataSource> Source = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "cf-fuzz-sqlite-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return new SqliteDatabase(Path.Combine(dir, "fuzz.db")) { Readers = 1 }.OpenSource();
    });

    // ---------- text arrays ----------

    static readonly SqliteStatement Each = new("SELECT value, type FROM json_each(?1) ORDER BY key", DataType.TextArray);

    /// <summary>The input's pieces between 0xFF bytes (which UTF-8 never has; a piece may still be invalid UTF-8) as one
    /// text array. Valid UTF-8 comes back element for element; invalid UTF-8 never adds or drops an element.</summary>
    public static void JsonArray(ReadOnlySpan<byte> input)
    {
        var elements = new List<byte[]>();
        for (int start = 0; input.Length > 0;)
        {
            int end = input[start..].IndexOf((byte)0xFF);
            if (end < 0)
            {
                elements.Add(input[start..].ToArray());
                break;
            }
            elements.Add(input.Slice(start, end).ToArray());
            start += end + 1;
        }
        var c = Source.Value.Read(Each);
        c.BeginTextArray();
        foreach (var e in elements) c.Element(e);
        c.EndTextArray();
        var rows = new List<(byte[] Value, string Type)>();
        var r = Sqlite.Run<Elements>(c.Hold(), rows, default).AsTask().GetAwaiter().GetResult();
        bool valid = elements.TrueForAll(e => System.Text.Unicode.Utf8.IsValid(e));
        if (!r.IsOk)
        {
            if (valid) throw new FuzzFinding("sqlite-json-array: valid UTF-8 refused", r.ToString());
            return;
        }
        if (rows.Count != elements.Count) throw new FuzzFinding("sqlite-json-array: the element count", $"{rows.Count} rows for {elements.Count} elements");
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type != "text") throw new FuzzFinding("sqlite-json-array: an element isn't text", $"element {i} is {rows[i].Type}");
            if (System.Text.Unicode.Utf8.IsValid(elements[i]) && !rows[i].Value.AsSpan().SequenceEqual(elements[i]))
                throw new FuzzFinding("sqlite-json-array: an element changed", $"element {i}: {Convert.ToHexString(elements[i])} came back {Convert.ToHexString(rows[i].Value)}");
        }
    }

    sealed class Elements : IRowReader
    {
        public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct
        {
            var rows = (List<(byte[], string)>)state!;
            rows.Add((row.GetBytes(0).ToArray(), Encoding.UTF8.GetString(row.GetBytes(1))));
            return rows.Count;
        }
    }

    // ---------- a write's kept rows ----------

    const int Columns = 8;

    /// <summary>Column k is what SQLite makes of ?(2k+2), picked by ?(2k+1): the value as bound, a blob, a number parsed
    /// from it, a float, or JSON text.</summary>
    static readonly SqliteStatement Values = new(
        "SELECT " + string.Join(", ", Enumerable.Range(0, Columns).Select(k =>
            $"CASE ?{2 * k + 1} WHEN 0 THEN ?{2 * k + 2} WHEN 1 THEN CAST(?{2 * k + 2} AS BLOB) WHEN 2 THEN CAST(?{2 * k + 2} AS NUMERIC) " +
            $"WHEN 3 THEN ?{2 * k + 2} * 1.0 ELSE json_quote(?{2 * k + 2}) END")),
        [.. Enumerable.Range(0, 2 * Columns).Select(_ => DataType.Int64)]);

    /// <summary>One to three statements of <see cref="Values"/>, the values from the input, run as a read (rows read in
    /// place) and as a write (rows kept, then read once its group committed). Each kept row reads as the row in place:
    /// the same NULLs, and wherever a getter of the kept row answers, the same answer.</summary>
    public static void SavedRow(ReadOnlySpan<byte> input)
    {
        int at = 0;
        int statements = input.Length > 0 ? input[at++] % 3 + 1 : 1;
        var values = new (long Pick, byte Kind, long Int, double Double, byte[] Text)[statements * Columns];
        for (int i = 0; i < values.Length; i++)
        {
            long pick = Next(input, ref at) % 5;
            byte kind = (byte)(Next(input, ref at) % 4);
            values[i] = kind switch
            {
                0 => (pick, kind, 0, 0, []),
                1 => (pick, kind, Number(input, ref at), 0, []),
                2 => (pick, kind, 0, BitConverter.Int64BitsToDouble(Number(input, ref at)), []),
                _ => (pick, kind, 0, 0, Text(input, ref at)),
            };
        }
        var live = new List<Field[]>();
        var read = Sqlite.Run<Capture>(Bind(Source.Value.Read(Values), values, statements), live, default).AsTask().GetAwaiter().GetResult();
        if (!read.IsOk) throw new FuzzFinding("sqlite-saved-row: the read failed", read.ToString());
        var kept = new List<Field[]>();
        var write = Sqlite.Run<Capture>(Bind(Source.Value.Write(Values), values, statements), kept, default).AsTask().GetAwaiter().GetResult();
        if (!write.IsOk) throw new FuzzFinding("sqlite-saved-row: the write failed", write.ToString());
        if (kept.Count != live.Count || live.Count != statements) throw new FuzzFinding("sqlite-saved-row: the row count", $"{kept.Count} kept, {live.Count} read, {statements} statements");
        for (int r = 0; r < live.Count; r++)
            for (int c = 0; c < Columns; c++)
            {
                Field l = live[r][c], k = kept[r][c];
                string where = $"row {r} column {c} ({values[r * Columns + c]})";
                if (l.Null != k.Null) throw new FuzzFinding("sqlite-saved-row: IsNull", where);
                if (l.Null) continue;
                if (l.Bytes is null) throw new FuzzFinding("sqlite-saved-row: a column read in place that isn't NULL has no text", where);
                if (k.Bytes is null && k.Int is null) throw new FuzzFinding("sqlite-saved-row: a kept column is neither text nor a number", where);
                if (k.Bytes is not null && !k.Bytes.AsSpan().SequenceEqual(l.Bytes))
                    throw new FuzzFinding("sqlite-saved-row: GetBytes", $"{where}: kept {Convert.ToHexString(k.Bytes)}, in place {Convert.ToHexString(l.Bytes)}");
                if (k.Int is { } i && i != l.Int) throw new FuzzFinding("sqlite-saved-row: GetInt64", $"{where}: kept {i}, in place {l.Int}");
                if (k.Double is { } d && BitConverter.DoubleToInt64Bits(d) != BitConverter.DoubleToInt64Bits(l.Double!.Value))
                    throw new FuzzFinding("sqlite-saved-row: GetDouble", $"{where}: kept {d:R}, in place {l.Double:R}");
            }
    }

    static PendingCommand Bind(SqliteCommand c, (long Pick, byte Kind, long Int, double Double, byte[] Text)[] values, int statements)
    {
        for (int s = 0; s < statements; s++)
        {
            if (s > 0) c.Then(Values);
            for (int k = 0; k < Columns; k++)
            {
                var v = values[s * Columns + k];
                c.Int64(v.Pick);
                switch (v.Kind)
                {
                    case 0:
                        c.Null();
                        break;
                    case 1:
                        c.Int64(v.Int);
                        break;
                    case 2:
                        c.Double(v.Double);
                        break;
                    default:
                        c.Text(v.Text);
                        break;
                }
            }
        }
        return c.Hold();
    }

    static byte Next(ReadOnlySpan<byte> input, ref int at) => at < input.Length ? input[at++] : (byte)0;

    static long Number(ReadOnlySpan<byte> input, ref int at)
    {
        Span<byte> b = stackalloc byte[8];
        for (int i = 0; i < 8; i++) b[i] = Next(input, ref at);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    static byte[] Text(ReadOnlySpan<byte> input, ref int at)
    {
        int n = Math.Min(Next(input, ref at), input.Length - at);
        var t = input.Slice(at, n).ToArray();
        at += n;
        return t;
    }

    /// <summary>A column as each getter read it; null where it threw.</summary>
    readonly record struct Field(bool Null, byte[]? Bytes, long? Int, double? Double);

    sealed class Capture : IRowReader
    {
        public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct
        {
            var rows = (List<Field[]>)state!;
            var fields = new Field[row.FieldCount];
            for (int c = 0; c < fields.Length; c++)
            {
                if (row.IsNull(c))
                {
                    fields[c] = new Field(true, null, null, null);
                    continue;
                }
                byte[]? bytes = null;
                long? i = null;
                double? d = null;
                try
                {
                    bytes = row.GetBytes(c).ToArray();
                }
                catch (InvalidOperationException)
                {
                }
                try
                {
                    i = row.GetInt64(c);
                }
                catch (InvalidOperationException)
                {
                }
                try
                {
                    d = row.GetDouble(c);
                }
                catch (InvalidOperationException)
                {
                }
                fields[c] = new Field(false, bytes, i, d);
            }
            rows.Add(fields);
            return rows.Count;
        }
    }
}
