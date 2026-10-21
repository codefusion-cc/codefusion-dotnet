# The $12 entry on the library's spec options, and what the SQLite review fixes cost it

- **Question:** what the entry gains or loses with its hand-written auth, body and id checks replaced by
  feat/http-spec-options (Bearer tokens with a username claim, a 401 body per reason, body errors, `[Range]` ids), on
  DLLs that also carry the SQLite review fixes and the transport's latest commits.
- **Code:**
  - `cf-batch`: the previous entry, DLLs from e3b4ce0 ([its run](2026-10-21-twelve-entry-event-loop-batches.md)).
  - `cf-specopts`: the entry with `Auth.cs`, `Json.cs` and the id parser gone, DLLs from build/twelve-entry 854c44c
    (feat/sqlite-provider 13890fe, perf/http-transport 7ca8e69 and feat/http-spec-options d1a1f85 merged).
  - `cf-fixes-old-app`: cf-batch's app on DLLs from 2531854 (13890fe with 7ca8e69, no spec options), to split the two.
  - `cf-probe-no-commit-read`: the same with one line removed, the batch's commit before a read on the loop (review
    finding 7's fix). A probe, never committed.
- **Machine:** the Colima VM cf-bench (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro; the server pinned to CPU 0 with 2 GiB and
  no swap, the load generator on CPUs 1 to 3. The Mac's load was 2 to 6 from other sessions.
- **Method:** tools/bench/bench.sh (containers `sqlite-srv`): `test` first (42 of 42 for cf-specopts and
  cf-fixes-old-app), then `saturate 256 30`: 256 connections running bench/load.js's loop with no think time, 30 s
  after a 10 s warm-up, a fresh seed database each run. Variants alternated in the order below. Peak memory is the
  cgroup's `memory.peak` (resident memory plus the mapped file's page cache); peak RSS is the server process's
  `VmHWM`, measured from the bisect runs on.

| Run | Variant | Mix req/s | p50 | p95 | p99 | Server CPU | Peak memory | Peak RSS |
|---|---|---|---|---|---|---|---|---|
| 1 | cf-batch | 51,315 | 4.54 ms | 6.98 ms | 19.46 ms | 92.4% | 334 MiB | |
| 2 | cf-specopts | 45,836 | 4.83 ms | 14.58 ms | 18.23 ms | 88.8% | 298 MiB | |
| 3 | cf-specopts | 45,856 | 4.82 ms | 14.82 ms | 18.42 ms | 88.7% | 298 MiB | |
| 4 | cf-batch | 53,522 | 4.34 ms | 6.58 ms | 18.95 ms | 92.4% | 303 MiB | |
| 5 | cf-batch | 52,178 | 4.42 ms | 6.89 ms | 19.47 ms | 92.3% | 302 MiB | |
| 6 | cf-specopts | 47,022 | 4.67 ms | 14.77 ms | 18.61 ms | 88.3% | 299 MiB | |
| 7 | cf-fixes-old-app | 45,319 | 4.76 ms | 15.15 ms | 20.15 ms | 87.8% | 300 MiB | 83 MiB |
| 8 | cf-batch | 53,281 | 4.35 ms | 6.66 ms | 19.37 ms | 92.3% | 303 MiB | 85 MiB |
| 9 | cf-fixes-old-app | 45,542 | 4.92 ms | 14.28 ms | 18.15 ms | 89.2% | 299 MiB | 83 MiB |
| 10 | cf-specopts | 45,302 | 4.73 ms | 14.76 ms | 20.66 ms | 87.7% | 298 MiB | 81 MiB |
| 11 | cf-probe-no-commit-read | 50,066 | 4.49 ms | 8.02 ms | 19.67 ms | 91.2% | 302 MiB | 84 MiB |
| 12 | cf-fixes-old-app | 45,523 | 4.89 ms | 14.72 ms | 18.08 ms | 88.7% | 299 MiB | 82 MiB |
| 13 | cf-probe-no-commit-read | 49,135 | 4.36 ms | 9.16 ms | 21.24 ms | 89.9% | 302 MiB | 84 MiB |

No errors in any run.

- **Conclusion:**
  - The spec options cost nothing: cf-specopts and cf-fixes-old-app are level (45.3k to 47.0k against 45.3k to 45.5k).
    The entry keeps them.
  - The entry is 11% slower than cf-batch (51.3k to 53.5k), with p95 doubled and the server CPU lower: it waits.
  - Most of it is the finding-7 fix. A read on the loop commits the batch before it runs, so a wake-up that mixes
    writes and reads commits after every write a read follows, not once. Without that line the build does 49.1k to
    50.1k with p95 8 to 9 ms. The 3 to 7% left (the transport since d7fc5b5, or the other review fixes) is within this
    run's spread and needs its own rounds.
  - Next: keep finding 7's guarantee without the extra commits. A read on the loop runs inside the batch's
    transaction, and its answer is held to the COMMIT as the writes' answers are, failing with it. Then measure again.

## /posts/:id: the last hand-written step, gone without a library change

The entry awaited `/posts/:id`'s row (a copy into the request's arena) only because the handler had to answer an
invalid id itself, so it couldn't return the query. With the id bound by `[Range(1, long.MaxValue)]`, the library
answers an invalid id (after auth, as the spec asks), and the handler returns `db.Post(id)`: the row streams into the
reply as SQLite steps it, and no row is the database's `DataErrors.NotFound` (404 "post not found"). 42 of 42 checks
pass. The entry has it now; not measured yet. No library task is left for the entry.
