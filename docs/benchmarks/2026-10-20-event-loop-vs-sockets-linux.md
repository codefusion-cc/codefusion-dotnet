# Throughput of the $12 challenge entry on the event-loop transport against Sockets, on one Linux CPU

- **Question:** how much faster is the challenge entry per endpoint with `HttpTransport.EventLoop` (epoll) than with
  Sockets (SocketAsyncEventArgs and the runtime's socket thread)? Does the Sockets path stay where it was? And how far is
  the entry from the best C# entry (#74, csharp-epoll-gichamba, its own epoll loop)?
- **Code:** before: feat/codefusion-packages 35fcf86. After: perf/http-transport 89d366a, with EventLoop (image
  twelve-cf-after) and with Sockets (twelve-cf-after-sockets). All three are vendored into csharp-codefusion-XeonFX,
  with the same entry code apart from the one Transport line. #74 runs as staged for arm64 (tools/bench/README.md).
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. .NET SDK 10.0.402, SQLite
  3.54.1. Server pinned to CPU 0 with 2 GiB and no swap; twelve-loadgen on CPUs 1 to 3. Nothing else ran in the VM.
- **Method:** `tools/bench/bench.sh` in twelve-dollar-codefusion. The 42 checks, then `saturate` with 256 connections and
  no think time: health, post (GET /posts/:id) and feed alone for 15 s each, then the mix for 30 s. One full round per
  side. The Sockets check is two alternating rounds of health (before, after-sockets, before, after-sockets). #74 got
  two rounds of health in the same session. `tools/bench/profile.sh` (perf, 997 Hz, 15 s) on health for the after
  build and for #74.

| Case | Before (Sockets) | After (EventLoop) | Change |
|---|---:|---:|---:|
| 42 checks | 42 pass | 42 pass | |
| GET /health | 110,272 req/s | 133,529 | +21% |
| GET /posts/:id | 74,377 | 84,842 | +14% |
| GET /feed | 34,272 | 37,570 | +10% |
| mix | 33,710 | 35,370 | +5% |
| health p99 | 4.37 ms | 2.70 ms | |
| mix p99 | 11.63 ms | 10.67 ms | |

| Health, other runs | Round 1 | Round 2 |
|---|---:|---:|
| before (Sockets) | 112,060 | 115,665 |
| after, Sockets | 115,341 | 118,009 |
| after, EventLoop | 136,207 | 133,637 |
| #74 (epoll entry) | 181,637 | 180,724 |

- **Memory and CPU:**
  - Server CPU was 95 to 98% in every run. Peak memory was 262 to 264 MiB on the single endpoints. On the mix it was
    661 MiB before and 438 MiB after; SQLite's writes dominate that.
  - Context switches on health are about 0.0001 per request for #74, and as few for the after build.
  - Profile on health, after:
    - 81% in the kernel, 20% of it in `__wake_up_sync_key` (the client's wakeup).
    - 11% in managed code, spread thin: `RunAsync` 1.4%, `ParseHead` 0.8%, `FlushAsync` 0.6%, the runtime's send
      wrapper 0.6%.
    - libc 3%.
  - Profile on health, #74: 93% in the kernel (the same 20% in `__wake_up_sync_key`) and 4% in its own code.
  - Per request that is about 6.0 µs in the kernel and 1 µs elsewhere for us, against 5.1 µs in the kernel for #74.
    Both responses are 38-byte JSON with the same headers.
- **Conclusion:**
  - Keep the event loop: +21% on the transport-bound endpoint, +5 to 14% where SQLite and JSON take their share.
  - Sockets is unchanged (within run-to-run noise, slightly up).
  - The 26% left to #74 is not system calls: both make one receive and one send per request. It is about 0.9 µs more
    kernel per request and 1 µs of our own code.
  - Next to try:
    - Send the answers after reading every ready connection, as #74 does. Whether that explains the kernel's extra
      time is untested.
    - Call `recv` and `send` directly on the loop path.
  - Not measured here: throughput on macOS (kqueue). Only its allocation and receive-count tests ran:
    [allocations](2026-10-20-event-loop-allocations-macos.md) and [one receive](2026-10-20-one-receive-per-request.md).
