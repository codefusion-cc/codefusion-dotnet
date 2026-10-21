using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using CodeFusion.Data.Generator;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Npgsql;
using NpgsqlTypes;
using Xunit;

#pragma warning disable xUnit1031 // properties run synchronously

namespace CodeFusion.Data.Tests;

/// <summary>
/// Property tests. The translator: random predicates over nullable and non-nullable columns and parameters, compiled
/// by the generator and run by Postgres, return exactly the rows C# itself would pick (lifted operators: a comparison
/// with a null is false, == of two nulls true). The encoders: whatever the values, the JSON a row is written as is
/// what System.Text.Json writes for them, and an awaited row's text is its bytes, each in its own place in the arena.
/// </summary>
public sealed class PropertyTests
{
    // ---------- the translator against C# ----------

    public sealed record Person(long Id, string Name, string? Nick, int Age, double? Score, bool Active, long? TeamId);

    static readonly Person[] Rows = Enumerable.Range(1, 30).Select(i => new Person(
        i,
        ((char)('a' + i % 6)).ToString() + (i % 4 == 0 ? "é" : ""),
        (i % 5) switch { 0 => null, 1 => "a", 2 => "b", 3 => "é", _ => ((char)('a' + i % 6)).ToString() },
        i * 7 % 50,
        (i % 4) switch { 0 => null, 1 => 0.0, 2 => 1.5, _ => -2.5 },
        i % 3 != 0,
        (i % 3) switch { 0 => null, 1 => 1, _ => 2 })).ToArray();

    static readonly Lazy<Task> PropSchema = new(async () =>
    {
        await using var db = TestDb.Admin();
        var b = new StringBuilder("DROP SCHEMA IF EXISTS cfdata_prop CASCADE; CREATE SCHEMA cfdata_prop; SET search_path = cfdata_prop; " +
            "CREATE TABLE people (id bigint PRIMARY KEY, name text NOT NULL, nick text, age int NOT NULL, score double precision, active boolean NOT NULL, team_id bigint); ");
        foreach (var r in Rows)
            b.Append(CultureInfo.InvariantCulture, $"INSERT INTO people VALUES ({r.Id}, '{r.Name}', {(r.Nick is null ? "NULL" : "'" + r.Nick + "'")}, {r.Age}, " +
                $"{(r.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : "NULL")}, {(r.Active ? "true" : "false")}, {(r.TeamId?.ToString(CultureInfo.InvariantCulture) ?? "NULL")}); ");
        await db.Execute(b.ToString());
    });

    /// <summary>A parameter: its C# type, its value, and its Npgsql type.</summary>
    public sealed record Prm(string Type, object? Value, NpgsqlDbType Db);

    /// <summary>A predicate over a row, as C# text and as C#'s own semantics.</summary>
    public abstract record Pred
    {
        public abstract string Cs(List<Prm> ps);
        public abstract bool Eval(Person p, List<Prm> ps, ref int k);
    }

    public abstract record Num
    {
        public abstract string Cs(List<Prm> ps);
        public abstract long Eval(Person p, List<Prm> ps, ref int k);
    }

    public sealed record Column(string Name) : Num
    {
        public override string Cs(List<Prm> ps) => "p." + Name;
        public override long Eval(Person p, List<Prm> ps, ref int k) => Name == "Age" ? p.Age : p.Id;
    }

    public sealed record Literal(int N) : Num
    {
        public override string Cs(List<Prm> ps) => N.ToString(CultureInfo.InvariantCulture);
        public override long Eval(Person p, List<Prm> ps, ref int k) => N;
    }

