# SQLite: every waiter parks after a 50 µs spin, and reads stop for a writer in line

- **Question:** a thread waiting for the connection spun until it got it, unless a loop's batch held it. On Linux
  runners with fewer cores than waiting threads that starved the holder: CI's `ReadsAndWritesThatNeverStopTakeTurns`
  (4 readers, 4 vCPUs, arm64 only) took 12 to 16 s for 200 writes, about 15 reads a write where the turns allow two. Now
  every waiter, in every mode, spins 50 µs and then parks; a give-back wakes the parked writer, or else one parked read,
  and a group's or batch's end wakes them all. Parking alone didn't hold on Linux (below), so reads also stop for a
  writer in line once one read has gone after it came, without the writer having to run to say so. What does it cost,
  and does the turn test hold?
- **Code:** "before" is feat/sqlite-provider 6384f10; "parking" is it with the parking change; "final" adds the turn
  rule; on the Mac also "200 µs", parking with a 200 µs spin.
- **Machine:** the Apple M1 Pro (8 cores, load 6.5 to 11.8 from other sessions); cf-bench, the Colima VM (vz, aarch64,
  4 vCPU, 8 GiB), each container held to the CPUs named.
- **Method:** `CodeFusion.Data.Sqlite.Throughput <mode> normal 2`, the sides alternated; the turn tests in Linux
  containers; the $12 entry's mix (tools/bench/bench.sh `saturate 256 30`) on one CPU with the entry built on each side.

## Mac: parking

Three rounds of before and after, then two of before, after and 200 µs. Medians over the rounds (all five where a side
ran in both sets):

| Exclusive, NORMAL | before | parking (50 µs) | 200 µs |
|---|---|---|---|
| point reads, 1 thread | 1.54 M/s | 1.77 M/s | 1.90 to 1.95 M/s |
| point reads, 4 threads | 1.01 M/s | 0.93 M/s | 0.92 to 0.96 M/s |
| updates, 64 callers | 100.6 k/s | 110.7 k/s | 102 to 110 k/s |
| updates, 1 caller | 48.2 k/s | 46.4 k/s | 39.5 to 46.9 k/s |
| 9 reads : 1 update, 16 callers | 363.7 k ops/s | 270.9 k ops/s | 226 to 342 k ops/s |

| Batches, NORMAL | before | parking (50 µs) |
|---|---|---|
| 1 loop | 325.9 k ops/s | 337.1 k ops/s |
| 2 loops | 248.1 k ops/s | 252.9 k ops/s |
| 1 loop + 4 readers off the loop (ops + reads) | 355.6 k/s | 321.5 k/s |

Reader-pool mode (`default`) was even: 4-thread reads 6.1 to 6.7 M/s on both sides, updates within noise.

- The same before side swung from 209 k to 397 k ops/s on the 16-caller mix: the Mac's load moves these numbers more
  than most differences here. The mix's loss, about a quarter, held across rounds: with 16 callers on 8 busy cores a
  holder is often preempted past 50 µs, its waiters park, and each handoff then pays a wake-up.
- 200 µs was no better than 50 µs, so the change keeps 50 µs, which parks sooner where it matters (one CPU).

## Linux (cf-bench): parking, one CPU

Three rounds, before and parking alternated, each container on CPU 0. Medians, with the three rounds:

| Exclusive, NORMAL | before | parking |
|---|---|---|
| point reads, 1 thread | 1.43 M/s (1.25, 1.43, 1.66) | 1.05 M/s (0.92, 1.05, 1.42) |
| point reads, 4 threads | 1.41 M/s (1.52, 1.41, 1.34) | 1.31 M/s (1.31, 1.08, 1.32) |
| updates, 64 callers | 52.1 k/s (38.6, 63.6, 52.1) | 61.7 k/s (61.7, 44.2, 67.3) |
| updates, 1 caller | 36.2 k/s (36.2, 38.5, 23.0) | 39.0 k/s (39.9, 17.6, 39.0) |
| 9 reads : 1 update, 16 callers | 327.7 k ops/s (327.7, 333.7, 255.8) | 390.6 k ops/s (447.4, 292.7, 390.6) |

| Batches, NORMAL | before | parking |
|---|---|---|
| 1 loop | 256.3 k ops/s (87.4, 256.3, 274.1) | 221.8 k ops/s (224.1, 169.3, 221.8) |
| 2 loops | 226.6 k ops/s (98.7, 226.6, 252.9) | 236.7 k ops/s (236.7, 250.3, 171.5) |
| 1 loop + 4 readers: loop ops / reads | 6.0 k/s / 1,061 k/s | 4.4 k/s / 1,216 k/s |

| The $12 entry, `saturate 256 30`, one CPU | before | parking |
|---|---|---|
| requests/s | 40,826 (45,832, 39,470, 40,826) | 45,829 (45,074, 45,829, 47,177) |
| p99 | 22.4 ms (20.5, 25.4, 22.4) | 19.7 ms (19.9, 19.7, 19.5) |

