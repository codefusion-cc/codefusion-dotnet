# $12 contest: three entries scored on GCP

- **Question:** how many users does each entry hold for the contest's 5-minute hold: CodeFusion's own (cf-tables),
  the fastest C# entry (PR #74) and the fastest Rust entry (PR #82)? What stops each one?
- **Code:**
  - cf-tables: CodeFusion with the EventLoop transport, from /Volumes/Dev/cf-bench/stage/cf-tables.
  - pr74 (csharp-epoll-gichamba): a hand-rolled epoll server, Native AOT.
  - pr82 (rust-epoll-scooter1337): Rust on raw io_uring with fixed socket descriptors.
  - pr74 and pr82 come from twelve-dollar-codefusion 95aa405. All three served HTTP directly, with no nginx.
- **Machine:**
  - Target: GCP c4-highcpu-2 held to the contest box with `nr_cpus=1 mem=2G`, systemd `LimitNOFILE=65535`.
  - Load: 5 spot n2-highmem-4 boxes running k6, all in europe-west1-b.
- **Method:** `cbench search twelve <entry> --label <label>`, the contest's own procedure.
  - A warm-up, then probes that double and bisect to a step of 250 users.
  - Then the 5-minute hold, stepping down until a hold passes.
  - A probe fails on p95 ≥ 500 ms, p99 ≥ 1 s, errors ≥ 1% or a crash.
  - A probe is load-limited when a k6 box averages over 75% CPU.

## Scores

| Entry | Users at hold | req/s at hold | p95 / p99 ms | Server CPU | Server RSS avg / max | First failure above | Label |
|---|---|---|---|---|---|---|---|
| pr82 (Rust, io_uring) | **188,250** | 17,507 | 147 / 235 | 26% | 91 / 109 MB | 188,750: killed for memory | full1-pr82 |
| pr74 (C#, epoll, AOT) | 65,500 | 6,119 | 10.1 / 34.0 | 10% | 52 / 59 MB | 65,750: out of descriptors, abort | full1-pr74 |
| cf-tables (CodeFusion) | 65,250 | 6,095 | 14.7 / 38.5 | 12% | 470 / 478 MB | out of descriptors, abort | full1 |

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
  - Above that the kernel killed it for memory (200,000, 240,000 and 320,000 were all killed). Socket memory, not
    CPU, is its ceiling.
- **Why each stops:**
  - pr82 isn't bound by the 65,535 limit, because its sockets live in io_uring's fixed tables. It stops on memory,
    at about 0.6 KB of server RSS per connection.
  - pr74 and cf-tables stop on descriptors. At the hold pr74 keeps about 0.9 KB per connection, and cf-tables about
    7.3 KB (478 against 59 MB at the same count). That per-connection cost is in codefusion-dotnet's
    docs/PERFORMANCE-BACKLOG.md.

## What went wrong in the runs

- **A shared cache label:** the first pr74 and pr82 searches reused cf-tables' phases. They were rerun with labels of
  their own, and those rows are not in the table.
- **A resumed search never switched back:** the second pr82 search skipped its switch because `switch.ok` was cached
  from its first run. The box still ran pr74, so its "65,500" was pr74's.
  - That run's phases are kept in `full1-pr82-was-pr74`, and the probes to 20,000 that ran on pr82 were kept.
  - Search.cs now switches whenever the live entry isn't the search's.
- **A summary line for a phase with no samples threw:** a crashed entry's phase had no target figures, and
  `(double)(node ?? 0)` can't convert an int node. Fixed.
- **Load at 320,000:** the k6 boxes ran at 87-89% CPU. A score past about 250,000 users needs more load boxes; pr82's
  is below that.

## Cost

$2.79 over 38 VM-hours for the $12 rig, from cbench's ledger. The three custom images (95 GB) are kept for the next run.

## Conclusion

- **The descriptor limit decides the top for every entry that uses ordinary sockets**, at about 65,000 users.
- **To score past it, CodeFusion would need:**
  - a connection cap below the limit, to stay up;
  - accepted sockets that don't take process descriptors, to go higher (io_uring fixed files, as pr82 does);
  - far less memory per connection, since pr82 runs out of memory at 188,000 users with about 0.5 KB of user memory
    per connection.
- **This is backlog, not work for now.**
