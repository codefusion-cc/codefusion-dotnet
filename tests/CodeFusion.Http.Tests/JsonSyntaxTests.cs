using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// The body binders' exception-free syntax check against <c>Utf8JsonReader</c> with the binders' options: the same
/// verdict on every input, so a malformed body is told apart without a JsonException, and no body the reader takes is
/// refused.
/// </summary>
public sealed class JsonSyntaxTests
{
    static readonly JsonReaderOptions Options = new() { MaxDepth = 64 };

    internal static bool ReaderTakes(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json, Options);
            bool any = false;
            while (r.Read()) any = true;
            return any;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static void Same(byte[] json)
    {
        SameTokens(json);
        bool reader = ReaderTakes(json);
        bool ours = JsonSyntax.IsValid(json, 64);
        Assert.True(reader == ours, $"reader {(reader ? "takes" : "refuses")}, the check {(ours ? "takes" : "refuses")}: {Encoding.UTF8.GetString(json)}");
    }

    /// <summary>One token as both readers give it: its type, start, bytes, escape, and a number's three readings.</summary>
    internal readonly record struct Token(JsonTokenType Type, long Start, string Bytes, bool Escaped, string Number);

    static string Numbers(bool i64, long l, bool i32, int i, bool dbl, double d) =>
        $"{(i64 ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")} {(i32 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")} {(dbl ? BitConverter.DoubleToInt64Bits(d).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}";

    /// <summary>The cursor gives the reader's tokens one by one, up to where either stops, and stops malformed exactly
    /// where the reader throws or reads nothing.</summary>
    static void SameTokens(byte[] json)
    {
        var ours = new System.Collections.Generic.List<Token>();
        var c = new JsonCursor(json, 64);
        while (c.Read())
        {
            string n = c.TokenType != JsonTokenType.Number ? ""
                : Numbers(c.TryGetInt64(out long l), l, c.TryGetInt32(out int i), i, c.TryGetDouble(out double d), d);
            ours.Add(new Token(c.TokenType, c.TokenStart, Convert.ToHexString(c.ValueSpan), c.ValueIsEscaped, n));
        }
        var theirs = new System.Collections.Generic.List<Token>();
        bool threw = false;
        try
        {
            var r = new Utf8JsonReader(json, Options);
            while (r.Read())
            {
                string n = r.TokenType != JsonTokenType.Number ? ""
                    : Numbers(r.TryGetInt64(out long l), l, r.TryGetInt32(out int i), i, r.TryGetDouble(out double d), d);
                theirs.Add(new Token(r.TokenType, r.TokenStartIndex, Convert.ToHexString(r.ValueSpan), r.ValueIsEscaped, n));
            }
        }
        catch (JsonException)
        {
            threw = true;
        }
        string show = Encoding.UTF8.GetString(json);
        Assert.True((!threw && theirs.Count > 0) == !c.Malformed, $"reader {(threw ? "threw" : "read " + theirs.Count)}, the cursor {(c.Malformed ? "malformed" : "fine")}: {show}");
        int both = Math.Min(ours.Count, theirs.Count);
        for (int k = 0; k < both; k++) Assert.True(ours[k] == theirs[k], $"token {k} differs: ours {ours[k]}, the reader's {theirs[k]}: {show}");
        if (!c.Malformed) Assert.Equal(theirs.Count, ours.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData(" { } ")]
    [InlineData("{\"a\":1}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("[,1]")]
    [InlineData("{,}")]
    [InlineData("{\"a\"}")]
    [InlineData("{\"a\":}")]
    [InlineData("{\"a\" 1}")]
    [InlineData("{1:1}")]
    [InlineData("{\"a\":1 \"b\":2}")]
    [InlineData("[1 2]")]
    [InlineData("[1}")]
    [InlineData("{]")]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("01")]
    [InlineData("-")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1.5e")]
    [InlineData("1.5e+")]
    [InlineData("1.5E-7")]
    [InlineData("+1")]
    [InlineData("1x")]
    [InlineData("true")]
    [InlineData("tru")]
    [InlineData("truex")]
    [InlineData("nul")]
    [InlineData("null null")]
    [InlineData("\"a\"")]
    [InlineData("\"a")]
    [InlineData("\"\\u00e9\"")]
    [InlineData("\"\\u00g9\"")]
    [InlineData("\"\\u00e\"")]
    [InlineData("\"\\x\"")]
    [InlineData("\"\\\"")]
    [InlineData("\"a\tb\"")]
    [InlineData("\"a\u007fb\"")]
    [InlineData("\"\\ud800\"")]
    [InlineData("'a'")]
    [InlineData("// x\n1")]
    [InlineData("/* x */ 1")]
    [InlineData("1 // x")]
    [InlineData("\u00a01")]
    [InlineData("\f1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("[1e999999]")]
    public void TheCheckAgreesWithTheReader(string json) => Same(Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public void TheDepthLimitIsTheReaders(int depth)
    {
        Same(Encoding.ASCII.GetBytes(new string('[', depth) + new string(']', depth)));
        Same(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth)));
        Same(Encoding.ASCII.GetBytes(new string('[', depth))); // cut short
    }

    [Fact]
    public void RawBytesThatArentUtf8AreTheReadersToRefuse()
    {
        Same([(byte)'"', 0xFF, (byte)'"']);
        Same([(byte)'"', 0xC3, (byte)'"']);
        Same([0xFF]);
        Same([0xEF, 0xBB, 0xBF, (byte)'1']); // a BOM the binder didn't skip
    }

    static readonly string[] Pieces =
    [
        "{", "}", "[", "]", ",", ":", " ", "\n", "\t", "\"a\"", "\"\\\"\"", "\"\\u00e9\"", "\"\\u12\"", "\"é\"", "1", "-1", "0", "01", "1.5",
        "2e10", "-0", "-0.0", "9223372036854775807", "9223372036854775808", "-9223372036854775809", "2147483647", "2147483648", "1e400", "-1e-400", "0.1e1", "123456789012345678901234567890", "1.7976931348623157e308", "4.9e-324", "1.", "-", "true", "false", "null", "tru", "x", "\"", "\\", "/", "\u0001", "\"k\":", "1e", "e5", ".", "+",
    ];

    static Gen<byte[]> Inputs()
    {
        Gen<string> Value(int depth) => depth == 0
            ? Gen.Elements("1", "-2.5e3", "true", "null", "\"s\"", "\"\\n\\u00e9\"", "[]", "{}")
            : Gen.OneOf(
                Value(0),
                Value(depth - 1).ListOf().Select(vs => "[" + string.Join(",", vs) + "]"),
                (from k in Gen.Elements("\"a\"", "\"b\"", "\"\\u0061\"") from v in Value(depth - 1) select k + ":" + v).ListOf()
                    .Select(ms => "{" + string.Join(",", ms) + "}"));
        var pieces = Gen.Elements(Pieces).ListOf().Select(ps => string.Concat(ps));
        var text = Gen.Frequency((3, Value(3)), (2, pieces));
        return from t in text
               from mutation in Gen.Choose(0, 9)
               from at in Gen.Choose(0, Math.Max(0, t.Length - 1))
               from piece in Gen.Elements(Pieces)
               select Encoding.UTF8.GetBytes(mutation switch
               {
                   0 => t[..at],
                   1 => t.Length == 0 ? t : t.Remove(at, 1),
                   2 => t.Insert(at, piece),
                   3 => " " + t + " ",
                   _ => t,
               });
    }

    [Property(MaxTest = 20000)]
    public Property EveryInputGetsTheReadersVerdict() => Prop.ForAll(Inputs().ToArbitrary(), json =>
    {
        Same(json);
        return true;
    });

    [Property(MaxTest = 5000)]
    public Property ArbitraryBytesToo() => Prop.ForAll(ArbMap.Default.ArbFor<byte[]>(), json =>
    {
        Same(json ?? []);
        return true;
    });
}
