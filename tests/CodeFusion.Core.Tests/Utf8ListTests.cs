using System;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Core.Tests;

public class Utf8ListTests
{
    static Utf8List Of(params string[] texts)
    {
        byte[][] bytes = [.. texts.Select(Encoding.UTF8.GetBytes)];
        var buffer = new byte[3 + Utf8List.RecordBytes(bytes.Sum(b => b.Length), bytes.Length)];
        int at = 3; // not at the buffer's start
        foreach (var b in bytes) at += Utf8List.Write(b, buffer.AsSpan(at));
        return new Utf8List(buffer, 3, at - 3, texts.Length);
    }

    static string[] Texts(Utf8List list)
    {
        var all = new System.Collections.Generic.List<string>();
        foreach (Utf8 t in list) all.Add(t.ToString());
        return [.. all];
    }

    [Property(MaxTest = 300)]
    public bool EnumeratesTheTextsWrittenInOrder(NonNull<string>[] texts)
    {
        string[] t = [.. texts.Select(x => x.Get)];
        var list = Of(t);
        return list.Count == t.Length && list.IsEmpty == (t.Length == 0)
            && Texts(list).SequenceEqual(t.Select(s => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s))));
    }

    [Fact]
    public void EmptyAndDefaultHaveNoTexts()
    {
        Assert.True(Utf8List.Empty.IsEmpty);
        Assert.Equal(0, default(Utf8List).Count);
        Assert.Empty(Texts(Utf8List.Empty));
        Assert.True(Of() == Utf8List.Empty);
        Assert.Equal("[]", Utf8List.Empty.ToString());
    }

    [Fact]
    public void KeepsEmptyTextsAndRepeats()
    {
        var list = Of("", "a", "", "a");
        Assert.Equal(["", "a", "", "a"], Texts(list));
        Assert.Equal(4, list.Count);
    }

    [Fact]
    public void EqualityIsTheSameTextsInTheSameOrder()
    {
        Assert.True(Of("a", "é😀") == Of("a", "é😀"));
        Assert.Equal(Of("a", "b").GetHashCode(), Of("a", "b").GetHashCode());
        Assert.True(Of("a", "b") != Of("b", "a"));
        Assert.True(Of("a") != Of("a", ""));
        Assert.True(Of("ab") != Of("a", "b"));
    }

    [Fact]
    public void ACountTheBytesDontHoldIsRefused()
    {
        var buffer = new byte[Utf8List.RecordBytes(2, 2)];
        int at = Utf8List.Write("a"u8, buffer);
        at += Utf8List.Write("b"u8, buffer.AsSpan(at));
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, at, 1));
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, at, 3));
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, at - 1, 2)); // the last record cut short
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, 3, 0));      // trailing bytes that are no record
        Assert.Equal(2, new Utf8List(buffer, 0, at, 2).Count);
    }

    [Fact]
    public void ARecordLongerThanTheListIsRefused()
    {
        var buffer = new byte[8];
        BitConverter.TryWriteBytes(buffer, 100); // claims 100 bytes in an 8-byte list
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, 8, 1));
        BitConverter.TryWriteBytes(buffer, -1);
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, 8, 1));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 11)]
    [InlineData(11, 0)]
    public void BytesOutsideTheBufferAreRefused(int start, int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Utf8List(new byte[10], start, length, 0));

    [Fact]
    public void ARecordOneByteLongerThanWhatRemainsIsRefused()
    {
        // a 3-byte text record (7 bytes), cut so that 2 text bytes remain: its length claims one more than there is
        var buffer = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, 3);
        Assert.Throws<ArgumentException>(() => new Utf8List(buffer, 0, 6, 1));
        var exact = new byte[7];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(exact, 3);
        Assert.Equal(1, new Utf8List(exact, 0, 7, 1).Count); // exactly what remains is fine
    }

    [Fact]
    public void BytesPastTheBufferFromAnOffsetAreRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Utf8List(new byte[10], 4, 7, 0)); // 4 + 7 > 10

    [Fact]
    public void ANullBufferIsRefused() => Assert.Throws<ArgumentNullException>(() => new Utf8List(null!, 0, 0, 0));

    [Fact]
    public void RecordBytesOverflowThrowsInsteadOfWrapping()
    {
        Assert.Equal(15, Utf8List.RecordBytes(7, 2));
        Assert.Throws<OverflowException>(() => Utf8List.RecordBytes(int.MaxValue, 1));
        Assert.Throws<OverflowException>(() => Utf8List.RecordBytes(0, int.MaxValue / 2));
    }

    [Fact]
    public void DifferentListsHashDifferently()
    {
        var hashes = new System.Collections.Generic.HashSet<int>();
        foreach (var l in (Utf8List[])[Of(), Of("a"), Of("b"), Of("a", "b"), Of("b", "a"), Of("ab"), Of("")]) hashes.Add(l.GetHashCode());
        Assert.True(hashes.Count >= 6, $"{hashes.Count} distinct hashes of 7 lists");
    }

    [Fact]
    public void AnUncheckedRecordRunningPastTheListsEndStopsTheWalk()
    {
        // The trusted constructor skips validation: a record whose length runs past the list's end, though not past
        // the buffer, must not be handed out.
        var buffer = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 10);
        var list = new Utf8List(buffer, 8, 6, 1, trusted: true);
        var e = list.GetEnumerator();
        Assert.False(e.MoveNext());
    }

    [Fact]
    public void ToStringListsTheTextsForLogs()
    {
        Assert.Equal("[]", Of().ToString());
        Assert.Equal("[\"a\"]", Of("a").ToString());
        Assert.Equal("[\"a\", \"é\", \"\"]", Of("a", "é", "").ToString());
    }

    [Fact]
    public void ANegativeCountIsRefused() => Assert.Throws<ArgumentOutOfRangeException>(() => new Utf8List(new byte[4], 0, 0, -1));

    [Fact]
    public void EnumeratingAllocatesNothing()
    {
        var list = Of("dragons", "training", "é😀");
        int Run() { int n = 0; foreach (Utf8 t in list) n += t.Length; return n; }
        for (int i = 0; i < 100; i++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
