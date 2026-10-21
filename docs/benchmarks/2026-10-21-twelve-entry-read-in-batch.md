# The $12 entry after the second SQLite review: reads in the batch

- **Question:** the [previous run](2026-10-21-twelve-entry-spec-options.md) found the entry 11% below e3b4ce0, mostly
  from finding 7's early commit before a loop read. With that replaced by reads in the batch (second review, 2), and
  the other fixes in, where does the entry stand, and what is left of the gap?
- **Code:**
  - `cf-batch`: the e3b4ce0 entry ([its run](2026-10-21-twelve-entry-event-loop-batches.md)). It answered a loop
    read from writes not committed yet, the unsafe behaviour finding 7 was about.
  - `cf-review2`: the entry on build/twelve-entry 0252cc1 (feat/sqlite-provider c5b8f27 with perf/http-transport
    7ca8e69 and feat/http-spec-options e955c3e), `/posts/:id` returning `db.Post(id)` streamed. 42 of 42.
  - Probes, not committed: `cf-probe-nodefer` is cf-review2 with reads not taken into the batch, so they read writes
    not committed yet as e3b4ce0 did. `cf-probe-awaited` is cf-review2 with `/posts/:id` awaiting its row as before.
- **Machine:** the Colima VM cf-bench (vz, 4 vCPU, 8 GiB) on the Apple M1 Pro; the server pinned to CPU 0 with 2 GiB and
  no swap, the load generator on CPUs 1 to 3. The Mac's load was 2.4 to 4.2 from other sessions.
- **Method:** tools/bench/bench.sh (containers `sqlite-srv`): `test` first (42 of 42), then `saturate 256 30`: 256
  connections running bench/load.js's loop with no think time, 30 s after a 10 s warm-up, a fresh seed database each
  run. In run order: three rounds alternating cf-batch and cf-review2, then the probes between the two.

| Run | Variant | Mix req/s | p50 | p95 | p99 | Server CPU | Peak RSS |
|---|---|---|---|---|---|---|---|
| 1 | cf-batch | 52,814 | 4.38 ms | 6.83 ms | 19.00 ms | 92.2% | 84 MiB |
| 2 | cf-review2 | 52,138 | 4.52 ms | 6.43 ms | 18.87 ms | 92.5% | 86 MiB |
| 3 | cf-review2 | 52,543 | 4.50 ms | 6.35 ms | 18.56 ms | 92.8% | 87 MiB |
| 4 | cf-batch | 54,541 | 4.26 ms | 6.44 ms | 18.89 ms | 92.5% | 86 MiB |
| 5 | cf-batch | 54,598 | 4.27 ms | 6.47 ms | 18.68 ms | 92.4% | 85 MiB |
| 6 | cf-review2 | 49,484 | 4.75 ms | 7.10 ms | 19.19 ms | 92.7% | 85 MiB |
| 7 | cf-probe-nodefer | 55,497 | 4.12 ms | 6.41 ms | 19.40 ms | 91.5% | |
| 8 | cf-probe-awaited | 49,713 | 4.64 ms | 7.57 ms | 19.78 ms | 92.5% | |
| 9 | cf-review2 | 52,555 | 4.43 ms | 6.78 ms | 18.98 ms | 92.0% | |
| 10 | cf-probe-nodefer | 55,896 | 4.14 ms | 6.22 ms | 19.30 ms | 92.0% | |
| 11 | cf-probe-awaited | 52,308 | 4.46 ms | 6.84 ms | 18.79 ms | 92.7% | |
| 12 | cf-batch | 54,606 | 4.23 ms | 6.57 ms | 18.90 ms | 92.3% | |

Peak memory (the cgroup's, with the mapped file's page cache) was 302 to 335 MiB in every run. No errors.

- **Conclusion:**
  - cf-review2 does 49.5k to 52.6k against cf-batch's 52.8k to 54.6k: about 5% below, p95 and p99 level. The early
    commit's 11% is gone.
  - The whole of the rest is reads waiting for the batch's COMMIT: with them not taken into the batch, the same build
    does 55.5k to 55.9k, above cf-batch. That is the price of finding 7's guarantee, that no read is answered from a
    write its COMMIT could still lose; e3b4ce0 didn't have it.
  - Everything else in this round (the transport's commits, the review fixes, the spec options) costs nothing here,
    and streaming `/posts/:id` is level with awaiting it or better.
  - To get the 5% back without the risk: answer the read at once and have the transport replace the wake-up's held
    answers if the COMMIT fails (the review's other suggestion, a CodeFusion.Http change); or take a read into the
    batch only when the batch has written a table the read reads.
