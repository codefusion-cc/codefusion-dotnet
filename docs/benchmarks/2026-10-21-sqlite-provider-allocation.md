# CodeFusion.Data.Sqlite allocates nothing per query

- **Question:** whether the SQLite provider allocates per query once warm, measured over the whole process (writer
  thread and completions included), with the same test the Postgres provider passes.
- **Code:** codefusion-dotnet feat/sqlite-provider 579fa06 (shared allocation test) and d328bfd (Exclusive mode's).
- **Machine:** Apple M1 Pro, macOS, .NET 10.0.402 SDK, Release, SourceGear's `e_sqlite3` 3.50.4. Other sessions shared
  the Mac. Postgres 18 on the same Mac for the Postgres numbers.
- **Method:** `CodeFusion.Data.Tests.AllocationTests.QueriesAllocateNothingOnceWarm`, linked unchanged into
  CodeFusion.Data.Sqlite.Tests: the Conduit reads and writes from 4 callers after a warm-up, the whole process's
  `GC.GetTotalAllocatedBytes(precise: true)` over 14,600 queries and over 29,200, each size measured three times in turn
  and the smallest kept. `CODEFUSION_TEST_REPORT` collects the lines; 16 runs of the whole suite. Exclusive mode:
  `ExclusiveTests.QueriesAllocateNothingOnceWarm`, 10,000 reads and 1,000 writes against 20,000 and 2,000.

| Case | 14,600 queries | 29,200 queries | Per extra query |
|---|---|---|---|
| SQLite, readers of their own (16 runs of 16) | 2,928 B | 2,928 B | 0 B |
| Postgres, the same test | 4,528 B | 4,528 B | 0 B |

The fixed bytes are the test's own tasks and async boxes. The Exclusive test passes its bounds (under 2,000 B for
11,000 queries, under 500 B more for twice as many).

- **Memory and CPU:** see above; no GC during the measured runs.
- **Conclusion:** 0 B per query on both providers. On the way, about one run in five measured 21.5 KB too much: a
  reader connection the warm-up never used prepared its first statement during a measured run, which grew its
  statement table. The warm-up now runs 16 callers at once, more than any provider's connections. Two fixes in the
  shared test: writes before reads (two writers toggled one favorite that reads counted), and three measurements per
  size in turn.
