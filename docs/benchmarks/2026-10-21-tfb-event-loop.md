# CodeFusion on perf/http-transport's event loop against the Sockets transport, on the six TechEmpower tests

- **Question:** what does the event-loop transport (epoll, one receive per request, direct recv and send, answers
  sent per wake-up) give on TechEmpower's tests, against the Sockets transport with the same app?
- **Code:** Sockets: perf/techempower 0d78552 ([its run](2026-10-21-tfb-each-fortunes.md)). Event loop: the same
  tree with perf/http-transport (6895999) merged in locally (not committed anywhere), and
  `builder.Options.Transport = HttpTransport.EventLoop` (one loop per core, the default).
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** TFB's sequence, one round each, the event loop run right after the Sockets one.

| Test | Sockets | Event loop | Change | Best Rust here |
|---|---|---|---|---|
| plaintext | 6,192,771 | 7,426,910 | +20% | 8,908,020 (xitca-web) |
| json | 788,360 | 1,123,177 | +42% | 1,263,920 (xitca-web) |
| db | 518,064 | 567,200 | +9% | 628,393 (may-minihttp) |
| queries (20) | 49,497 | 48,787 | -1% | 33,892 (may-minihttp) |
| fortunes | 427,298 | 466,405 | +9% | 576,688 (xitca-web) |
| updates (20) | 4,285 | 4,091 | -5% | 22,669 (may-minihttp) |

- **Memory and CPU:** the app VM was 99 to 100% busy on plaintext and json for both; wrk rose from 68% to 88%
  busy on json, so the event loop's json number is close to what this load VM can drive. No collections, RSS 61 to
  134 MB.
- **Conclusion:**
  - The event loop is the largest single gain so far on the HTTP-bound tests: json from 38% behind the best Rust
    entry to 11% behind, plaintext from 30% to 17% behind, and past aspnetcore Platform (7.30M) and may-minihttp
    (7.51M is within 1%).
  - The database tests gain what the HTTP layer gave back (+9%); queries and updates are bound elsewhere.
  - Sent to the transport session (perf/http-transport) as data for its branch; no HTTP change here.
