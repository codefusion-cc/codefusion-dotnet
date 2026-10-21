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
| 2026-10-20 | [one-receive-per-request](2026-10-20-one-receive-per-request.md) | System calls per response, Sockets against EventLoop (epoll), $12 entry, one CPU | 1 recvfrom + 1 sendto per response, down from 2 + 1 |
| 2026-10-20 | [event-loop-vs-sockets-linux](2026-10-20-event-loop-vs-sockets-linux.md) | Throughput of the $12 entry, Sockets against EventLoop, against #74 | health +21%, post +14%, feed +10%, mix +5%; #74 still 26% ahead on health |
| 2026-10-20 | [event-loop-allocations-macos](2026-10-20-event-loop-allocations-macos.md) | Allocations per request, kqueue against Sockets, Release | ≤ 0.07 B/request on both; zero kept |
| 2026-10-21 | [direct-calls-and-batched-sends](2026-10-21-direct-calls-and-batched-sends.md) | Event loop with direct recv/send and per-wake-up sends, against the event loop before them | health +17 to 25%, post +21%, feed +8%, mix +26% |
| 2026-10-21 | [listener-per-loop-and-loop-count](2026-10-21-listener-per-loop-and-loop-count.md) | SO_REUSEPORT listener per loop on one CPU; Sockets and 1 to 3 loops on two CPUs | one CPU: health +7%, mix level; two CPUs: inconclusive (25% swings), loop p99 higher on the mix |
| 2026-10-21 | [review-fixes-and-batch-size](2026-10-21-review-fixes-and-batch-size.md) | Review fixes on one CPU; Sockets, batches of 512 and of 64 on two CPUs | one CPU: -2.5% health, -6% mix (one round); two CPUs: loop +22% over Sockets, p99 16-19 vs 10-11 ms; batches of 64 worse |
| 2026-10-21 | [second-review-fixes-send-and-listeners](2026-10-21-second-review-fixes-send-and-listeners.md) | 00cc86e against 805f8d9; send with and without its GC transition; listener per loop against one on two CPUs | health level or better, mix -6% on the loop (not syscalls, not the edge word); keep the transition; listeners level |

## Template

Name the file `YYYY-MM-DD-<short-slug>.md` and add a row to the table above.

```markdown
# <What was tested, as a sentence>

- **Question:** what this run was meant to find out, and why.
- **Code:** the commit or branch of each side (and of codefusion-dotnet, for a contest entry).
- **Machine:** the hardware or VM shape, OS, .NET SDK, Postgres or SQLite version, and what else was running.
- **Method:** the tool and its settings (BenchmarkDotNet job, load generator and users, the contest's scoring steps),
  the rounds and their order, and how the result was picked (best of, median of).

| Case | Before | After | Change |
|---|---|---|---|

- **Memory and CPU:** RSS, GC heap, allocations per request, CPU per request, where measured.
- **Conclusion:** what we learned, what we keep or revert, and what to try next.
```
