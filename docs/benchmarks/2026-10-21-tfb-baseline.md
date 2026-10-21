# CodeFusion against TFB's leaders on the six TechEmpower tests, first run

- **Question:** where does CodeFusion stand against the best entries of TechEmpower's Round 23 on each test type,
  measured the same way on the same machines, and against the best Rust entry in particular?
- **Code:** CodeFusion: perf/techempower ba052ee (the suite as first written: queries and updates await each query
  in turn, fortunes sorted with `Span.Sort`). References: TechEmpower/FrameworkBenchmarks 57d92fbec6f8, each built
  from its own dockerfile with its own settings: aspnetcore (Platform), aspnetcore-minimal, aspnetcore-aot,
  may-minihttp, xitca-web barebone, h2o, libreactor, libreactor-server, appmpower odbc-pg.
- **Machine:** the reduced rig (32 vCPUs, the project's quota; not comparable with full-rig runs). GCP
  europe-west1-b, one compact placement:
  - app c4-highcpu-8;
  - Postgres c4-highcpu-8, with TFB's postgres:18 image and config;
  - wrk n2-highcpu-16 (the C4 quota is 24 per region).
  - All VMs on Ubuntu 24.04 (kernel 7.0.0-1014-gcp), every server in Docker on the host's network.
- **Method:** `scripts/TechEmpowerRig.cs bench`, which is TFB's toolset sequence:
  - a 5 s primer, then a 15 s warmup at 512 connections;
  - then 15 s per level: plaintext pipelined 16 deep at 256, 1,024, 4,096 and 16,384 connections; json, db and
    fortunes at 16 to 512; queries and updates at 1 to 20 queries on 512 connections.
  - TFB's score: answered requests without errors over 15 s, at the best level (the 20-query level for queries and
    updates). One round each, run one after another between 22:40 and 01:15 UTC.

| Test | CodeFusion | Best Rust | Gap to best Rust | Leader here | Gap to leader | aspnetcore Platform |
|---|---|---|---|---|---|---|
| plaintext | 6,575,444 | 8,908,020 (xitca-web) | -26% | 9,461,172 (libreactor) | -31% | 7,297,751 |
| json | 835,600 | 1,263,920 (xitca-web) | -34% | 1,483,605 (libreactor) | -44% | 703,920 |
| db | 520,160 | 628,393 (may-minihttp) | -17% | 628,393 (may-minihttp) | -17% | 381,963 |
| queries (20) | 45,193 | 33,892 (may-minihttp) | +33% | 45,193 (CodeFusion) | | 32,210 |
| fortunes | 430,145 | 576,688 (xitca-web) | -25% | 576,688 (xitca-web) | -25% | 307,474 |
| updates (20) | 6,830 | 22,669 (may-minihttp) | -70% | 22,669 (may-minihttp) | -70% | 18,505 |

| Entry | plaintext | json | db | queries | fortunes | updates |
|---|---|---|---|---|---|---|
| codefusion ba052ee | 6,575,444 | 835,600 | 520,160 | 45,193 | 430,145 | 6,830 |
| aspnetcore (Platform) | 7,297,751 | 703,920 | 381,963 | 32,210 | 307,474 | 18,505 |
| aspnetcore-minimal | 3,076,848 | 535,959 | 302,311 | 27,976 | 236,958 | 18,834 |
| aspnetcore-aot | 6,726,124 | 653,318 | 368,346 | 31,213 | 279,260 | 18,675 |
| may-minihttp | 7,507,802 | 1,214,721 | 628,393 | 33,892 | 567,122 | 22,669 |
| xitca-web barebone | 8,908,020 | 1,263,920 | 611,351 | 31,551 | 576,688 | 20,213 |
| h2o | 3,204,344 | 1,125,393 | 622,992 | 32,235 | 553,926 | 22,386 |
| libreactor | 9,461,172 | 1,483,605 | | | | |
| libreactor-server | 9,328,326 | 1,307,686 | | | | |
| appmpower odbc-pg (AOT) | | | 193,352 | 16,742 | 144,177 | 12,744 |

- **Where the rig, not the server, is the limit** (each VM's busiest 15 s, from vmstat):
  - Postgres is 93 to 100% busy for the leaders on db, queries and updates, so those three are capped by an 8-vCPU
    database. CodeFusion's db test had Postgres at 32%.
  - wrk is 92 to 100% busy for the fastest servers on json and plaintext (libreactor at 99 and 100%), so their
    numbers are low bounds.
  - CodeFusion's app VM was 97 to 100% busy on plaintext and json: those are CodeFusion's own cost per request.
- **Memory and CPU:**
  - CodeFusion's RSS was 57 to 102 MB, except fortunes at 1,373 MB.
  - dotnet-counters, CodeFusion: no collections in plaintext, json, db, queries or updates. Fortunes: one gen0 and
    one gen1, at about 105 B per request.
  - aspnetcore Platform: 13 to 59 gen0 collections on the database tests, at 1.6 to 20 KB per request.
  - Native AOT entries (aspnetcore-aot, appmpower) show no counters: dotnet-counters can't attach to them.
- **Conclusion:**
  - CodeFusion beats every .NET entry on json, db, queries and fortunes, and every entry on queries: its reads go to
    Postgres in groups with one Sync, so Postgres does far less work per query.
  - Updates is the big gap. The app sat 61% idle: each request awaited 20 writes one after another, so 20 group
    commits each.
  - Fortunes allocated because `Span.Sort` with a struct comparer boxes it and makes a delegate per call; fixed in
    844ad61 (an allocation-free sort) and 25c6f2c (the page written into one span).
  - Plaintext and json are CPU per request in the HTTP layer; the event-loop transport (perf/http-transport) is the
    next measurement there.
  - Next: CodeFusion with `Each` and the fortunes fixes, the event-loop merge, and the batched-update and pipelining
    branches; then the 60 s allocation gate.
  - Rig: a full-size rig (app 16, Postgres 32, wrk 32 vCPUs) needs the quota raise.
