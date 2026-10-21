# A per-thread cache in front of the Postgres job pool, on TechEmpower's db and fortunes tests

- **Question:** perf showed the job pool's lock (a job rented on a request's thread, returned on a loop's) and
  `PgJob.Rent` at about 2 to 4% of the db and fortunes tests' CPU. What does a per-thread cache in front of the
  shared stack give?
- **Code:** before: the TechEmpower rig's codefusion-all build (perf/techempower with perf/data-batched-updates,
  perf/pg-pipelining to d612116 and perf/http-transport merged, event loop, two readers per core, four writers).
  After: the same with perf/pg-pipelining 11a2d71 (64 jobs per thread, moved 32 at a time to or from the stack).
- **Machine:** GCP europe-west1-b, the reduced rig: app c4-highcpu-8, Postgres c4-highcpu-8 (TFB's postgres:18
  image and config), wrk n2-highcpu-16.
- **Method:** `scripts/TechEmpowerRig.cs quick` on perf/techempower: per run a 10 s warmup and 15 s measured at 512
  connections; before and after alternating, three rounds of db then fortunes each.

| Round | db before | db after | fortunes before | fortunes after |
|---|---|---|---|---|
| 1 | 647,248 | 696,508 | 488,251 | 491,243 |
| 2 | 625,466 | 699,325 | 481,996 | 497,165 |
| 3 | 645,607 | 682,502 | 477,969 | 490,523 |
| Mean | 639,440 | 692,778 (+8.3%) | 482,739 | 492,977 (+2.1%) |

- **Memory and CPU:** no errors; p99 2.1 to 2.4 ms on db after, 2.4 to 2.8 before. The allocation test with
  10,240 reads in flight still allocates nothing; Debug's job checks pass.
- **Conclusion:** keep it. db gains more than the lock's share in the profile: the shared stack's cache line no
  longer moves between the request threads and the loops on every command. db is now 10% past may-minihttp's
  628,393 on this rig. Fortunes gains less: its time goes to the page and the list reader.
