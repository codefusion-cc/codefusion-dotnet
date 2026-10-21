# One receive call per request with the event-loop transport, against two with Sockets

- **Question:** does `HttpTransport.EventLoop` remove the zero-byte receive that Sockets makes before every request?
  With Sockets, on Linux, strace showed 2 `recvfrom` and 1 `sendto` per response. The first receive is
  `ReceiveAsync`'s immediate try, which finds nothing right after a response.
- **Code:** before: feat/codefusion-packages 35fcf86. After: perf/http-transport 89d366a. Each was vendored into the $12
  challenge entry (csharp-codefusion-XeonFX, `lib/CodeFusion.Http`). The after entry sets
  `Options.Transport = HttpTransport.EventLoop`.
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. Kernel 6.8.0-117, .NET SDK
  10.0.402, SQLite 3.54.1. Server container pinned to CPU 0 with 2 GiB and no swap; the load generator on CPUs 1 to 3.
  The entry runs with `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1`.
- **Method:** twelve-loadgen `saturate`, 256 keep-alive connections, no think time. 10 s after the start,
  `strace -c -f` on the whole server for 5 s. The counts are divided by the `sendto` count, one per response. One round
  per side, health alone and then the mix.

| Calls per response | Before (Sockets) | After (EventLoop) | Change |
|---|---:|---:|---:|
| health: recvfrom | 2.000 | 1.000 | -1 |
| health: sendto | 1.000 | 1.000 | 0 |
| health: epoll_pwait | 0.001 | 0.004 | +0.003 |
| health: futex | 0.009 | 0.007 | |
| health: all calls | 3.010 | 2.011 | -33% |
| mix: recvfrom | 2.000 | 1.000 | -1 |
| mix: sendto | 1.000 | 1.000 | 0 |
| mix: all calls (SQLite's included) | 5.664 | 3.991 | -30% |

On macOS (kqueue), the Debug build counts every receive call on a connection's socket (`HttpServer.SocketReceives`).
`EventLoopTests.EachKeepAliveRequestCostsThisManyReceives` sends 200 sequential keep-alive requests on one connection.
It asserts exactly 1 receive per request on the event loop and 2 on Sockets, allowing one more for the wait after the last answer. Both pass.

- **Memory and CPU:** see [the throughput run](2026-10-20-event-loop-vs-sockets-linux.md). Peak memory is unchanged
  (262 MiB on health).
- **Conclusion:** it does: one receive and one send per response, and epoll waits stay rare under load (one per
  ~250 responses). Sockets can't get there: no `System.Net.Sockets` option skips `ReceiveAsync`'s first try. Keep it.
