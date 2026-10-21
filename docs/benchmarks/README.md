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
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md) | body binding: before the spec options, with the syntax check first, and binding first | check first: valid bodies 65-75% slower, malformed 0 B; binding first: valid within 2-8% of the base, malformed back to one 1,192 B exception |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md#second-iteration-one-reader-that-binds-and-checks-in-a-single-pass) | body binding through one non-throwing reader in a single pass, against the three above | 7-20% faster than the base on valid and bad-field bodies; malformed 0.1-0.25 µs, 0 B, no exception |
| 2026-10-21 | [2026-10-21-body-syntax-check.md](2026-10-21-body-syntax-check.md#third-iteration-realistic-bodies-three-rounds-median) | the single pass on realistic bodies (to 2 KB, numbers, nested, skipped keys), early and late malformation, three rounds | valid and bad-field bodies within noise to 35% faster than the base; malformed 23 ns to 1.4 µs, 0 B |

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
