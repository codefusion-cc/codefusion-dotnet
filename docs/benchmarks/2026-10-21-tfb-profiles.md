# Where the CPU goes: CodeFusion against may-minihttp on db and json, with perf

- **Question:** with db, queries and updates ahead of the best Rust entry, what is left in the per-request cost of
  db, fortunes and json, and how does it split between the kernel and our own code against may-minihttp?
- **Code:** the rig's codefusion-all build (every branch merged, the event loop, two readers per core, four
  writers); may-minihttp from TFB 57d92fbec6f8.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16).
- **Method:** `scripts/TechEmpowerRig.cs profile <entry> <test> 512`: 15 s of steady load, then `perf stat` of
  system calls and context switches for 10 s, then `perf record -F 999 -g` for 10 s, the report by symbol with
  .NET's perf map (W^X off for these runs so perf can name JIT code). Profiling costs throughput, so the req/s
  here are lower than the bench's.

| Entry, test | req/s while profiled | System calls per request | Context switches per request | Kernel | Own code | Runtime (coreclr) |
|---|---|---|---|---|---|---|
| CodeFusion db | 579,532 | 2.56 | 0.181 | 67% | 24% | 7% |
| may-minihttp db | 628,089 | 5.08 | 0.099 | 85% | 11% | |
| CodeFusion fortunes | 435,419 | 2.72 | 0.241 | 60% | 32% | 5% |
| CodeFusion json | 1,173,568 | 2.08 | 0.000 | 83% | 13% | 1% |
| may-minihttp json | 1,203,035 | 3.05 | 0.000 | 91% | 4% | |
| CodeFusion plaintext | 7,284,168 | 0.13 | 0.000 | | | |

- **The kernel's part:** softirq network processing 11% (`handle_softirqs`), conntrack (`__nf_conntrack_find_get`,
  2%: Docker's netfilter, the same for every entry), the scheduler (`finish_task_switch`, 2% on db and fortunes).
- **Our own code, db:** the connection loop's `RunAsync` 2.7%, the generated row reader 2.0%, `HttpApp.Dispatch`
  1.7%, `FlushAsync` 1.2%, `ReleaseBuffers` 1.1%, `System.Threading.Lock` enter and exit 1.7%, `PgJob.Rent` 1.0%,
  `BeginResponse` 1.0%, `ReplyRun.OnRun` 0.6%, the pipelined read loop 0.6%.
- **Fortunes adds:** the page writer 3.9%, the generated list reader 2.7%, `Lock` 3.0%, `PgConnection.Row` 1.3%,
  the sort's compares 1.2%, `DataContext.Bind` 0.9%.
- **json:** `Lock` 2.0% and `ReleaseBuffers` 0.7% with no database at all, so the HTTP side takes a lock per request
  too (its buffer pool, which an idle connection's buffers go back to); then the parser (`ParseHead`, `Header`,
  `IndexOf`) 2.2%, `RunAsync` 1.5%, `BeginResponse` 1.0%.
- **Conclusion:**
  - CodeFusion makes half the system calls per request of may-minihttp (one receive and one send; may-minihttp
    adds its own polling), so what is left is our own code: 2 to 3 times may-minihttp's share per request.
  - The locks are the clearest piece: the Postgres job pool and queue (rent on the request's thread, return on the
    loop's, under one lock each), and the HTTP buffer pool (json shows it alone). Lock-free pools are next, the
    Postgres one here and the HTTP one for the transport session.
  - Context switches on db and fortunes (0.18 to 0.24 per request, against may-minihttp's 0.10) come from the
    Postgres loops sleeping in recv and being woken; fewer read loops trade queries' throughput for that.
