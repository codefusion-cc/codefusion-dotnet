using System;
using System.ComponentModel;
using System.Text.Json;

namespace CodeFusion.Data;

/// <summary>
/// Columns written as JSON, for the generated readers: each goes from the row's bytes into the writer, with no
/// string and no copy. Text is escaped as JSON needs (the writer's encoder decides how much). The JSON rules are
/// Core's (<see cref="JsonEnvelopeAttribute"/>).
/// </summary>
// The generated readers call these once per column with their own writer, never null: a check per column buys
// nothing.
#pragma warning disable CA1062
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RowJson
{
    /// <summary>A text column as a string property.</summary>
    public static void Text<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        json.WriteString(name, row.GetBytes(column));

    /// <summary>A text column as a string property, or null.</summary>
    public static void NullableText<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNull(name);
        else json.WriteString(name, row.GetBytes(column));
    }

    /// <summary>A timestamp column as its RFC 3339 text.</summary>
    public static void Timestamp<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        Span<byte> text = stackalloc byte[CodeFusion.Timestamp.FormattedLength];
        if (!row.GetTimestamp(column).TryFormat(text, out _)) throw OutOfRange(column);
        json.WriteString(name, text);
    }

    /// <summary>A timestamp column as its RFC 3339 text, or null.</summary>
    public static void NullableTimestamp<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNull(name);
        else Timestamp(json, name, row, column);
    }

    /// <summary>JSON the database built, copied as it is; NULL (an aggregate of no rows) is <c>[]</c>.</summary>
    public static void Raw<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        json.WritePropertyName(name);
        RawValue(json, row, column);
    }

    /// <summary>JSON the database built, or null.</summary>
    public static void NullableRaw<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNull(name);
        else Raw(json, name, row, column);
    }

    /// <summary>An integer column as a number.</summary>
    public static void Int64<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        json.WriteNumber(name, row.GetInt64(column));

    /// <summary>An integer column as a number, or null.</summary>
    public static void NullableInt64<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNull(name);
        else json.WriteNumber(name, row.GetInt64(column));
    }

    /// <summary>A floating-point column as a number.</summary>
    public static void Double<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        json.WriteNumber(name, row.GetDouble(column));

    /// <summary>A floating-point column as a number, or null.</summary>
    public static void NullableDouble<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNull(name);
        else json.WriteNumber(name, row.GetDouble(column));
    }

    /// <summary>A bool column.</summary>
    public static void Boolean<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        json.WriteBoolean(name, row.GetBoolean(column));

    /// <summary>A bool column, or null.</summary>
    public static void NullableBoolean<TRow>(Utf8JsonWriter json, JsonEncodedText name, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNull(name);
        else json.WriteBoolean(name, row.GetBoolean(column));
    }

    // ---------- values without a name: a scalar reply ----------

    /// <summary>A text column as a string value, or null.</summary>
    public static void TextValue<TRow>(Utf8JsonWriter json, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNullValue();
        else json.WriteStringValue(row.GetBytes(column));
    }

    /// <summary>A timestamp column as its text, or null.</summary>
    public static void TimestampValue<TRow>(Utf8JsonWriter json, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column))
        {
            json.WriteNullValue();
            return;
        }
        Span<byte> text = stackalloc byte[CodeFusion.Timestamp.FormattedLength];
        if (!row.GetTimestamp(column).TryFormat(text, out _)) throw OutOfRange(column);
        json.WriteStringValue(text);
    }

    /// <summary>JSON the database built, copied as it is; NULL is <c>[]</c>.</summary>
    public static void RawValue<TRow>(Utf8JsonWriter json, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteRawValue("[]"u8, skipInputValidation: true);
        else json.WriteRawValue(row.GetJson(column), skipInputValidation: true);
    }

    /// <summary>An integer column as a number, or null.</summary>
    public static void Int64Value<TRow>(Utf8JsonWriter json, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNullValue();
        else json.WriteNumberValue(row.GetInt64(column));
    }

    /// <summary>A floating-point column as a number, or null.</summary>
    public static void DoubleValue<TRow>(Utf8JsonWriter json, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNullValue();
        else json.WriteNumberValue(row.GetDouble(column));
    }

    /// <summary>A bool column, or null.</summary>
    public static void BooleanValue<TRow>(Utf8JsonWriter json, scoped in TRow row, int column) where TRow : IDataRow, allows ref struct
    {
        if (row.IsNull(column)) json.WriteNullValue();
        else json.WriteBooleanValue(row.GetBoolean(column));
    }

    static InvalidOperationException OutOfRange(int column) => new($"column {column}: the timestamp is outside years 1 to 9999, which RFC 3339 can't write");
}
#pragma warning restore CA1062

