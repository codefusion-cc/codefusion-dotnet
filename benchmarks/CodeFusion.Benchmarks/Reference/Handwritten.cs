using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Pooled = CodeFusion.Http.PooledValueTaskMethodBuilder;

namespace CodeFusion.Benchmarks.Reference;

/// <summary>
/// The contest rewrite's (PR #8) hand-built commands and row handlers for the queries DataBenchmarks runs, as its
/// Data/Db.cs, Endpoints/Rows.cs, Articles.cs and Comments.cs have them: each column written from the receive buffer
/// into the response's JSON writer, the reply finished in a pooled async method. Only the response differs: an
/// <see cref="IResponse"/> instead of the server's exchange, so both sides of the comparison write into the same.
/// </summary>
static class Handwritten
{
    public static readonly PgStatement ArticleBySlug = new(Sql.ArticleBySlug, PgType.Int8, PgType.Text);
    public static readonly PgStatement GlobalList = new(Sql.List[(int)Sql.ListKind.Global], PgType.Int8, PgType.Int8, PgType.Int8);
    public static readonly PgStatement Comments = new(Sql.Comments, PgType.Int8, PgType.Text);
    public static readonly PgStatement UpdateUser = new(Sql.UpdateUser, PgType.Int8, PgType.Text, PgType.Text, PgType.Text, PgType.Text, PgType.Text);

    static class Names
    {
        public static readonly JsonEncodedText Article = JsonEncodedText.Encode("article");
        public static readonly JsonEncodedText Articles = JsonEncodedText.Encode("articles");
        public static readonly JsonEncodedText ArticlesCount = JsonEncodedText.Encode("articlesCount");
        public static readonly JsonEncodedText Comments = JsonEncodedText.Encode("comments");
        public static readonly JsonEncodedText Email = JsonEncodedText.Encode("email");
        public static readonly JsonEncodedText Username = JsonEncodedText.Encode("username");
        public static readonly JsonEncodedText Bio = JsonEncodedText.Encode("bio");
        public static readonly JsonEncodedText Image = JsonEncodedText.Encode("image");
        public static readonly JsonEncodedText Following = JsonEncodedText.Encode("following");
        public static readonly JsonEncodedText Slug = JsonEncodedText.Encode("slug");
        public static readonly JsonEncodedText Title = JsonEncodedText.Encode("title");
        public static readonly JsonEncodedText Description = JsonEncodedText.Encode("description");
        public static readonly JsonEncodedText Body = JsonEncodedText.Encode("body");
        public static readonly JsonEncodedText TagList = JsonEncodedText.Encode("tagList");
        public static readonly JsonEncodedText CreatedAt = JsonEncodedText.Encode("createdAt");
        public static readonly JsonEncodedText UpdatedAt = JsonEncodedText.Encode("updatedAt");
        public static readonly JsonEncodedText Favorited = JsonEncodedText.Encode("favorited");
        public static readonly JsonEncodedText FavoritesCount = JsonEncodedText.Encode("favoritesCount");
        public static readonly JsonEncodedText Author = JsonEncodedText.Encode("author");
        public static readonly JsonEncodedText Id = JsonEncodedText.Encode("id");
    }

    // ---------- Endpoints/Rows.cs and Http/JsonFields.cs ----------

    static void WriteText(Utf8JsonWriter w, JsonEncodedText name, in PgRow r, int c) => w.WriteString(name, r.GetBytes(c));

    static void WriteNullableText(Utf8JsonWriter w, JsonEncodedText name, in PgRow r, int c)
    {
        if (r.IsNull(c)) w.WriteNull(name);
        else WriteText(w, name, r, c);
    }

    static void WriteRaw(Utf8JsonWriter w, in PgRow r, int c) => w.WriteRawValue(r.GetBytes(c), skipInputValidation: true);

    const long PgEpochTicks = 630822816000000000;

