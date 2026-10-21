# The review fixes on one CPU, and the event loop's batch size against Sockets on two CPUs

- **Question:**
  - What do the review fixes (counted edges, the 16-read budget per wake-up, cancels on the thread pool, sends that
    wait on the loop) cost on one CPU?
  - On two CPUs, does sending each wake-up's answers every 64 events, instead of after the whole wake-up (up to 512),
    bring the mix's p99 down to Sockets'?
- **Code:**
  - Before: twelve-cf-reuse, the per-loop listener change at 805f8d9.
  - After: perf/http-transport 5c4eb32 (the review fixes at 3a8d89d, merged with feat/codefusion-packages), image
    twelve-cf-fixes.
  - Variant: the same with the batch's actions and sends every 64 events, image twelve-cf-chunk64, staged copy only.
  - All are vendored into the $12 entry. In the staged copy, `TRANSPORT` and `LOOPS` choose the transport and the
    loop count.
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. .NET SDK 10.0.402, SQLite
  3.54.1. Nothing else ran in the VM (the coordinator's turn, 02:23 to 02:30).
- **Method:** `bench.sh` with `transport-` container names and the CPU sets as parameters. 256 connections, no think
  time.
  - One CPU (server on CPU 0, load on CPUs 1 to 3): health 15 s and the mix 30 s, one round each, before then after.
  - Two CPUs (server on CPUs 0 to 1, load on CPUs 2 to 3): the mix 30 s, two rounds of Sockets, batches of 512 and
    batches of 64, in that order.
  - The 42 checks on the after build.

| One CPU | Before (805f8d9) | After (5c4eb32) | Change |
|---|---:|---:|---:|
| 42 checks | | 42 pass | |
| GET /health | 148,224 req/s | 144,500 | -2.5% |
| health p99 | 3.21 ms | 2.79 ms | |
| mix | 47,171 | 44,406 | -5.9% |
| mix p99 | 9.38 ms | 10.76 ms | |

| Two CPUs, mix | Sockets | Event loop, batch of up to 512 | Event loop, every 64 events |
|---|---:|---:|---:|
| round 1 | 86,365 req/s | 109,603 | 89,390 |
| round 2 | 87,801 | 104,883 | 98,178 |
| p50, rounds 1 and 2 | 2.59, 2.59 ms | 1.58, 1.47 ms | 1.79, 1.66 ms |
| p95, rounds 1 and 2 | 5.80, 5.56 ms | 7.38, 9.41 ms | 9.47, 9.21 ms |
| p99, rounds 1 and 2 | 10.98, 10.19 ms | 16.40, 18.89 ms | 22.46, 18.85 ms |

- **Memory and CPU:** not looked at in this turn. No errors in any run.
- **Conclusion:**
  - **Batches of 64 are out.** Throughput was 7 to 18% below whole batches, and the tail no shorter. The code keeps
    one batch per wake-up.
  - **Two CPUs:** the event loop does 22% more than Sockets, with a lower median and a p99 about 6 to 8 ms higher.
    That's down from 21 to 30 ms in the previous session's two-CPU runs (before the read budget and the cancels on the
    pool), though those runs were noisy too.
    - The remaining tail is work a handler does on its loop before its first await; nothing in the server can
      preempt that.
    - Whether to trade that tail for the throughput is for the C4 multicore run to settle.
  - **One CPU:** the fixes measured 2.5 to 6% below the build before them, in one round each. Earlier rounds of one
    build on this VM moved by up to 7%, so this is within noise but in the same direction twice. It's worth a check
    with more rounds, or a profile of the counted edge word (one CAS loop per edge instead of one CAS), before
    calling it free.
