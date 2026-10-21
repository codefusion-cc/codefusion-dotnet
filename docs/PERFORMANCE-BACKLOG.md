# Performance work set aside

On 2026-10-21 the performance push paused so that the open branches could be finished and fixed first. This file is
where to start when it resumes. Each item says what is known, where the numbers are, and what to try. The
TechEmpower-specific ranking is in `benchmarks/CodeFusion.TechEmpower/BACKLOG.md` (it lands with perf/techempower);
this file holds everything else, and the TechEmpower items that cut across packages.

Every run, before and after, goes in [docs/benchmarks](benchmarks/README.md), in alternating order.

## Where we stood

On the reduced TechEmpower rig (app c4-highcpu-8, Postgres c4-highcpu-8, wrk n2-highcpu-16), with every performance
branch merged:
- **Ahead of the best Rust entry:** single query (+12%), multiple queries (+92%) and updates (+2.5%).
- **Behind it:** plaintext (-16%), JSON (-5%) and fortunes (-15%).
- **Ahead of ASP.NET Core** on all six.
- **No garbage collection** in any test.

The contests were still being scored. The $12 entry held every probe up to 40,000 users at p99 under 1 ms, and ran
out of file descriptors past 65,535.

## The rig and the comparison

- **One alternated TechEmpower run.** Every entry (CodeFusion, xitca-web, may-minihttp, libreactor, aspnetcore
  Platform) in the same session, the order rotated every round, three rounds or more. Record per entry and test:
  peak and steady RSS, the app VM's CPU, and for .NET entries bytes allocated per request and gen0/1/2 counts. That
  table replaces the README's, which comes from single runs taken one after another.
- **A full-size rig.** App 16, Postgres 32 and wrk 32 vCPUs need the GCP quota raised from 32 to 128 vCPUs
  ("CPUs (all regions)"). On the reduced rig Postgres is the limit on the database tests (93-100% busy for the
  leaders), and wrk on plaintext and JSON for the fastest servers (92-100%).
- **Contest scoring.** Score every entry with the contest's own procedure (users at the 5-minute hold) on the same
  box: the $12 entry against the best Rust (#82, #19, #90) and C# (#74) entries, and the multicore entry against
  #8, #20, #22 and Rust #11. The rig is benchmarks/CodeFusion.ContestBench on perf/contest-tuning.

## HTTP (CodeFusion.Http)

- **Plaintext, 16% behind xitca-web:** at 0.13 system calls per request the time is our own code. Profile the
  pipelined path (the request parser, writing the response head, the date header) and remove what shows.
- **The once-a-second heartbeat sweep** costs about 0.36 µs per connection: 18 ms per beat at 50,000 connections,
  36 ms at 100,000, which holds a one-CPU box's requests back that long. A timer wheel, or sweeping a slice per beat,
  would make it proportional to the connections that are due
  ([twelve-entry-connection-memory](benchmarks/2026-10-21-twelve-entry-connection-memory.md)).
- **Memory per idle connection** is about 5.5 KB (3.0-3.4 KB in the process, 1.8-2.3 KB in the kernel); an idle
  connection holds no buffer already. Only worth more work if a contest is ever memory-bound, which 2 GB is not.
- **The event loop's p99 on a mixed load** was about 7 ms above Sockets on two CPUs (2026-10-21-review-fixes-and-batch-size.md)
  before the fairness fixes; measure it again on C4.

## Data and Postgres

- **Fortunes, 15% behind xitca-web:** the page writer, the list reader copying every row, the sort, `DataContext.Bind`.
- **The read queue's lock per command** (`PgQueue.TryAdd`): 1-2% on the single query.
- **Context switches on db and fortunes** (0.18-0.24 per request against may-minihttp's 0.10): the read loops sleep in
  recv. Try a short spin first, and measure CPU against latency.
- **The database tests' last allocations,** 16-67 KB a minute (at most 0.013 B per request): partly the job pool
  growing at a peak. Pre-sizing per loop went into perf/pg-pipelining (8e5d421); trace what is left
  (dotnet-trace gc-verbose).
- **More writers on updates.** The TechEmpower app runs one writer loop by default. With a group's updates now one
  statement (#22), measure 2 and 4 writers (`DB_WRITERS`) on the updates test: before #22, concurrent groups over
  the same rows deadlocked (docs/benchmarks/2026-10-21-tfb-pg-loops.md).
- **The catalog checks** feat/data-linq-shapes adds to Follow, Favorite and the other write-then-read statements cost
  a few catalog index lookups per call. Measure them on the contest's multicore workload, and cache per connection
  if they show.

## SQLite

- **Reads joining a batch:** most of the $12 entry's reads touch tables the same wake-up writes (posts, likes), so
  they wait for its commit. The cost was about 5% before table-aware reads, and level after
  (2026-10-21-twelve-entry-reads-by-table.md, which lands with feat/sqlite-provider). Measure it again on a quiet
  machine.
- **The load generator** can't drive more than about 60,000 load.js users on 3 vCPUs: one `Task.Delay` per user's
  think time saturates .NET's timer queue. A timer wheel in tools/loadgen would fix it.

## The contests

- **The file-descriptor limit decides the top of the $12 contest.** The scoring box's hard limit was 65,535, and
  the best published result (64,000 users) sits right at it. Past it, the server must refuse connections cleanly
  (fix/accept-past-resource-limits), and the entry caps its connections at the limit minus 1,024 and raises its soft
  limit itself.
- **Packaging.** Both contests forbid prebuilt binaries. A real submission needs the libraries from NuGet (pinned) or
  as source, not the DLLs the test entries use.

## Production

The gaps for general production use (TLS to Postgres, observability, proxy headers, more auth, more Postgres types,
HTTP/2, Microsoft.Extensions integration, a Kestrel host for the generated endpoints) are in the owner's notes
outside the repository, `codefusion-dotnet-production-gaps.md`.
