# The SQLite provider prototype against Microsoft.Data.Sqlite and Dapper, on the Mac

- **Question:** what the prototype's direct calls into SQLite, kept snapshots and group commit gain over the usual
  .NET clients, for reads and for writes.
- **Code:** codefusion-sqlite-prototype f21a476. Microsoft.Data.Sqlite 10.0.12 with SQLitePCLRaw 3.0.3, one open
  connection, commands prepared once, text read as strings. Dapper on the same connection.
- **Machine:** Apple M1 Pro, 8 cores, macOS, .NET 10.0.13, SQLite 3.50.4 for all three clients, the same file and
  pragmas (WAL, mmap 256 MiB, 8 MiB cache, temp_store memory). Other sessions shared the Mac (load 3 to 5).
- **Method:** BenchmarkDotNet 0.15.8, `--job short` (3 iterations), out of process:
  `dotnet run -c Release --project bench/CodeFusion.Data.Sqlite.Benchmarks -- --job short --filter '*'` in the
  prototype. Each setup checks that the three read the same values.

| Case | CodeFusion | Microsoft.Data.Sqlite | Dapper |
|---|---|---|---|
| point read (5 columns by key) | 377 ns, 0 B | 1,054 ns, 672 B | 5,446 ns, 2,003 B |
| 20 rows by a range | 2.29 µs, 0 B | 10.52 µs, 3,864 B | 19.14 µs, 7,995 B |
| point reads from 8 threads, per read | 135 ns (7.4 M/s), 0 B | 1,467 ns (0.68 M/s), 672 B | |
| insert, one at a time, NORMAL | 15.4 µs, 0 B | 15.6 µs, 488 B | |
| insert, 64 concurrent writers, per write, NORMAL | 3.1 µs, 0 B | 16.4 µs, 583 B (3.2 µs batched by hand) | |
| insert, one at a time, FULL | 40.6 µs, 0 B | 41.5 µs, 488 B | |
| insert, 64 concurrent writers, per write, FULL | 3.4 µs, 0 B | 45.7 µs, 583 B (3.7 µs batched by hand) | |

- **Memory and CPU:** the provider's "Allocated" for writes read 2 B and 10 B: the benchmark's own async boxes. The
  prototype's checks measured 0 B over 200,000 reads and 160 B over 20,000 awaited writes (one async box) with
  `GC.GetTotalAllocatedBytes(precise: true)`.
- **Conclusion:** a read is SQLite's own B-tree search plus column reads; the usual clients add a reader object,
  strings and a read transaction per statement. One write at a time costs the commit for every client. Under
  concurrency group commit is 5x (NORMAL) to 13x (FULL) faster than a transaction per write, and as fast as batching by
  hand. FULL costs little under load, so it stays the default.
