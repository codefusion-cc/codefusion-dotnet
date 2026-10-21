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
| $12 contest: feed on SQLite, users at the 5-minute hold | GCP C4 held to 1 vCPU / 2 GB | not measured yet | not measured yet | | | | |
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
| 2026-10-21 | [2026-10-21-pg-job-cache.md](2026-10-21-pg-job-cache.md) | A per-thread cache in front of the job pool, TechEmpower db and fortunes | db +8.3% (692,778), fortunes +2.1% |
| 2026-10-21 | [2026-10-21-pg-read-burst.md](2026-10-21-pg-read-burst.md) | 32 reads queued at once on idle loops, before and after pipelining and shared bursts | 162-172 µs to 70-87 µs per read; Npgsql 106-122 |
| 2026-10-20 | [one-receive-per-request](2026-10-20-one-receive-per-request.md) | System calls per response, Sockets against EventLoop (epoll), $12 entry, one CPU | 1 recvfrom + 1 sendto per response, down from 2 + 1 |
| 2026-10-20 | [event-loop-vs-sockets-linux](2026-10-20-event-loop-vs-sockets-linux.md) | Throughput of the $12 entry, Sockets against EventLoop, against #74 | health +21%, post +14%, feed +10%, mix +5%; #74 still 26% ahead on health |
| 2026-10-20 | [event-loop-allocations-macos](2026-10-20-event-loop-allocations-macos.md) | Allocations per request, kqueue against Sockets, Release | ≤ 0.07 B/request on both; zero kept |
| 2026-10-21 | [direct-calls-and-batched-sends](2026-10-21-direct-calls-and-batched-sends.md) | Event loop with direct recv/send and per-wake-up sends, against the event loop before them | health +17 to 25%, post +21%, feed +8%, mix +26% |
| 2026-10-21 | [listener-per-loop-and-loop-count](2026-10-21-listener-per-loop-and-loop-count.md) | SO_REUSEPORT listener per loop on one CPU; Sockets and 1 to 3 loops on two CPUs | one CPU: health +7%, mix level; two CPUs: inconclusive (25% swings), loop p99 higher on the mix |
| 2026-10-21 | [review-fixes-and-batch-size](2026-10-21-review-fixes-and-batch-size.md) | Review fixes on one CPU; Sockets, batches of 512 and of 64 on two CPUs | one CPU: -2.5% health, -6% mix (one round); two CPUs: loop +22% over Sockets, p99 16-19 vs 10-11 ms; batches of 64 worse |
| 2026-10-21 | [second-review-fixes-send-and-listeners](2026-10-21-second-review-fixes-send-and-listeners.md) | 00cc86e against 805f8d9; send with and without its GC transition; listener per loop against one on two CPUs | health level or better, mix -6% on the loop (not syscalls, not the edge word); keep the transition; listeners level |
| 2026-10-21 | [mix-bisect](2026-10-21-mix-bisect.md) | The mix at five commits from 805f8d9 to 00cc86e, two rounds, profiles of both ends | no commit costs the mix: +4% in round 1, -5% in round 2, by position in the run; profiles alike |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md) | body binding: before the spec options, with the syntax check first, and binding first | check first: valid bodies 65-75% slower, malformed 0 B; binding first: valid within 2-8% of the base, malformed back to one 1,192 B exception |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md#second-iteration-one-reader-that-binds-and-checks-in-a-single-pass) | body binding through one non-throwing reader in a single pass, against the three above | 7-20% faster than the base on valid and bad-field bodies; malformed 0.1-0.25 µs, 0 B, no exception |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md#third-iteration-realistic-bodies-three-rounds-median) | the single pass on realistic bodies (to 2 KB, numbers, nested, skipped keys), early and late malformation, three rounds | valid and bad-field bodies within noise to 35% faster than the base; malformed 23 ns to 1.4 µs, 0 B |
| 2026-10-21 | [loop-pools-c4](2026-10-21-loop-pools-c4.md) | A buffer stack per event loop against the locked pool, C4, TechEmpower plaintext and json, ABBA | plaintext +2.5%, json +1.9%, the lock gone from the profile; one listener level; 6 loops on 8 cores -10 to 16% |
| 2026-10-21 | [twelve-entry-connection-memory](2026-10-21-twelve-entry-connection-memory.md) | The $12 entry at 10k, 50k and 100k concurrent load.js users, one CPU and 2 GB: memory, latency, CPU | about 5.5 KB a connection (3.0-3.4 KB process, 1.8-2.3 KB kernel); idle connections hold no buffer; latency here was the Mac's noise |
| 2026-10-21 | [twelve-entry-fd-limit-crash](2026-10-21-twelve-entry-fd-limit-crash.md) | The $12 entry past a 65,535 descriptor limit, the GCP crash at 80k users | out of descriptors the runtime aborts; a connection cap 1,024 below the limit keeps it up (64,511 held, p99 41 ms) |
| 2026-10-21 | [default-connection-limit-past-fd-limit](2026-10-21-default-connection-limit-past-fd-limit.md) | The $12 entry at 8,192 descriptors and 10k users, with no cap and with the library's default limit | no cap: 99.9% CPU, nothing answered, the runtime aborts; the default holds 7,168, 1,324 req/s at 15% CPU |

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
