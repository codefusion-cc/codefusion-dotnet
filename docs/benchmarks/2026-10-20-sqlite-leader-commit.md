# Leader commit: a lone write committed on its caller's thread

- **Question:** on one core a lone write pays two thread switches to reach the writer thread
  ([the 1 vCPU run](2026-10-20-sqlite-prototype-1vcpu.md)). Does committing it on the caller's thread, when the writer
  is idle and nothing is queued, make writes cheaper?
- **Code:** codefusion-sqlite-prototype 47e60c3 (`LeaderCommit`, off by default), and the $12 entry with it turned on.
- **Machine:** Apple M1 Pro for the check; the 1 vCPU / 2 GiB Colima VM for the $12 entry's load runs
  ([its setup](2026-10-20-twelve-entry-saturation.md)).
- **Method:** the prototype's leader-commit check (one thread submitting 400 writes one after another, transactions
  counted); then the $12 entry under saturation with the option on and off. Only the conclusion of the entry's runs was
  written down at the time, not their numbers.

| Case | Writer thread | Leader commit |
|---|---|---|
| 400 writes from one thread, one after another: transactions | 4 | 400 |
| the $12 entry under saturation | | no faster, p99 worse |

- **Memory and CPU:** not measured.
- **Conclusion:** a thread that submits writes one after another, as an event loop on one core does, commits each
  alone with leader commit, where the writer thread, woken once, takes everything queued meanwhile as one group. Kept as
  an option, off by default; the entry doesn't use it. The idea that does fit an event loop is one commit per loop
  wake-up, which needs a hook from the HTTP server.
