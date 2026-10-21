using System;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;
using static CodeFusion.Data.Postgres.Tests.PgTestDb;

#pragma warning disable xUnit1069, xUnit1051, xUnit1031 // .Result only after IsCompleted: the reads that race

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// A pooled job is reused the moment its caller reads its result: the next command may take the same job while the
/// loop thread that completed it is still inside its completion. The runtime's ManualResetValueTaskSourceCore marks
/// the result ready before it claims the continuation slot, so a caller that sees the result in that window, reads
/// it (which resets the job for its next use) and then awaits the job's next command could get the old completion's
/// sentinel in the new operation: its continuation runs before its result exists, and GetResult throws. Core's
/// InlineValueTaskSourceCore claims the slot first. The first test runs that reuse hard (on .NET 10.0.13 the
/// runtime's core passes it too: its GetStatus reports nothing ready until the slot is claimed, so a caller that
/// polls can't reach the window). The second is the other half of the change, which does fail on the runtime's core:
/// a continuation registered just after the job completed runs right there, where the runtime's core queues a
/// thread-pool work item (40 B, and a hop to another thread).
/// </summary>
[Collection(nameof(Alone))]
public sealed class PgJobReuseTests
{
    static readonly PgStatement PlusOne = new("SELECT $1::int8 + 1", PgType.Int8);

    static readonly PgRowHandler Value = static (in PgRow r, object? _) => r.GetInt64(0);

    /// <summary>One caller: reads every other result the moment it is ready (spinning on IsCompleted, the window the
    /// race needs), and awaits the others, so the job it just read and reset is often the one it awaits next.</summary>
    static async Task<string?> Caller(PgReader reader, int me, int commands)
    {
        for (int i = 0; i < commands; i++)
        {
            long key = (long)me << 32 | (uint)i;
            PgResult r;
            try
            {
                // an await continues on the loop thread that completed it; spinning there would block the loop that
                // has to run this caller's next command, so the spinning reads happen on a pool thread
                if (i % 2 == 0 && !Thread.CurrentThread.IsThreadPoolThread) await Task.Yield();
                var pending = reader.Command(PlusOne).Int8(key).Run(Value, null);
                if (i % 2 == 0)
                {
                    var spin = new SpinWait();
                    while (!pending.IsCompleted) spin.SpinOnce(sleep1Threshold: -1);
                    r = pending.Result;
                }
                else r = await pending.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return $"caller {me}, command {i}: {e.GetType().Name}: {e.Message}";
            }
            if (!r.IsOk || r.Rows != 1 || r.Value != key + 1) return $"caller {me}, command {i}: {r} for {key}";
        }
        return null;
    }

    [Fact(Timeout = 300_000)]
    public async Task AJobReadAndReusedAtOnceGetsOnlyItsOwnCompletion()
    {
        Require();
        var reader = new PgReader(Settings());
        reader.Start(4);
        try
        {
            const int Callers = 16, Commands = 6000;
            var callers = new Task<string?>[Callers];
            for (int c = 0; c < Callers; c++)
            {
                int me = c;
                // off xunit's synchronization context: awaits continue on the loop threads that complete them
                callers[c] = Task.Run(() => Caller(reader, me, Commands));
            }
            foreach (var t in callers) Assert.Null(await t);
        }
        finally
        {
            LoopChecks.Stop(reader);
        }
    }

    [Fact(Timeout = 60_000)]
    public void AContinuationRegisteredAfterTheJobCompletedRunsInline()
    {
        Require();
        var reader = new PgReader(Settings());
        reader.Start(1);
        try
        {
            for (int i = 0; i < 50; i++)
            {
                var pending = reader.Command(PlusOne).Int8(i).Run(Value, null);
                var spin = new SpinWait();
                while (!pending.IsCompleted) spin.SpinOnce(sleep1Threshold: -1);
                // as an await does when the command completed between its IsCompleted check and its registration
                int ranOn = -1;
                pending.GetAwaiter().UnsafeOnCompleted(() => ranOn = Environment.CurrentManagedThreadId);
                Assert.Equal(Environment.CurrentManagedThreadId, ranOn); // at once, on this thread
                var r = pending.Result;
                Assert.Equal(i + 1, r.Value);
            }
        }
        finally
        {
            LoopChecks.Stop(reader);
        }
    }
}
