# Closing the gaps: the optimization backlog

Ranked by measured or estimated gain on the TechEmpower tests, from the runs in `docs/benchmarks/` (reduced rig:
app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16). "Best Rust" is the best Rust entry measured on the
same rig: xitca-web barebone for plaintext, json and fortunes, may-minihttp for db, queries and updates.

## Done, measured

| Change | Where | Gain |
|---|---|---|
| Event-loop transport: one receive per request, epoll, direct recv/send, answers sent per wake-up | CodeFusion.Http, perf/http-transport (the transport session) | json +42%, plaintext +20%, db and fortunes +9% |
| Pipelined read groups (2 in flight per connection), bursts shared among idle read loops | Data.Postgres, perf/pg-pipelining | queries +30%; a 32-read burst 2x faster, now ahead of Npgsql |
| Batched keyed updates (a group's run of `UPDATE ... WHERE key = $n` as one `UPDATE ... FROM unnest(...)`), with four writers | Data and Data.Postgres, perf/data-batched-updates | updates 6.8k to 23.0k with the fixes below |
| Writer queue as deep as the reader's (65,536) | Data.Postgres, perf/pg-pipelining | updates: no more 500s at 10+ queries (writes refused as Busy) |
| Job pool keeps 16,384, and a per-thread cache in front of it | Data.Postgres, perf/pg-pipelining | 5.3 MB per 10,240-read burst to 0; db +8.3%, fortunes +2.1% |
| `db.Each`: a request's reads or writes started together | Data, perf/techempower | queries +10% |
| Fortunes: allocation-free sort, the page in one span | the app | 105 B/request and a GC to 0; RSS 1.37 GB to 77 MB |
| Two Postgres readers per core | the app | queries +9% |

## Next, ranked

| # | Change | Where | Estimate |
|---|---|---|---|
| 1 | The HTTP buffer pool's lock per request (json: `Lock` 2% with no database), lock-free or per loop | CodeFusion.Http (transport session) | 2 to 3% on every test |
| 2 | Plaintext: 18% behind xitca-web; perf of the pipelined path (0.13 system calls per request, so our own code) | CodeFusion.Http (transport session) | the gap's size |
| 3 | Fortunes, 14% behind xitca-web: the page writer (3.9%), the list reader copying every row (2.7%), the sort (1.2%), `DataContext.Bind` (0.9%) | the app, Data | 5 to 8% |
| 4 | The read queue's lock per command (`PgQueue.TryAdd`) | Data.Postgres | 1 to 2% on db |
| 5 | Context switches on db and fortunes, 0.18 to 0.24 per request against may-minihttp's 0.10: the read loops sleep in recv; a short spin before sleeping | Data.Postgres | uncertain: CPU for latency |
| 6 | Writer groups bigger than 128 writes once merged (cap the statements per transaction, not the writes) | Data.Postgres | updates, if the writer becomes round-trip bound on a bigger database VM |
| 7 | A full-size rig (app 16, Postgres 32, wrk 32 vCPUs) | GCP quota (raise to 128 asked) | Postgres limits db, queries and updates for the leaders here, wrk limits json and plaintext; needed to settle those rows |

## The allocation gate

Measured on the warmup's connections: plaintext and json allocate exactly 0 B in a minute (7.0M and 1.2M req/s);
db, queries, fortunes and updates 16 to 67 KB a minute (0.0006 to 0.013 B per request), part of it the job pool
making jobs at a peak (20 to 32 in five minutes), the rest unnamed yet (needs stacks: dotnet-trace gc-verbose).


No collection of any generation in any test since the fortunes fix. dotnet-counters' own session allocates about
40 KB/s in the process it watches, so the gate reads the app's own report (`CODEFUSION_GC_REPORT`:
`GC.GetTotalAllocatedBytes` and the collection counts, written without allocating) over a minute measured on the
warmup's connections (a new connection costs a Socket and its handle, about 360 B, which is the connection's).

## Not worth it on this evidence

- Native AOT: aspnetcore-aot was 3 to 9% below aspnetcore's JIT build on five of the six tests here (updates equal),
  and appmpower (AOT) far below both; Tiered PGO stays on.
- Server GC tuning: with nothing allocated per request, the GC never runs; the settings only matter at startup.
