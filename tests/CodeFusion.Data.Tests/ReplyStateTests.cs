using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>A context with no queries: the tests hand it results of their own.</summary>
public sealed partial class BareDb : DataContext<CodeFusion.Data.Postgres.Postgres>;

/// <summary>
/// The state a run's reader works with belongs to that run: an awaited write started while a reply waits for its
/// rows leaves the reply's state alone. The results here run on queries of the test's own, so the order is exact.
/// </summary>
public sealed class ReplyStateTests
{
    sealed class NoSource : DataSource
    {
        public override void Stop() { }
    }

    /// <summary>A query that completes when the test says, recording the state its run was given.</summary>
    sealed class HeldOps(ResultKind kind) : QueryOps
    {
        public readonly TaskCompletionSource<DataResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<RowState>? OnRun;
        public RowState? State;

        public override string Name => "Held";
        public override ResultKind Kind => kind;
        public override bool IsWrite => kind == ResultKind.Write;

        public override ValueTask<DataResult> RunJson(in PendingCommand command, RowState state, CancellationToken aborted)
        {
            State = state;
            OnRun?.Invoke(state);
            return new ValueTask<DataResult>(Done.Task);
        }

        public override ValueTask<DataResult> RunCopy(in PendingCommand command, RowState state, CancellationToken aborted) => throw new NotSupportedException();

        public override void Discard(in PendingCommand command) { }
    }

    static PendingCommand Command(int token) => new(new object(), new object(), token);

    [Fact]
    public async Task AnAwaitedWriteLeavesTheRowsOfAReplyInProgressAlone()
    {
        var db = new NoSource().CreateContext<BareDb>();
        var response = new FakeResponse();
        db.Bind(response);
        // the reply's reader has read its first row and waits for the rest
        var read = new HeldOps(ResultKind.One) { OnRun = s => s.Seen = 1 };
        var replying = new One<long>(db, Command(1), read).WriteAsync(response);
        Assert.False(replying.IsCompleted);

        var write = new HeldOps(ResultKind.Write);
        write.Done.SetResult(new DataResult(DataStatus.Ok));
        var outcome = await new Write(db, Command(2), write);
        Assert.True(outcome.IsOk);
        Assert.NotSame(read.State, write.State);
        Assert.Equal(1, read.State!.Seen);

        read.Done.SetResult(new DataResult(DataStatus.Ok, rows: 2));
        await replying;
        Assert.Equal(200, response.Status);
    }
}
