# Benchmarks

Every benchmark iteration gets its own file here, written right after the run: what was tested, how, the numbers, and
what we concluded. That includes runs that made things worse and changes that were thrown away. Older results, from
before this folder existed, are in [ENGINEERING.md](../ENGINEERING.md).

## Standing against the best Rust entries

The goal: CodeFusion as fast as possible with zero allocations, beating or coming close to every Rust entry. Each
row is the latest measured result on the same machine and method; the iteration files have the details. Allocation
is the GC collections and allocation rate of the CodeFusion process during a sustained run.

| Workload | Machine | CodeFusion | Best Rust (entry) | Gap | Allocation | Iteration |
|---|---|---|---|---|---|---|
| Multicore contest: Conduit on Postgres, users at the 5-minute hold | GCP c4-highcpu-8 | not measured yet | not measured yet | | | |
| $12 contest: feed on SQLite, users at the 5-minute hold | GCP C4 held to 1 vCPU / 2 GB | not measured yet | not measured yet | | | |
| TechEmpower plaintext (pipelined), requests/s | GCP C4 | not measured yet | not measured yet | | | |
| TechEmpower JSON, requests/s | GCP C4 | not measured yet | not measured yet | | | |
| TechEmpower single query, requests/s | GCP C4 | not measured yet | not measured yet | | | |
| TechEmpower multiple queries (20), requests/s | GCP C4 | not measured yet | not measured yet | | | |
| TechEmpower fortunes, requests/s | GCP C4 | not measured yet | not measured yet | | | |
| TechEmpower updates (20), requests/s | GCP C4 | not measured yet | not measured yet | | | |

## Iterations

| Date | File | What was tested | Result |
|---|---|---|---|
| 2026-10-20 | [sqlite-prototype-kept-snapshots](2026-10-20-sqlite-prototype-kept-snapshots.md) | SQLite point reads across threads, a read transaction per statement against kept snapshots | 0.93 to 11.58 M reads/s on 8 threads; kept |
| 2026-10-20 | [sqlite-prototype-vs-microsoft-data-sqlite](2026-10-20-sqlite-prototype-vs-microsoft-data-sqlite.md) | The SQLite prototype against Microsoft.Data.Sqlite and Dapper, Mac | point read 377 ns vs 1,054; 64 writers 3.1 µs vs 16.4; 0 B |
| 2026-10-20 | [sqlite-prototype-1vcpu](2026-10-20-sqlite-prototype-1vcpu.md) | The same on 1 vCPU / 2 GB | point read 510 ns vs 2,976; 64 writers 3.7 µs vs 27.5 |
| 2026-10-20 | [sqlite-twelve-statements](2026-10-20-sqlite-twelve-statements.md) | The $12 contest's statements on the provider | 7.0 µs of database per request (9.4 on 1 vCPU) |
| 2026-10-20 | [sqlite-leader-commit](2026-10-20-sqlite-leader-commit.md) | A lone write committed on its caller's thread | 400 transactions for 400 writes vs 4; not used |
| 2026-10-20 | [twelve-entry-saturation](2026-10-20-twelve-entry-saturation.md) | The $12 entry against #74 and #82 at saturation, 1 CPU | mix 34,218 vs 57,458 (C#) and 89,297 (Rust) req/s |
| 2026-10-20 | [twelve-entry-tried-not-kept](2026-10-20-twelve-entry-tried-not-kept.md) | Native AOT, range-scan like counts, leader commit, checkpoint interval | none faster; none kept |
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
| 2026-10-21 | [sqlite-park-with-batches](2026-10-21-sqlite-park-with-batches.md) | SQLite Exclusive with event-loop batches: parking against spinning | NORMAL level; FULL with readers off the loop: loop -12 to -19%, readers +23 to +50%, both about -5% |
| 2026-10-21 | [twelve-entry-read-in-batch](2026-10-21-twelve-entry-read-in-batch.md) | The $12 entry after the second SQLite review, against e3b4ce0, with probes | about -5% (49.5-52.6k vs 52.8-54.6k), p95/p99 level; all of it reads waiting for the COMMIT (not deferred: 55.5-55.9k) |
| 2026-10-21 | [twelve-entry-reads-by-table](2026-10-21-twelve-entry-reads-by-table.md) | The $12 entry with reads joining the batch only by table, against e3b4ce0 | level within the spread (51.8-52.6k vs 51.5-53.9k), p95/p99 a little lower; most reads still wait, as the guarantee needs |

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