    static void WriteTimestamp(Utf8JsonWriter w, JsonEncodedText name, in PgRow r, int c)
    {
        Span<byte> b = stackalloc byte[24];
        long ticks = PgEpochTicks + r.GetPgMicroseconds(c) * 10;
        new DateTime(ticks).Deconstruct(out int y, out int mo, out int d);
        long ms = ticks % TimeSpan.TicksPerDay / TimeSpan.TicksPerMillisecond;
        int h = (int)(ms / 3_600_000), mi = (int)(ms / 60_000 % 60), s = (int)(ms / 1000 % 60), f = (int)(ms % 1000);
        b[0] = (byte)('0' + y / 1000); b[1] = (byte)('0' + y / 100 % 10); b[2] = (byte)('0' + y / 10 % 10); b[3] = (byte)('0' + y % 10);
        b[4] = (byte)'-'; b[5] = (byte)('0' + mo / 10); b[6] = (byte)('0' + mo % 10);
        b[7] = (byte)'-'; b[8] = (byte)('0' + d / 10); b[9] = (byte)('0' + d % 10);
        b[10] = (byte)'T'; b[11] = (byte)('0' + h / 10); b[12] = (byte)('0' + h % 10);
        b[13] = (byte)':'; b[14] = (byte)('0' + mi / 10); b[15] = (byte)('0' + mi % 10);
        b[16] = (byte)':'; b[17] = (byte)('0' + s / 10); b[18] = (byte)('0' + s % 10);
        b[19] = (byte)'.'; b[20] = (byte)('0' + f / 100); b[21] = (byte)('0' + f / 10 % 10); b[22] = (byte)('0' + f % 10);
        b[23] = (byte)'Z';
        w.WriteString(name, b);
    }

    static void WritePerson(Utf8JsonWriter w, in PgRow r, int c)
    {
        WriteText(w, Names.Username, r, c);
        WriteNullableText(w, Names.Bio, r, c + 1);
        WriteNullableText(w, Names.Image, r, c + 2);
    }

    // ---------- Endpoints/Articles.cs ----------

