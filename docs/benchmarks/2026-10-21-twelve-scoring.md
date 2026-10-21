# $12 contest: three entries scored on GCP

- **Question:** how many users does each entry hold for the contest's 5-minute hold: CodeFusion's own (cf-tables),
  the fastest C# entry (PR #74) and the fastest Rust entry (PR #82)? What stops each one?
- **Code:**
  - cf-tables: CodeFusion with the EventLoop transport. Its CodeFusion libraries were built from codefusion-dotnet
    150f0e6 (build/twelve-entry). The app itself was deployed from a folder that isn't a git checkout
    (cf-bench/stage/cf-tables), so this row can't be rebuilt from a commit. The archive that ran is kept as
    artifacts/contest/entries/cf-tables.tgz (sha256 ba6c00d21a09dcd2). The rig now refuses folder deploys like it.
  - pr74 (csharp-epoll-gichamba): a hand-rolled epoll server, Native AOT, from twelve-dollar-codefusion 2d589c4.
  - pr82 (rust-epoll-scooter1337): Rust on raw io_uring with fixed socket descriptors, from twelve-dollar-codefusion
    95aa405.
  - All three served HTTP directly, with no nginx.
  - The rig: feat/contest-rig 29d05c1. Its search ran uncommitted on perf/contest-tuning during these runs; the
    switch-back fix was in from the pr82 run at 09:58.
- **Machine:**
  - Target: GCP c4-highcpu-2 held to the contest box with `nr_cpus=1 mem=2G`, systemd `LimitNOFILE=65535`.
  - Load: 5 spot n2-highmem-4 boxes running k6, all in europe-west1-b.
- **Method:** `cbench search twelve <entry> --label <label>`, the contest's own procedure.
  - A warm-up, then probes that double and bisect to a step of 250 users.
  - Then the 5-minute hold, stepping down until a hold passes.
  - A probe fails on p95 ≥ 500 ms, p99 ≥ 1 s, errors ≥ 1% or a crash.
  - A probe is load-limited when a k6 box averages over 75% CPU.
- **How pr82's score was reached:** with a flag, on a second search.
  - The first search (`--label full1-pr82`, no flags) reached 320,000 users. pr82 was OOM-killed there, k6 spun on
    refused connections at 87-89% CPU, and the rig of that day checked load before crashes, so it stopped at 10:17:05:
    "LG-limited at 320000 users".
  - The second search (10:17:33) was `cbench search twelve pr82 --label full1-pr82 --max 240000`. It reused the cached
    phases up to 160,000, probed 240,000, and bisected and held from there. Everything after 160,000 in the table
    comes from that run.
  - The flag was a workaround, and the log didn't record it. The rig now treats a crash as a fail whatever the load
    boxes did, and logs the options on the search's first line. With those rules a search with no flags takes the
    same path: 320,000 fails, and the bisect probes 240,000 next. A unit test replays this run
    (TwelveSearchTests.AnEntryThatCrashesWhileTheLoadBoxesAreBusyIsBisectedNotStopped).
  - What the code of that day didn't do: warm up again after a crash restart. Every probe after a crash ran on an entry
    restarted on a fresh database seconds before. The rig now warms up again first, so a rerun may land a step or two
    higher.

## Scores

