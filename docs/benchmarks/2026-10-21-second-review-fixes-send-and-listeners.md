# The second review's fixes on one CPU, direct sends with and without their GC transition, and one listener against a listener per loop on two CPUs

- **Question:**
  - Do the fixes since 805f8d9 cost anything on one CPU? These are the review fixes, the Core seam, the batch rounds,
    the handler budget, and first requests on the loop.
  - Where does the time go: is the counted edge word on the hot path?
  - Does a direct `send` without SuppressGCTransition cost more than with it (both capped at 64 KB per call)?
  - On two CPUs, does a listener per loop (SO_REUSEPORT, the kernel's hash) beat one listener with connections
    handed out in turn?
- **Code:**
  - Before: twelve-cf-reuse (805f8d9).
  - After: twelve-cf-head, perf/http-transport 00cc86e, with Core's sources from the same commit.
  - Variant: twelve-cf-head-sendgc, the same without SuppressGCTransition on `send`.
  - All are vendored into the $12 entry. In the staged copy only, `TRANSPORT`, `LOOPS` and `LISTENER=one` choose the
    transport, the loop count and the listener.
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. .NET SDK 10.0.402, SQLite
  3.54.1. The coordinator's turn ran 03:10 to 03:22, with nothing else in the VM.
- **Method:** `bench.sh` and `profile.sh` with `transport-` container names. 256 connections, no think time.
  - One CPU: health twice (15 s, alternating) and the mix once (30 s); the send variant once each.
  - Two CPUs, 2 loops: the mix twice (30 s), alternating per-loop and one listener.
  - perf on health (997 Hz, 15 s, with `DOTNET_EnableWriteXorExecute=0` for symbols).
  - strace of the mix (5 s).
  - The mix on Sockets on both builds.

| One CPU | Before (805f8d9) | After (00cc86e) | Change |
|---|---:|---:|---:|
| 42 checks | | 42 pass | |
| GET /health, round 1 | 154,988 req/s | 160,032 | +3% |
| GET /health, round 2 | 124,259 | 147,052 | +18% (a noisy round) |
| mix | 47,163 | 44,239 | -6% |
| mix p99 | 9.63 ms | 11.28 ms | |
| mix on Sockets | 33,416 | 34,095 | +2% |
| mix: calls per response (strace) | 3.93 | 3.81 | |

| Direct send, one CPU | With SuppressGCTransition | Without |
|---|---:|---:|
| GET /health | 160,032 and 147,052 | 143,565 |
| mix | 44,239 | 41,721 |

| Two CPUs, mix, 2 loops | Listener per loop | One listener, in turn |
|---|---:|---:|
| round 1 | 108,130 req/s, p99 17.24 ms | 117,674, p99 16.01 ms |
| round 2 | 119,889, p99 16.85 ms | 113,979, p99 16.76 ms |

- **Profile (health, one CPU):** managed code is 7.8% after against 8.1% before. Its top is `RunAsync` 1.06%,
  `ParseHead` 0.63% and the copied write barrier 0.86%. Nothing from the edge word (`Readable`, `SetEdge`, `Disarm`,
  `Arm`) reaches 0.5%.
- **Conclusion:**
  - **Keep SuppressGCTransition on `send`,** with the 64 KB cap: without it was 3 to 10% slower in this one round.
  - **The listener choice:** per-loop listeners and one listener are level on two CPUs. Per-loop stays the default,
    and `ListenerPerLoop = false` is there for a few long-lived connections.
  - **The edge word isn't hot.**
  - **Health didn't lose,** but the mix on the event loop is 6% lower, the second turn in a row to show it. Sockets is
    level on the same builds, and calls per response went down, so the cause is in the event loop's changes since
    805f8d9, in CPU spent somewhere other than the system calls.
  - **Next:**
    - a perf profile of the mix on both builds;
    - a bisect over 3a8d89d (review fixes), 6c5cced (batch rounds, handler budget, send-before-resume) and 00cc86e
      (first request on the loop).
