# Direct recv and send, and answers sent per wake-up, on the event loop, against the event loop before them

- **Question:** what do the event loop's direct `recv` and `send` (LibraryImport with SuppressGCTransition, past
  Socket) and its batched sends (every ready connection of one wake-up handled first, then the answers sent) add on one
  Linux CPU?
- **Code:**
  - Before: perf/http-transport 89d366a, the event loop with Socket's calls, image twelve-cf-after.
  - After: d260fc5, image twelve-cf-batch.
  - Both are vendored into the $12 entry (csharp-codefusion-XeonFX) with `Transport = HttpTransport.EventLoop` and no
    batch hook set.
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. .NET SDK 10.0.402, SQLite
  3.54.1. Server pinned to CPU 0 with 2 GiB and no swap; twelve-loadgen on CPUs 1 to 3. Nothing else ran in the VM.
- **Method:**
  - The 42 checks on the after build.
  - twelve-loadgen `saturate`, 256 connections, no think time, before and after alternating in each case: health twice
    (15 s each), post and feed once (15 s), the mix once (30 s).
  - Then `strace -c -f` for 5 s on the after build under health, counts per `sendto`.
  - Script: scratchpad `ab.sh` over `tools/bench/bench.sh`.

| Case | Before (89d366a) | After (d260fc5) | Change |
|---|---:|---:|---:|
| 42 checks | | 42 pass | |
| GET /health, round 1 | 127,685 req/s | 149,273 | +17% |
| GET /health, round 2 | 125,088 | 156,358 | +25% |
| GET /posts/:id | 82,369 | 100,071 | +21% |
| GET /feed | 34,973 | 37,816 | +8% |
| mix | 35,492 | 44,573 | +26% |
| health p99 (rounds 1, 2) | 4.53, 3.57 ms | 3.50, 2.76 ms | |
| mix p99 | 13.55 ms | 12.74 ms | |
| recvfrom, sendto, epoll_pwait per response (health) | 1.000, 1.000, 0.004 | 0.999, 1.000, 0.004 | |

- **Memory and CPU:**
  - Server CPU was 95 to 98% in every run.
  - The allocation tests stay at 0.004 to 0.14 B/request on the event loop (Release, macOS). A first version held each
    answer inside `FlushAsync` and allocated 17 to 20 B/request with 8 connections: the runtime's pooled boxes ran out
    when several were held at once. The tests caught it before any run here; the hold moved into `RunAsync`.
  - The mix's maximum latency was 205 ms after, against 90 ms before: one run each, not looked into.
- **Conclusion:**
  - Keep both. The system calls are the same, so the gain is in what each one costs: no GC transition or Socket wrapper
    around `recv` and `send`, and the kernel's send work done together after all receives.
  - The two weren't measured apart.
  - #74 did 181k on health in the last session; the gap is now about 15%.
  - Next: one listener per loop (SO_REUSEPORT), then the loop count on more than one CPU.
