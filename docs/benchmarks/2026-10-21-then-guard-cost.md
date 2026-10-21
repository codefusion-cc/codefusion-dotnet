# What the check after Query.Then costs Follow and Unfollow

- **Question:** every Then with a Select now runs a check of the catalog in its own statement: rules, triggers,
  partitions, the keys a delete acts on, and the columns of a corrected read. How much does it add to Conduit's
  Follow (an insert) and Unfollow (a delete), which the contest calls on every follow toggle?
- **Code:** feat/data-linq-shapes at 85326a9 (the walk over partitions and update edges). `ThenGuardBenchmarks`
  runs each statement as the Conduit context compiles it, and again with the condition of its final WHERE taken out.
- **Machine:** Apple M1 Pro, macOS, .NET 10.0.13, BenchmarkDotNet 0.15.8, Postgres 18.4 on the same Mac. The Mac was
  shared and busy: its load average was 15 to 22 on 8 cores throughout.
- **Method:**
  - `dotnet run -c Release --project benchmarks/CodeFusion.Benchmarks -- --job short --filter "*ThenGuardBenchmarks*"`
    with BENCH_DB pointing at conduit_bdn_pg, run twice. Each statement is prepared on one Npgsql connection, over
    PgBenchmarks' small seed: 200 users, the follow of user7 by user 3. After the first call, Follow's key is there
    (ON CONFLICT skips it) and Unfollow finds no row, so each call does the same work.
  - The check alone was timed with pgbench: `-M prepared -t 3000 -c 1`, as `SELECT <check> = 0` and in parts, six
    rounds in rotating order. The fastest round is the one least disturbed by the load, so the table gives that one.

| Case | Without the check | With it | Change |
|---|---|---|---|
| Follow, BenchmarkDotNet mean, run 1 / run 2 | 138.9 / 107.9 µs | 253.6 / 191.3 µs | +115 / +83 µs |
| Unfollow, BenchmarkDotNet mean, run 1 / run 2 | 182.9 / 94.0 µs | 238.7 / 173.1 µs | +56 / +79 µs |
| Follow's check alone, pgbench, fastest of 6 | `SELECT 1`: 25 µs | 57 µs | +32 µs |
| the same, pg_class by oid only | 25 µs | 30 µs | +5 µs |
| the same, without the column check | 25 µs | 47 µs | +22 µs |

- **Memory and CPU:** about 1.1 KB allocated per call on both sides, all of it Npgsql's.
- **Conclusion:**
  - Measured alone, the check costs about 30 µs per statement on this Mac. About 17 µs of that is the walk: pg_inherits
    and the trigger probe, even for an insert, whose key walk is empty. About 10 µs is the column check of a
    corrected read: its `v.t::regtype` parses a type name on every execution.
  - End to end, the difference was larger and very noisy on the loaded Mac: +56 to +115 µs on statements of about
    100 to 180 µs. The Error column of the short job is larger than the difference itself, so these numbers bound
    the cost but don't settle it.
  - A delete pays more for each table it can reach, so the cost grows with how far it cascades. Conduit's follows
    reaches nothing.
  - The check is kept, as it is what makes the read after the write right on a database the model can't see. To try
    next, on a quiet machine with the full job:
    - bind each column's type as a regtype constant, which the plan folds once;
    - leave pg_inherits out where the table has no children, `relhassubclass`.
