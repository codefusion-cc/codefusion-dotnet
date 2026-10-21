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
| TechEmpower plaintext (pipelined 16), requests/s | reduced rig: app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16 | 7.43M (event loop) | 8.91M (xitca-web) | -17% | 0 GC, 0 B | perf/techempower: 2026-10-21-tfb-event-loop.md |
| TechEmpower JSON, requests/s | same | 1.12M (event loop) | 1.26M (xitca-web) | -11% | 0 GC, 0 B | same |
| TechEmpower single query, requests/s | same | 567k (event loop) | 628k (may-minihttp) | -10% | 0 GC, 0 B | same |
| TechEmpower multiple queries (20), requests/s | GCP C4 | not measured yet | not measured yet | | | |
| TechEmpower fortunes, requests/s | same | 466k (event loop) | not measured yet | | 0 GC, 0 B | same |
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
  the rounds and their order, and how the result was picked (best of, median of). Alternate the order (A B B A, or
  rotate it each round): on a shared VM a build's place in the run moved results by ±5%, more than the change measured
  (2026-10-21-mix-bisect.md).

| Case | Before | After | Change |
|---|---|---|---|

- **Memory and CPU:** RSS, GC heap, allocations per request, CPU per request, where measured.
- **Conclusion:** what we learned, what we keep or revert, and what to try next.
```
