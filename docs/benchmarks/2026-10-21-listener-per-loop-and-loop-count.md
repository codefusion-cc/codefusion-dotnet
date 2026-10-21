# One SO_REUSEPORT listener per event loop on Linux, and how many loops on two CPUs

- **Question:**
  - Does giving each event loop its own listening socket (SO_REUSEPORT, accepted on the loop's thread) change anything
    on one CPU?
  - On two CPUs, how many loops should be the default, and does the event loop still beat Sockets there?
- **Code:**
  - Before: perf/http-transport d260fc5, image twelve-cf-batch.
  - After: the per-loop listener change, uncommitted on top of 4e74bb4, image twelve-cf-reuse.
  - Both are vendored into the $12 entry. In this staged copy only, `TRANSPORT=Sockets` and `LOOPS=n` choose the
    transport and the loop count.
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. .NET SDK 10.0.402, SQLite
  3.54.1.
  - One CPU: the server on CPU 0 and twelve-loadgen on CPUs 1 to 3.
  - Two CPUs: the server on CPUs 0 to 1 and the load on CPUs 2 to 3, so the load generator has only two CPUs.
- **Method:** `bench.sh` with the CPU sets as parameters. 256 connections, no think time.
  - One CPU: health twice (15 s), alternating before and after; the mix once each (30 s); and `users` with 5,000
    users, a 20 s ramp and a 30 s hold, which opens connections all along. The 42 checks on the after build.
  - Two CPUs: Sockets and 1, 2 and 3 loops; health once (15 s); the mix in two rounds (30 s).

| One CPU | Before (one listener) | After (one per loop) | Change |
|---|---:|---:|---:|
| 42 checks | | 42 pass | |
| GET /health, round 1 | 144,526 req/s | 155,282 | +7% |
| GET /health, round 2 | 131,469 | 140,137 | +7% |
| mix | 45,784 | 45,996 | 0% |
| users 5,000 | 527 | 528 | 0% (paced by think time) |
| users p99 | 3.40 ms | 3.38 ms | |

| Two CPUs | Sockets | 1 loop | 2 loops | 3 loops |
|---|---:|---:|---:|---:|
| GET /health | 212,748 | 168,583 | 213,820 | 222,394 |
| mix, round 1 | 86,095 | 55,256 | 95,989 | 94,336 |
| mix, round 2 | 79,130 | | 72,246 | 91,989 |
| mix p99, rounds 1 and 2 | 9.30, 18.96 ms | 8.31 ms | 20.87, 29.80 ms | 21.72, 21.01 ms |
| mix p50, rounds 1 and 2 | 2.70, 2.59 ms | 4.45 ms | 1.62, 1.96 ms | 1.24, 1.36 ms |

- **Memory and CPU:**
  - Two CPUs: server CPU was 176 to 190%. Peak memory was 1.4 to 1.7 GiB on the mix; SQLite's cache and maps
    dominate it.
  - The round-1 mix with 3 loops had 1 error in 2.8 M requests. The run printed only its totals, so which endpoint is
    unknown. Round 2, with every endpoint shown, had none.
- **Conclusion:**
  - Keep the per-loop listeners. On one CPU they are as fast or faster, and on more they spread connections in the
    kernel with no handoff.
  - The loop count can't be settled on this machine:
    - The same configuration moved by up to 25% between rounds (2 loops: 96k, then 72k).
    - Health on two CPUs is probably limited by the two-CPU load generator: Sockets and 2 loops land on the same 213k.
  - What holds across rounds:
    - One loop on two CPUs loses badly.
    - 3 loops on 2 CPUs was never worse than 2.
    - The event loop's median latency is lower than Sockets', but its p99 on the mix is higher (21 to 30 ms against 9
      to 19 ms). A connection stays on its loop, so a slow feed query or write delays the others on that loop, where
      Sockets spreads them over the thread pool.
  - The default stays one loop per core until a machine with enough CPUs for the load generator measures it. An Intel
    C4 VM is the obvious next step; the room to leave for Postgres and bcrypt has to be measured there too.