- Single-thread reads swung more between rounds than between the sides (before: 1.25 to 1.66 M/s). The one-thread
  read never waits, so its loss is the Mac's load on the VM, not parking.
- On one CPU every waiter that finds the connection taken parks once its 50 µs are up, so each turn costs a wake-up:
  a context switch where spinning would have yielded to the holder. The tool's single-caller update and the loop with
  4 readers lose by it; the mixes with many callers gain, the waiters no longer taking the holder's CPU. The entry, which
  is what runs on one CPU, gains about 12%, with p99 lower.

## Linux (cf-bench): the turn tests

Both turn tests alone (`-method "*NeverStopTakeTurns*"`) passed before and with parking, at 4 CPUs and at 1: the VM
didn't reproduce CI. Under the full Sqlite suite, as CI runs them, every build failed some runs:

| Build | 4 CPUs | 1 CPU |
|---|---|---|
| before | 2 of 4 runs failed (2.6 to 3.6 s for 200 writes) | 0 of 2 failed |
| parking | 3 of 9 failed (2.3 to 5.2 s) | 2 of 3 failed (1.6 to 3.8 s) |

- On one CPU parking failed where spinning passed: each turn paid a wake-up, and the bound was six reads' time a write.
- What still let reads overtake with parking: a writer in line waits for one read to go, and then had to run to tell
  the reads to stop. Woken on a busy CPU it waited milliseconds for one, and reads went on starting meanwhile.
- Two other timing tests failed now and then under the loaded suite, before as with parking:
  `AWriteOffTheLoopGoesToTheWriterAndWaitsForTheBatch` (an Assert.False: once before, once with parking, and in
  both 4-CPU runs of the spinning mutant below in an earlier round) and
  `ABatchOpenedAfterItsEndRanStillEndsInItsWakeUp` (4.3 s, once before).
  Neither failed in the final build's 18 runs (two rounds of 9); they are flaky under load, not fixed here.

**The fix and the test.** Reads now stop once one has gone after a writer came (`_writerQueued`, checked again after
the take), whether or not the writer runs. The test asserts the turns rather than a time: a Debug counter of the reads
that start while one write waits, at most 2 (one that took the connection as the writer came, one let through after
it). The time stays only as a guard against a hang, ten times the old bound. The full suite in Debug on cf-bench,
the last round (an earlier one, with the counter started a little sooner, came out the same way):

| Build | 4 CPUs, 4 test threads | 4 CPUs, 2 (as CI) | 1 CPU |
|---|---|---|---|
| final | 3 of 3 green | 3 of 3 green | 3 of 3 green |
| final without the turn rule | 2 of 2 red: 3 to 32 reads started while one write waited | | 2 of 2 red: 6 and 7 |
| without the rule, waiters spinning, never parked | 2 of 2 red: 3 to 7 | | 2 of 2 red: 4 and 5 |
| the writer waits while any read waits | both turn tests timed out (alone) | | |

The spinning build also failed `NothingSpinsWhileALoopsBatchHoldsTheConnection` (6384f10 parked for batches).

## Linux (cf-bench): the final build, one CPU

The entry again, before against final, alternated (final first in round 2), later and with the Mac busier:

| The $12 entry, `saturate 256 30`, one CPU | before | final |
|---|---|---|
| requests/s | 34,041 (31,468, 34,041, 38,810) | 37,667 (37,667, 29,240, 40,792) |
| p99 | 44.6 ms (44.6, 58.7, 30.8) | 29.0 ms (29.0, 74.5, 26.1) |

Final won rounds 1 and 3 and lost round 2, by about the spread within each side.

## Conclusion

- Parking keeps spinning waiters from taking the CPU a holder needs. On one CPU it costs a wake-up a turn: the tool's
  single-caller update and the loop with 4 readers lose. The entry, the load that runs there, gains or stays level:
  +12% with parking alone, +11% median with the final build in a noisier run, p99 lower in both.
- On the Mac the 16-caller mix lost about a quarter: holders preempted past 50 µs make waiters park, and each handoff
  then pays a wake-up. 200 µs was no better, so 50 µs stays.
- Parking didn't make the turn test hold on Linux. The time bound measured the scheduler as much as the turns. The
  turn rule makes the turns hold without the writer on a CPU, and the test now asserts them (at most 2 reads per
  write's wait, Debug) and keeps the time only as a hang guard. Green 9 of 9 under the loaded suite; the same test is
  red against no turn rule, against waiters that only spin, and against a writer that waits for every read.
- The Mac and one-CPU tool tables measured parking without the turn rule; the rule only changes what a read checks
  before it takes the connection while a writer is in line.
- Not fixed here: `AWriteOffTheLoopGoesToTheWriterAndWaitsForTheBatch` and `ABatchOpenedAfterItsEndRanStillEndsInItsWakeUp`
  fail now and then under a loaded suite on the VM.
