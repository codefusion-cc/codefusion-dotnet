# The allocation gate's first run, with dotnet-counters, on the merged build

- **Question:** under each test's load for a minute, does CodeFusion collect or allocate anything?
- **Code:** perf/techempower with perf/data-batched-updates and perf/pg-pipelining (dc81c30) merged locally (the
  rig's codefusion-pg build), before the writer queue (79bfe25) and job pool (d612116) fixes.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `scripts/TechEmpowerRig.cs gate codefusion-pg 60`: per test a 30 s warmup, then wrk for 60 s at 1,024
  (plaintext, pipelined) or 512 connections (queries and updates at 20), with dotnet-counters attached to the
  server collecting System.Runtime every second; the GC counts and bytes allocated summed over the session.

| Test | gen0/1/2 | Allocated in 69 s | Per second | req/s |
|---|---|---|---|---|
| plaintext | 0/0/0 | 3,178,776 B | 46 KB/s | 5,969,092 |
| json | 0/0/0 | 2,709,504 B | 39 KB/s | 761,043 |
| db | 0/0/0 | 3,169,720 B | 46 KB/s | 529,926 |
| queries (20) | 0/0/0 | 8,042,784 B | 117 KB/s | 55,779 |
| fortunes | 0/0/0 | 3,111,352 B | 45 KB/s | 410,298 |
| updates (20) | 0/0/0 | 7,511,584 B | 109 KB/s | 39,713, mostly 500s (writes refused: fixed in 79bfe25) |

- **Memory and CPU:** no collection of any generation in any test.
- **Conclusion:**
  - Four tests sit at the same 39 to 46 KB/s whatever their rate (6M down to 410k req/s): dotnet-counters' own
    session in the process, not the requests. The gate can't read an exact zero through it, so it now reads the
    app's own report instead (CODEFUSION_GC_REPORT, written without allocating; 1d7239e).
  - Queries and updates had about 70 KB/s more, roughly 1 B per request: the Postgres job pool kept 4,096 jobs
    while a request's 20 reads started together put 10,240 in flight, so each burst made about 6,000 jobs anew
    (5.3 MB per burst in the allocation test that now pins it). The pool keeps 16,384 since d612116.
  - Next: the gate again on the build with every branch merged, through the app's report.
