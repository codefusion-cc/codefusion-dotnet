# The $12 entry: memory and latency per concurrent user

- **Question:** the $12 contest scores concurrent k6 users, each on its own keep-alive connection and about 0.1 requests
  a second (bench/load.js's loop). At 64,000 users that is only about 7,000 requests a second, but 64,000 open
  connections on 1 vCPU and 2 GB. What does each connection cost, and where does the entry stop?
- **Code:** `cf-tables`, the entry on codefusion-dotnet 150f0e6 (build/twelve-entry before the third SQLite review), the
  stage GCP scored. Its HTTP code is the same as main's for everything measured here.
- **Machine:** the Colima VM cf-bench (vz, aarch64, 4 vCPU, 8 GiB) on the Apple M1 Pro. The server on CPU 0 with 2 GiB and
  no swap, and Ubuntu's default descriptor limit (1024 soft, 524288 hard). The load generator on CPUs 1 to 3, in the
  server's network namespace, connecting from 127.0.0.2 to 127.0.0.9 (one address has about 28,000 ports to one server
  port). **The Mac's load was about 12 from other sessions throughout.**
- **Method:** a new `steps` mode in tools/loadgen (twelve-dollar-codefusion, not committed there): load.js's user loop,
  ramped at 1,000 users a second to each level, 25 s to settle, then a 60 s hold. tools/bench/conns.sh reads the server
  at the start and end of each hold: VmRSS and RssAnon of the process, the cgroup's memory.current with its anon, sock
  and kernel parts, open descriptors and their limit, and CPU over the hold. Run 1 used the first loadgen; run 2 one that
  waits for a response with a zero-byte receive before renting a buffer, with workstation GC and a 4 GiB cap.

## Memory

| Users | Run | RSS | of which anon | Kernel (cgroup) | Open fds / limit |
|---|---|---|---|---|---|
| 10,000 | 1 | 107 MiB | 55 MiB | 24-33 MiB | 10,075 / 524,288 |
| 10,000 | 2 | 111 MiB | 59 MiB | 24-32 MiB | 10,081 / 524,288 |
| 50,000 | 1 | 251 MiB | 185 MiB | 93-128 MiB | 50,082 / 524,288 |
| 50,000 | 2 | 254 MiB | 189 MiB | 92-127 MiB | 50,110 / 524,288 |
| 100,000 | 1 | 400 MiB | 331 MiB | 157-202 MiB | 100,124 / 524,288 |

- **Per connection:** 3.0 to 3.4 KB of anonymous memory in the process (130 MiB more from 10k to 50k, 145 MiB more from
  50k to 100k) and 1.8 to 2.3 KB in the kernel (its socket structures, charged to the server's cgroup). About 5.5 KB in
  all. GCP's 458 MB at 80,000 users agrees.
- An idle connection holds no buffer: CodeFusion.Http returns the request and response buffers when it waits for the
  next request (`HttpExchange.ReleaseBuffers`). What is left is the connection's objects and its socket.
- **2 GB is not the limit.** At 64,000 users the process takes about 330 MiB and the kernel about 140 MiB.
- **The descriptor limit:** .NET raises the soft limit to the hard one before Main. It started at 1024:524288 and ran
  at 524288:524288 from start to peak. The entry now raises it itself too, and logs it.

## Latency and CPU

| Users | Run | Requests/s | p50 | p95 | p99 | Server CPU | Loadgen CPU (of 300%) |
|---|---|---|---|---|---|---|---|
| 10,000 | 1 | 1,041 | 0.22 ms | 3.09 ms | 18.15 ms | 8.5% | not measured |
| 10,000 | 2 | 1,038 | 0.49 ms | 21.85 ms | 84.99 ms | 16.3% | 41.9% |
| 50,000 | 1 | 5,267 | 0.34 ms | 68.57 ms | 203.57 ms | 29.4% | not measured |
| 50,000 | 2 | 5,277 | 0.98 ms | 78.31 ms | 177.14 ms | 43.2% | 81.2% |
| 100,000 | 1 | 4,271 | 4,332 ms | over 10 s | over 10 s | 59.9% | about 286%, VM at 95 MiB free |

- **These latencies measure the Mac, not the entry.** The same 10,000-user hold gave p95 3 ms in one run and 22 ms in the
  next. GCP, on its own box, measured p99 under 1 ms at 10,000 users and 7.4 ms at 20,000.
- **100,000 users overran the load generator:** 286% of its 3 CPUs and 5 GiB, with the VM thrashing, and only 4,271 of
  the 10,000 requests a second due. Run 2's load generator is lighter, but at 10,000 users it still spends about 0.4 ms of
  CPU per request, most of it in .NET's timer queue (one `Task.Delay` per user's think time). Above about 60,000 users
  on 3 vCPUs it can't keep load.js's pace. A timer wheel would fix that.
- **The heartbeat:** the server's once-a-second sweep over every connection took 1.06 s of CPU in the 59 s of the
  50,000-user hold, about 18 ms a beat (0.36 µs a connection). On one CPU each beat holds the event loop's requests back
  that long, 36 ms at 100,000.

## Conclusion

- Memory per connection is about 5.5 KB with nothing left to drop from an idle connection's buffers; 2 GB holds far more
  users than the CPU can serve. Memory isn't the lever.
- What ended the GCP run was the descriptor limit: see
  [twelve-entry-fd-limit-crash](2026-10-21-twelve-entry-fd-limit-crash.md).
- Next: a heap dump at 50,000 users to see what the 3 KB per connection are; the heartbeat's sweep spread over the
  second, or skipped for connections with no deadline near; latency at fixed users on GCP rather than here.
