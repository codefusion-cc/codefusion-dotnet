# Changes to the $12 entry tried and thrown away

- **Question:** whether four changes make the $12 entry faster under the saturation load.
- **Code:** the $12 entry of [the saturation run](2026-10-20-twelve-entry-saturation.md), with one change at a time.
- **Machine:** the same Colima VM, the server pinned to one CPU with 2 GiB.
- **Method:** `tools/bench/bench.sh <entry> saturate 256 30` before and after each change. This file is a backfill:
  only the conclusions were written down at the time, not each run's numbers.

| Change | Result |
|---|---|
| Native AOT instead of the JIT with dynamic PGO | the same throughput, within the noise |
| the feed's like counts as one range scan, in SQL or as three statements | no faster than the 20 counts |
| committing a lone write on the request's thread (leader commit) | no faster under load, p99 worse |
| a checkpoint every 4,000 WAL frames instead of 1,000 | no gain |

- **Memory and CPU:** not recorded.
- **Conclusion:** none was kept. The time goes to the transport and the kernel, not to these. Every such run from
  here on gets its own file with its numbers.
