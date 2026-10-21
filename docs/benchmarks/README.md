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
| 2026-10-21 | [2026-10-21-pg-job-cache.md](2026-10-21-pg-job-cache.md) | A per-thread cache in front of the job pool, TechEmpower db and fortunes | db +8.3% (692,778), fortunes +2.1% |
| 2026-10-21 | [2026-10-21-pg-read-burst.md](2026-10-21-pg-read-burst.md) | 32 reads queued at once on idle loops, before and after pipelining and shared bursts | 162-172 µs to 70-87 µs per read; Npgsql 106-122 |
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
