using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// Reads on the caller's thread with kept snapshots: a read sees every write that completed before it started and never
/// half of a group; a held command runs once; more readers than connections all finish; a read past the timeout is
/// interrupted; idle readers don't keep the WAL from being checkpointed.
/// </summary>
public sealed class ReadTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AReadAfterItsWriteSeesIt()
    {
        var s = _db.Open(_db.Database(readers: 4));
        int missed = 0;
        await Parallel.ForAsync(0, 4, async (t, ct) =>
        {
            for (int i = 0; i < 500; i++)
            {
                string key = $"ryw-{t}-{i}";
                Assert.True((await Kv.Write(s, key, i, ct)).IsOk);
                if (Kv.Read(s, key).Rows != 1) Interlocked.Increment(ref missed);
            }
        });
        Assert.Equal(0, missed);
    }

    [Fact]
    public async Task AReadNeverSeesHalfOfAUnit()
    {
        var s = _db.Open(_db.Database(readers: 2));
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < 1500; i++)
            {
                // two rows per unit: a reader sees both or neither
                var c = s.Write(Kv.Put);
                c.Text("pair-a-" + i);
                c.Int64(i);
                c.Then(Kv.Put);
                c.Text("pair-b-" + i);
                c.Int64(i);
                Assert.True((await Sqlite.Run<Value>(c.Hold(), null, default)).IsOk);
            }
            stop.Cancel();
        }, TestContext.Current.CancellationToken);
        long odd = 0, reads = 0;
        var readers = new Task[3];
        for (int r = 0; r < readers.Length; r++)
            readers[r] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (Kv.Count(s) % 2 != 0) Interlocked.Increment(ref odd);
                    Interlocked.Increment(ref reads);
                }
            }, TestContext.Current.CancellationToken);
        await writer;
        await Task.WhenAll(readers);
        Assert.Equal(0, odd);
        Assert.True(reads > 100, $"only {reads} reads ran beside the writes");
        Assert.Equal(3000, Kv.Count(s));
    }

    [Fact]
    public async Task AHeldCommandRunsOnceAndAStaleCopyIsRefused()
    {
        var s = _db.Open(_db.Database());
        Assert.True((await Kv.Write(s, "k", 7)).IsOk);
        var c = s.Read(Kv.Get);
        c.Text("k");
        var held = c.Hold();
        Assert.Equal(7, Kv.Done(Sqlite.Run<Value>(held, null, default)).Value);
        Assert.Throws<InvalidOperationException>(() => Sqlite.Run<Value>(held, null, default));
        Sqlite.Discard(held); // given back already: nothing happens
        // a write too
        var w = s.Write(Kv.Put);
        w.Text("w");
        w.Int64(1);
        var heldWrite = w.Hold();
        Assert.True((await Sqlite.Run<Value>(heldWrite, null, default)).IsOk);
        Assert.Throws<InvalidOperationException>(() => Sqlite.Run<Value>(heldWrite, null, default));
    }

    [Fact]
    public void RunAndDiscardRacingOnOneHeldCommandLetExactlyOneTakeIt()
    {
        var s = _db.Open(_db.Database());
        for (int i = 0; i < 2000; i++)
        {
            var c = s.Read(Kv.CountAll);
            var held = c.Hold();
            int ran = 0, threw = 0;
            Parallel.Invoke(
                () =>
                {
                    try
                    {
                        _ = Sqlite.Run<Value>(held, null, default).Result;
                        Interlocked.Increment(ref ran);
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref threw);
                    }
                },
                () => Sqlite.Discard(held));
            Assert.Equal(1, ran + threw);
        }
    }

    [Fact]
    public async Task MoreReadersThanConnectionsAllFinish()
    {
        var s = _db.Open(_db.Database(readers: 2));
        Assert.True((await Kv.WriteMany(s, "r", 1000)).IsOk);
        long total = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (int i = 0; i < 500; i++) Interlocked.Add(ref total, Kv.Count(s));
        });
        Assert.Equal(16L * 500 * 1000, total);
    }

    [Fact]
    public async Task AReadPastTheTimeoutIsInterruptedAndTheConnectionReadsOnAfter()
    {
        var s = _db.Open(_db.Database(readers: 1, timeout: TimeSpan.FromMilliseconds(200)));
        Assert.True((await Kv.Write(s, "k", 1)).IsOk);
        var c = s.Read(Kv.Spin);
        c.Int64(10_000_000_000);
        var r = Kv.Done(Sqlite.Run<Value>(c.Hold(), null, default));
        Assert.Equal(DataStatus.StatementTimeout, r.Status);
        Assert.Equal("SQLITE_INTERRUPT", r.NativeCode);
        Assert.True(s.Interrupted >= 1);
        Assert.Equal(1, Kv.Read(s, "k").Value);
    }

    [Fact]
    public async Task IdleReadersDontKeepTheWalFromBeingCheckpointed()
    {
        var db = _db.Database(readers: 4);
        var s = _db.Open(db);
        // every reader connection reads once, then sits idle with its snapshot
        Parallel.For(0, 64, _ => Kv.Count(s));
        for (int i = 0; i < 400; i++) Assert.True((await Kv.WriteMany(s, $"w{i}-", 50)).IsOk);
        long wal = new FileInfo(db.Path + "-wal").Length;
        Assert.True(wal < 16L << 20, $"the WAL grew to {wal >> 20} MiB");
        Assert.True(s.Writer.Counters.Checkpoints > 0);
    }

    [Fact]
    public void AReaderThatThrowsFailsItsReadAndLeavesTheConnectionReady()
    {
        var s = _db.Open(_db.Database(readers: 1));
        var c = s.Read(Kv.Spin);
        c.Int64(10);
        var r = Kv.Done(Sqlite.Run<Throws>(c.Hold(), null, default));
        Assert.Equal(DataStatus.ReaderFailed, r.Status);
        Assert.Equal(1, r.Rows);
        Assert.IsType<InvalidOperationException>(r.Error);
        Assert.Equal(0, Kv.Count(s));
    }

    sealed class Throws : IRowReader
    {
        public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct =>
            throw new InvalidOperationException("the reader failed");
    }
}
