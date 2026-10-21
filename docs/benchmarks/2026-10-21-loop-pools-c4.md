# A buffer stack per event loop, against the shared locked pool, on 8 cores (TechEmpower plaintext and json)

- **Question:**
  - Does giving each event loop its own lock-free buffer stack remove the lock the C4 json profile showed (about 2% of
    the app), and what does it buy?
  - On the same rig: does one listener do as well as a listener per loop, and do 6 loops on 8 cores beat 8?
- **Code:**
  - A: perf/techempower abebd63 with perf/http-transport 7ca8e69.
  - B: the same with perf/http-loop-pools 906971d.
  - Both on EventLoop at its defaults (8 loops, a listener per loop).
- **Machine:** GCP; the app on c4-highcpu-8 and wrk on n2-highcpu-16. Run by the world-perf session; its own record is
  perf/techempower e1fe731, docs/benchmarks/2026-10-21-tfb-loop-pools.md. The VMs are deleted.
- **Method:** TechEmpower's own sequence and scoring, in the order A, B, B, A. perf on json at 512 connections for
  each build. Single 15 s levels for `ListenerPerLoop = false` and `EventLoops = 6` on both builds.

| Test | A | B | B | A | Mean A | Mean B | Change |
|---|---:|---:|---:|---:|---:|---:|---:|
| plaintext (pipelined 16) | 7,279,446 | 7,559,301 | 7,341,658 | 7,263,301 | 7.27M | 7.45M | +2.5% |
| json | 1,152,791 | 1,167,196 | 1,195,523 | 1,166,670 | 1.160M | 1.181M | +1.9% |

| Single levels, 15 s | A | B |
|---|---:|---:|
| plaintext, one listener (against a listener per loop) | 7.34M (7.16M) | 7.45M (7.50M) |
| json, one listener (against a listener per loop) | 1.205M (1.187M) | 1.180M (1.211M) |
| `EventLoops = 6` against 8 | 10 to 16% lower on both | |

- **Memory and CPU:**
  - The app VM was 98 to 100% busy throughout.
  - json profile: A has `Lock.Exit` 0.92% plus `EnterAndGetCurrentThreadId` 0.75%. B has no Lock symbol, and
    `BufferPool.Rent` is 0.49%.
  - Both make 2.06 to 2.08 system calls per request with 0 context switches.
  - The allocation tests are at zero for both.
- **Conclusion:**
  - **Keep the per-loop stacks:** both orders favour B, and the lock is gone from the profile.
  - **One listener per core stays the default:** one listener is within ±2.5%, either way, so per-loop listeners stay
    the default, and 6 loops on 8 cores lose 10 to 16%.
  - **Still behind xitca-web:** plaintext 7.45M against 8.91M, json 1.18M against 1.26M.
  - **Next:** the managed side of plaintext (parse, response head, date header), at 0.13 system calls per request.
