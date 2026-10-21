# CodeFusion with Each for queries and updates, and fortunes sorted without allocating

- **Question:** what do `db.Each` (a request's 20 reads, then its 20 writes, started together) and the fortunes fixes
  (an allocation-free sort, the page in one span) change against the first run?
- **Code:** before: perf/techempower ba052ee ([the first run](2026-10-21-tfb-baseline.md)). After: perf/techempower
  as of 0d78552 (Each 1d0ccb4/ccdb2f1, fortunes 844ad61 and 25c6f2c, rebuilt on 4e8dc9f).
- **Machine:** the reduced rig, as in the first run (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** TFB's sequence (`scripts/TechEmpowerRig.cs bench codefusion`), one round, about 2 h after the first.

| Test | Before | After | Change |
|---|---|---|---|
| plaintext | 6,575,444 | 6,192,771 | -6% (same HTTP code: noise) |
| json | 835,600 | 788,360 | -6% (same HTTP code: noise) |
| db | 520,160 | 518,064 | 0% |
| queries (20) | 45,193 | 49,497 | +10% |
| fortunes | 430,145 | 427,298 | -1% |
| updates (20) | 6,830 | 4,285 | -37% |

- **Memory and CPU:**
  - Fortunes: RSS from 1,373 MB to 77 MB, no collection (one gen0 and one gen1 before), about 3 B per request
    against 105, and that remainder is the counters' own noise (1.2 MB/s on every test, idle included).
  - Queries: Postgres from 64% to 73% busy at a higher rate.
  - Updates: the app from 39% to 70% busy, Postgres from 30% to 70%.
- **Conclusion:**
  - Keep Each for queries: one batch per request's reads instead of 20 round trips.
  - The fortunes fix removed its only allocation; its throughput is CPU per request elsewhere.
  - Updates got worse: a request's 20 writes now reach the writer at once, so its group commits are full (128
    UPDATE statements in one transaction, often on the same rows), and one writer runs them one statement after
    another. The batched keyed updates (perf/data-batched-updates) turn such a group into one statement; that is
    the next run.
  - Plaintext and json moved 6% with unchanged code: one round is not enough to read differences under about 6%.
    Later comparisons of the HTTP tests need repeat rounds.
