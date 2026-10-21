# SQLite Exclusive: parking a waiter after 200 µs

- **Question:** the [64-spin version](2026-10-21-sqlite-park-after-64-spins.md) parked too soon. Does a time budget,
  200 µs of spinning before a waiter parks, keep the throughput?
- **Code:** as that run, with the 64 spins replaced by 200 µs measured with `Stopwatch` ("after"), against 59c8c53
  ("before"). Not committed.
- **Machine:** Apple M1 Pro, 8 cores, load 8 to 11 from other sessions. .NET 10, SQLite 3.50.4.
- **Method:** `CodeFusion.Data.Sqlite.Throughput exclusive normal 2`, both builds alternated, three rounds.

| Exclusive, NORMAL | before | after |
|---|---|---|
| point reads, 1 thread | 1.92 to 1.98 M/s | 1.91 to 2.18 M/s |
| point reads, 4 threads | 1.19 to 1.28 M/s | 0.90 to 0.92 M/s |
| updates, 64 loops | 99 to 128 k/s | 94 to 128 k/s |
| 9 reads : 1 update, 16 loops | 370 to 405 k ops/s | 80 to 94 k ops/s |

- **Conclusion:** thrown away, worse on the mixed load. With 16 readers and the writer, a group commit lasts past
  200 µs often enough that readers park, and each read's end wakes them all. Parking has to be kept to the one case
  that holds the connection for long: a loop's batch.
