# CodeFusion.Data.Postgres

A Postgres client for .NET 10 that allocates nothing per query. It speaks the wire protocol itself: statements are
prepared once per connection and then only bound and executed with binary parameters, and a row handler reads each
row in place, in the connection's receive buffer.

A few connections do all the work. A `PgReader`'s loops each take whatever reads have queued and send them as one
batch; a `PgWriter`'s loops commit whatever writes have queued in one transaction (group commit), so one WAL flush
commits them all. Requests never hold a connection, and a refused or failed command comes back as a status, not an
exception.

An update of one row by its key whose values are all parameters (`Where(w => w.Id == id).Update(w => new World {
RandomNumber = n })`) goes further: a group commit's run of them becomes one statement, each parameter an array of
theirs unnested. A run ends at any other statement and before a key comes twice, so the group's order holds and every
write is checked as it would be alone. If the merged statement fails, the writes go again unmerged, each in a
savepoint of its own, in one transaction per 64 writes: the writes in one round trip and the commit in another. Each
gets its own outcome, only a write at fault gets an error, and the rest commit together. If the commit itself fails
(a deferred constraint), nothing of it committed and each write runs again alone. A table with a trigger or a rule, partitions or inheritance children, or a
unique index on a column the update sets never takes merged updates: a check of the table runs first, as its own
statement in the same transaction, and the update is skipped. Such a statement goes unmerged for five minutes, then
is tried again; `PgCounters.Unmerged` counts each time. So does one whose merged run failed with no write at fault,
unless another transaction failed it (a deadlock, a serialization failure, a lock or statement timeout).

A read loop keeps up to two groups in flight on its connection (`PgReader.MaxInFlight`): it sends what has queued
while Postgres still answers the group before, so the backend never sits a round trip without work. A burst that
arrives while other read loops sleep is shared out among them, so its reads run on several backends at once.

A statement made with `RunsAlone = true` (or a query method marked `[RunsAlone]`) runs apart: on connections of their
own, each command in its own transaction, so a slow sign-up or report holds up no group commit or read group. A reader
or writer opens `PgSettings.AloneConnections` of them (2 by default, at least 1) at its first such command, on top of
its loops' connections; each runs one command at a time and the next goes to a free one, so one slow report holds up no sign-up.

It is also CodeFusion.Data's Postgres provider: a context deriving from `DataContext<Postgres>` has its LINQ
compiled to Postgres's SQL at build time and run on these loops, registered with
`services.AddDatabase<ConduitDb>(Postgres.FromUrl(url), readers, writers)`, or `services.AddDatabase<ConduitDb>(source)`
with a `PgDataSource` the app built itself (its own loops, error replies or failure log, its loops started).

```bash
dotnet add package CodeFusion.Data.Postgres
```

## Example

```csharp
using CodeFusion.Data.Postgres;

// Parsed once per connection, then only bound and executed: make each statement once (a static field).
var articleBySlug = new PgStatement("SELECT title, favorites FROM articles WHERE slug = $1", PgType.Text);

var settings = PgSettings.Parse(Environment.GetEnvironmentVariable("DATABASE_URL")!) // postgres://user:pw@host/db
    .Set("statement_timeout", "30s");
var reader = new PgReader(settings);
reader.Start(16); // 16 connections, each with a loop thread of its own

var result = await reader.Command(articleBySlug).Text(slug).Run(static (in PgRow row, object? state) =>
{
    // The row is read in place, in the connection's receive buffer: copy what you keep.
    ReadOnlySpan<byte> title = row.GetBytes(0);
    return row.GetInt64(1); // becomes result.Value
}, null);

if (result.IsOk && result.Rows == 1) Console.WriteLine($"{result.Value} favorites");
```

`slug` is a `ReadOnlySpan<byte>`, straight from a request's buffer if you like: parameters are copied as they are
added. The task completes on the loop's thread, so continue quickly and never block there.

## Zero allocations, measured

`PgAllocationTests` counts every byte the whole process allocates (`GC.GetTotalAllocatedBytes`) over 20,000 reads
and 4,000 writes from 8 callers after warm-up, against a real Postgres 18: about 4 KB in all, which is the test's own
8 tasks, or 0.17 B per operation.

