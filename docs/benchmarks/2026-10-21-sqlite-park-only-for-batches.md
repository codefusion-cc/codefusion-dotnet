# SQLite Exclusive: parking only while a loop's batch holds the connection

- **Question:** review finding 6. A loop's batch holds the Exclusive connection for its loop's whole wake-up, and the
  writer thread and other threads' reads spun all that time. Does parking only those waiters keep the throughput of
  the spinning version?
- **Code:** feat/sqlite-provider at 59c8c53 ("before") against the finding 6 fix ("after"). A waiter spins as before
  while a read or a group holds the connection. While a loop's batch holds it (`SqliteReaders.Owner` set), the waiter
  spins 50 µs, then parks on a monitor that the batch's `Exit` pulses. The batch also asks its loop for another round
  (`EventLoopThread.RequestBatchEnd`), so it ends in the wake-up that opened it.
- **Machine:** Apple M1 Pro, 8 cores, load 6 to 11 from other sessions. .NET 10, SQLite 3.50.4.
- **Method:** `CodeFusion.Data.Sqlite.Throughput exclusive normal 2`, both builds alternated, three rounds. The test
  `NothingSpinsWhileALoopsBatchHoldsTheConnection` measures the CPU directly: a batch holds the connection for 500 ms
  while a queued write and a read wait.

| Exclusive, NORMAL | before | after |
|---|---|---|
| point reads, 1 thread | 1.98 to 2.24 M/s | 2.06 to 2.07 M/s |
| point reads, 4 threads | 1.21 to 1.31 M/s | 1.22 to 1.25 M/s |
| updates, 64 loops | 111 to 128 k/s | 120 to 127 k/s |
| updates, 1 loop | 42 to 59 k/s | 49 to 64 k/s |
| 9 reads : 1 update, 16 loops | 378 to 475 k ops/s | 418 to 460 k ops/s |

| While a batch holds the connection for 500 ms | before | after |
|---|---|---|
| process CPU, a queued write and a read waiting | 1,039 ms | less than 300 ms above the CPU of a window without them (the test's bound) |

- **Memory:** 0 B per query, as before (`ProviderAllocationTests`).
- **Correction (second review, 8):** this run's tool had no EventLoopBatches, so no batch held the connection and no
  waiter parked: "throughput level" shows only that the extra check is free. [The run with batches
  on](2026-10-21-sqlite-park-with-batches.md) measures parking itself.
- **Conclusion:** kept. Throughput is level within the load's noise, and the two waiters no longer burn two cores
  while a batch holds the connection. The event-loop $12 entry has not been rerun on the VM with it yet.
