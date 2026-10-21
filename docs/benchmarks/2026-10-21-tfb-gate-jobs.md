# Counting the Postgres jobs made under load, to name part of what db and fortunes allocate

- **Question:** are the bursts of Byte[] in [the traced fortunes run](2026-10-21-tfb-gate-trace-fortunes.md) the job
  pool making jobs at a new peak?
- **Code:** the rig's codefusion-cache build plus a diagnostic counter (scratch only, not on a branch): every
  `PgJob` made counted, and the count printed in the app's GC report.
- **Machine:** the reduced rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16). The VMs were deleted
  after this run.
- **Method:** `scripts/TechEmpowerRig.cs gate codefusion-cache 300 fortunes db`, no trace.

| Test | req/s | gen0/1/2 | Allocated in 300 s | Jobs made in 300 s | Their share (about 860 B each) |
|---|---|---|---|---|---|
| fortunes | 497,673 | 0/0/0 | 167,224 B | 32 | about 27 KB (16%) |
| db | 690,601 | 0/0/0 | 243,088 B | 20 | about 17 KB (7%) |

- **Conclusion:**
  - The job pool does make jobs under steady load, in small numbers (20 to 32 in five minutes): a thread's cache
    and the shared stack both empty while jobs sit in other threads' caches. That is a small part of what is left.
  - The rest, 140 to 225 KB in five minutes (about 0.001 B per request), is still unnamed; AllocationTick only
    samples once per 100 KB and gives no stack. Next: dotnet-trace's gc-verbose session with stacks, read in
    PerfView, on one of these tests.
  - For the jobs: let a thread whose cache and the stack are both empty take from a full cache's spill sooner
    (spill at half full), or keep the stack's reserve topped up; either needs the counter as a real metric first.
