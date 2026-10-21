using System;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// The job core every provider's pooled job holds: one claim per hold, never by a stale generation; Debug builds refuse
/// a step out of order and count jobs out of the pool; the pool gives a thread its own job back first.
/// </summary>
public sealed class JobLifeTests
{
    static bool Debug =>
        typeof(JobLife).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            is [System.Diagnostics.DebuggableAttribute { IsJITOptimizerDisabled: true }];

    static JobLife Held(out int generation)
    {
        var life = new JobLife("Test", queuesUnheld: false);
        life.To(JobStep.Building);
        generation = life.Generation;
        life.Hold();
        return life;
    }

    [Fact]
    public void ANewJobCantBeClaimed()
    {
        var life = new JobLife("Test", queuesUnheld: false);
        Assert.False(life.Claim(0));
        Assert.False(life.Claim(-1));
    }

    [Fact]
    public void AHeldJobIsClaimedOnce()
    {
        var life = Held(out int g);
        Assert.True(life.Claim(g));
        Assert.False(life.Claim(g));
    }

    [Fact]
    public void AnotherGenerationsClaimFails()
    {
        var life = Held(out int g);
        Assert.False(life.Claim(g + 1));
        Assert.False(life.Claim(g - 1));
        Assert.True(life.Claim(g));
    }

    [Fact]
    public void AReturnedJobsHoldIsGone()
    {
        var life = Held(out int g);
        life.CheckReturnable();
        life.Returned();
        Assert.Equal(g + 1, life.Generation);
        Assert.False(life.Claim(g));
        Assert.False(life.Claim(g + 1));
    }

    [Fact]
    public void TheGenerationWrapsAndStillClaimsRight()
    {
        var life = new JobLife("Test", queuesUnheld: false) { Generation = int.MaxValue };
        life.To(JobStep.Building);
        life.Hold();
        Assert.False(life.Claim(int.MinValue));
        Assert.True(life.Claim(int.MaxValue));
        life.Returned();
        Assert.Equal(int.MinValue, life.Generation);
        life.To(JobStep.Building);
        life.Hold();
        Assert.False(life.Claim(int.MaxValue));
        Assert.True(life.Claim(int.MinValue));
    }

    [Fact]
    public async Task OfTwoRacingClaimsOneWins()
    {
        for (int round = 0; round < 1_000; round++)
        {
            var box = new StrongBox { Life = Held(out int g) };
            var a = Task.Run(() => box.Claim(g));
            var b = Task.Run(() => box.Claim(g));
            Assert.True(await a ^ await b);
        }
    }

    sealed class StrongBox
    {
        public JobLife Life;
        public bool Claim(int g) => Life.Claim(g);
    }

    [Fact]
    public void DebugRefusesAStepOutOfOrder()
    {
        Assert.SkipUnless(Debug, "the checks compile away in Release");
        var life = new JobLife("Test", queuesUnheld: false);
        Assert.Contains("went from Pooled to Queued", Assert.Throws<InvalidOperationException>(() => life.To(JobStep.Queued)).Message, StringComparison.Ordinal);
        life.To(JobStep.Building);
        // only a provider that queues unheld jobs may skip the hold
        Assert.Throws<InvalidOperationException>(() => life.To(JobStep.Queued));
        var unheld = new JobLife("Test", queuesUnheld: true);
        unheld.To(JobStep.Building);
        unheld.To(JobStep.Queued);
        unheld.To(JobStep.Completed);
        Assert.Contains("completed while Completed", Assert.Throws<InvalidOperationException>(() => unheld.To(JobStep.Completed)).Message, StringComparison.Ordinal);
        unheld.Returned();
        Assert.Contains("returned twice", Assert.Throws<InvalidOperationException>(() => unheld.CheckReturnable()).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => unheld.CheckIs(JobStep.Queued));
    }

    [Fact]
    public void DebugCountsAJobOutUntilItIsBack()
    {
        Assert.SkipUnless(Debug, "the count compiles away in Release");
        var owner = new JobCounter();
        var life = new JobLife("Test", queuesUnheld: false);
        life.To(JobStep.Building);
        life.CountOut(owner);
        Assert.Equal(1, owner.Out);
        life.Returned();
        Assert.Equal(0, owner.Out);
    }

    [Fact]
    public void DebugSeesABufferWrittenAfterItsReturn()
    {
        Assert.SkipUnless(Debug, "the check compiles away in Release");
        var life = new JobLife("Test", queuesUnheld: false);
        var buffer = new byte[16];
        JobLife.Poison(buffer);
        life.CheckPoisoned(buffer);
        life.CheckPoisoned(null);
        buffer[15] = 1;
        Assert.Contains("written to after its return", Assert.Throws<InvalidOperationException>(() => life.CheckPoisoned(buffer)).Message, StringComparison.Ordinal);
    }

    sealed class LocalJob;

    [Fact]
    public void AThreadGetsItsOwnJobBackFirst()
    {
        var mine = new LocalJob();
        JobPool<LocalJob>.Give(mine, local: true);
        Assert.Same(mine, JobPool<LocalJob>.Take(local: true));
    }

    sealed class SharedJob;

    [Fact]
    public async Task ASharedJobGoesToAnyThread()
    {
        var job = new SharedJob();
        JobPool<SharedJob>.Give(job, local: false);
        Assert.Same(job, await Task.Run(() => JobPool<SharedJob>.Take(local: false)));
        Assert.Null(JobPool<SharedJob>.Take(local: false));
    }
}
