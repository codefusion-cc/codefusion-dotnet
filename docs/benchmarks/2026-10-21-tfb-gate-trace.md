# What the gate counted on db and fortunes: a five-minute traced run

- **Question:** the gate through the app's report left 135 KB a minute on db and 82 KB on fortunes
  ([that run](2026-10-21-tfb-gate-report.md)). What allocates?
- **Code:** the rig's codefusion-all build, with two readers per core and four writers.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `TFB_ALLOC_TRACE=1 scripts/TechEmpowerRig.cs gate codefusion-all 300 db fortunes`: the gate over 300 s,
  with the app's EventListener summing the runtime's AllocationTick events (one per about 100 KB allocated, naming
  the type that crossed it).

| Test | req/s | gen0/1/2 | Allocated in 300 s | Types sampled |
|---|---|---|---|---|
| db | 643,342 | 0/0/0 | 187,504 B | SafeSocketHandle, SocketAsyncContext, Byte[], ReadOnlyCollection<Object> |
| fortunes | 479,757 | 0/0/0 | 549,296 B | Socket, the connection loop's StateMachineBox (RunAsync), Byte[] |

- **Memory and CPU:** no collection.
- **Conclusion:**
  - Over five minutes db allocated about what one minute did (187 KB against 135 KB): it isn't per request. The
    types are a connection's: a Socket, its handle and context, and its loop's state machine.
  - The gate ran its warmup and its measured stretch as two wrk runs, so the measured one opened 512 new
    connections at its start (about 360 B each in the runtime's socket objects: 184 KB). That is the gate's
    method, not the requests: it now measures inside one wrk run, past the warmup, on the same connections.
  - ReadOnlyCollection<Object> is the trace's own EventListener (payload names), and the Byte[] samples may be the
    new connections' buffers; the corrected gate settles it.
