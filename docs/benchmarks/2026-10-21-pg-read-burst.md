# A burst of 32 reads on idle loops, before and after pipelined groups and shared bursts

- **Question:** the last recorded PgBenchmarks run had 32 reads queued together at 160 to 178 µs per read against
  Npgsql's 105 to 123, the one case where the client was slower. Does sharing a burst among sleeping read loops (and
  pipelining groups) fix it?
- **Code:** before: feat/codefusion-packages 4e8dc9f. After: perf/pg-pipelining dc81c30.
- **Machine:** Apple M-series MacBook Pro, 8 cores shared with other sessions (load average 15 to 25 during the
  runs). Postgres 18 on the same machine (scratch cluster, fsync off). SDK 10.0.301.
- **Method:** `PgBenchmarks.ReadGroup32`, `--job short --inProcess` (the SDK global.json pins isn't installed here,
  so the in-process toolchain), before and after alternating, two rounds. Per read: the run's mean over 32 reads.

| Round | Before, ours | Before, Npgsql | After, ours | After, Npgsql |
|---|---|---|---|---|
| 1 | 172.2 µs | 110.2 µs | 86.6 µs | 106.3 µs |
| 2 | 162.5 µs | 107.4 µs | 69.7 µs | 122.2 µs |

- **Memory and CPU:** no allocations from the client in either (the benchmark's own tasks and arrays only). Not
  measured: CPU per read.
- **Conclusion:** the burst no longer runs on one backend after another: the first loop takes its share (at least 4)
  and wakes the next, so the 32 reads run on several connections at once. From 1.5 times slower than Npgsql to 1.2
  to 1.8 times faster. The Mac was busy with other work: read the ratio, not the absolute numbers. Next: the
  TechEmpower db, queries and updates tests on the GCP rig.
