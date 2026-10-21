# The SQLite provider prototype on one vCPU with 2 GB

- **Question:** whether the Mac's numbers hold on the shape of the $12 contest's box, before building for it.
- **Code:** codefusion-sqlite-prototype f21a476, the same benchmarks and checks as
  [the Mac run](2026-10-20-sqlite-prototype-vs-microsoft-data-sqlite.md).
- **Machine:** a Colima VM (vz) on the Apple M1 Pro, 1 vCPU and 2 GiB, an Ubuntu 24.04 container, .NET 10.0.13,
  linux-arm64 `e_sqlite3` 3.50.4. Other sessions shared the Mac.
- **Method:** BenchmarkDotNet `--job short` in the VM, and the prototype's `checks scale`. The short job is noisier
  there (some errors are larger than the means): the large ratios hold, the near-ties only say "about equal".

| Case | CodeFusion | Microsoft.Data.Sqlite | Dapper |
|---|---|---|---|
| point read | 510 ns, 0 B | 2,976 ns, 672 B | 12,753 ns, 2,003 B |
| 20 rows | 4.24 µs, 0 B | 12.08 µs, 3,864 B | 41.49 µs, 8,043 B |
| point reads from 8 threads on the one core, per read | 521 ns | 3,409 ns | |
| insert, one at a time, NORMAL | 24.5 µs (±58) | 18.1 µs | |
| insert, 64 writers, per write, NORMAL | 3.7 µs | 27.5 µs (4.3 µs batched by hand) | |
| insert, one at a time, FULL | 127 µs | 140 µs | |
| insert, 64 writers, per write, FULL | 7.7 µs | 122.8 µs (6.2 µs batched by hand) | |

`checks scale` there: 1.70 M point reads/s on 1 thread, and 1.74, 1.63 and 1.88 M/s with 2, 4 and 8 threads sharing
the core.

- **Memory and CPU:** all checks pass there, 0 B per read and per write.
- **Conclusion:** inline reads are right on one core (a hop to a reader thread would be a context switch per read).
  More threads than cores cost nothing: a reader spins only while every connection is taken. A lone write pays the hop
  to the writer thread (about 6 µs, inside the noise), which led to trying leader commit
  ([its run](2026-10-20-sqlite-leader-commit.md)). One shard is enough on one core.
