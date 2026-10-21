# perf/http-loop-pools against perf/http-transport on plaintext and json, A B B A

- **Question:** does the transport session's per-loop buffer pool (perf/http-loop-pools) gain anything over the
  shared pool, whose lock the json profile showed at about 2%? And on either build, does `ListenerPerLoop = false`
  or 6 event loops instead of 8 help?
- **Code:** both are perf/techempower abebd63 with Program.cs knobs for the event loops and the listener (local,
  not committed).
  - A = merged with perf/http-transport 7ca8e69.
  - B = merged with perf/http-loop-pools 906971d.
  - Both use the EventLoop transport's defaults: one loop per core (8) and a listener per loop.
- **Machine:** reduced rig, app and wrk VMs only (no Postgres needed): app c4-highcpu-8, wrk n2-highcpu-16, GCP
  europe-west1-b, Ubuntu 24.04.
- **Method:**
  - TFB's sequence (`scripts/TechEmpowerRig.cs bench`), in the order A, B, B, A; the best level of each.
  - Then one perf profile of json at 512 connections on each.
  - Then single 15 s levels (`quick`): plaintext at 256 connections (pipelined 16), json at 512, each with the
    defaults, `CODEFUSION_LISTENER_PER_LOOP=false` and `CODEFUSION_EVENT_LOOPS=6`.
- **Results (requests/s):**

| Test | A (1st) | B (1st) | B (2nd) | A (2nd) | A mean | B mean | B vs A |
|---|---|---|---|---|---|---|---|
| plaintext | 7,279,446 | 7,559,301 | 7,341,658 | 7,263,301 | 7,271,374 | 7,450,480 | +2.5% |
| json | 1,152,791 | 1,167,196 | 1,195,523 | 1,166,670 | 1,159,731 | 1,181,360 | +1.9% |

| Single level | A defaults | A listener shared | A 6 loops | B defaults | B listener shared | B 6 loops |
|---|---|---|---|---|---|---|
| plaintext, 256 | 7,163,806 | 7,343,024 | 6,421,104 | 7,497,597 | 7,447,145 | 6,243,664 |
| json, 512 | 1,186,731 | 1,204,703 | 1,012,524 | 1,210,536 | 1,180,034 | 1,028,034 |

- **Profiles (json, 512):**
  - A: `System.Threading.Lock` Exit and Enter at 0.92% and 0.75% of samples.
  - B: no `Lock` at all; `BufferPool.Rent` at 0.49%.
  - Both make 2.06 to 2.08 system calls per request, with no context switches.
- **CPU and memory:** the app VM is 98 to 100% busy in every run; wrk 88 to 91%. RSS 49 to 51 MB on json, 129 to 130
  MB on plaintext.
- **Conclusion:**
  - The per-loop pools take the lock out and gain 2 to 2.5% on both tests, beyond the spread between the two runs of
    the same build (A's two runs differ by 0.2% and 1.2%).
  - Sharing one listener is within ±2.5% either way: no reason to change the default.
  - 6 loops on 8 vCPUs lose 10 to 16%: one loop per core stays the default.