    public sealed record NumParam(int N) : Num
    {
        public override string Cs(List<Prm> ps)
        {
            ps.Add(new Prm("int", N, NpgsqlDbType.Integer));
            return "x" + (ps.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        public override long Eval(Person p, List<Prm> ps, ref int k) => (int)ps[k++].Value!;
    }

    public sealed record Sum(Num A, Num B) : Num
    {
        public override string Cs(List<Prm> ps) => $"({A.Cs(ps)} + {B.Cs(ps)})";
        public override long Eval(Person p, List<Prm> ps, ref int k) => A.Eval(p, ps, ref k) + B.Eval(p, ps, ref k);
    }

    static bool Compare(string op, long a, long b) => op switch { "==" => a == b, "!=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, _ => a >= b };

    public sealed record Cmp(string Op, Num A, Num B) : Pred
    {
        public override string Cs(List<Prm> ps) => $"({A.Cs(ps)} {Op} {B.Cs(ps)})";
        public override bool Eval(Person p, List<Prm> ps, ref int k) => Compare(Op, A.Eval(p, ps, ref k), B.Eval(p, ps, ref k));
    }

    /// <summary>p.Score op v, v a nullable parameter: C#'s lifted comparison.</summary>
    public sealed record ScoreCmp(string Op, double? V) : Pred
    {
        public override string Cs(List<Prm> ps)
        {
            ps.Add(new Prm("double?", V, NpgsqlDbType.Double));
            return $"(p.Score {Op} x{ps.Count - 1})";
        }

        public override bool Eval(Person p, List<Prm> ps, ref int k)
        {
            var v = (double?)ps[k++].Value;
            return Op switch
            {
                "==" => p.Score == v,
                "!=" => p.Score != v,
                "<" => p.Score < v,
                ">" => p.Score > v,
                _ => p.Score >= v,
            };
        }
    }

    /// <summary>A text column against a parameter, nullable or not: C#'s lifted ==.</summary>
    public sealed record TextEq(string Col, bool Equal, string? V, bool NullableParam) : Pred
    {
        public override string Cs(List<Prm> ps)
        {
            ps.Add(new Prm(NullableParam ? "Utf8?" : "Utf8", V, NpgsqlDbType.Text));
            string left = Col == "Both" ? "(p.Nick ?? p.Name)" : "p." + Col;
            return $"({left} {(Equal ? "==" : "!=")} x{ps.Count - 1})";
        }

        public override bool Eval(Person p, List<Prm> ps, ref int k)
        {
            var v = (string?)ps[k++].Value;
            string? left = Col switch { "Name" => p.Name, "Nick" => p.Nick, _ => p.Nick ?? p.Name };
            bool same = string.Equals(left, v, StringComparison.Ordinal);
            return Equal ? same : !same;
        }
    }

    public sealed record TeamEq(bool Equal, long? V) : Pred
    {
        public override string Cs(List<Prm> ps)
        {
            ps.Add(new Prm("long?", V, NpgsqlDbType.Bigint));
            return $"(p.TeamId {(Equal ? "==" : "!=")} x{ps.Count - 1})";
        }

        public override bool Eval(Person p, List<Prm> ps, ref int k)
        {
            var v = (long?)ps[k++].Value;
            return Equal ? p.TeamId == v : p.TeamId != v;
        }
    }

    public sealed record IsNull(string Col, bool Negated) : Pred
    {
        public override string Cs(List<Prm> ps) => Col switch
        {
            "Score" => Negated ? "p.Score.HasValue" : "(p.Score is null)",
            "TeamId" => Negated ? "(p.TeamId != null)" : "(p.TeamId == null)",
            _ => Negated ? "(p.Nick is not null)" : "(p.Nick == null)",
        };

        public override bool Eval(Person p, List<Prm> ps, ref int k) =>
            (Col switch { "Score" => p.Score is null, "TeamId" => p.TeamId is null, _ => p.Nick is null }) != Negated;
    }

    public sealed record Active : Pred
    {
        public override string Cs(List<Prm> ps) => "p.Active";
        public override bool Eval(Person p, List<Prm> ps, ref int k) => p.Active;
    }

    public sealed record Negation(Pred X) : Pred
    {
        public override string Cs(List<Prm> ps) => $"!{X.Cs(ps)}";
        public override bool Eval(Person p, List<Prm> ps, ref int k) => !X.Eval(p, ps, ref k);
    }

    public sealed record Logic(bool And, Pred A, Pred B) : Pred
    {
        public override string Cs(List<Prm> ps) => $"({A.Cs(ps)} {(And ? "&&" : "||")} {B.Cs(ps)})";

        public override bool Eval(Person p, List<Prm> ps, ref int k)
        {
            // both sides read their parameters, as the C# text numbers them, whatever the short circuit
            bool a = A.Eval(p, ps, ref k), b = B.Eval(p, ps, ref k);
            return And ? a && b : a || b;
        }
    }

    static Gen<Num> NumGen(int depth)
    {
        var leaf = Gen.OneOf(
            Gen.Elements("Age", "Id").Select(c => (Num)new Column(c)),
            Gen.Choose(-5, 55).Select(n => (Num)new Literal(n)),
            Gen.Choose(-5, 55).Select(n => (Num)new NumParam(n)));
        if (depth <= 0) return leaf;
        return Gen.Frequency((3, leaf), (1, from a in NumGen(depth - 1) from b in NumGen(depth - 1) select (Num)new Sum(a, b)));
    }

    static readonly string?[] Texts = [null, "a", "b", "c", "é", "aé", "z"];

    static Gen<Pred> PredGen(int depth)
    {
        var leaf = Gen.OneOf(
            from op in Gen.Elements("==", "!=", "<", "<=", ">", ">=") from a in NumGen(1) from b in NumGen(1) select (Pred)new Cmp(op, a, b),
            from op in Gen.Elements("==", "!=", "<", ">", ">=") from v in Gen.Elements<double?>(null, 0.0, 1.5, -2.5, 1.0) select (Pred)new ScoreCmp(op, v),
            from col in Gen.Elements("Name", "Nick", "Both") from eq in Gen.Elements(true, false) from nullable in Gen.Elements(true, false)
            from v in Gen.Elements(Texts) where nullable || v is not null select (Pred)new TextEq(col, eq, v, nullable),
            from eq in Gen.Elements(true, false) from v in Gen.Elements<long?>(null, 1, 2, 3) select (Pred)new TeamEq(eq, v),
            from col in Gen.Elements("Score", "TeamId", "Nick") from neg in Gen.Elements(true, false) select (Pred)new IsNull(col, neg),
            Gen.Constant((Pred)new Active()));
        if (depth <= 0) return leaf;
        return Gen.Frequency(
            (2, leaf),
            (1, PredGen(depth - 1).Select(x => (Pred)new Negation(x))),
            (2, from and in Gen.Elements(true, false) from a in PredGen(depth - 1) from b in PredGen(depth - 1) select (Pred)new Logic(and, a, b)));
    }

    public static Arbitrary<Pred[]> Predicates() => Gen.ArrayOf(PredGen(3), 12).ToArbitrary();

    [Property(MaxTest = 8, Arbitrary = [typeof(PropertyTests)], SkipUnless = nameof(TestDb.Set), SkipType = typeof(TestDb), Skip = "CODEFUSION_TEST_DB is not set")]
    public void APredicateSelectsInPostgresWhatItSelectsInCSharp(Pred[] predicates)
    {
        TestDb.Require();
        PropSchema.Value.GetAwaiter().GetResult();
        var methods = new StringBuilder();
        var parameters = new List<List<Prm>>();
        for (int i = 0; i < predicates.Length; i++)
        {
            var ps = new List<Prm>();
            string cs = predicates[i].Cs(ps);
            parameters.Add(ps);
            string args = string.Join(", ", ps.Select((p, j) => $"{p.Type} x{j}"));
            methods.Append(CultureInfo.InvariantCulture, $"    public Many<long> Q{i}({args}) => People.Where(p => {cs}).OrderBy(p => p.Id).Select(p => p.Id).Many();\n");
        }
        var result = GeneratorHarness.Run(GeneratorHarness.Context(methods.ToString()));
        Assert.True(result.Generator.IsEmpty && result.Errors.IsEmpty, string.Join("\n", result.Generator.Concat(result.Errors)) + "\n" + methods);
        using var db = TestDb.Admin("cfdata_prop");
        using var conn = db.OpenConnection();
        for (int i = 0; i < predicates.Length; i++)
        {
            string sql = result.Sql("Q" + i);
            var bound = result.Bound("Q" + i);
            using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var code in bound)
            {
                // a parameter is a query method's parameter, or C# arithmetic over them (x0 + 3), which C# computes
                if (System.Text.RegularExpressions.Regex.IsMatch(code, @"^x\d+$"))
                {
                    var p = parameters[i][int.Parse(code[1..], CultureInfo.InvariantCulture)];
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = p.Db, Value = p.Value ?? DBNull.Value });
                }
                else cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = Arithmetic(code, parameters[i]) });
            }
            var got = new List<long>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) got.Add(reader.GetInt64(0));
            var want = Rows.Where(r =>
            {
                int k = 0;
                return predicates[i].Eval(r, parameters[i], ref k);
            }).Select(r => r.Id).ToList();
            Assert.True(want.SequenceEqual(got), $"{predicates[i].Cs([])}\nSQL: {sql}\nC# picks {string.Join(",", want)}, Postgres {string.Join(",", got)}");
        }
    }

    /// <summary>The value of C# integer arithmetic over the parameters, as the generated binder computes it.</summary>
    static int Arithmetic(string code, List<Prm> ps)
    {
        int at = 0;
        int Term()
        {
            while (code[at] == ' ') at++;
            if (code[at] == '(')
            {
                at++;
                int left = Term();
                while (code[at] == ' ') at++;
                char op = code[at++];
                int right = Term();
                while (code[at] == ' ') at++;
                at++; // ')'
                return op == '+' ? left + right : left - right;
            }
            int start = at;
            if (code[at] == 'x')
            {
                at++;
                while (at < code.Length && char.IsDigit(code[at])) at++;
                return (int)ps[int.Parse(code[(start + 1)..at], CultureInfo.InvariantCulture)].Value!;
            }
            if (code[at] == '-') at++;
            while (at < code.Length && char.IsDigit(code[at])) at++;
            return int.Parse(code[start..at], CultureInfo.InvariantCulture);
        }
        return Term();
    }

    // ---------- the encoders ----------

    /// <summary>A row of values, as a provider's row would hand them over.</summary>
    readonly struct Row(object?[] values) : IDataRow
    {
        public int FieldCount => values.Length;
        public bool IsNull(int c) => values[c] is null;
        public ReadOnlySpan<byte> GetBytes(int c) => values[c] is byte[] b ? b : Encoding.UTF8.GetBytes((string)values[c]!);
        public ReadOnlySpan<byte> GetJson(int c) => GetBytes(c);
        public long GetInt64(int c) => (long)values[c]!;
        public bool GetBoolean(int c) => (bool)values[c]!;
        public double GetDouble(int c) => (double)values[c]!;
        public Timestamp GetTimestamp(int c) => (Timestamp)values[c]!;
    }

    static readonly JsonWriterOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, Relaxed))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static readonly JsonEncodedText Name = JsonEncodedText.Encode("v");

    /// <summary>Text that is valid UTF-16, as any text from Postgres is valid UTF-8.</summary>
    static string Valid(string? s) => s is null ? "" : string.Concat(s.Where(c => !char.IsSurrogate(c)));

    [Property(MaxTest = 500)]
    public bool TextIsWrittenAsSystemTextJsonWritesTheString(string? text, bool isNull)
    {
        string? s = isNull ? null : Valid(text);
        var row = new Row([s]);
        string got = Write(w => RowJson.NullableText(w, Name, row, 0));
        string want = Write(w => { if (s is null) w.WriteNull(Name); else w.WriteString(Name, s); });
        return got == want;
    }

    [Property(MaxTest = 500)]
    public bool ATimestampIsItsRfc3339TextTruncatedToMilliseconds(long micros)
    {
        // every instant from year 1 to 9999
        const long Min = -62135596800000000, Max = 253402300799999999;
        long us = Min + (long)((ulong)micros % (ulong)(Max - Min + 1));
        var row = new Row([new Timestamp(us)]);
        string got = Write(w => RowJson.Timestamp(w, Name, row, 0));
        var t = DateTime.UnixEpoch.AddTicks(us * 10);
        string want = "{\"v\":\"" + t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture) + "\"}";
        return got == want;
    }

    [Fact]
    public void ATimestampOutsideRfc3339IsRefused()
    {
        var row = new Row([new Timestamp(long.MaxValue)]);
        Assert.Throws<InvalidOperationException>(() => Write(w => RowJson.Timestamp(w, Name, row, 0)));
    }

    [Property(MaxTest = 300)]
    public bool NumbersAndBoolsAreWrittenAsTheirValues(long n, double d, bool b, bool nulls)
    {
        if (!double.IsFinite(d)) d = 0;
        var row = new Row([nulls ? null : n, nulls ? null : d, nulls ? null : b]);
        string got = Write(w =>
        {
            RowJson.NullableInt64(w, Name, row, 0);
            RowJson.NullableDouble(w, Name, row, 1);
            RowJson.NullableBoolean(w, Name, row, 2);
        });
        string want = Write(w =>
        {
            if (nulls)
            {
                w.WriteNull(Name);
                w.WriteNull(Name);
                w.WriteNull(Name);
                return;
            }
            w.WriteNumber(Name, n);
            w.WriteNumber(Name, d);
            w.WriteBoolean(Name, b);
        });
        return got == want;
    }

    [Property(MaxTest = 200)]
    public bool RawJsonIsCopiedAsItIs(int[]? numbers, bool isNull)
    {
        string json = isNull ? "" : "[" + string.Join(",", (numbers ?? []).Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]";
        var row = new Row([isNull ? null : Encoding.UTF8.GetBytes(json)]);
        return Write(w => RowJson.Raw(w, Name, row, 0)) == "{\"v\":" + (isNull ? "[]" : json) + "}";
    }

    [Property(MaxTest = 300)]
    public bool AnAwaitedRowsTextIsItsBytesEachInItsOwnPlace(string?[]? texts)
    {
        texts ??= [];
        var values = texts.Select(t => t is null ? null : (object)Encoding.UTF8.GetBytes(Valid(t))).ToArray();
        var row = new Row(values);
        var arena = new FakeArena();
        int length = 0;
        for (int c = 0; c < values.Length; c++) length += RowCopy.Length(row, c);
        var bytes = length == 0 ? default : arena.Allocate(length);
        int at = 0;
        var copies = new Utf8?[values.Length];
        for (int c = 0; c < values.Length; c++) copies[c] = RowCopy.NullableText(row, c, bytes, ref at);
        if (at != length || arena.Used != length) return false;
        for (int c = 0; c < values.Length; c++)
        {
            if (values[c] is null != copies[c] is null) return false;
            if (values[c] is byte[] b && !copies[c]!.Value.Span.SequenceEqual(b)) return false;
        }
        // overwriting the arena changes every non-empty copy: they all live there
        arena.Scribble();
        for (int c = 0; c < values.Length; c++)
            if (copies[c] is { Length: > 0 } u && u.Span.IndexOfAnyExcept((byte)'#') >= 0) return false;
        return true;
    }

    // ---------- names ----------

    public static Arbitrary<string> Identifiers() =>
        (from n in Gen.Choose(0, 8)
         from parts in Gen.ArrayOf(Gen.Elements("A", "b", "C", "d", "E", "X", "Y", "1", "_", "ID", "Url", "x", " "), n)
         select string.Concat(parts)).ToArbitrary();

    [Property(MaxTest = 1000, Arbitrary = [typeof(PropertyTests)])]
    public bool CamelCaseIsSystemTextJsons(string name) => Names.Camel(name) == JsonNamingPolicy.CamelCase.ConvertName(name);

    [Property(MaxTest = 500)]
    public bool SnakeCaseJoinsTheWordsOfAPascalCaseName(NonEmptyArray<NonEmptyString> words)
    {
        // words of two letters or more: "AA" is one word to EF too
        var parts = words.Get.Select(w => new string(w.Get.Where(char.IsAsciiLetterLower).ToArray())).Where(w => w.Length > 1).ToArray();
        if (parts.Length == 0) return true;
        string pascal = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return Names.Snake(pascal) == string.Join("_", parts) && Names.Snake(Names.Snake(pascal)) == Names.Snake(pascal);
    }

    [Theory]
    [InlineData("Id", "id")]
    [InlineData("AuthorId", "author_id")]
    [InlineData("ArticleTags", "article_tags")]
    [InlineData("HTMLParser", "html_parser")]
    [InlineData("UserID", "user_id")]
    [InlineData("Utf8Value", "utf8_value")]
    [InlineData("already_snake", "already_snake")]
    public void SnakeCaseAsEfNamingConventionsWritesIt(string name, string snake) => Assert.Equal(snake, Names.Snake(name));
}
