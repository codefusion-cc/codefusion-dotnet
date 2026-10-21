# SQLite point reads across threads: a read transaction per statement against kept snapshots

- **Question:** why the first SQLite provider prototype read no faster on 8 threads than on 1, and what lifts it.
- **Code:** codefusion-sqlite-prototype 6881e00 (a read transaction per statement, jobs from a locked pool) and
  f21a476 (kept snapshots, thread-local jobs, padded flags). The prototype later became `src/CodeFusion.Data.Sqlite`.
- **Machine:** Apple M1 Pro, 8 cores, macOS, .NET 10.0.402 SDK, SQLite 3.50.4 (`e_sqlite3` from SourceGear.sqlite3).
  Other sessions shared the Mac.
- **Method:** the prototype's `checks scale`: point reads by primary key of a 100,000-row table from 1, 2, 4 and 8
  threads for a fixed time, reads per second. The "raw" rows call SQLite directly, without the provider, as limits.

| Reads per second | 1 thread | 2 | 4 | 8 |
|---|---|---|---|---|
| a read transaction per statement, jobs from a locked pool | 0.73 M | 0.60 M | 0.80 M | 0.93 M |
| raw: own connection per thread, a read transaction per statement | 0.81 M | 1.01 M | 1.25 M | 1.18 M |
| raw: own connection, one read transaction for all, a job per thread | 2.73 M | 4.77 M | 8.32 M | 16.28 M |
| kept snapshots, thread-local jobs, padded flags | 1.98 M | 3.89 M | 6.97 M | 11.58 M |

- **Memory and CPU:** 0 B per read in both versions.
- **Conclusion:** beginning and ending a WAL read transaction takes the WAL index's locks, which every connection to
  the file shares: 0.9 µs of a 1.4 µs read, and the reason 8 threads ran no faster than 1 (Microsoft.Data.Sqlite has
  the same limit). Kept: a reader connection keeps its snapshot until the writer commits, a commit clock tells it when
  to take a new one, and jobs come from a cache per thread. The gap to the raw 16.3 M/s is the clock read and the
  connection flags.
