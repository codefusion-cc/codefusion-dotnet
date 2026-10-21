using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Security.Tests;

/// <summary>Tests that measure the whole process: nothing else may run while they do.</summary>
[CollectionDefinition(nameof(Measuring), DisableParallelization = true)]
public sealed class Measuring;

// The queue is measured with no token, as most callers use it, and separately with a cancellable one.
#pragma warning disable xUnit1051

/// <summary>Exact bytes allocated, after a warm-up so the JIT, tiering and the queue's pool are settled.</summary>
[Collection(nameof(Measuring))]
public class AllocationTests
{
    static readonly string Hash4 = Bcrypt.Hash("password123", 4);
    const int Calls = 20;

    static readonly bool Unoptimized = typeof(Bcrypt).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
        is [System.Diagnostics.DebuggableAttribute { IsJITOptimizerDisabled: true }];

    static long OnThisThread(Action action)
    {
        for (int i = 0; i < 3; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Calls; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The queue tests measure every thread's bytes, inline in the test method: a helper taking an async lambda
    // would allocate that lambda's state machine on each call. They keep the least of three tries after a warm-up:
    // the test host's own threads may allocate meanwhile, which only ever adds, while a real per-call allocation
    // shows in every try.
    const int Tries = 4;

    [Fact]
    public void VerifyAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        Assert.Equal(0, OnThisThread(() => Bcrypt.Verify("password123", Hash4)));
        Assert.Equal(0, OnThisThread(() => Bcrypt.Verify("wrong", Hash4)));
        Assert.Equal(0, OnThisThread(() => Bcrypt.Verify("password123", "malformed")));
        string tooLong = new('a', 100);
        Assert.Equal(0, OnThisThread(() => Bcrypt.Verify(tooLong, Hash4)));
    }

    [Fact]
    public void BatchVerifyAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        string[] passwords = Enumerable.Repeat("password123", 11).ToArray(), hashes = Enumerable.Repeat(Hash4, 11).ToArray();
        var results = new bool[11];
        Assert.Equal(0, OnThisThread(() => Bcrypt.Verify(passwords, hashes, results)));
        Assert.All(results, Assert.True);
    }

