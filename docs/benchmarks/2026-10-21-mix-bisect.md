# Is the event loop's 6% loss on the mix since 805f8d9 real? A bisect over the commits in between

- **Question:** two turns showed the $12 entry's mix 6% lower on the event loop at the branch's head than at 805f8d9,
  with Sockets level and fewer system calls per response. Which commit costs it, and why?
- **Code:** five images of the $12 entry, each vendoring CodeFusion.Http (and, from the merge on, Core) at one commit:
  - 805f8d9 (twelve-cf-reuse);
  - 3a8d89d, the first review's fixes;
  - 5c4eb32, the merge of feat/codefusion-packages;
  - 6c5cced, the second review's fixes;
  - 00cc86e, first requests on the loop (twelve-cf-head).
  - The Core seam (d7fc5b5) changes no code path the entry runs, so it wasn't built on its own.
- **Machine:** Colima VM cf-bench (4 vCPU arm64, 8 GiB) on an Apple M-series MacBook Pro. .NET SDK 10.0.402, SQLite
  3.54.1. Server on CPU 0 and load on CPUs 1 to 3. The coordinator's turn ran 03:24 to 03:34, with nothing else in
  the VM.
- **Method:**
  - The mix, 256 connections, 30 s.
  - Two rounds, each running the five images in commit order.
  - perf (997 Hz, 15 s) on the mix for both ends, with symbols.

| Mix, req/s | Round 1 | Round 2 |
|---|---:|---:|
| 805f8d9 | 47,252 | 47,359 |
| 3a8d89d | 47,808 | 46,482 |
| 5c4eb32 | 48,057 | 44,165 |
| 6c5cced | 49,418 | 44,925 |
| 00cc86e | 49,084 (+3.9%) | 44,910 (-5.2%) |
| p99, 805f8d9 against 00cc86e | 9.79 against 8.67 ms | 9.52 against 10.35 ms |

- **Profile (mix):** SQLite leads both ends (`walFindFrame` 7.1% and 6.2%, `sqlite3VdbeExec` 5.5% and 6.1%,
  `sqlite3BtreeTableMoveto` 5.4% and 5.8%), then the entry's `PostJson.Read` (2.2% and 2.4%).
  - `RunAsync` is 0.58% and 0.59%; nothing else of CodeFusion.Http reaches 0.5% at either end.
  - The kernel's `__wake_up_sync_key` is 3.6% against 5.2%, the one visible difference, and within what one run to
    the next shows on this VM.
- **Conclusion:** there is no 6% loss from any one commit.
  - In round 1 every later commit was faster than 805f8d9; in round 2 every one from the merge on was slower, by up
    to 7%.
  - The swing follows the position in the run, not the code: a fresh copy of the database each run and this shared
    VM move the mix by about ±5%.
  - Every earlier comparison ran 805f8d9 first in each pair, which explains the earlier "−6%".
  - Nothing to fix. A claim of a few percent on the mix needs alternating rounds over several orders, or the C4
    machine.