| Entry | Users at hold | req/s at hold | p95 / p99 ms | Server CPU | Server RSS avg / max | RSS per connection over the warm-up | What failed above it | Label |
|---|---|---|---|---|---|---|---|---|
| pr82 (Rust, io_uring) | **188,250** | 17,507 | 147 / 235 | 26% | 91 / 109 MB | 0.5 KB | the 188,500 hold and the 188,750 probe: SIGKILL | full1-pr82 |
| pr74 (C#, epoll, AOT) | 65,500 | 6,119 | 10.1 / 34.0 | 10% | 52 / 59 MB | 0.7 KB | the 65,750 probe: out of descriptors, abort | full1-pr74 |
| cf-tables (CodeFusion) | 65,250 | 6,095 | 14.7 / 38.5 | 12% | 470 / 478 MB | 6.7 KB | the 65,500 probe: out of descriptors, abort | full1 |

- **RSS per connection:** the server's maximum RSS in the hold, less its maximum in the 1,000-user warm-up, divided by
  the users added. The warm-up maxima were 18.6, 11.7 and 58.6 MB. The rig records no idle baseline with zero
  connections, so this also counts what grows with load besides connections (buffers, caches, the JIT for
  cf-tables). It is RSS, not GC allocation.

- **pr74 and cf-tables stop at the descriptor limit.**
  - Neither raises its limit, and the box's hard limit is 65,535 anyway.
  - pr74 aborts in `Console.Error` (OpenStandardError) inside `Feed.Server.Accept`, with no descriptor left to open
    stderr.
  - cf-tables aborts the same way (PAL_SEHException, see codefusion-dotnet's
    2026-10-21-twelve-entry-fd-limit-crash.md).
  - Both were at 10-12% CPU at the hold.
- **pr82 gets past the limit.**
  - Accepted sockets go into io_uring's fixed-descriptor tables (eight rings of 65,535 slots), not the process's
    descriptor table.
  - At 188,250 users it held 188,455 TCP sockets at 31% box CPU, with 1.26 GB of the 2 GB used.
  - At 320,000 the OOM killer killed it (systemd: "Failed with result 'oom-kill'").
  - From 188,750 to 240,000, and in the 188,500 hold, it ended with SIGKILL and no OOM record in the unit's journal,
    with at most 1.34 GB of the box's 1.9 GB in use. The rig saved neither the kernel log nor dmesg, so who sent the
    kill isn't known. Memory is the likely ceiling, but these runs don't prove it. The rig now saves the kernel's
    OOM lines from the phase's window.
- **Why each stops:**
  - pr82 isn't bound by the 65,535 limit, because its sockets live in io_uring's fixed tables. It stops at about
    188,500 users, at 0.5 KB of server RSS per connection.
  - pr74 and cf-tables stop on descriptors. pr74 adds about 0.7 KB of RSS per connection, and cf-tables about 6.7 KB
    (478 against 59 MB at about the same count). That per-connection cost is in codefusion-dotnet's
    docs/PERFORMANCE-BACKLOG.md.

## What went wrong in the runs

- **A shared cache label:** the first pr74 and pr82 searches reused cf-tables' phases. They were rerun with labels of
  their own, and those rows are not in the table.
- **A resumed search never switched back:** the second pr82 search skipped its switch because `switch.ok` was cached
  from its first run. The box still ran pr74, so its "65,500" was pr74's.
  - That run's phases are kept in `full1-pr82-was-pr74`, and the probes to 20,000 that ran on pr82 were kept.
  - Search.cs now switches whenever the live entry isn't the search's.
- **Crash lines named the wrong entry:** each crash.log tails the unit's journal, which holds every entry that ran on
  the box, so pr82's crash lines quote pr74's C# stack. The rig now reads only the phase's window.
- **A summary line for a phase with no samples threw:** a crashed entry's phase had no target figures, and
  `(double)(node ?? 0)` can't convert an int node. Fixed.
- **Load at 320,000:** the k6 boxes ran at 87-89% CPU. A score past about 250,000 users needs more load boxes; pr82's
  is below that.

## Cost

$2.56 over 34.5 VM-hours, image builders included ($0.06 of it).
- cbench's ledger said $2.79 over 38 VM-hours. It counted the spot box cbench-tw-load-0 as running until the rig went
  down at 11:13. Google preempted it at 07:45:32 (from the zone's operations), so it ran 1.9 hours, not 5.4.
- The rig now writes a "gone" row for such a VM at the time Google gives.
- The three custom images are kept for the next run: 95 GB of disks, 8.7 GB stored, $0.45 to $4.75 a month.

## Conclusion

- **The descriptor limit decides the top for every entry that uses ordinary sockets**, at about 65,000 users.
- **To score past it, CodeFusion would need:**
  - a connection cap below the limit, to stay up;
  - accepted sockets that don't take process descriptors, to go higher (io_uring fixed files, as pr82 does);
  - far less memory per connection: pr82 stops at about 188,500 users at 0.5 KB of RSS per connection, against
    cf-tables' 6.7 KB.
- **This is backlog, not work for now.**
