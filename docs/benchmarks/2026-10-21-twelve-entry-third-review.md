# The $12 entry after the third SQLite review, and what the kept rows cost

- **Question:** the third review's fix 2 keeps, for each column of a row held till the COMMIT (a joined read's, a
  write's RETURNING), what each of SQLite's column calls returns. That is more work per kept row on the hot path. What
  does the entry do now against e3b4ce0?
- **Code:**
  - `cf-batch`: the e3b4ce0 entry.
  - `cf-review3`: the entry on build/twelve-entry b664b30 (feat/sqlite-provider ad8a148 with the transport and spec
    options), with `CodeFusion.Data.Sqlite.OwnSqliteBuild` set (it builds SQLite 3.54.1 itself). 42 of 42.
  - `cf-probe-save`: cf-review3 keeping only each column's own value, as before the fix (wrong answers on
    crossovers; a measurement only).
  - Locally: the throughput tool's `batches` mode before and after eafbbd8, which writes an integer's text in C#
    instead of asking SQLite for it.
- **Machine:** the Colima VM cf-bench (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro; the server pinned to CPU 0 with 2 GiB, the
  load generator on CPUs 1 to 3. **The Mac's load was 5.5 to 8.8 from other sessions**, against 2.4 to 4.9 in the
  earlier runs: these numbers are noisier and lower all round.
- **Method:** tools/bench/bench.sh, `saturate 256 30`, alternating; then the probe between them.

| Run | Variant | Mix req/s | p95 | p99 |
|---|---|---|---|---|
| 1 | cf-batch | 52,322 | 7.33 ms | 19.24 ms |
| 2 | cf-review3 | 45,139 | 9.16 ms | 20.40 ms |
| 3 | cf-review3 | 40,135 | 14.17 ms | 29.11 ms |
| 4 | cf-batch | 50,511 | 7.85 ms | 20.02 ms |
| 5 | cf-batch | 52,724 | 7.02 ms | 19.30 ms |
| 6 | cf-review3 | 44,089 | 11.11 ms | 23.39 ms |
| 7 | cf-probe-save | 46,406 | 9.54 ms | 21.66 ms |
| 8 | cf-review3 | 45,464 | 9.50 ms | 20.50 ms |
| 9 | cf-probe-save | 49,142 | 7.79 ms | 19.73 ms |
| 10 | cf-batch | 49,717 | 7.93 ms | 20.35 ms |

Peak RSS 81 to 84 MiB in every run; no errors.

Locally, `CodeFusion.Data.Sqlite.Throughput batches normal 2`, before and after eafbbd8, three rounds at load 7.6 to
11.3: 1 loop 341 to 368 k ops/s before, 313 to 369 k after; 2 loops 283 to 304 k before, 272 to 290 k after; 1 loop
with 4 readers off it 339 to 358 k in all before, 342 to 462 k after. Level within the noise.

- **Conclusion:** under this load the entry measured 40.1k to 45.5k against e3b4ce0's 49.7k to 52.7k, about 13% below,
  with p95 higher. Keeping only each column's own value (the probe) did 46.4k and 49.1k, so the kept rows' extra calls
  cost part of it; the rest is inside this run's noise and not attributed. eafbbd8 removes the costliest of those
  calls (SQLite formatting an integer as text, an allocation in the row each time) while keeping every answer exact.
  The entry needs a rerun on a quiet machine before any number here is quoted.
