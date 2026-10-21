# SQLite Exclusive: parking a waiter after 64 spins

- **Question:** review finding 6 asks that no thread spin on the Exclusive connection while a loop's batch holds it.
  What does parking every waiter after 64 spins cost?
- **Code:** feat/sqlite-provider at 59c8c53 ("before"), against the same tree with every wait in `SqliteReaders`
  (the writer's turn and its wait for the connection, a read's wait) parked on a monitor after 64 `SpinOnce` calls,
  and woken by `Exit` and by each read's end ("after"). Not committed.
- **Machine:** Apple M1 Pro, 8 cores, load 10 to 18 from other sessions. .NET 10, SQLite 3.50.4.
- **Method:** `CodeFusion.Data.Sqlite.Throughput exclusive normal 2`, both builds alternated, three rounds.

| Exclusive, NORMAL | before | after |
|---|---|---|
| point reads, 1 thread | 1.91 to 2.10 M/s | 1.94 to 2.03 M/s |
| point reads, 4 threads | 1.23 to 1.45 M/s | 0.61 to 0.76 M/s |
| updates, 64 loops | 85 to 127 k/s | 112 to 126 k/s |
| 9 reads : 1 update, 16 loops | 136 to 449 k ops/s | 119 to 171 k ops/s |

- **Conclusion:** thrown away. Reads that contend for the connection use up 64 spins in microseconds, park, and every
  read's end then pulses the monitor: reads on 4 threads halve and the mixed load drops.