/// <summary>
/// Columns copied out of a row for an awaited result, for the generated readers: text goes into the request's
/// arena (<see cref="RowState.Arena"/>), and the values point there.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RowCopy
{
    static readonly byte[] EmptyArray = "[]"u8.ToArray();

    /// <summary>The bytes column <paramref name="column"/> needs in the arena; 0 for NULL.</summary>
    public static int Length<TRow>(scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? 0 : row.GetBytes(column).Length;

    /// <summary>The bytes JSON column <paramref name="column"/> needs in the arena; 0 for NULL.</summary>
    public static int JsonLength<TRow>(scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? 0 : row.GetJson(column).Length;

    /// <summary>The arena bytes for a row whose text needs <paramref name="length"/> of them.</summary>
    public static ArenaBytes Allocate(RowState state, int length)
    {
        ArgumentNullException.ThrowIfNull(state);
        return length == 0 ? default : state.Arena.Allocate(length);
    }

    /// <summary>A text column, copied into <paramref name="bytes"/> at <paramref name="at"/>.</summary>
    public static Utf8 Text<TRow>(scoped in TRow row, int column, in ArenaBytes bytes, ref int at) where TRow : IDataRow, allows ref struct =>
        Copy(row.GetBytes(column), bytes, ref at);

    static Utf8 Copy(ReadOnlySpan<byte> value, in ArenaBytes bytes, ref int at)
    {
        if (value.Length == 0) return Utf8.Empty;
        value.CopyTo(bytes.Span[at..]);
        var text = bytes.Text(at, value.Length);
        at += value.Length;
        return text;
    }

    /// <summary>A text column copied into the arena, or null.</summary>
    public static Utf8? NullableText<TRow>(scoped in TRow row, int column, in ArenaBytes bytes, ref int at) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? null : Text(row, column, bytes, ref at);

    /// <summary>JSON the database built, copied into the arena; NULL is <c>[]</c>.</summary>
    public static RawJson Raw<TRow>(scoped in TRow row, int column, in ArenaBytes bytes, ref int at) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? new RawJson(new Utf8(EmptyArray, 0, 2)) : new RawJson(Copy(row.GetJson(column), bytes, ref at));

    /// <summary>JSON the database built, copied into the arena, or null.</summary>
    public static RawJson? NullableRaw<TRow>(scoped in TRow row, int column, in ArenaBytes bytes, ref int at) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? null : new RawJson(Copy(row.GetJson(column), bytes, ref at));

    /// <summary>A timestamp column, or null.</summary>
    public static Timestamp? NullableTimestamp<TRow>(scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? null : row.GetTimestamp(column);

    /// <summary>An integer column, or null.</summary>
    public static long? NullableInt64<TRow>(scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? null : row.GetInt64(column);

    /// <summary>A floating-point column, or null.</summary>
    public static double? NullableDouble<TRow>(scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? null : row.GetDouble(column);

    /// <summary>A bool column, or null.</summary>
    public static bool? NullableBoolean<TRow>(scoped in TRow row, int column) where TRow : IDataRow, allows ref struct =>
        row.IsNull(column) ? null : row.GetBoolean(column);
}
