# Exclusive mode: reads and the writer take turns

- **Question:** one Exclusive test took 16.5 s, against 0.3 s with its counting reader off. Do reads that never stop
  hold the writer back, and does turn-taking fix it without starving the reads?
- **Code:** codefusion-dotnet feat/sqlite-provider 9b2fcd4 (before) and bbbd73f (writer first in line, then a waiting
  read before the next group, no 1 ms sleeps on either side).
- **Machine:** Apple M1 Pro, 8 cores, shared with other sessions; Release.
- **Method:** `ExclusiveTests.ReadsAndWritesThatNeverStopTakeTurns`: 20,000 rows, 4 threads scanning them back to back
  (`LIKE 'seed%'`), 200 awaited writes timed, and the reads finished during the writes counted. Five runs after the
  fix; the timings were read from the test's failure message with its bounds set out of reach.

| Case | 200 writes took | Reads beside them |
|---|---|---|
| before: readers retake the connection, the writer sleeps 1 ms at a time | 21,863 ms (one run) | not counted |
| writer first in line only | 13 to 15 ms | 7 to 19 |
| turns: writer first in line, a waiting read before the next group | 289 to 401 ms | 319 to 380 |

- **Memory and CPU:** no change to allocation (0 B per query).
- **Conclusion:** kept. A write waits for the read running, a read for the group running, about one of the other's
  each. The test asserts both: 200 writes under 1.5 s and at least 100 reads beside them. Throughput was measured again
  after it ([the run](2026-10-21-sqlite-exclusive-after-turns.md)).
