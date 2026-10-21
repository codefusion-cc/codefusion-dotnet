# One lock per Each for its slots, not two per read

- **Question:** an Each took the shared slot pool's lock twice per read: once to rent the read's slot, once to give it
  back. Does renting all of an Each's slots under one lock and returning them under one lock show on TFB's queries
  test?
- **Code:** perf/techempower on 99c51dd with the third check's N1 and N3 fixes (before), then with the batched slots
  (after 1), then with the pools' entry budget as well (after 2). All three uncommitted on top of 99c51dd.
- **Machine:** the shared Apple M1 Pro (8 cores), Postgres 17 in the local test cluster. Other sessions were busy:
  the load average was 54 when the last run ended.
- **Method:** `EachBenchmarks` in benchmarks/CodeFusion.Benchmarks (new): TFB's World table (10,000 rows) in schema
  pgbench_tfb, `Each(20)` of `WorldById`, from one connection (One) and from 16 at once (Sixteen). BenchmarkDotNet's
  full job, 16 read loops. "Client CPU" is the process's CPU time per operation over the whole run, warm-up included.

```
cd /Volumes/Dev/Projects/codefusion-dotnet-techempower/benchmarks/CodeFusion.Benchmarks && BENCH_DB="Host=127.0.0.1;Port=55461;Username=codefusion;Password=codefusion;Database=codefusion_test" dotnet bin/Release/net10.0/CodeFusion.Benchmarks.dll --filter '*EachBenchmarks*' --job full
```

## Runs

| Run | Method | Mean | StdDev | Client CPU per op | Lock contentions per op | Allocated |
|---|---|---|---|---|---|---|
| before | One | 138.91 µs | 2.25 µs | 165.33 µs | 0.0095 | 193 B |
| before | Sixteen | 37.21 µs | 0.84 µs | 55.49 µs | 0.0098 | 157 B |
| after 1 | One | 138.56 µs | 6.50 µs | 151.58 µs | 0.0076 | 190 B |
| after 1 | Sixteen | 42.90 µs | 3.62 µs | 46.37 µs | 0.0044 | 157 B |
| after 2 | One | 136.84 µs | 6.19 µs | 158.09 µs | 0.0115 | 191 B |
| after 2 | Sixteen | 47.23 µs | 7.35 µs | 49.48 µs | 0.0105 | 156 B |

The allocations are the benchmark's own async methods; the Each allocates nothing in any run.

- **Locks per Each(20) on the slot pool:** 40 before, 2 after. The run, list and arena pools take one lock each to
  rent and one to return, unchanged.

## Conclusion

- **Inconclusive on this machine.** The time is Postgres's round trips. The spread grew three to nine times across
  the runs as the Mac's load rose, and Sixteen's mean moved +15% and +27% in the noise. Client CPU per op fell 8%
  and 16% after the change, then 4% and 11% on the last run, which was the most loaded.
- **Kept:** 38 fewer lock acquisitions per Each(20) costs nothing measurable and removes the contention point. The
  coordinator asked for no rig run; an idle machine would be needed to measure it.
- **The entry budget (after 2)** replaces the 1,024-entry cap on each pooled run's arrays. Arrays of up to 128
  entries are always kept; larger ones only while the pool's together stay within 8,192 entries, the least recently
  used letting go first. A strict 128 cap would make every Each(500) allocate its arrays anew.
