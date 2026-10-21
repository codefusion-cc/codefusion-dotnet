using System;
using System.Globalization;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Core.Tests;

public class TimestampTests
{
    const long MinMicros = -62135596800000000; // 0001-01-01T00:00:00Z
    const long MaxMicros = 253402300799999999; // 9999-12-31T23:59:59.999999Z

    static string Format(Timestamp t)
    {
        Span<byte> b = stackalloc byte[Timestamp.FormattedLength];
        Assert.True(t.TryFormat(b, out int n));
        Assert.Equal(Timestamp.FormattedLength, n);
        return Encoding.ASCII.GetString(b);
    }

    /// <summary>What the spec asks for, by DateTime: UTC, three fractional digits, truncated.</summary>
    static string Expected(long micros) =>
        new DateTime(621355968000000000 + micros * 10, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    [Property(MaxTest = 5000)]
    public bool FormatsAsRfc3339WithMillisecondsTruncated(long seed)
    {
        long micros = MinMicros + (long)((ulong)seed % (ulong)(MaxMicros - MinMicros + 1));
        return Format(new Timestamp(micros)) == Expected(micros);
    }

    [Theory]
    [InlineData(0, "1970-01-01T00:00:00.000Z")]
    [InlineData(999, "1970-01-01T00:00:00.000Z")]
    [InlineData(1000, "1970-01-01T00:00:00.001Z")]
    [InlineData(-1, "1969-12-31T23:59:59.999Z")]
    [InlineData(-1000, "1969-12-31T23:59:59.999Z")]
    [InlineData(-1001, "1969-12-31T23:59:59.998Z")]
    [InlineData(1743508800123456, "2025-04-01T12:00:00.123Z")]
    [InlineData(951782400000000, "2000-02-29T00:00:00.000Z")]
    [InlineData(MinMicros, "0001-01-01T00:00:00.000Z")]
    [InlineData(MaxMicros, "9999-12-31T23:59:59.999Z")]
    public void KnownInstants(long micros, string text) => Assert.Equal(text, Format(new Timestamp(micros)));

    [Theory]
    [InlineData(MinMicros - 1)]
    [InlineData(MaxMicros + 1)]
    [InlineData(long.MinValue)] // how Postgres's -infinity arrives
    [InlineData(long.MaxValue)] // and +infinity
    public void InstantsOutsideYears1To9999CantBeWritten(long micros)
    {
        Span<byte> b = stackalloc byte[Timestamp.FormattedLength];
        Assert.False(new Timestamp(micros).TryFormat(b, out int n));
        Assert.Equal(0, n);
        Assert.False(new Timestamp(micros).TryToDateTime(out _));
    }

    [Fact]
    public void AShortDestinationIsRefused()
    {
        Span<byte> b = stackalloc byte[Timestamp.FormattedLength - 1];
        Assert.False(new Timestamp(0).TryFormat(b, out int n));
        Assert.Equal(0, n);
    }

    [Property(MaxTest = 2000)]
    public bool RoundTripsThroughDateTimeToTheMicrosecond(long seed)
    {
        long micros = MinMicros + (long)((ulong)seed % (ulong)(MaxMicros - MinMicros + 1));
        var t = new Timestamp(micros);
        return t.TryToDateTime(out var utc) && utc.Kind == DateTimeKind.Utc && Timestamp.FromDateTime(utc) == t;
    }

    [Theory]
    [InlineData(621355968000000009, 0)]   // 0.9 µs after 1970: the ticks below a microsecond are dropped
    [InlineData(621355967999999999, -1)]  // 0.1 µs before 1970: floored, not rounded toward zero
    [InlineData(621355967999999990, -1)]
    [InlineData(621355967999999989, -2)]
    public void FromDateTimeFloorsToTheMicrosecond(long ticks, long micros) =>
        Assert.Equal(micros, Timestamp.FromDateTime(new DateTime(ticks, DateTimeKind.Utc)).UnixMicroseconds);

    [Fact]
    public void ALocalDateTimeIsRefused() =>
        Assert.Throws<ArgumentException>(() => Timestamp.FromDateTime(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void OrdersByInstant()
    {
        Timestamp a = new(-5), b = new(7);
        Timestamp same = new(-5);
        Assert.True(a < b && b > a && a <= same && a >= same && b >= a && a != b && a == same);
        Assert.Equal(-1, a.CompareTo(b));
        Assert.False(a < same); // strict
        Assert.False(a > same);
        Assert.Equal(0, a.CompareTo(same));
    }

    [Fact]
    public void FormattingAllocatesNothing()
    {
        Span<byte> b = stackalloc byte[Timestamp.FormattedLength];
        var t = new Timestamp(1743508800123456);
        for (int i = 0; i < 100; i++) t.TryFormat(b, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) t.TryFormat(b, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RawJsonComparesItsBytes()
    {
        var json = new RawJson(new Utf8(Encoding.ASCII.GetBytes("""["a","b"]"""), 0, 9));
        Assert.Equal("""["a","b"]""", json.ToString());
        Assert.True(json == new RawJson(new Utf8(Encoding.ASCII.GetBytes("""x["a","b"]"""), 1, 9)));
        Assert.True(json.Span.SequenceEqual("""["a","b"]"""u8));
        Assert.False(json != new RawJson(new Utf8(Encoding.ASCII.GetBytes("""["a","b"]"""), 0, 9)));
        Assert.True(json != new RawJson(new Utf8(Encoding.ASCII.GetBytes("""["a","c"]"""), 0, 9)));
    }

    [Fact]
    public void ToStringIsTheRfc3339TextOrTheMicrosecondsPastIt()
    {
        Assert.Equal("1970-01-01T00:00:00.000Z", new Timestamp(0).ToString());
        Assert.Equal($"{long.MaxValue} µs", new Timestamp(long.MaxValue).ToString());
    }
}