What makes that possible is a pool of commands (jobs), and its memory is bounded. The shared pool holds at most
32 MB, each job counted whole (about 512 B of its own and its buffers); past that a returning job grown past 4 KB
goes, and a smaller one takes the place of the jobs used least recently.
It also keeps no more jobs than the started readers and writers can hold at once (their queues and groups in
flight, given back when they complete), at most 16,384, and never more than were ever out together. A buffer past
64 KB is never kept. Each thread keeps up to 32 jobs whose buffers are 4 KB or less in front of the shared pool,
and gives them back when it ends: at most 32 × 8.5 KB a thread on top of the 32 MB. Each loop that starts makes the
jobs its groups and its thread's cache can hold (and the first, a cache's worth for each core's callers), so steady
load makes none: a thread whose cache ran dry finds the shared pool never empty while others' caches hold jobs.

Against Npgsql 10 as the contest server used it before (BenchmarkDotNet short run, Postgres 18 on the same Apple
M-series Mac, 16 read loops and 2 writers for both, rows read as bytes the way a JSON writer reads them). Client CPU
is the benchmark process's over its operations; Postgres's own CPU is not in it.

| | Mean, ours | Mean, Npgsql | Client CPU, ours | Client CPU, Npgsql | Npgsql allocated |
|---|---|---|---|---|---|
| 64 concurrent callers, per read | 51 µs | 50 µs | 14.3 µs | 30.2 µs | 1.5 KB |
| Write group of 64, per write | 47 µs | 45 µs | 14.1 µs | 21.5 µs | 1.1 KB |
| Article by slug, one at a time | 88 µs | 127 µs | 79 µs | 132 µs | 2.0 KB |
| Write group of 1 | 132 µs | 163 µs | 78 µs | 158 µs | 2.2 KB |

The rows under load are the fair CPU comparison: half the client CPU per read at the same throughput, which
Postgres on the same 8 cores limits. One at a time, most of the CPU is the benchmark's own waiting. BenchmarkDotNet's
"Allocated" for this client reads 180 to 350 B per operation: that is the benchmark's own async methods and arrays,
the same for both clients.

An idle burst of 32 reads takes 70 to 87 µs per read against Npgsql's 106 to 122: the burst is shared out among the
loops asleep, so it runs on several backends at once (it took 162 to 172 µs on one before,
[2026-10-21-pg-read-burst.md](../../docs/benchmarks/2026-10-21-pg-read-burst.md)). Under load, one Sync per group is
what saves Postgres CPU.

## Limits

No TLS: use it on a private network or over a tunnel. A connection string, URL or libpq environment variable
(`PGSSLMODE` and the like) that asks for TLS makes connecting fail instead of going on in the clear. Logins are
SCRAM-SHA-256 (with SASLprep, as libpq does; a password that needs Unicode normalization needs ICU, so not with
InvariantGlobalization) or trust without a password. Cleartext, md5, SCRAM below 4096 iterations and trust while a
password is set need `AllowWeakPasswordAuth = true`. Parameter types are `int8`, `int4`, `bool`, `float8`,
`timestamptz`, `text` and `text[]`. A row's getters check each column's type from its RowDescription: an int4 reads as
a double too, but an int8 or a float read as the other throws `InvalidCastException`.

A statement that opens, commits or rolls back a transaction itself (BEGIN, COMMIT, ROLLBACK and the like) breaks a
write's group: its writes end `Broken` and the connection is replaced. So does one that drops the statements the
connection prepared (DEALLOCATE ALL, DISCARD ALL, or a DEALLOCATE of one of them), in a read's group too: the next
connection parses them again. The app's own `DEALLOCATE myplan` leaves the connection be. A DEALLOCATE run inside a
function or a `DO` block isn't seen, and the next use of a statement it dropped fails with 26000. After a
failed connect a loop waits `ReconnectDelay`, doubling up to `MaxReconnectDelay`, and fails its commands at once
meanwhile.
