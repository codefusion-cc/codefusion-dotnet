# Exclusive mode against readers of their own, after turn-taking

- **Question:** what turn-taking changed in throughput, and the numbers the package README quotes.
- **Code:** codefusion-dotnet feat/sqlite-provider bbbd73f, measured with
  `benchmarks/CodeFusion.Data.Sqlite.Throughput` (added with this file).
- **Machine:** Apple M1 Pro, 8 cores, load 8 to 23 from other sessions; and a container pinned to one CPU with 2 GiB on
  the Colima VM cf-bench (vz, linux-arm64, `mcr.microsoft.com/dotnet/runtime:10.0`). .NET 10, SQLite 3.50.4.
- **Method:** `CodeFusion.Data.Sqlite.Throughput <default|exclusive> <normal|full> 2`: 100,000 rows, 2 s each of point
  reads on 1 and 4 threads, updates from 64 awaiting loops and from 1, and 16 loops of 9 reads and an awaited update.
  The container: `docker run --cpuset-cpus=0 --memory=2g --memory-swap=2g`. Three rounds alternating the modes with
  NORMAL, two with FULL; ranges over the rounds.

| 1 vCPU | Readers of their own | Exclusive |
|---|---|---|
| point reads, 1 thread, NORMAL | 1.51 to 1.71 M/s | 1.46 to 1.70 M/s |
| updates, 64 loops, NORMAL | 63 to 65 k/s | 64 to 71 k/s |
| updates, 1 loop, NORMAL | 31 to 41 k/s | 39 to 46 k/s |
| 9 reads : 1 update, 16 loops, NORMAL | 50 to 77 k ops/s | 388 to 486 k ops/s |
| 9 reads : 1 update, 16 loops, FULL | 74 to 75 k ops/s | 120 to 198 k ops/s |

| 8-core Mac, NORMAL | Readers of their own | Exclusive |
|---|---|---|
| point reads, 1 thread | 1.65 to 1.80 M/s | 1.51 to 1.96 M/s |
| point reads, 4 threads | 6.37 to 6.44 M/s | 1.21 to 1.78 M/s |
| 9 reads : 1 update, 16 loops | 165 to 214 k ops/s | 217 to 316 k ops/s |

- **Memory and CPU:** 0 B per query in both modes ([allocation](2026-10-21-sqlite-provider-allocation.md)).
- **Conclusion:** turn-taking cost nothing in throughput and helps the mixed load. On one core Exclusive runs the
  read-mostly mix 5 to 8 times faster with NORMAL and 1.6 to 2.7 times with FULL, with reads and writes alone about
  equal. On many cores it caps reads at one connection. The $12 entry should use it; the next step for that entry is one
  commit per event-loop wake-up.
