# Allocations per keep-alive request on the event-loop transport (kqueue) against Sockets, on macOS

- **Question:** does the event-loop transport keep the server at zero allocations per request? It adds a loop thread,
  a made-up edge on the thread pool for closes, and a new path for completing the wait.
- **Code:** perf/http-transport 89d366a.
- **Machine:** Apple M-series MacBook Pro (8 cores, shared with other work), macOS 26, .NET SDK 10.0.402, Release
  build.
- **Method:**
  - `AllocationTests` and `AppAllocationTests` in `CodeFusion.Http.Tests`. Each warms up with 2 × 20,000 requests per
    connection, then counts `GC.GetTotalAllocatedBytes(precise: true)` for the whole process over 50,000 requests per
    connection.
  - Run once, with `CODEFUSION_HTTP_TRANSPORT=EventLoop --max-threads 2 --report-xunit-trx`; the numbers come from
    the trx output.
  - The rows name their transport explicitly, so both transports ran in the same run. The pass mark is 0.5 B/request.

| Case (AppAllocationTests) | Sockets | EventLoop |
|---|---:|---:|
| route, query, viewer, singleton, JSON; 1 connection | 0.044 B/req | 0.044 |
| route, query, viewer, singleton, JSON; 8 connections | 0.008 | 0.004 |
| body with escapes and tags, token issued into the arena; 8 connections | 0.072 | 0.042 |
| async handler, wait ends on another thread; 1 connection | 0.056 | 0.022 |
| async handler, wait ends on another thread; 8 connections | 0.005 | 0.017 |
| reply whose write waits; 1 connection | 0.038 | 0.049 |

`AllocationTests` (bare server: sync and async handlers on 1 and 8 connections, with both transports) printed seven
results without their case names. They ranged from 0.008 to 0.061 B/request, all under the mark.

- **Memory and CPU:** a few KB per run for the whole process, which is the listener's events and the test runner. One
  allocation per request would add at least 24 B/request.
- **Conclusion:** zero allocations per request on both transports; the differences are noise. Keep it. The EventLoop
  rows stay in both tests as the guard.
