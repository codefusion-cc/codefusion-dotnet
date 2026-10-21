# The allocation gate through the app's own GC report, on the build with every branch merged

- **Question:** under each test's load for a minute, does CodeFusion collect or allocate anything at all, measured
  without a diagnostics session in the process?
- **Code:** perf/techempower (1d7239e, the GC report) with perf/data-batched-updates, perf/pg-pipelining (to
  d612116: the writer queue and job pool fixes) and perf/http-transport merged locally, on the event loop: the
  rig's codefusion-all build. One reader per core and one writer (the loop sweep came after).
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `scripts/TechEmpowerRig.cs gate codefusion-all 60`. Per test:
  - a 30 s warmup, then wrk for 71 s at 1,024 connections (plaintext, pipelined) or 512 (queries and updates at 20);
  - the app writes `GC.GetTotalAllocatedBytes(precise: true)` and the collection counts every 10 s from one
    buffer (CODEFUSION_GC_REPORT); the gate takes the first and last report in the measured minute.

| Test | req/s | gen0/1/2 | Allocated in 60 s | Per request |
|---|---|---|---|---|
| plaintext | 7,087,663 | 0/0/0 | 2,072 B | 0.000005 B |
| json | 1,178,361 | 0/0/0 | 2,096 B | 0.00003 B |
| db | 628,803 | 0/0/0 | 135,528 B | 0.0036 B |
| queries (20) | 64,441 | 0/0/0 | 56 B | 0.00001 B |
| fortunes | 485,820 | 0/0/0 | 82,112 B | 0.0028 B |
| updates (20) | 13,488 | 0/0/0 | 336 B | 0.0004 B |

- **Memory and CPU:** RSS 60 to 137 MB.
- **Conclusion:**
  - No collection of any generation under any test: the gate's first condition holds everywhere.
  - Bytes: not exactly zero anywhere, but within a few hundred bytes to 135 KB a minute, against the 40 KB/s
    dotnet-counters added on its own. On an idle app the report allocates nothing between lines (checked
    locally), so these are the server's.
  - plaintext and json: about 35 B a second at any rate, a small allocation once a second or so, not one per
    request.
  - db and fortunes (about 2 KB/s): the read path through a reply, not an awaited row (queries, which awaits
    them, has 56 B). A 5-minute traced gate (AllocationTick by type, one sample per ~100 KB) is queued to name it.
