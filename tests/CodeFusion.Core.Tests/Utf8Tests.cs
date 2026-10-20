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
}
