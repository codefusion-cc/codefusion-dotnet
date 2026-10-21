# The $12 entry rebuilt on LINQ and Exclusive mode, against the baseline

- **Question:** what the owner's rebuild of the $12 entry costs or gains: an ordinary minimal-API app whose queries are
  LINQ compiled by CodeFusion.Data's generator, on DLLs built from codefusion-dotnet, with CodeFusion.Data.Sqlite in
  its one-connection `Exclusive` mode instead of reader connections of its own.
- **Code:** before, the frozen baseline (`/Volumes/Dev/cf-bench/stage/xeonfx-baseline-2026-10-21`: hand-written SQL and
  replies, the provider's source vendored from df2c1b0, separate reader connections). After, twelve-dollar-codefusion
  branch feat/csharp-codefusion, uncommitted: `src/Data/FeedDb.cs` and `src/Program.cs`, DLLs from codefusion-dotnet
  d7c687b in `lib/`. Both keep their hand-written JWT and body checks.
- **Machine:** the Colima VM cf-bench (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro, Ubuntu 24.04 containers; the server
  pinned to CPU 0 with 2 GiB and no swap, the load generator on CPUs 1 to 3. The Mac's load was 12 to 17 from other
  sessions.
- **Method:** `tools/bench/bench.sh twelve-cf-<baseline|linq> <label> saturate 256 30`: 256 connections running
  bench/load.js's loop with no think time, 30 s after a 10 s warm-up, a fresh seed database each run. Three rounds,
  alternating. The new image passed the challenge's 42 checks first (`bench.sh … test`).

| Mix at saturation, req/s | Round 1 | Round 2 | Round 3 |
|---|---|---|---|
| baseline | 32,459 | 33,566 | 34,564 |
| LINQ + Exclusive | 37,415 | 41,091 | 37,143 |

| | Baseline | LINQ + Exclusive |
|---|---|---|
| p50 (mix) | 7.10 to 7.72 ms | 5.44 to 6.18 ms |
| p99 (mix) | 12.2 to 15.2 ms | 20.1 to 27.2 ms |
| `/feed` req/s | 14,955 to 15,924 | 17,122 to 18,940 |
| errors | 0 | 0 |

- **Memory and CPU:** the server used 96 to 98% of its CPU in every run. Peak memory 504 to 873 MiB before, 293 to
  295 MiB after (one connection's page cache instead of one per reader, and no shared-memory WAL index).
- **Conclusion:** kept. The generated queries are the spec's own statements, so LINQ costs nothing, and Exclusive mode
  gives 11 to 19% on the mix (15% on average) with less than half the memory. The tail is longer: a read waits while a
  group commits on the one connection. Next: one commit per event-loop wake-up on the epoll transport
  (perf/http-transport), which sends a batch's replies after its commit, and the CodeFusion.Http gaps (Bearer auth,
  the username claim, the spec's 401 and 400 bodies) so the hand-written auth goes.
