# Exclusive mode with a read transaction kept between groups

- **Question:** whether keeping a read transaction open on the shared connection, ended by the writer before its
  BEGIN, recovers the read speed [the first try](2026-10-21-sqlite-exclusive-first-try.md) lost.
- **Code:** codefusion-dotnet feat/sqlite-provider d328bfd. The same throwaway tool as the first try.
- **Machine:** the same: the 1-CPU container on Colima, and the 8-core Mac (load 16 to 18 by the Mac run).
- **Method:** the same loops; three rounds alternating with `synchronous=NORMAL`, two with FULL.

| 1 vCPU | Readers of their own | Exclusive |
|---|---|---|
| point reads, 1 thread, NORMAL | 1.55 to 1.67 M/s | 1.43 to 1.74 M/s |
| updates, 64 loops, NORMAL | 59 to 74 k/s | 58 to 69 k/s |
| 9 reads : 1 update, 16 loops, NORMAL | 49 to 78 k ops/s | 374 to 431 k ops/s |
| 9 reads : 1 update, 16 loops, FULL | 48 to 51 k ops/s | 144 to 179 k ops/s |

| 8-core Mac, NORMAL | Readers of their own | Exclusive |
|---|---|---|
| point reads, 4 threads | 6.11 to 6.83 M/s | 1.82 to 2.08 M/s |
| 9 reads : 1 update, 16 loops | 169 to 223 k ops/s | 80 to 238 k ops/s |

- **Memory and CPU:** not measured here.
- **Conclusion:** reads are back to the separate readers' speed, and the mixed load keeps its 5 to 8 times on one
  core. Kept, committed as d328bfd. A test written afterwards found that a reader looping back to back could make the
  writer wait for many reads ([the fix](2026-10-21-sqlite-exclusive-turns.md)), so these numbers were measured again.
