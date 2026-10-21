using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;
using static CodeFusion.Data.Postgres.Tests.PgTestDb;

#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>Alone, so no other test's allocations are counted.</summary>
[CollectionDefinition(nameof(Alone), DisableParallelization = true)]
public sealed class Alone;

/// <summary>
/// After warm-up a read and a write through the client allocate nothing: every byte counted while they run comes
/// from somewhere else. The whole process is counted (GC.GetTotalAllocatedBytes), loop threads included.
/// </summary>
[Collection(nameof(Alone))]
public sealed class PgAllocationTests
{
    static readonly PgStatement Page = new(
        "SELECT g, 'title ' || g, g % 2 = 0, now(), convert_to('[\"a\",\"b\"]', 'UTF8'), count(*) OVER () FROM generate_series(1, $1::int8) g WHERE $2::text <> ''",
        PgType.Int8, PgType.Text);

    /// <summary>What a handler does with a row: reads every column, as the JSON writers do.</summary>
    static readonly PgRowHandler Touch = static (in PgRow r, object? s) =>
    {
        long sum = r.GetInt64(0) + r.GetBytes(1).Length + (r.GetBoolean(2) ? 1 : 0) + r.GetUnixMicroseconds(3) % 7 + r.GetBytes(4).Length;
        ((long[])s!)[0] += sum;
        return r.GetInt64(5);
    };

    const int Reads = 20_000, Writes = 4_000;

    static bool Debug() => typeof(PgReader).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
        is [System.Diagnostics.DebuggableAttribute { IsJITOptimizerDisabled: true }];

    [Fact(Timeout = 120_000)]
    public async Task ReadsAndWritesAllocateNothingOnceWarm()
    {
        Require();
        Assert.SkipWhen(Debug(), "Debug builds make every async method's state machine a class: run in Release");
        string table = "pg_alloc_" + Guid.NewGuid().ToString("N");
        await using var admin = Admin();
        await admin.Scalar($"CREATE TABLE {table} (id bigint PRIMARY KEY, n bigint NOT NULL, note text)");
        await admin.Scalar($"INSERT INTO {table} SELECT g, 0, NULL FROM generate_series(1, 64) g");
        var write = new PgStatement($"UPDATE {table} SET n = n + 1, note = $2 WHERE id = $1 RETURNING n", PgType.Int8, PgType.Text);
        var reader = new PgReader(Settings());
        var writer = new PgWriter(WriterSettings(TimeSpan.FromSeconds(2)));
        reader.Start(2);
        writer.Start(1);
        try
        {
            var state = new long[1];
            // off xunit's synchronization context, so continuations run where the loops complete them
            async Task<long> Run(int reads, int writes, int callers)
            {
                var tasks = new Task<long>[callers];
                for (int c = 0; c < callers; c++)
                {
                    int me = c;
                    tasks[c] = Task.Run(async () =>
                    {
                        long bad = 0;
                        for (int i = me; i < reads; i += callers)
                        {
                            var res = await reader.Command(Page).Int8(20).Text("x"u8).Run(Touch, state).ConfigureAwait(false);
                            if (!res.IsOk || res.Rows != 20 || res.Value != 20) bad++;
                        }
                        for (int i = me; i < writes; i += callers)
                        {
                            var res = await writer.Command(write).Int8(i % 64 + 1).Text(i % 3 == 0 ? null : "note").Run(Touch2, state).ConfigureAwait(false);
                            if (!res.IsOk || res.Rows != 1) bad++;
                        }
                        return bad;
                    });
                }
                long failures = 0;
                foreach (var t in tasks) failures += await t.ConfigureAwait(false);
                return failures;
            }

            Assert.Equal(0, await Run(4000, 1000, 8)); // warm: pools, buffers, prepared statements, JIT
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long failed = await Run(Reads, Writes, 8);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.Equal(0, failed);
            TestContext.Current.TestOutputHelper?.WriteLine($"{allocated} bytes for {Reads + Writes} operations");
            // 8 callers' Task.Run and async boxes (a few hundred bytes each) are the test's own; under 1 byte per operation
            if (allocated < Reads + Writes) return;
            // Only now the sampler, for a second run: its event dispatch allocates tens of KB of its own, so it can't
            // watch the measured one.
            using var sampled = new AllocationSampler();
            await Run(Reads, Writes, 8);
            Assert.Fail($"{allocated} bytes for {Reads + Writes} operations ({(double)allocated / (Reads + Writes):F2} B/op); sampled: {sampled}");
        }
        finally
        {
            LoopChecks.Stop(reader, writer);
            await admin.Scalar($"DROP TABLE {table}");
        }
    }

    /// <summary>The runtime's allocation samples (one per ~100 KB allocated), by type: what to look at when the test fails.</summary>
    sealed class AllocationSampler : EventListener
    {
        readonly ConcurrentDictionary<string, int> _types = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // GC
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName == "GCAllocationTick_V4" && e.PayloadNames is { } names)
                _types.AddOrUpdate((string)e.Payload![names.IndexOf("TypeName")]!, 1, (_, n) => n + 1);
        }

        public override string ToString() => string.Join(", ", _types.OrderByDescending(t => t.Value).Select(t => $"{t.Key} x{t.Value}"));
    }

    static readonly PgRowHandler Touch2 = static (in PgRow r, object? s) => ((long[])s!)[0] += r.GetInt64(0);

    static readonly PgStatement Times10 = new("SELECT $1::bigint * 10", PgType.Int8);
    static readonly PgRowHandler First = static (in PgRow r, object? _) => r.GetInt64(0);

    /// <summary>Starts <paramref name="pending"/>'s reads all at once, then reads their results: the jobs of every
    /// read are out of the pool together.</summary>
    static async Task<long> Burst(PgReader reader, ValueTask<PgResult>[] pending)
    {
        for (int i = 0; i < pending.Length; i++) pending[i] = reader.Command(Times10).Int8(i).Run(First, null);
        long sum = 0;
        for (int i = 0; i < pending.Length; i++) sum += (await pending[i].ConfigureAwait(false)).Value;
        return sum;
    }

    [Fact(Timeout = 120_000)]
    public async Task AsManyReadsInFlightAs512RequestsOf20AllocateNothingOnceWarm()
    {
        Require();
        Assert.SkipWhen(Debug(), "Debug builds make every async method's state machine a class: run in Release");
        // TechEmpower's queries test with each request's reads started together: 512 times 20 in flight at once
        var reader = new PgReader(Settings());
        reader.Start(4);
        try
        {
            var pending = new ValueTask<PgResult>[512 * 20];
            long expected = 10L * pending.Length * (pending.Length - 1) / 2;
            Assert.Equal(expected, await Task.Run(() => Burst(reader, pending)));
            Assert.Equal(expected, await Task.Run(() => Burst(reader, pending)));
            var (sum, allocated) = await Task.Run(async () =>
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                long s = await Burst(reader, pending).ConfigureAwait(false);
                return (s, GC.GetTotalAllocatedBytes(precise: true) - before);
            });
            Assert.Equal(expected, sum);
            // the test's own async method: a few hundred bytes; a job made anew is about a kilobyte, per read
            Assert.True(allocated < 4096, $"{allocated} bytes for {pending.Length} reads");
        }
        finally
        {
            LoopChecks.Stop(reader);
        }
    }
}
