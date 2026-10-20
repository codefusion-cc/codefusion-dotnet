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
| 2026-10-21 | [sqlite-provider-allocation](2026-10-21-sqlite-provider-allocation.md) | CodeFusion.Data.Sqlite's allocations per query, whole process | 0 B per extra query (Postgres too) |
| 2026-10-21 | [sqlite-exclusive-first-try](2026-10-21-sqlite-exclusive-first-try.md) | One connection for reads and writes, a read transaction per read | 1 vCPU mix 317 to 437 k vs 33 to 55 k ops/s; reads slower |
| 2026-10-21 | [sqlite-exclusive-kept-read](2026-10-21-sqlite-exclusive-kept-read.md) | The same with a read transaction kept between groups | reads back to par; mix 374 to 431 k ops/s |
| 2026-10-21 | [sqlite-exclusive-turns](2026-10-21-sqlite-exclusive-turns.md) | Reads and the writer taking turns on the one connection | 200 writes beside scans: 21.9 s to 0.3 to 0.4 s |
| 2026-10-21 | [sqlite-exclusive-after-turns](2026-10-21-sqlite-exclusive-after-turns.md) | Exclusive against readers of their own after turn-taking | 1 vCPU mix 388 to 486 k vs 50 to 77 k ops/s |

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
