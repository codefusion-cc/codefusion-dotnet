# The allocation gate on the warmup's connections, with the job cache

- **Question:** measured on the same connections as the warmup (so no connection is accepted inside the measured
  minute), what do the six tests still allocate?
- **Code:** the rig's codefusion-cache build: perf/techempower (1d7239e on, the gate as of 6d417f3) with
  perf/data-batched-updates, perf/pg-pipelining (11a2d71, the job cache) and perf/http-transport merged, event loop,
  two readers per core, four writers.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `scripts/TechEmpowerRig.cs gate codefusion-cache 60`: one wrk run per test for 101 s, the measured
  minute starting 30 s in; the app's own report (allocated bytes and collection counts, every 10 s) read at its
  ends.

| Test | req/s | gen0/1/2 | Allocated in 60 s | Per request | Gate |
|---|---|---|---|---|---|
| plaintext | 7,034,669 | 0/0/0 | 0 B | 0 | PASS |
| json | 1,210,715 | 0/0/0 | 0 B | 0 | PASS |
| db | 686,483 | 0/0/0 | 26,376 B | 0.0006 B | fail |
| queries (20) | 64,253 | 0/0/0 | 16,416 B | 0.0043 B | fail |
| fortunes | 502,514 | 0/0/0 | 66,968 B | 0.0022 B | fail |
| updates (20) | 22,983 | 0/0/0 | 17,336 B | 0.0126 B | fail |

- **Memory and CPU:** no collection anywhere.
- **Conclusion:**
  - With no connection accepted in the window, plaintext and json allocate exactly nothing for a minute at 7M and
    1.2M req/s.
  - The four database tests still allocate 16 to 67 KB a minute: not per request (one byte per 200 to 1,600
    requests), so something now and then on the database path. A 10-minute traced fortunes run is next, to name it.
