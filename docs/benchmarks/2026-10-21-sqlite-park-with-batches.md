# SQLite Exclusive: parking against spinning, with event-loop batches on

- **Question:** the [first parking run](2026-10-21-sqlite-park-only-for-batches.md) never had EventLoopBatches on, so no
  batch held the connection and nothing parked (second review, 8). With batches on, what does parking cost against
  spinning, and what do reads in the batch (second review, 2) do to it?
- **Code:** feat/sqlite-provider at afe13bb plus the throughput tool's new `batches` mode ("after"), against the same
  tree with the spin before parking set to never end ("spin": the waiters spin as before 47f0bab). Not committed.
- **Machine:** Apple M1 Pro, 8 cores, load 2.5 to 3.4 from other sessions. .NET 10, SQLite 3.50.4.
- **Method:** `CodeFusion.Data.Sqlite.Throughput batches <normal|full> 2`: Exclusive with EventLoopBatches, 100,000
  rows. Event-loop threads (marked as CodeFusion.Http's are) run wake-ups of 16 requests, a write then three reads,
  four times; each read after a write runs in the batch and completes at its end. Cases: 1 loop, 2 loops, and 1 loop
  with 4 threads reading off the loop, which wait while the batch holds the connection. Three rounds alternating the
  builds at NORMAL, three at FULL; ranges over the rounds.

| | after (park) | spin |
|---|---|---|
| NORMAL, 1 loop | 393 to 401 k ops/s | 360 to 399 k ops/s |
| NORMAL, 2 loops | 325 to 326 k ops/s | 313 to 323 k ops/s |
| NORMAL, 1 loop + 4 readers | 327 to 331 k ops/s, readers 45 to 51 k/s | 330 to 343 k ops/s, readers 42 to 44 k/s |
| FULL, 1 loop | 239 to 245 k ops/s | 243 to 247 k ops/s |
| FULL, 2 loops | 181 to 183 k ops/s | 187 to 189 k ops/s |
| FULL, 1 loop + 4 readers | 169 to 178 k ops/s, readers 38 to 41 k/s | 194 to 209 k ops/s, readers 26 to 33 k/s |

- **CPU:** not measured here. `ProviderAllocationTests.NothingSpinsWhileALoopsBatchHoldsTheConnection` measures it: a
  batch holding the connection for 500 ms while a queued write and a read wait cost 1,039 ms of process CPU spinning.
  Parked, the test's bound is 300 ms above the same process's CPU in a window with no waiters.
- **Conclusion:** kept. With nothing waiting off the loop, parking costs nothing. With readers waiting off the loop at
  FULL, the loop does 8 to 19% fewer operations and the readers 15 to 57% more (over the ranges): parked readers get their
  turn when the batch ends instead of losing it to the loop's next batch. Both together are about 1% fewer than spinning
  at NORMAL and about 8% fewer at FULL (207 to 219 k against 220 to 242 k), against spinning that burns a core per
  waiter for as long as a batch holds the connection. (Corrected after the third review: this said "about 5%".) A wake-up's reads now run in its batch, so a batch
  holds the connection a little longer (its reads' rows are kept), but it commits once.
