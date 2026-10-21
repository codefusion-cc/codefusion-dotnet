# Benchmarks

Every benchmark iteration gets its own file here, written right after the run: what was tested, how, the numbers, and
what we concluded. That includes runs that made things worse and changes that were thrown away. Older results, from
before this folder existed, are in [ENGINEERING.md](../ENGINEERING.md).

## Standing against the best Rust entries

The goal: CodeFusion as fast as possible with zero allocations, beating or coming close to every Rust entry. Each
row is the latest measured result on the same machine and method; the iteration files have the details. Allocation
is the GC collections and allocation rate of the CodeFusion process during a sustained run.

| Workload | Machine | CodeFusion | Best Rust (entry) | Gap to Rust | Best other .NET (entry) | Allocation | Iteration |
|---|---|---|---|---|---|---|---|
| Multicore contest: Conduit on Postgres, users at the 5-minute hold | GCP c4-highcpu-8 | not measured yet | not measured yet | | | | |
| $12 contest: feed on SQLite, users at the 5-minute hold | GCP c4-highcpu-2 held to 1 vCPU / 2 GB, 65,535 descriptors | 65,250 (cf-tables) | 188,250 (PR #82) | -65% | 65,500 (PR #74) | not measured (RSS over the warm-up: 6.7 KB a connection, PR #82 0.5 KB) | 2026-10-21-twelve-scoring.md |
| TechEmpower plaintext (pipelined 16), requests/s | reduced TFB rig: app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16; TFB 57d92fbec6f8 | 7,506,549 | 8,908,020 (xitca-web) | -16% | 7,297,751 (aspnetcore Platform) | 0 GC, 0 B in a measured minute | perf/techempower: 2026-10-21-tfb-final.md |
| TechEmpower JSON, requests/s | same | 1,201,705 | 1,263,920 (xitca-web) | -5% | 703,920 (aspnetcore Platform) | 0 GC, 0 B in a measured minute | same |
| TechEmpower single query, requests/s | same | 706,377 | 628,393 (may-minihttp) | **+12%** | 381,963 (aspnetcore Platform) | 0 GC, at most 0.013 B/request | same |
| TechEmpower multiple queries (20), requests/s | same | 64,920 | 33,892 (may-minihttp) | **+92%** | 32,210 (aspnetcore Platform) | 0 GC, at most 0.013 B/request | same |
| TechEmpower fortunes, requests/s | same | 491,027 | 576,688 (xitca-web) | -15% | 307,474 (aspnetcore Platform) | 0 GC, at most 0.013 B/request | same |
| TechEmpower updates (20), requests/s | same | 23,240 | 22,669 (may-minihttp) | **+2.5%** | 18,834 (aspnetcore minimal) | 0 GC, at most 0.013 B/request | same |

The CodeFusion build is every performance branch merged: the event loop, pg-pipelining, data-batched-updates, two
readers per core and four writers. These are reduced-rig numbers; the TechEmpower rounds run on larger hardware.

Two things the table leaves out:
- **The fastest overall is in C, not Rust, on two tests.** libreactor does 9,461,172 on plaintext and 1,483,605 on
  JSON on the same rig. On single query, multiple queries and updates CodeFusion leads every entry measured; on
  fortunes, xitca-web leads.
- **On single query, multiple queries and updates, Postgres is the limit.** Its 8 vCPUs ran 93 to 100% busy for the
  leaders, so a larger database machine could reorder them.

## Iterations

| Date | File | What was tested | Result |
|---|---|---|---|
| 2026-10-21 | [2026-10-21-twelve-scoring.md](2026-10-21-twelve-scoring.md) | The $12 contest on GCP: cf-tables, PR #74 (C# epoll) and PR #82 (Rust io_uring), users at the 5-minute hold | pr82 188,250 (memory-bound); pr74 65,500 and cf-tables 65,250 (descriptor limit) |
| 2026-10-21 | [2026-10-21-rust-entries-techniques.md](2026-10-21-rust-entries-techniques.md) | Code study of the best Rust entries in both contests: what carries over, where, and the expected gain | 12 ideas ranked; #1 is mostly on perf/http-transport; the rest is new library work (Data.Postgres pipelining, Data.Sqlite per-wake-up transaction) |
| 2026-10-21 | [2026-10-21-each-slot-locks.md](2026-10-21-each-slot-locks.md) | An Each's slots rented and returned under one lock each, not two per read; Each(20) of World, 1 and 16 connections | 40 slot-pool locks per Each(20) down to 2; times within the busy Mac's noise; kept |
| 2026-10-21 | [2026-10-21-tfb-loop-pools.md](2026-10-21-tfb-loop-pools.md) | perf/http-loop-pools against perf/http-transport, plaintext and json, A B B A; a shared listener, 6 loops | Pools +2.5% plaintext, +1.9% json, the lock gone; shared listener ±2.5%; 6 loops -10 to -16% |
| 2026-10-21 | [2026-10-21-tfb-gate-jobs.md](2026-10-21-tfb-gate-jobs.md) | Postgres jobs made under steady load | 20-32 in 5 min: 7-16% of what db and fortunes allocate; the rest (≈0.001 B/request) unnamed |
| 2026-10-21 | [2026-10-21-tfb-final.md](2026-10-21-tfb-final.md) | One full run of the best build against TFB's leaders | Leads db (+12% over best Rust), queries (+92%), updates (+2.5%); json -5%, fortunes -15%, plaintext -16% |
| 2026-10-21 | [2026-10-21-tfb-gate-trace-fortunes.md](2026-10-21-tfb-gate-trace-fortunes.md) | Ten minutes of fortunes, traced | 0.0013 B/request, in a dozen bursts of 7-37 KB of Byte[]; jobs made at a peak or socket operations, to tell apart |
| 2026-10-21 | [2026-10-21-tfb-gate-steady.md](2026-10-21-tfb-gate-steady.md) | The gate on the warmup's connections, with the job cache | plaintext and json 0 B in a minute; database tests 16-67 KB a minute, no GC anywhere |
| 2026-10-21 | [2026-10-21-tfb-gate-trace.md](2026-10-21-tfb-gate-trace.md) | Five-minute traced gate on db and fortunes | What it counted were connections the measured wrk run opened, not requests; the gate now stays on one wrk run |
| 2026-10-21 | [2026-10-21-tfb-profiles.md](2026-10-21-tfb-profiles.md) | perf profiles of db, fortunes, json against may-minihttp | Half the system calls of may-minihttp; 2-3x its own-code share; locks in the job and buffer pools are the clearest piece |
| 2026-10-21 | [2026-10-21-tfb-all-merged.md](2026-10-21-tfb-all-merged.md) | Every branch merged against TFB's leaders | Ahead of the best Rust entry on db (+3%), queries (+92%), updates (+1.5%); json -2%, fortunes -14%, plaintext -18% |
| 2026-10-21 | [2026-10-21-tfb-gate-report.md](2026-10-21-tfb-gate-report.md) | The allocation gate through the app's GC report, every branch merged | No GC on any test; 56 B to 135 KB a minute (≤ 0.004 B/request), db and fortunes to be named |
| 2026-10-21 | [2026-10-21-tfb-pg-loops.md](2026-10-21-tfb-pg-loops.md) | Read and write loop counts on db, queries, updates | 16 readers +9% on queries; 4 writers +86% on updates (25.2k) once updates are batched |
| 2026-10-21 | [2026-10-21-tfb-gate-counters.md](2026-10-21-tfb-gate-counters.md) | The allocation gate with dotnet-counters, merged build | No GC anywhere; 40 KB/s is the counters' own; queries/updates' extra ~1 B/req was the job pool (fixed) |
| 2026-10-21 | [2026-10-21-tfb-event-loop.md](2026-10-21-tfb-event-loop.md) | perf/http-transport's event loop against Sockets on the six tests | json +42%, plaintext +20%, db and fortunes +9% |
| 2026-10-21 | [2026-10-21-tfb-each-fortunes.md](2026-10-21-tfb-each-fortunes.md) | Each for queries and updates, fortunes without allocating | queries +10%; fortunes 0 GC, RSS 1.37 GB to 77 MB; updates -37% (full group commits); HTTP tests ±6% noise |
| 2026-10-21 | [2026-10-21-tfb-baseline.md](2026-10-21-tfb-baseline.md) | CodeFusion against TFB's leaders on the six tests, reduced rig | Ahead of every .NET entry on json, db, queries, fortunes; best of all on queries; Rust ahead by 17-34% on the rest, 70% on updates |
| 2026-10-21 | [2026-10-21-tfb-rig-smoke.md](2026-10-21-tfb-rig-smoke.md) | The TechEmpower rig end to end on aspnetcore's json test | Tooling works; two parsers fixed; numbers not comparable |
| 2026-10-20 | [sqlite-prototype-kept-snapshots](2026-10-20-sqlite-prototype-kept-snapshots.md) | SQLite point reads across threads, a read transaction per statement against kept snapshots | 0.93 to 11.58 M reads/s on 8 threads; kept |
| 2026-10-20 | [sqlite-prototype-vs-microsoft-data-sqlite](2026-10-20-sqlite-prototype-vs-microsoft-data-sqlite.md) | The SQLite prototype against Microsoft.Data.Sqlite and Dapper, Mac | point read 377 ns vs 1,054; 64 writers 3.1 µs vs 16.4; 0 B |
| 2026-10-20 | [sqlite-prototype-1vcpu](2026-10-20-sqlite-prototype-1vcpu.md) | The same on 1 vCPU / 2 GB | point read 510 ns vs 2,976; 64 writers 3.7 µs vs 27.5 |
| 2026-10-20 | [sqlite-twelve-statements](2026-10-20-sqlite-twelve-statements.md) | The $12 contest's statements on the provider | 7.0 µs of database per request (9.4 on 1 vCPU) |
| 2026-10-20 | [sqlite-leader-commit](2026-10-20-sqlite-leader-commit.md) | A lone write committed on its caller's thread | 400 transactions for 400 writes vs 4; not used |
| 2026-10-20 | [twelve-entry-saturation](2026-10-20-twelve-entry-saturation.md) | The $12 entry against #74 and #82 at saturation, 1 CPU | mix 34,218 vs 57,458 (C#) and 89,297 (Rust) req/s |
| 2026-10-20 | [twelve-entry-tried-not-kept](2026-10-20-twelve-entry-tried-not-kept.md) | Native AOT, range-scan like counts, leader commit, checkpoint interval | none faster; none kept |
| 2026-10-21 | [2026-10-21-pg-job-cache.md](2026-10-21-pg-job-cache.md) | A per-thread cache in front of the job pool, TechEmpower db and fortunes | db +8.3% (692,778), fortunes +2.1% |
| 2026-10-21 | [2026-10-21-pg-read-burst.md](2026-10-21-pg-read-burst.md) | 32 reads queued at once on idle loops, before and after pipelining and shared bursts | 162-172 µs to 70-87 µs per read; Npgsql 106-122 |
| 2026-10-20 | [one-receive-per-request](2026-10-20-one-receive-per-request.md) | System calls per response, Sockets against EventLoop (epoll), $12 entry, one CPU | 1 recvfrom + 1 sendto per response, down from 2 + 1 |
| 2026-10-20 | [event-loop-vs-sockets-linux](2026-10-20-event-loop-vs-sockets-linux.md) | Throughput of the $12 entry, Sockets against EventLoop, against #74 | health +21%, post +14%, feed +10%, mix +5%; #74 still 26% ahead on health |
| 2026-10-20 | [event-loop-allocations-macos](2026-10-20-event-loop-allocations-macos.md) | Allocations per request, kqueue against Sockets, Release | ≤ 0.07 B/request on both; zero kept |
| 2026-10-21 | [sqlite-provider-allocation](2026-10-21-sqlite-provider-allocation.md) | CodeFusion.Data.Sqlite's allocations per query, whole process | 0 B per extra query (Postgres too) |
| 2026-10-21 | [sqlite-exclusive-first-try](2026-10-21-sqlite-exclusive-first-try.md) | One connection for reads and writes, a read transaction per read | 1 vCPU mix 317 to 437 k vs 33 to 55 k ops/s; reads slower |
| 2026-10-21 | [sqlite-exclusive-kept-read](2026-10-21-sqlite-exclusive-kept-read.md) | The same with a read transaction kept between groups | reads back to par; mix 374 to 431 k ops/s |
| 2026-10-21 | [sqlite-exclusive-turns](2026-10-21-sqlite-exclusive-turns.md) | Reads and the writer taking turns on the one connection | 200 writes beside scans: 21.9 s to 0.3 to 0.4 s |
| 2026-10-21 | [sqlite-exclusive-after-turns](2026-10-21-sqlite-exclusive-after-turns.md) | Exclusive against readers of their own after turn-taking | 1 vCPU mix 388 to 486 k vs 50 to 77 k ops/s |
| 2026-10-21 | [twelve-entry-linq-exclusive](2026-10-21-twelve-entry-linq-exclusive.md) | The $12 entry on LINQ, DLLs and Exclusive mode, against the baseline | mix 37,143 to 41,091 vs 32,459 to 34,564 req/s; p99 longer |
| 2026-10-21 | [twelve-entry-event-loop-batches](2026-10-21-twelve-entry-event-loop-batches.md) | The $12 entry on the event loop with one SQLite commit per wake-up, against cf-linq | mix 42,517 to 44,894 vs 34,730 to 38,144 req/s; server CPU 85 to 91% |
| 2026-10-21 | [direct-calls-and-batched-sends](2026-10-21-direct-calls-and-batched-sends.md) | Event loop with direct recv/send and per-wake-up sends, against the event loop before them | health +17 to 25%, post +21%, feed +8%, mix +26% |
| 2026-10-21 | [listener-per-loop-and-loop-count](2026-10-21-listener-per-loop-and-loop-count.md) | SO_REUSEPORT listener per loop on one CPU; Sockets and 1 to 3 loops on two CPUs | one CPU: health +7%, mix level; two CPUs: inconclusive (25% swings), loop p99 higher on the mix |
| 2026-10-21 | [review-fixes-and-batch-size](2026-10-21-review-fixes-and-batch-size.md) | Review fixes on one CPU; Sockets, batches of 512 and of 64 on two CPUs | one CPU: -2.5% health, -6% mix (one round); two CPUs: loop +22% over Sockets, p99 16-19 vs 10-11 ms; batches of 64 worse |
| 2026-10-21 | [second-review-fixes-send-and-listeners](2026-10-21-second-review-fixes-send-and-listeners.md) | 00cc86e against 805f8d9; send with and without its GC transition; listener per loop against one on two CPUs | health level or better, mix -6% on the loop (not syscalls, not the edge word); keep the transition; listeners level |
| 2026-10-21 | [mix-bisect](2026-10-21-mix-bisect.md) | The mix at five commits from 805f8d9 to 00cc86e, two rounds, profiles of both ends | no commit costs the mix: +4% in round 1, -5% in round 2, by position in the run; profiles alike |
| 2026-10-21 | [sqlite-park-after-64-spins](2026-10-21-sqlite-park-after-64-spins.md) | SQLite Exclusive: every waiter parked after 64 spins, against spinning | reads on 4 threads 0.61-0.76 vs 1.23-1.45 M/s, mixed 119-171 vs 136-449 k ops/s; thrown away |
| 2026-10-21 | [sqlite-park-after-200us](2026-10-21-sqlite-park-after-200us.md) | SQLite Exclusive: every waiter parked after 200 µs | mixed 80-94 vs 370-405 k ops/s; thrown away |
| 2026-10-21 | [sqlite-park-only-for-batches](2026-10-21-sqlite-park-only-for-batches.md) | SQLite Exclusive: waiters park only while a loop's batch holds the connection | throughput level; a 500 ms hold costs under 100 ms of CPU, not 1,039; kept |
| 2026-10-21 | [twelve-entry-spec-options](2026-10-21-twelve-entry-spec-options.md) | The $12 entry on feat/http-spec-options, and the SQLite review fixes split out | spec options level; entry -11% (45.3-47.0k vs 51.3-53.5k), mostly the commit before a loop read; that line out: 49.1-50.1k |
| 2026-10-21 | [sqlite-park-with-batches](2026-10-21-sqlite-park-with-batches.md) | SQLite Exclusive with event-loop batches: parking against spinning | NORMAL level; FULL with readers off the loop: loop -8 to -19%, readers more, both about -8% |
| 2026-10-21 | [twelve-entry-read-in-batch](2026-10-21-twelve-entry-read-in-batch.md) | The $12 entry after the second SQLite review, against e3b4ce0, with probes | about -5% (49.5-52.6k vs 52.8-54.6k), p95/p99 level; all of it reads waiting for the COMMIT (not deferred: 55.5-55.9k) |
| 2026-10-21 | [twelve-entry-reads-by-table](2026-10-21-twelve-entry-reads-by-table.md) | The $12 entry with reads joining the batch only by table, against e3b4ce0 | level within the spread (51.8-52.6k vs 51.5-53.9k), p95/p99 a little lower; most reads still wait, as the guarantee needs |
| 2026-10-21 | [twelve-entry-third-review](2026-10-21-twelve-entry-third-review.md) | The $12 entry after the third review, with a probe of the kept rows; the throughput tool before and after eafbbd8 | at load 5.5-8.8: 40.1-45.5k vs 49.7-52.7k; probe 46.4-49.1k; eafbbd8 level locally; needs a quiet rerun |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md) | body binding: before the spec options, with the syntax check first, and binding first | check first: valid bodies 65-75% slower, malformed 0 B; binding first: valid within 2-8% of the base, malformed back to one 1,192 B exception |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md#second-iteration-one-reader-that-binds-and-checks-in-a-single-pass) | body binding through one non-throwing reader in a single pass, against the three above | 7-20% faster than the base on valid and bad-field bodies; malformed 0.1-0.25 µs, 0 B, no exception |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md#third-iteration-realistic-bodies-three-rounds-median) | the single pass on realistic bodies (to 2 KB, numbers, nested, skipped keys), early and late malformation, three rounds | valid and bad-field bodies within noise to 35% faster than the base; malformed 23 ns to 1.4 µs, 0 B |
| 2026-10-21 | [sqlite-waiters-park](2026-10-21-sqlite-waiters-park.md) | SQLite: every waiter parks after 50 µs, and reads stop for a writer in line; the turn tests under the loaded suite | entry on one CPU +12% (parking) and +11% (final, noisier); Mac 16-caller mix -25%; turn test green 9 of 9, red against three mutants; kept |
| 2026-10-21 | [sqlite-flaky-tests](2026-10-21-sqlite-flaky-tests.md) | SQLite: the turn test and three timing tests on cf-bench beside busy loops, at 4 CPUs and at 1 | before: the turn test red 3 of 3 suites, the rollback test 7 of 9 runs, the loop tests 1 each at 1 CPU; after: all green, 3 suites and 16 class runs; kept |
| 2026-10-21 | [loop-pools-c4](2026-10-21-loop-pools-c4.md) | A buffer stack per event loop against the locked pool, C4, TechEmpower plaintext and json, ABBA | plaintext +2.5%, json +1.9%, the lock gone from the profile; one listener level; 6 loops on 8 cores -10 to 16% |
| 2026-10-21 | [twelve-entry-connection-memory](2026-10-21-twelve-entry-connection-memory.md) | The $12 entry at 10k, 50k and 100k concurrent load.js users, one CPU and 2 GB: memory, latency, CPU | about 5.5 KB a connection (3.0-3.4 KB process, 1.8-2.3 KB kernel); idle connections hold no buffer; latency here was the Mac's noise |
| 2026-10-21 | [twelve-entry-fd-limit-crash](2026-10-21-twelve-entry-fd-limit-crash.md) | The $12 entry past a 65,535 descriptor limit, the GCP crash at 80k users | out of descriptors the runtime aborts; a connection cap 1,024 below the limit keeps it up (64,511 held, p99 41 ms) |
| 2026-10-21 | [default-connection-limit-past-fd-limit](2026-10-21-default-connection-limit-past-fd-limit.md) | The $12 entry at 8,192 descriptors and 10k users, with no cap and with the library's default limit | no cap: 99.9% CPU, nothing answered, the runtime aborts; the default holds 7,168, 1,324 req/s at 15% CPU |
| 2026-10-21 | [then-guard-cost](2026-10-21-then-guard-cost.md) | The catalog check after Query.Then, in Follow and Unfollow and alone, M1 Pro under load | about +30 µs alone (pgbench, fastest of 6); +56 to +115 µs end to end, within the short job's noise; the third review's checks add 2 to 14 µs alone, inside the noise end to end |

## Template

Name the file `YYYY-MM-DD-<short-slug>.md` and add a row to the table above.

```markdown
# <What was tested, as a sentence>

- **Question:** what this run was meant to find out, and why.
- **Code:** the commit or branch of each side (and of codefusion-dotnet, for a contest entry).
- **Machine:** the hardware or VM shape, OS, .NET SDK, Postgres or SQLite version, and what else was running.
- **Method:** the tool and its settings (BenchmarkDotNet job, load generator and users, the contest's scoring steps),
  the rounds and their order, and how the result was picked (best of, median of). Alternate the order (A B B A, or
  rotate it each round): on a shared VM a build's place in the run moved results by ±5%, more than the change measured
  (2026-10-21-mix-bisect.md).

| Case | Before | After | Change |
|---|---|---|---|

- **Memory and CPU:** RSS, GC heap, allocations per request, CPU per request, where measured.
- **Conclusion:** what we learned, what we keep or revert, and what to try next.
```
