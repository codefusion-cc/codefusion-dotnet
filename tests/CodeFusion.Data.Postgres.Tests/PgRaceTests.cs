using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;

// The timeouts turn a command that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// A caller that cancels, loops that stop, and a server that drops the connection, at each place a command waits:
/// queued behind a group whose reply hasn't come, sent with no reply yet, and with its first rows arrived and the rest
/// not. The scripted server holds the first batch's reply for the test to send when it likes, so each place is
/// reached exactly, in Release builds too. Each command ends once, as the place and the act say it must, and the loops
/// stop clean (<see cref="LoopChecks"/>: no unexpected exception, nothing queued, every job back in the pool).
/// </summary>
public sealed class PgRaceTests
{
    public enum Kind { Reader, Writer }

    public enum Where { Queued, Sent, RowsArriving }

    public enum Act { Cancel, Stop, Reset }

    public static TheoryData<Kind, Where, Act> Cases()
    {
        var data = new TheoryData<Kind, Where, Act>();
        foreach (Kind k in Enum.GetValues<Kind>())
            foreach (Where w in Enum.GetValues<Where>())
                foreach (Act a in Enum.GetValues<Act>())
                    data.Add(k, w, a);
        return data;
    }

    static readonly PgStatement Race = new("race", PgType.Int8);

    /// <summary>Two rows per command: its parameter, then one more.</summary>
    static IEnumerable<byte[]?[]> TwoRows(Bind b) => [[ServerWriter.BE64(b.Int8(0))], [ServerWriter.BE64(b.Int8(0) + 1)]];

    sealed class Rows
    {
        public int Count;
        public readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static readonly PgRowHandler Count = static (in PgRow r, object? s) =>
    {
        var rows = (Rows)s!;
        if (Interlocked.Increment(ref rows.Count) == 1) rows.First.TrySetResult();
        return r.GetInt64(0);
    };

    static Task<PgResult> Run(PgLoops loops, long v, Rows rows, CancellationToken aborted = default) =>
        loops.Command(Race).Int8(v).Run(Count, rows, aborted).AsTask();

    [Theory(Timeout = 20_000)]
    [MemberData(nameof(Cases))]
    public async Task EachCommandEndsOnceAndTheLoopsStopClean(Kind kind, Where where, Act act)
    {
        var held = new HeldServer();
        PgLoops loops = kind == Kind.Reader ? new PgReader(held.Server.Settings()) : new PgWriter(held.Server.Settings());
        using var cts = new CancellationTokenSource();
        var targetRows = new Rows();
        Task<PgResult> target;
        Task<PgResult>? blocker = null;
        FakeConnection c;
        byte[] reply;
        try
        {
            loops.Start(1);
            if (where == Where.Queued)
            {
                // the only loop waits on the blocker's reply, so the target stays in the queue
                blocker = Run(loops, 1, new Rows());
                (c, reply) = await held.First;
                target = Run(loops, 10, targetRows, cts.Token);
                Assert.Equal(1, loops.Queued);
            }
            else
            {
                target = Run(loops, 10, targetRows, cts.Token);
                (c, reply) = await held.First;
            }
            byte[] rest = reply;
            if (where == Where.RowsArriving)
            {
                (byte[] head, rest) = AfterFirstRow(reply);
                c.Write(head);
                // a read hands each row over as it comes; a write keeps them until its commit, so wait until it took them in
                if (kind == Kind.Reader) await targetRows.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
                else await Until(() => c.Available == 0);
            }

            switch (act)
            {
                case Act.Cancel: cts.Cancel(); break;
                case Act.Stop: loops.Complete(); break;
                default: c.Close(); break;
            }
            if (act != Act.Reset) c.Write(rest);

            if (blocker is not null) Assert.Equal(act == Act.Reset ? PgStatus.Broken : PgStatus.Ok, (await blocker).Status);
            PgResult result = await target;
            // a cancel counts only before a loop took the command; a dropped connection fails what was sent on it
            // (a write's outcome unknown), and the next group connects again
            PgStatus expected = act switch
            {
                Act.Cancel when where == Where.Queued => PgStatus.Canceled,
                Act.Reset when where != Where.Queued => PgStatus.Broken,
                _ => PgStatus.Ok,
            };
            Assert.Equal(expected, result.Status);
            int rowsHandedOver = expected switch
            {
                PgStatus.Ok => 2,
                // a read's first row was handed over before the drop; a write hands over nothing it didn't commit
                PgStatus.Broken when where == Where.RowsArriving && kind == Kind.Reader => 1,
                _ => 0,
            };
            Assert.Equal(rowsHandedOver, targetRows.Count);
            if (expected == PgStatus.Ok) Assert.Equal((2, 11L), (result.Rows, result.Value));
        }
        finally
        {
            LoopChecks.Stop(loops);
        }
    }

    /// <summary>The reply cut after its first DataRow.</summary>
    static (byte[] Head, byte[] Tail) AfterFirstRow(byte[] reply)
    {
        for (int at = 0; at < reply.Length;)
        {
            int next = at + 1 + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at + 1));
            if (reply[at] == (byte)'D') return (reply[..next], reply[next..]);
            at = next;
        }
        throw new InvalidOperationException("no DataRow in the reply");
    }

    static async Task Until(Func<bool> condition)
    {
        for (long until = Environment.TickCount64 + 10_000; !condition(); await Task.Delay(5))
            if (Environment.TickCount64 > until) throw new TimeoutException();
        await Task.Delay(20); // taken in is not yet read: give the loop a moment to read it
    }

    /// <summary>A scripted server whose first batch waits for the test: the reply Postgres would send is handed to
    /// the test to send (or cut, or drop) when it likes. Every later batch is answered at once.</summary>
    sealed class HeldServer
    {
        public readonly FakeServer Server = new() { Rows = TwoRows };
        readonly TaskCompletionSource<(FakeConnection, byte[])> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _batches;

        public HeldServer() => Server.Respond = (c, batch) =>
        {
            if (Interlocked.Increment(ref _batches) > 1)
            {
                Server.Auto(c, batch);
                return;
            }
            var probe = new FakeConnection(Server);
            foreach (string p in c.Prepared) probe.Prepared.Add(p);
            Server.Auto(probe, batch);
            foreach (string p in probe.Prepared) c.Prepared.Add(p);
            var reply = new byte[probe.Available];
            probe.Receive(reply);
            _first.SetResult((c, reply));
        };

        public Task<(FakeConnection, byte[])> First => _first.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