    static void Write(Utf8JsonWriter w, in PgRow r, int c, bool body)
    {
        w.WriteStartObject();
        WriteText(w, Names.Slug, r, c++);
        WriteText(w, Names.Title, r, c++);
        WriteText(w, Names.Description, r, c++);
        if (body) WriteText(w, Names.Body, r, c++);
        WriteTimestamp(w, Names.CreatedAt, r, c++);
        WriteTimestamp(w, Names.UpdatedAt, r, c++);
        w.WritePropertyName(Names.TagList);
        WriteRaw(w, r, c++);
        w.WriteNumber(Names.FavoritesCount, r.GetInt64(c++));
        w.WriteBoolean(Names.Favorited, r.GetBoolean(c++));
        w.WriteStartObject(Names.Author);
        WritePerson(w, r, c);
        w.WriteBoolean(Names.Following, r.GetBoolean(c + 3));
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static readonly PgRowHandler WriteSingle = static (in PgRow r, object? x) =>
    {
        var w = ((IResponse)x!).Json;
        w.WriteStartObject();
        w.WritePropertyName(Names.Article);
        Write(w, r, 0, true);
        w.WriteEndObject();
        return 0;
    };

    static readonly PgRowHandler WritePageRow = static (in PgRow r, object? x) =>
    {
        if (!r.IsNull(0)) Write(((IResponse)x!).Json, r, 0, false);
        return r.GetInt64(r.FieldCount - 1);
    };

    static void Failed(IResponse x, in PgResult r)
    {
        x.Reset();
        x.Status = r.Status is PgStatus.Busy or PgStatus.Canceled ? 503 : 500;
        x.Body.Write("""{"errors":{"body":["failed"]}}"""u8);
    }

    static void NotFound(IResponse x)
    {
        x.Reset();
        x.Status = 404;
        x.Body.Write("""{"errors":{"body":["not found"]}}"""u8);
    }

    static void Send(IResponse x, int status)
    {
        x.Json.Flush();
        x.Status = status;
    }

    public static ValueTask Get(PgReader reader, IResponse x, ReadOnlySpan<byte> slug, long viewer) =>
        Answer(x, reader.Command(ArticleBySlug).Int8(viewer).Text(slug).Run(WriteSingle, x, x.Aborted));

    [AsyncMethodBuilder(typeof(Pooled))]
    static async ValueTask Answer(IResponse x, ValueTask<PgResult> pending)
    {
        var r = await pending;
        if (!r.IsOk) Failed(x, r);
        else if (r.Rows == 0) NotFound(x);
        else Send(x, 200);
    }

    public static ValueTask List(PgReader reader, IResponse x, long viewer, long limit, long offset)
    {
        var w = x.Json;
        w.WriteStartObject();
        w.WriteStartArray(Names.Articles);
        return EndPage(x, reader.Command(GlobalList).Int8(viewer).Int8(limit).Int8(offset).Run(WritePageRow, x, x.Aborted));
    }

    [AsyncMethodBuilder(typeof(Pooled))]
    static async ValueTask EndPage(IResponse x, ValueTask<PgResult> page)
    {
        var r = await page;
        if (!r.IsOk)
        {
            Failed(x, r);
            return;
        }
        var w = x.Json;
        w.WriteEndArray();
        w.WriteNumber(Names.ArticlesCount, r.Value);
        w.WriteEndObject();
        Send(x, 200);
    }

    // ---------- Endpoints/Comments.cs ----------

    static void WriteComment(Utf8JsonWriter w, in PgRow r)
    {
        w.WriteStartObject();
        w.WriteNumber(Names.Id, r.GetInt64(0));
        WriteTimestamp(w, Names.CreatedAt, r, 1);
        WriteTimestamp(w, Names.UpdatedAt, r, 2);
        WriteText(w, Names.Body, r, 3);
        w.WriteStartObject(Names.Author);
        WritePerson(w, r, 4);
        w.WriteBoolean(Names.Following, r.GetBoolean(7));
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static readonly PgRowHandler WriteListed = static (in PgRow r, object? x) =>
    {
        if (!r.IsNull(0)) WriteComment(((IResponse)x!).Json, r);
        return 0;
    };

    public static ValueTask CommentsOf(PgReader reader, IResponse x, ReadOnlySpan<byte> slug, long viewer)
    {
        var w = x.Json;
        w.WriteStartObject();
        w.WriteStartArray(Names.Comments);
        return Listed(x, reader.Command(Comments).Int8(viewer).Text(slug).Run(WriteListed, x, x.Aborted));
    }

    [AsyncMethodBuilder(typeof(Pooled))]
    static async ValueTask Listed(IResponse x, ValueTask<PgResult> pending)
    {
        var r = await pending;
        if (!r.IsOk)
        {
            Failed(x, r);
            return;
        }
        if (r.Rows == 0)
        {
            NotFound(x);
            return;
        }
        var w = x.Json;
        w.WriteEndArray();
        w.WriteEndObject();
        Send(x, 200);
    }

    // ---------- Endpoints/Users.cs, as the compiled query replies (the user's row, without a token) ----------

    static readonly PgRowHandler WriteUser = static (in PgRow r, object? x) =>
    {
        var w = ((IResponse)x!).Json;
        w.WriteStartObject();
        WriteText(w, Names.Username, r, 0);
        WriteText(w, Names.Email, r, 1);
        WriteNullableText(w, Names.Bio, r, 2);
        WriteNullableText(w, Names.Image, r, 3);
        w.WriteEndObject();
        return 0;
    };

    /// <summary>An update that sends nothing new: every field NULL keeps its column.</summary>
    public static ValueTask Touch(PgWriter writer, IResponse x, long viewer) =>
        Answer(x, writer.Command(UpdateUser).Int8(viewer).Null().Null().Null().Null().Null().Run(WriteUser, x, x.Aborted));
}