    [Fact]
    public void EnhancedVerifyAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        string hash = Bcrypt.EnhancedHash("password123", 4);
        string longPassword = new('é', 500), longHash = Bcrypt.EnhancedHash(longPassword, 4);
        Assert.Equal(0, OnThisThread(() => Bcrypt.EnhancedVerify("password123", hash)));
        Assert.Equal(0, OnThisThread(() => Bcrypt.EnhancedVerify(longPassword, longHash))); // pooled buffer
    }

    [Fact]
    public void HashAllocatesOnlyItsString()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        long oneString = OnThisThread(() => GC.KeepAlive(new string('x', 60))) / Calls;
        Assert.Equal(oneString * Calls, OnThisThread(() => Bcrypt.Hash("password123", 4)));
        Assert.Equal(oneString * Calls, OnThisThread(() => Bcrypt.EnhancedHash("password123", 4)));
    }

    [Fact]
    public async Task QueuedVerifyAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1 });
        long least = long.MaxValue;
        for (int t = 0; t < Tries; t++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Calls; i++)
            {
                ValueTask<bool> a = q.VerifyAsync("password123", Hash4), b = q.VerifyAsync("wrong", Hash4), c = q.VerifyAsync("password123", Hash4);
                Assert.True(await a & !await b & await c);
            }
            if (t > 0) least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equal(0, least);
    }

    [Fact]
    public async Task QueuedVerifyWithACancellableTokenAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1 });
        using var cts = new CancellationTokenSource();
        long least = long.MaxValue;
        for (int t = 0; t < Tries; t++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Calls; i++) Assert.True(await q.VerifyAsync("password123", Hash4, cts.Token));
            if (t > 0) least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equal(0, least);
    }

    [Fact]
    public async Task QueuedHashAllocatesOnlyItsString()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1 });
        long oneString = OnThisThread(() => GC.KeepAlive(new string('x', 60))) / Calls;
        long least = long.MaxValue;
        for (int t = 0; t < Tries; t++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Calls; i++) GC.KeepAlive(await q.HashAsync("password123", 4));
            if (t > 0) least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equal(oneString * Calls, least);
    }

    static readonly byte[] Hash4Ascii = Encoding.ASCII.GetBytes(Hash4);

    [Fact]
    public void Utf8VerifyAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        Assert.Equal(0, OnThisThread(() => Assert.True(Bcrypt.Verify("password123"u8, Hash4Ascii))));
        Assert.Equal(0, OnThisThread(() => Assert.False(Bcrypt.Verify("wrong"u8, Hash4Ascii))));
        Assert.Equal(0, OnThisThread(() => Assert.False(Bcrypt.Verify("password123"u8, "malformed"u8))));
        byte[] tooLong = new byte[100];
        Assert.Equal(0, OnThisThread(() => Assert.False(Bcrypt.Verify(tooLong, Hash4Ascii))));
    }

    [Fact]
    public void Utf8HashAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        byte[] destination = new byte[Bcrypt.HashLength];
        Assert.Equal(0, OnThisThread(() => Assert.True(Bcrypt.Hash("password123"u8, 4, destination) == 60)));
    }

    [Fact]
    public async Task QueuedUtf8VerifyAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1 });
        long least = long.MaxValue;
        for (int t = 0; t < Tries; t++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Calls; i++)
            {
                ValueTask<bool> a = q.VerifyAsync("password123"u8, Hash4Ascii), b = q.VerifyAsync("wrong"u8, Hash4Ascii);
                ValueTask<BcryptStatus> c = q.TryVerifyAsync("password123"u8, Hash4Ascii), d = q.TryVerifyAsync("wrong"u8, Hash4Ascii);
                Assert.True(await a & !await b);
                Assert.True(await c == BcryptStatus.Match & await d == BcryptStatus.NoMatch); // Assert.Equal allocates
            }
            if (t > 0) least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equal(0, least);
    }

    [Fact]
    public async Task QueuedUtf8HashAllocatesNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1 });
        byte[] destination = new byte[Bcrypt.HashLength];
        long least = long.MaxValue;
        for (int t = 0; t < Tries; t++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Calls; i++)
            {
                ValueTask<BcryptHash> a = q.HashAsync("password123"u8, 4);
                ValueTask<BcryptHashResult> b = q.TryHashAsync("password123"u8, 4);
                (await a).CopyTo(destination);
                var tried = await b;
                Assert.True(tried.Status == BcryptStatus.Hashed && tried.Hash.Length == 60);
            }
            if (t > 0) least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equal(0, least);
        Assert.True(Bcrypt.Verify("password123"u8, destination));
    }

    /// <summary>A full queue and a cancelled token are statuses: no exception, no task, nothing allocated.</summary>
    [Fact]
    public async Task BusyAndCancelledAllocateNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1, Lanes = 1, MaxQueued = 1 });
        var slow = q.VerifyAsync("slow", Bcrypt.Hash("slow", 13));
        for (int i = 0; q.QueueLength != 0; i++)
        {
            Assert.True(i < 10_000, "the slow check was never taken");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        var queued = q.TryVerifyAsync("password123"u8, Hash4Ascii);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        CancellationToken cancelled = cts.Token;
        long least = long.MaxValue;
        for (int t = 0; t < Tries; t++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Calls; i++)
            {
                Assert.True(await q.TryVerifyAsync("password123"u8, Hash4Ascii) == BcryptStatus.Busy);
                Assert.True((await q.TryHashAsync("password123"u8, 4)).Status == BcryptStatus.Busy);
                Assert.True(await q.TryVerifyAsync("password123"u8, Hash4Ascii, cancelled) == BcryptStatus.Cancelled);
                Assert.True((await q.TryHashAsync("password123"u8, 4, cancelled)).Status == BcryptStatus.Cancelled);
            }
            if (t > 0) least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.False(slow.IsCompleted); // the queue stayed full throughout
        Assert.Equal(0, least);
        Assert.True(await slow);
        Assert.Equal(BcryptStatus.Match, await queued);
    }

    /// <summary>
    /// The contest server's measure: 200 logins and 25 sign-ups once warm, through the workers and the pool, allocate
    /// nothing anywhere in the process. On a pool thread, as a server without a synchronization context runs them.
    /// <para>
    /// Everything is made before the first window. The process may allocate meanwhile for reasons of its own (a pool
    /// thread starting, a method tiering up; once 1,160 B on .NET 11 in CI), which only ever adds, while a real
    /// allocation per call shows in every window. So it measures up to six windows and keeps the least, and a failure
    /// lists them all. (GC allocation tick events sample every 100 KB, too coarse to name a few stray bytes.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoginsAndSignUpsAllocateNothing()
    {
        Assert.SkipWhen(Unoptimized, "Debug builds allocate where Release ones don't (no inlining, async state machines as classes): run in Release");
        const int Logins = 200, Windows = 6;
        using var q = new BcryptQueue(new BcryptQueueOptions { Threads = 1 });
        byte[] pw = "password123"u8.ToArray(), wrong = "password124"u8.ToArray(), destination = new byte[Bcrypt.HashLength];
        long[] bytes = new long[Windows];
        int[] matched = new int[Windows];
        int windows = await Task.Run(async () =>
        {
            for (int w = -1; w < Windows; w++) // -1: the warm-up
            {
                int matches = 0;
                long before = GC.GetTotalAllocatedBytes(precise: true);
                for (int i = 0; i < Logins; i++)
                {
                    if (await q.TryVerifyAsync(i % 2 == 0 ? pw : wrong, Hash4Ascii) == BcryptStatus.Match) matches++;
                    if (i % 8 == 0) (await q.TryHashAsync(pw, 4)).CopyTo(destination);
                }
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                if (w < 0) continue;
                bytes[w] = allocated;
                matched[w] = matches;
                if (allocated == 0) return w + 1;
            }
            return Windows;
        }, TestContext.Current.CancellationToken);
        Assert.All(matched[..windows], m => Assert.Equal(Logins / 2, m));
        Assert.True(bytes[windows - 1] == 0, $"every window allocated: {string.Join(", ", bytes)} B");
        Assert.True(Bcrypt.Verify("password123"u8, destination));
    }
}
