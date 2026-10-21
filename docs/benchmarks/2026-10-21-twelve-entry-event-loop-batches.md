# The $12 entry with one SQLite commit per event-loop wake-up, against the writer thread

- **Question:** what CodeFusion.Http's event-loop transport together with CodeFusion.Data.Sqlite's `EventLoopBatches`
  gain over the previous entry. There, the Sockets transport handed each write to the writer thread. Now a wake-up's
  writes run on the loop in one transaction, and the wake-up's answers go out after its single COMMIT.
- **Code:** before, `cf-linq` ([its run](2026-10-21-twelve-entry-linq-exclusive.md): LINQ, Exclusive, DLLs from
  d7c687b, Sockets). After, `cf-batch`: the same app with `Options.Transport = HttpTransport.EventLoop` and
  `EventLoopBatches = true`, on DLLs from codefusion-dotnet e3b4ce0 (feat/sqlite-provider with perf/http-transport
  d7fc5b5 merged). Frozen at `/Volumes/Dev/cf-bench/stage/cf-batch`.
- **Machine:** the Colima VM cf-bench (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro, Ubuntu 24.04 containers; the server
  pinned to CPU 0 with 2 GiB and no swap, the load generator on CPUs 1 to 3. The Mac's load was 15 to 17 from other
  sessions.
- **Method:** tools/bench/bench.sh with the containers renamed `sqlite-srv`: first `test` (the challenge's 42 checks
  in the container), then `saturate 256 30` (256 connections running bench/load.js's loop with no think time, 30 s
  after a 10 s warm-up, a fresh seed database each run). Three rounds alternating cf-linq and cf-batch. Peak memory
  is the server cgroup's `memory.peak`: resident memory plus the page cache of the mapped file.

| Mix at saturation, req/s | Round 1 | Round 2 | Round 3 |
|---|---|---|---|
| cf-linq | 38,144 | 34,730 | 37,867 |
| cf-batch | 42,517 | 44,894 | 42,565 |

| | cf-linq | cf-batch |
|---|---|---|
| p50 (mix) | 5.30 to 5.86 ms | 4.49 to 4.92 ms |
| p95 (mix) | 15.3 to 17.3 ms | 9.3 to 15.8 ms |
| p99 (mix) | 25.9 to 31.0 ms | 22.9 to 33.3 ms |
| `/feed` req/s | 16,006 to 17,577 | 19,591 to 20,687 |
| server CPU | 96.5 to 97.1% | 85.5 to 90.5% |
| peak memory (cgroup) | 292 to 327 MiB | 297 to 299 MiB |
| errors | 0 | 0 |

- **Memory and CPU:** 0 B per batched write once warm (`ProviderAllocationTests.BatchedWritesAllocateNothingOnceWarm`).
  The server no longer used its whole CPU at saturation: 85 to 91% against 97%.
- **Conclusion:** kept. The mix is 11 to 29% faster (17% on average), and p50 and p95 are lower. p99 is about the
  same: one round was worse (33 ms), the others better. With CPU to spare on the server, the next limit is outside it:
  the load generator on three CPUs of the same shared Mac, or the loop waiting between wake-ups. That needs the GCP
  rig's separate load box to tell. Durability: `CrashTests.AKilledEventLoopLosesNoAnsweredWriteAndLeavesNoWakeUpHalfDone`
  kills a batching child mid-wake-up and finds every answered write and no half-done wake-up.
