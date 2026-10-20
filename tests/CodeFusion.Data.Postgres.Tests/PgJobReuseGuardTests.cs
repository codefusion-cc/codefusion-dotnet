using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051, xUnit1069

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// A command's builder after its job went back to the pool. Alone: the pool is shared by the process, and the job a
/// command gave back must be the one the next command takes, not one another test took first.
/// </summary>
[Collection(nameof(Alone))]
public sealed class PgJobReuseGuardTests : IDisposable
{
    static readonly PgStatement Echo = new("echo for reuse", PgType.Int8);
    readonly List<PgLoops> _started = [];

    sealed class Seen;

    static readonly PgRowHandler Collect = static (in PgRow r, object? _) => r.GetInt64(0);

    PgReader Reader(FakeServer server)
    {
        var r = new PgReader(server.Settings());
        _started.Add(r);
        return r;
    }

    public void Dispose()
    {
        LoopChecks.Stop(_started);
    }

    static FakeServer Server() => new() { Rows = static b => [[ServerWriter.BE64(b.Int8(0))]] };

    /// <summary>Waits for a command without an await: a <see cref="PgCommand"/> is a ref struct, which can't cross one.</summary>
    static PgResult Wait(ValueTask<PgResult> run)
    {
        var t = run.AsTask();
        Assert.True(t.Wait(TimeSpan.FromSeconds(10)), "the command didn't complete");
        return t.Result;
    }

    static bool Refused(PgCommand stale, long v)
    {
        try
        {
            stale.Int8(v);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Fact]
    public void ABuilderOfACommandThatRanCantTouchTheCommandThatReusesItsJob()
    {
        var r = Reader(Server());
        r.Start(1);
        var first = r.Command(Echo).Int8(1);
        Assert.Equal(1, Wait(first.Run(Collect, new Seen())).Value);
        var second = r.Command(Echo); // the pooled job the first one gave back
        Assert.True(Refused(first, 5));
        Assert.Equal(2, Wait(second.Int8(2).Run(Collect, new Seen())).Value);
    }

    [Fact]
    public void AHeldCommandRunsOnceAndOneGivenBackNever()
    {
        var r = Reader(Server());
        r.Start(1);
        var held = r.Command(Echo).Int8(3).Hold();
        Assert.Equal(3, Wait(held.Run(Collect, new Seen())).Value);
        Assert.Throws<InvalidOperationException>(() => { _ = held.Run(Collect, new Seen()); });
        var next = r.Command(Echo).Int8(8); // on the job the held command gave back
        held.Discard();                     // after its run: touches nothing
        Assert.Equal(8, Wait(next.Run(Collect, new Seen())).Value);

        var dropped = r.Command(Echo).Int8(4).Hold();
        dropped.Discard();
        dropped.Discard();
        Assert.Throws<InvalidOperationException>(() => { _ = dropped.Run(Collect, new Seen()); });

        var building = r.Command(Echo).Int8(5);
        var kept = building.Hold();
        Assert.True(Refused(building, 6)); // a held command's builder is done
        Assert.Equal(5, Wait(kept.Run(Collect, new Seen())).Value);

        bool missing;
        try
        {
            r.Command(Echo).Hold(); // a missing parameter
            missing = false;
        }
        catch (InvalidOperationException)
        {
            missing = true;
        }
        Assert.True(missing);
        Assert.Equal(7, Wait(r.Command(Echo).Int8(7).Run(Collect, new Seen())).Value);
    }

    static readonly PgStatement TwoTexts = new("two texts for reuse", PgType.Text, PgType.TextArray);

    /// <summary>Each way building a command can throw, from a value or from a mistake in the order of the calls.</summary>
    public static TheoryData<string> BuildFailures => new()
    {
        "lone surrogate in text", "lone surrogate in an element", "too many parameters", "a parameter inside a text[]",
        "an element outside a text[]", "a text[] not begun", "a text[] not ended", "too few parameters before Then",
    };

    static void Fail(PgCommand cmd, string how)
    {
        switch (how)
        {
            case "lone surrogate in text": cmd.Text("a\ud800".AsSpan()); break;
            case "lone surrogate in an element": cmd.Text("t").BeginTextArray().Element("\udc00".AsSpan()); break;
            case "too many parameters": cmd.Text("t").TextArray(["x"]).Text("u"); break;
            case "a parameter inside a text[]": cmd.Text("t").BeginTextArray().Text("u"); break;
            case "an element outside a text[]": cmd.Element("x"u8); break;
            case "a text[] not begun": cmd.Text("t").EndTextArray(); break;
            case "a text[] not ended": cmd.Text("t").BeginTextArray().Then(TwoTexts); break;
            case "too few parameters before Then": cmd.Text("t").Then(TwoTexts); break;
        }
    }

    [Theory]
    [MemberData(nameof(BuildFailures))]
    public void ACommandWhoseBuildThrowsGivesItsJobBackAndTakesNoMore(string how)
    {
        var r = Reader(new FakeServer());
        var cmd = r.Command(TwoTexts);
        bool threw;
        try
        {
            Fail(cmd, how);
            threw = false;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            threw = true;
        }
        Assert.True(threw);
#if DEBUG
        Assert.Equal(0, r.JobsOut); // back in the pool, though the caller never ran or held it
#endif
        // the builder is done: the job may already be another command's
        string? refusal = null;
        try
        {
            cmd.Text("again");
        }
        catch (InvalidOperationException e)
        {
            refusal = e.Message;
        }
        Assert.Contains("already", refusal, StringComparison.Ordinal);
        var next = r.Command(TwoTexts); // the job given back: untouched by the failed build
        r.Start(1);
        Assert.True(Wait(next.Text("t").TextArray(["x", null]).Run(null, null)).IsOk);
    }

    [Fact(Timeout = 60_000)]
    public void AHeldCommandRunOrDiscardedFromTwoThreadsAtOnceRunsOnceOrNever()
    {
        var r = Reader(Server());
        r.Start(1);
        const int Rounds = 3000;
        using var start = new Barrier(3);
        PgHeld held = default;
        var runs = new ValueTask<PgResult>?[2];
        int bothRan = 0, neitherRan = 0;
        var threads = new Thread[2];
        for (int t = 0; t < 2; t++)
        {
            int me = t;
            threads[t] = new Thread(() =>
            {
                for (int round = 0; round < Rounds; round++)
                {
                    start.SignalAndWait();
                    var h = held;
                    // by round: both run, both give it back, or one of each
                    if ((round % 3) switch { 0 => true, 1 => false, _ => me == 0 })
                        try
                        {
                            runs[me] = h.Run(Collect, new Seen());
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    else h.Discard();
                    start.SignalAndWait();
                }
            }) { IsBackground = true };
            threads[t].Start();
        }
        for (int round = 0; round < Rounds; round++)
        {
            held = r.Command(Echo).Int8(round).Hold();
            runs[0] = runs[1] = null;
            start.SignalAndWait();
            start.SignalAndWait();
            int ran = (runs[0] is null ? 0 : 1) + (runs[1] is null ? 0 : 1);
            if (ran == 2) bothRan++;
            if (round % 3 == 0 && ran == 0) neitherRan++;
            if ((runs[0] ?? runs[1]) is { } run) Assert.Equal(round, Wait(run).Value);
        }
        foreach (var t in threads) t.Join();
        Assert.Equal(0, bothRan);
        Assert.Equal(0, neitherRan); // of two runs one always wins
    }

}
