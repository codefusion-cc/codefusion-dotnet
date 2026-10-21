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

## Second round: the check after the third review (B1 to B3)

- **Question:** the third review's fixes add two checks: a table held under a table read (a mid-level partition, an
  inheriting table), and the found table and the table written as one partition tree. They also take each inserted
  column's types as a regtype[] constant instead of a name cast per execution, and skip pg_inherits for a table
  without children. Do they cost more than the check they replace, or less?
- **Code:**
  - A is 7e3a51d, the check of the first round.
  - B is the branch after the fixes, in two forms. cf76d02 took the descendants of the tables read in a second
    recursive CTE. 9175fe6 matches each table walked with its ancestors instead, through pg_inherits by inhrelid.
- **Machine:** the same M1 Pro and Postgres 18.4, still shared, with a load average of 9 to 54 during the runs.
- **Method:**
  - pgbench as before, each side's check for Follow and Unfollow alone, six rounds in A B, B A order. The fastest
    round is given.
  - BenchmarkDotNet's short job, ThenGuardBenchmarks built from each side, run in A B B A order.

| Case | A | B | Change |
|---|---|---|---|
| Follow's check alone, cf76d02 (descendants CTE), fastest of 6 | 60 µs | 66 µs | +6 µs |
| Unfollow's check alone, cf76d02, fastest of 6 | 74 µs | 86 µs | +12 µs |
| Follow's check alone, the ancestors form, beside the first, fastest of 5 | 62 µs | 64 µs | +2 µs |
| Follow's check alone, 9175fe6, fastest of 6 (load 9 to 10) | 67 µs | 73 µs | +6 µs |
| Unfollow's check alone, 9175fe6, fastest of 6 | 68 µs | 82 µs | +14 µs |
| Follow with the check, BenchmarkDotNet, A B B A | 146.7, 170.1 µs | 108.6, 155.0 µs | within noise |
| Unfollow with the check, BenchmarkDotNet, A B B A | 138.7, 215.8 µs | 126.1, 231.4 µs | within noise |

`SELECT 1` took 25 to 29 µs in the quieter rounds.

- **What the parts cost (Follow, alone, fastest of 4 or 5, beside A at 54 to 63 µs):**
  - A with the regtype[] constants: 57 µs. The name cast didn't measure here: in the cached plan the VALUES rows
    are constants, and the parse is lost in the noise.
  - B without the two new checks: 57 to 58 µs.
  - B with them as a second recursive CTE: 66 to 70 µs. B with them as the ancestors walk: 64 µs, which is why
    9175fe6 replaced cf76d02.
- **Conclusion:**
  - The two new checks cost a few µs per statement on the noisy Mac: 2 to 6 for Follow, up to 14 for Unfollow.
  - The regtype[] constant and the relhassubclass gate save about as much as the walk to the ancestors costs.
  - End to end the difference stays inside BenchmarkDotNet's error.
  - We keep 9175fe6. A quiet machine with the full job would show whether the constant types save anything at all.
