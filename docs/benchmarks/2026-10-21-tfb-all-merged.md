# CodeFusion with every branch merged against TFB's leaders

- **Question:** where does CodeFusion stand against the leaders with everything built so far together: the
  event-loop transport, pipelined read groups, batched keyed updates, `Each`, the fortunes fixes, and the loop counts
  from [the sweep](2026-10-21-tfb-pg-loops.md)?
- **Code:** perf/techempower (1d7239e) with perf/data-batched-updates (214fd63), perf/pg-pipelining (to d612116)
  and perf/http-transport merged locally, `Transport = EventLoop`: the rig's codefusion-all build. First run: one
  reader per core, one writer. Second run (db, queries, updates): two readers per core, four writers.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16). The references are
  [the first run's](2026-10-21-tfb-baseline.md), on the same VMs.
- **Method:** TFB's sequence, one round each.

| Test | CodeFusion | Best Rust | Gap to best Rust | Leader here | Gap to leader |
|---|---|---|---|---|---|
| plaintext | 7,328,657 | 8,908,020 (xitca-web) | -18% | 9,461,172 (libreactor) | -23% |
| json | 1,235,325 | 1,263,920 (xitca-web) | -2% | 1,483,605 (libreactor) | -17% |
| db | 645,064 | 628,393 (may-minihttp) | +3% | CodeFusion | |
| queries (20) | 65,109 | 33,892 (may-minihttp) | +92% | CodeFusion | |
| fortunes | 494,215 | 576,688 (xitca-web) | -14% | 576,688 (xitca-web) | -14% |
| updates (20) | 23,005 | 22,669 (may-minihttp) | +1.5% | CodeFusion | |

| Test | First run (8 readers, 1 writer) | Second run (16 readers, 4 writers) |
|---|---|---|
| db | 628,994 | 645,064 |
| queries (20) | 65,109 | 64,954 |
| updates (20) | 13,362 | 23,005 |

- **Memory and CPU:**
  - No collection in any test; RSS 60 to 137 MB. The gate: [2026-10-21-tfb-gate-report.md](2026-10-21-tfb-gate-report.md).
  - Postgres 99% busy on queries and 78% on updates; the app 83 to 100%.
  - wrk 88 to 92% busy on plaintext and json: the json number is near what this load VM drives.
- **Conclusion:**
  - From the first run to here, against the best Rust entry: plaintext from -26% to -18%, json from -34% to -2%,
    db from -17% to +3%, queries from +33% to +92%, fortunes from -25% to -14%, updates from -70% to +1.5%.
  - CodeFusion leads db, queries and updates on this rig. On queries Postgres is the limit (99% busy), and
    CodeFusion asks less of it per query than the others.
  - Left: plaintext (-18%) and fortunes (-14%) against xitca-web, and json within noise of it. All three are app
    CPU per request; the profiles come next.
  - This rig limits the top: Postgres for the leaders' db, queries and updates, wrk for json and plaintext. The
    full-size rig would settle those rows.
