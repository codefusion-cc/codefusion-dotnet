# How many Postgres read and write loops, on the db, queries and updates tests

- **Question:** the app ran one read loop per core and one writer. What do other counts give, now that read groups
  are pipelined and a group's keyed updates merge into one statement?
- **Code:** perf/techempower with perf/data-batched-updates and perf/pg-pipelining (to 79bfe25, the writer queue
  fix included) merged locally: the rig's codefusion-pg build.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `scripts/TechEmpowerRig.cs quick`: per setting, a 10 s warmup and 15 s measured at 512 connections
  (queries and updates at 20 per request), one round, the settings one after another.

| Test | Setting | req/s | p99 |
|---|---|---|---|
| db | 2 readers | 328,424 | 1.94 ms |
| db | 4 readers | 526,006 | 1.73 ms |
| db | 8 readers (default then) | 538,808 | 2.31 ms |
| db | 16 readers | 545,438 | 2.35 ms |
| queries (20) | 4 readers | 47,483 | 11.14 ms |
| queries (20) | 8 readers | 59,599 | 9.26 ms |
| queries (20) | 16 readers | 64,862 | 13.23 ms |
| updates (20) | 1 writer (default) | 13,514 | 39.43 ms |
| updates (20) | 2 writers | 20,475 | 28.05 ms |
| updates (20) | 4 writers | 25,168 | 23.44 ms |

- **Memory and CPU:** no errors and no non-2xx answers in any setting.
- **Conclusion:**
  - Reads: past 4 loops db barely moves; queries gains 9% from 8 to 16. The app's default is now two readers per
    core.
  - Writes: with merged updates the writers no longer step on each other, and 4 give 86% more than 1. 25,168 is
    past the best Rust entry's 22,669 here (may-minihttp, its full sequence), though this is one quick run. Only
    the batched build gets 4 writers: on perf/techempower alone a group is many single-row updates, and concurrent
    groups over the same rows deadlock, so it keeps one.
  - Next: the full sequence on the build with every branch merged and these settings.
