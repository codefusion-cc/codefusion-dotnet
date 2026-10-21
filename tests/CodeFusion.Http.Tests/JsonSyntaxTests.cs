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
        bool reader = ReaderTakes(json);
        bool ours = JsonSyntax.IsValid(json, 64);
        Assert.True(reader == ours, $"reader {(reader ? "takes" : "refuses")}, the check {(ours ? "takes" : "refuses")}: {Encoding.UTF8.GetString(json)}");
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
        "2e10", "1.", "-", "true", "false", "null", "tru", "x", "\"", "\\", "/", "\u0001", "\"k\":", "1e", "e5", ".", "+",
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
