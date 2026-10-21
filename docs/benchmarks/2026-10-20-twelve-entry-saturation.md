# The $12 entry against #74 (C#) and #82 (Rust) at saturation on one core

- **Question:** where the CodeFusion $12 entry stands against the fastest C# and Rust entries, and what limits it.
- **Code:** twelve-dollar-codefusion `submissions/csharp-codefusion-XeonFX` as of 2026-10-20 22:41, uncommitted, with
  codefusion-dotnet's source vendored in `lib/` from df2c1b0 (`lib/SNAPSHOT`). A frozen copy is at
  `/Volumes/Dev/cf-bench/stage/xeonfx-baseline-2026-10-21`. #74 and #82 built from their pull requests
  (`compare/`); #74 patched for arm64 only (SDK and RID, its EpollEvent struct's Pack).
- **Machine:** a Colima VM (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro, Ubuntu 24.04 containers. The server pinned to one
  CPU with 2 GiB and no swap, the load generator on the other three. A fresh seed database per run. Other work shared
  the Mac, so repeated runs differ by up to 10%.
- **Method:** `tools/bench/bench.sh <entry> saturate 256 30`: 256 connections running bench/load.js's loop with no
  think time, 30 s after a 10 s warm-up, requests per second, and the same per endpoint. Then
  `bench.sh <entry> users 20000 40 60`: bench/load.js at 20,000 users, 40 s ramp, 60 s hold. Profiles with
  `tools/bench/profile.sh` (perf).

| Requests/s at one CPU, 0 errors | mix | `/health` | `/posts/:id` | `/feed` | p99 (mix) |
|---|---|---|---|---|---|
| this entry | 34,218 | 113,798 | 76,513 | 33,181 | 11 ms |
| #74 C# epoll, Native AOT | 57,458 | 179,466 | 114,864 | 39,627 | 22 ms |
| #82 Rust io_uring | 89,297 | 198,645 | 129,761 | 59,766 | 7 ms |
| #114 Rust Axum | 45,885 | | | | |

| 20,000 users | p95 | p99 | CPU |
|---|---|---|---|
| this entry | 0.30 ms | 1.42 ms | 9% |
| #74 | 0.36 ms | 8.46 ms | 7% |

- **Memory and CPU:** this entry's profile on the mix: 52% kernel, 34% SQLite, 8% the entry's and CodeFusion's own
  code. 0 B per request once warm.
- **Conclusion:** the gap to #74 is the transport. .NET's sockets make two receives per request (a readiness wait, then
  the read), and the kernel spends about twice as long waking waiters (`__wake_up_sync_key` 12.7% against 6.7%), where
  an epoll loop reads once per request and batches its waits. Next: an epoll transport in CodeFusion.Http
  (perf/http-transport), then one SQLite commit per loop wake-up. Above about 30,000 users one load container became
  the limit for every server alike, so those runs say nothing about the servers. The contest-perf session's GCP rig
  scores the frozen copy as the baseline.
