# The $12 entry with reads joining the batch only by table

- **Question:** the [previous run](2026-10-21-twelve-entry-read-in-batch.md) put the entry's last 5% below e3b4ce0 on
  reads waiting for the batch's COMMIT. With a read joining the batch only when the batch wrote a table it reads (e000e49:
  the tables as SQLite's authorizer reports them while preparing), how much comes back?
- **Code:** `cf-batch`, the e3b4ce0 entry, against `cf-tables`: the entry on build/twelve-entry 150f0e6
  (feat/sqlite-provider 0d14741 with perf/http-transport 7ca8e69 and feat/http-spec-options e955c3e). 42 of 42.
- **Machine:** the Colima VM cf-bench (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro; the server pinned to CPU 0 with 2 GiB and
  no swap, the load generator on CPUs 1 to 3. The Mac's load was 3.5 to 4.9 from other sessions.
- **Method:** tools/bench/bench.sh (containers `sqlite-srv`): `test`, then `saturate 256 30` (256 connections, no think
  time, 30 s after a 10 s warm-up, a fresh seed database each run). Three rounds alternating the builds.

| Run | Variant | Mix req/s | p50 | p95 | p99 | Server CPU | Peak RSS |
|---|---|---|---|---|---|---|---|
| 1 | cf-batch | 53,930 | 4.29 ms | 6.52 ms | 19.60 ms | 91.5% | 85 MiB |
| 2 | cf-tables | 51,780 | 4.52 ms | 6.70 ms | 19.20 ms | 92.6% | 86 MiB |
| 3 | cf-tables | 52,568 | 4.45 ms | 6.55 ms | 19.12 ms | 92.6% | 86 MiB |
| 4 | cf-batch | 52,322 | 4.28 ms | 7.36 ms | 19.92 ms | 91.3% | 85 MiB |
| 5 | cf-batch | 51,462 | 4.31 ms | 7.53 ms | 19.91 ms | 91.2% | 84 MiB |
| 6 | cf-tables | 51,790 | 4.58 ms | 6.38 ms | 18.47 ms | 92.7% | 86 MiB |

Peak memory (the cgroup's, with the mapped file's page cache) 302 to 335 MiB. No errors.

- **Conclusion:** level with e3b4ce0 within this run's spread: 51.8k to 52.6k against 51.5k to 53.9k, about 1% below on
  average, with p95 and p99 a little lower. Against the [previous run](2026-10-21-twelve-entry-read-in-batch.md) the
  gap closed from about 5% to about 1%, though e3b4ce0 ran 1 to 2k lower here than there (the Mac's load was higher),
  so part of the closing is the baseline. The entry's reads use posts and likes, which most wake-ups also write, so
  most of them still wait for the COMMIT: that is the guarantee's cost, and the work stops here. e3b4ce0 answered those
  reads from writes its COMMIT could still lose.
