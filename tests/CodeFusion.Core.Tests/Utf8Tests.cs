using System;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Core.Tests;

public class Utf8Tests
{
    static Utf8 Of(string s, int padBefore = 0, int padAfter = 0)
    {
        byte[] text = Encoding.UTF8.GetBytes(s);
        var buffer = new byte[padBefore + text.Length + padAfter];
        buffer.AsSpan().Fill((byte)'#');
        text.CopyTo(buffer, padBefore);
        return new Utf8(buffer, padBefore, text.Length);
    }

    [Property(MaxTest = 500)]
    public bool EqualTextIsEqualWhereverItSitsInItsBuffer(NonNull<string> s, byte before, byte after)
    {
        Utf8 a = Of(s.Get, before % 7, after % 5), b = Of(s.Get, after % 3, before % 11);
        return a == b && a.Equals(b) && a.GetHashCode() == b.GetHashCode() && a.Equals(Encoding.UTF8.GetBytes(s.Get))
               && a.Length == Encoding.UTF8.GetByteCount(s.Get) && a.ToString() == Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s.Get));
    }

    [Property(MaxTest = 500)]
    public bool DifferentTextIsNotEqual(NonNull<string> s, NonNull<string> t) =>
        s.Get == t.Get || (Of(s.Get) != Of(t.Get) && !Of(s.Get).Equals(Encoding.UTF8.GetBytes(t.Get)));

    [Fact]
    public void ComparesWithUtf8Literals()
    {
        Assert.True(Of("feed") == "feed"u8);
        Assert.False(Of("feed") != "feed"u8);
        Assert.True(Of("feeds") != "feed"u8);
        Assert.True(Of("Feed") != "feed"u8); // bytes, not case-insensitive
        Assert.True(Of("é") == "é"u8);
    }

    [Fact]
    public void EmptyAndDefaultAreTheEmptyText()
    {
        Assert.True(Utf8.Empty.IsEmpty);
        Assert.True(default(Utf8).IsEmpty);
        Assert.Equal(0, Utf8.Empty.Length);
        Assert.True(Utf8.Empty == ""u8);
        Assert.True(Utf8.Empty == Of(""));
        Assert.True(Of("", 3, 3) == Utf8.Empty);
        Assert.Equal("", Utf8.Empty.ToString());
        Assert.True(Utf8.Empty.Span.IsEmpty);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(4, 6)]
    public void TheWholeBufferAndItsEdgesAreAccepted(int start, int length)
    {
        var buffer = Encoding.ASCII.GetBytes("0123456789");
        var text = new Utf8(buffer, start, length);
        Assert.Equal(buffer.AsSpan(start, length).ToArray(), text.Span.ToArray());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 11)]
    [InlineData(11, 0)]
    [InlineData(10, 1)]
    [InlineData(5, -1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void TextOutsideTheBufferIsRefused(int start, int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Utf8(new byte[10], start, length));

    [Fact]
    public void DifferentTextHashesDifferently()
    {
        var hashes = new System.Collections.Generic.HashSet<int>();
        foreach (string s in (string[])["", "a", "b", "ab", "ba", "feed", "how-to-train-your-dragon"]) hashes.Add(Of(s).GetHashCode());
        Assert.True(hashes.Count >= 6, $"{hashes.Count} distinct hashes of 7 texts");
    }

    [Fact]
    public void ANullBufferIsRefused() => Assert.Throws<ArgumentNullException>(() => new Utf8(null!, 0, 0));

    [Fact]
    public void InvalidUtf8BecomesReplacementCharactersOnlyInToString()
    {
        var text = new Utf8([0x61, 0xFF, 0x62], 0, 3);
        Assert.Equal("a�b", text.ToString());
        Assert.Equal(new byte[] { 0x61, 0xFF, 0x62 }, text.Span.ToArray()); // the bytes themselves are kept
    }

    [Fact]
    public void ReadsTheBufferAsItIsNow()
    {
        var buffer = Encoding.ASCII.GetBytes("abc");
        var text = new Utf8(buffer, 0, 3);
        buffer[1] = (byte)'X';
        Assert.True(text == "aXc"u8); // a view, not a copy: valid only while its owner keeps the bytes
    }

    [Fact]
    public void ComparingAndHashingAllocateNothing()
    {
        Utf8 a = Of("how-to-train-your-dragon"), b = Of("how-to-train-your-dragon", 2, 2);
        int Run() => (a == b ? 1 : 0) + (a == "feed"u8 ? 1 : 0) + a.GetHashCode() % 2 + (a.Span.Length > 0 ? 1 : 0);
        for (int i = 0; i < 100; i++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- Slice and Trim: views of the same buffer ----

    [Theory]
    [InlineData("hello", 0, 5, "hello")]
    [InlineData("hello", 0, 0, "")]
    [InlineData("hello", 5, 0, "")]
    [InlineData("hello", 1, 3, "ell")]
    [InlineData("hello", 4, 1, "o")]
    [InlineData("héllo", 1, 2, "é")]
    public void ASliceIsAViewOfThoseBytes(string s, int start, int length, string expected)
    {
        Utf8 text = Of(s, 3, 2);
        Utf8 slice = text.Slice(start, length);
        Assert.Equal(expected, slice.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), slice.Length);
        if (slice.Length > 0) Assert.True(slice.Span.Overlaps(text.Span)); // the same bytes, not a copy
    }

    // the buffer's padding around the text must stay out of reach
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 6)]
    [InlineData(1, 5)]
    [InlineData(6, 0)]
    [InlineData(5, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(int.MinValue, 0)]
    public void ASliceOutsideTheTextIsRefused(int start, int length)
    {
        Utf8 text = Of("hello", 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => text.Slice(start, length));
    }

    [Fact]
    public void TheEmptyTextSlicesOnlyToItself()
    {
        Assert.True(Utf8.Empty.Slice(0, 0).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8.Empty.Slice(0, 1));
        Assert.True(Utf8.Empty.Trim().IsEmpty);
    }

    [Theory]
    [InlineData("hi", "hi")]
    [InlineData("  hi there \n", "hi there")]
    [InlineData("\t\r\n hi\r\n\t ", "hi")]
    [InlineData(" a b ", "a b")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    [InlineData(" hi ", " hi ")]   // JSON's whitespace only: a no-break space is text
    [InlineData(" hi", " hi")]
    [InlineData("\fhi\v", "\fhi\v")]
    [InlineData(" é ", "é")]
    public void TrimDropsJsonWhitespaceAtBothEnds(string s, string expected)
    {
        Utf8 text = Of(s, 2, 2);
        Utf8 trimmed = text.Trim();
        Assert.Equal(expected, trimmed.ToString());
        if (trimmed.Length > 0) Assert.True(trimmed.Span.Overlaps(text.Span));
    }

    [Property(MaxTest = 1000)]
    public bool TrimIsTheSpansTrimAsAView(NonNull<string> s, byte before, byte after)
    {
        Utf8 text = Of(s.Get, before % 5, after % 5);
        return text.Trim().Span.SequenceEqual(text.Span.Trim(" \t\r\n"u8)) && text.Trim().Trim() == text.Trim();
    }

    [Fact]
    public void SlicingAndTrimmingAllocateNothing()
    {
        Utf8 a = Of("  how-to-train-your-dragon \n", 3, 3);
        int Run() => a.Trim().Length + a.Slice(2, 3).Length + a.Trim().Slice(1, 2).Length;
        for (int i = 0; i < 100; i++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
