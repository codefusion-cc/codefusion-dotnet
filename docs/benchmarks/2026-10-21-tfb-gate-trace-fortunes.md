# Ten minutes of fortunes, traced: what the database tests still allocate

- **Question:** on the warmup's connections fortunes still allocated 67 KB a minute
  ([the steady gate](2026-10-21-tfb-gate-steady.md)). Is it steady or in bursts, and of what type?
- **Code:** the rig's codefusion-cache build (every branch merged, the job cache, event loop, two readers per core,
  four writers).
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `TFB_ALLOC_TRACE=1 scripts/TechEmpowerRig.cs gate codefusion-cache 600 fortunes`: one wrk run, 600 s
  measured past a 30 s warmup, the app's report every 10 s, and its AllocationTick summary (by type, one sample per
  about 100 KB) every 10 s.

| | Value |
|---|---|
| req/s | 501,452 |
| Collections | 0 / 0 / 0 |
| Allocated in 600 s | 385,736 B (0.0013 B per request) |
| Every 10 s | +2,512 or +3,088 B, alternating: the trace's own summary (its sort, strings and EventListener payloads) |
| Now and then | +7 to +37 KB jumps, a dozen in the 600 s, at no regular interval |
| Types sampled in the window | Byte[] (2 samples), ReadOnlyCollection<Object> (1: the EventListener's own) |

- **Memory and CPU:** RSS steady; no collection.
- **Conclusion:**
  - Without the trace's own 168 KB, fortunes allocated about 220 KB in ten minutes, in bursts of 7 to 37 KB: no
    allocation per request, but something that happens a dozen times in ten minutes.
  - Two candidates fit bursts of Byte[]: the job pool making a few dozen jobs at a new peak (about 860 B each,
    mostly the 512-byte buffer), when the shared stack is empty while jobs sit in other threads' caches; or the
    runtime's socket operation objects when a send can't complete at once. A diagnostic build counts the jobs made
    (the app's report prints the count), to tell the two apart.
