# Exclusive mode, first try: one connection, a read transaction per read

- **Question:** whether one connection for every read and write, with the file locked to the process
  (`locking_mode=EXCLUSIVE`), beats reader connections of their own on a one-core server, and what it costs on many
  cores.
- **Code:** codefusion-dotnet feat/sqlite-provider at 579fa06 plus the first, uncommitted version of `Exclusive`, in
  which each read ran in its own implicit transaction. The tool was a throwaway program, the same loops as the later
  `benchmarks/CodeFusion.Data.Sqlite.Throughput`.
- **Machine:** Apple M1 Pro, 8 cores, under load from other sessions (load 8 to 11); and a container pinned to one CPU
  with 2 GiB on the Colima VM cf-bench (vz, linux-arm64, `mcr.microsoft.com/dotnet/runtime:10.0`). SQLite 3.50.4,
  `synchronous=NORMAL`.
- **Method:** 100,000 rows; 2 s each of point reads on 1 and 4 threads, updates from 64 awaiting loops and from 1, and
  16 loops of 9 reads and an awaited update. Three rounds alternating the two modes; ranges over the rounds.

| 1 vCPU | Readers of their own | Exclusive |
|---|---|---|
| point reads, 1 thread | 1.50 to 1.69 M/s | 1.03 to 1.36 M/s |
| updates, 64 loops | 54 to 71 k/s | 59 to 63 k/s |
| 9 reads : 1 update, 16 loops | 33 to 55 k ops/s | 317 to 437 k ops/s |

| 8-core Mac | Readers of their own | Exclusive |
|---|---|---|
| point reads, 4 threads | 1.77 to 6.43 M/s | 1.35 to 1.85 M/s |
| updates, 1 loop | 2.8 to 43.6 k/s | 48.6 to 51.0 k/s |
| 9 reads : 1 update, 16 loops | 128 to 216 k ops/s | 67 to 100 k ops/s |

- **Memory and CPU:** not measured here.
- **Conclusion:** on one core the mixed load runs 6 to 10 times faster, but a lone read is slower: a read transaction
  per statement costs what [kept snapshots](2026-10-20-sqlite-prototype-kept-snapshots.md) saved. Next: keep a read
  transaction on the shared connection between groups. On many cores Exclusive loses, as expected; it stays opt-in.
