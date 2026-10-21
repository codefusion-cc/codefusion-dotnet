# SQLite: the turn and timing tests on a busy machine

- **Question:** the check of #9 found two tests that failed now and then (`AWriteOffTheLoopGoesToTheWriterAndWaitsForTheBatch`,
  `ABatchOpenedAfterItsEndRanStillEndsInItsWakeUp`), and a real bug: two takers waiting for the one connection at once
  (the writer's group and a loop's batch) overwrote each other's marks, so reads overtook the one left unmarked. Do the
  fixed tests and the fixed turns hold on a machine with more runnable threads than CPUs, and did the old ones fail
  there?
- **Code:** "before" is the Debug test build of feat/sqlite-provider 23122a3 with `TwoTakersWaitingAtOnceEachKeepTheirTurn`
  added (no other fix); "after" is the same branch with the ticket turnstile, the marks raised at handover, and the
  test fixes. The second 1-CPU set's "after" also has the rollback test's fix.
- **Machine:** cf-bench, the Colima VM (vz, aarch64, 4 vCPU, 8 GiB) on the M1 Pro; each container held to the CPUs
  named, with busy loops beside the tests.
- **Method:** the test assembly in `mcr.microsoft.com/dotnet/runtime:10.0`, `-maxThreads 4`. At 4 CPUs with 8 busy
  loops: the full suite 3 times per side, then `EventLoopBatchTests` and `ServerLoopTests` 10 times per side,
  alternating. At 1 CPU with 16 busy loops: those two classes and `GroupCommitTests`, 6 times per side.

## 4 CPUs, 8 busy loops

| | before | after |
|---|---|---|
| full suite | 3 of 3 failed: `TwoTakersWaitingAtOnceEachKeepTheirTurn` every time (37, 9, 27 reads where 4 may), `ATransactionSqliteRollsBackRunsAgainWithoutTheWriteThatFailed` once | 3 of 3 passed (232 tests, 7 skipped), 40 to 46 s |
| the two loop classes | 10 of 10 passed | 10 of 10 passed |

## 1 CPU, 16 busy loops

| | before | after |
|---|---|---|
| the two loop classes and `GroupCommitTests` | 6 of 6 failed: the rollback test every time, `AWriteOffTheLoop…` once (its write complete before it was made), `ABatchOpenedAfterItsEndRan…` once (request 2 took 2,645 ms, bound 2 s) | 6 of 6 passed, 20 to 32 s |

## Off the VM

On the Mac, with the delay a busy machine causes put in on purpose (300 ms before a loop makes its writes, 3 s in the
server's first request), the old `AWriteOffTheLoop…`, `ABatchsCompletionsStayItsOwnWhileAnotherLoopBatches` and
`ABatchOpenedAfterItsEndRan…` failed and the new ones passed. The new `ABatchOpenedAfterItsEndRan…` still fails within
its 60 s read timeout with the end-again request ignored (`EventLoop.EndBatch`), and the new rollback test with the
re-run turned off. The two turn tests, 5 to 10 runs each on the Mac: red every time without the turnstile (5 to 33
reads, and 3 to 40 started while a taker's marks were up, where 2 may), green every time with it, Debug and Release.

## Conclusion

The turn bug showed under load every time and is gone with the fix. The loop tests' flakes need more than 4 busy CPUs
to show; at 1 they did, and the rollback test's long first write failed every time. All four tests pass under both
loads now. Kept.
