# CodeFusion for .NET

Server and data packages for .NET 10 that allocate nothing per request, and a bcrypt for .NET 8 and up. They come
from the hand-tuned C# server of the
[multicore server challenge](https://github.com/arjaythedev/multicore-server-challenge/pull/8), taken apart so an
application can use the same code without writing it by hand.

| Package | What it is |
|---|---|
| [CodeFusion.Http](src/CodeFusion.Http/README.md) | An HTTP/1.1 server, and minimal-API endpoints a source generator compiles into code that writes straight into the connection. 0 B per request. |
| [CodeFusion.Data](src/CodeFusion.Data/README.md) | A DbContext-shaped data layer whose LINQ queries a source generator compiles to SQL at build time. 0 B per query. |
| [CodeFusion.Data.Postgres](src/CodeFusion.Data.Postgres/README.md) | A Postgres wire-protocol client and CodeFusion.Data's Postgres provider: prepared statements, read groups, group commit, rows read in place. 0 B per query. |
| [CodeFusion.Data.Sqlite](src/CodeFusion.Data.Sqlite/README.md) | CodeFusion.Data's SQLite provider, in process: reads on the caller's thread, group commit on one writer thread, the same LINQ as on Postgres. 0 B per query. |
| [CodeFusion.Security.Bcrypt](src/CodeFusion.Security.Bcrypt/README.md) | bcrypt for .NET 8 and up, BCrypt.Net-Next compatible: allocation-free, up to five hashes interleaved per thread, a batching queue for servers. No dependencies, and a version of its own. |

Handlers that read like minimal APIs are in CodeFusion.Http, and LINQ queries compiled to SQL at build time are in
CodeFusion.Data. Each package's README explains it.

## Performance

The six TechEmpower tests, run with TechEmpower's own toolset (pinned at 57d92fbec6f8) on Google Cloud Intel C4:
the app on c4-highcpu-8, Postgres on c4-highcpu-8, and wrk on n2-highcpu-16. The leaders were built and run on the
same machines, in requests per second:

| Test | CodeFusion | Best Rust | Best ASP.NET Core | Against Rust | Against ASP.NET Core |
|---|---:|---:|---:|---:|---:|
| Plaintext (pipelined) | 7,506,549 | 8,908,020 (xitca-web) | 7,297,751 (Platform) | -16% | +3% |
| JSON | 1,201,705 | 1,263,920 (xitca-web) | 703,920 (Platform) | -5% | +71% |
| Single query | 706,377 | 628,393 (may-minihttp) | 381,963 (Platform) | +12% | +85% |
| Multiple queries (20) | 64,920 | 33,892 (may-minihttp) | 32,210 (Platform) | +92% | +102% |
| Fortunes | 491,027 | 576,688 (xitca-web) | 307,474 (Platform) | -15% | +60% |
| Updates (20) | 23,240 | 22,669 (may-minihttp) | 18,834 (minimal APIs) | +2.5% | +23% |

During those runs CodeFusion ran no garbage collection at all. Plaintext and JSON allocated 0 B in a measured minute;
the database tests allocated at most 0.013 B per request.

What the numbers rest on:
- **Nothing allocated per request:** pooled buffers and value-task sources, and rows read in place from the wire.
- **An event loop per core on Linux:** one receive per request, direct `recv` and `send`, the answers of one wake-up
  sent together, and a buffer stack per loop with no lock.
- **Code generated at build time:** a source generator replaces each `Map` call and each LINQ query with specialised
  code, so nothing is reflected over at run time.
- **The server and the data layer working together:** Postgres group commit, pipelined reads, and the updates of one
  group merged into one statement.

Read these with care:
- **This is a reduced rig.** TechEmpower's own rounds run on larger hardware.
- **How the other entries were run:** each was built from TechEmpower's repository (57d92fbec6f8) with its own
  Dockerfile and settings, and scored with TechEmpower's sequence. Each was run once, one after another. CodeFusion's
  numbers come from a later run, not in alternating order with them, so differences of a few percent are noise.
- **wrk was the limit for the fastest servers:** it ran 92 to 100% busy for xitca-web and libreactor on plaintext and
  JSON. Their numbers are lower bounds, so the real gaps there are probably larger than the table shows.
- **Postgres limits the database tests:** its 8 vCPUs ran 93 to 100% busy for the leaders, and 32% for CodeFusion on
  the single query. CodeFusion's lead there comes from asking Postgres for less work per query, and the 8-vCPU
  database caps the others.
- **The overall leader on plaintext and JSON is libreactor, written in C:** 9,461,172 and 1,483,605 on this rig.
- **Some of the CodeFusion build isn't on main yet:** these numbers come from every performance branch merged
  together, and Postgres pipelining and the merged updates are still in review.

[docs/benchmarks](docs/benchmarks/README.md) keeps every run, with its method and the commits measured.

### What ASP.NET Core has that CodeFusion doesn't

CodeFusion is not a drop-in replacement for Kestrel. It's a fast stack for HTTP/1.1 JSON and database APIs behind a
proxy. It doesn't have:
- HTTP/2, HTTP/3 or TLS: put a proxy in front;
- ASP.NET's middleware, its OIDC and RS256 authentication, WebSockets, or compression;
- request or response bodies streamed past 1 MB;
- dependency injection, configuration and logging through Microsoft.Extensions;
- an event loop on Windows, where it uses its Sockets transport;
- years of production traffic.

## Building and testing

The SDK is pinned in `global.json` (10.0.402).

```bash
dotnet build -c Release
dotnet test --project tests/CodeFusion.Core.Tests -c Release -- --max-threads 2
dotnet test --project tests/CodeFusion.Http.Tests -c Release -- --max-threads 2
dotnet test --project tests/CodeFusion.Http.Generators.Tests -c Release -- --max-threads 2
dotnet test --project tests/CodeFusion.Security.Bcrypt.Tests -c Release -f net10.0 -- --max-threads 2
```

bcrypt's tests run on each framework it supports (`-f net8.0`, `net9.0`, `net10.0`, and `net11.0` with an SDK of 11
or newer); CI runs all of them.

The generator's output is checked in under `tests/CodeFusion.Http.Generators.Tests/Snapshots`; after changing the
generator, run its tests with `UPDATE_SNAPSHOTS=1` and read the diff before keeping it.

The Postgres client's integration tests need a database; without `CODEFUSION_TEST_DB` they skip. With
`CODEFUSION_TEST_PG_TRUST`, `_CLEAR`, `_MD5` and `_SCRAM` each naming a user that `pg_hba.conf` logs in that way, the
login tests run against the real server too. The SASLprep logins create roles of their own, so they need a
`CODEFUSION_TEST_DB` user with CREATEROLE (CI's is the superuser); without it they skip.

```bash
CODEFUSION_TEST_DB="Host=127.0.0.1;Port=5432;Username=codefusion;Password=codefusion;Database=codefusion_test" dotnet test --project tests/CodeFusion.Data.Postgres.Tests -c Release -- --max-threads 2
```

The data layer's tests (the generator's SQL and errors, execution, plans against the contest's statements,
allocations) use the same database:

```bash
CODEFUSION_TEST_DB="Host=127.0.0.1;Port=5432;Username=codefusion;Password=codefusion;Database=codefusion_test" dotnet test --project tests/CodeFusion.Data.Tests -c Release -- --max-threads 2
```

The SQLite provider's tests need nothing set up: the same execution and operator tests as on Postgres, against files
in a temp folder, and its own (group commit, crash recovery with a child process killed mid-write, races):

```bash
dotnet test --project tests/CodeFusion.Data.Sqlite.Tests -c Release -- --max-threads 2
```

The allocation tests run in Release only: a Debug build makes every async state machine an object. They count the
whole process's allocations, so turn off the test platform's telemetry, which allocates in the test process:
`export TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.

## Benchmarks

`benchmarks/CodeFusion.Benchmarks` compares the server with Kestrel and the client with Npgsql, each set up as the
contest server used them before the rewrite (commit 21abce6, frozen in `Reference/`). `PgBenchmarks` seeds a small
schema in the database `BENCH_DB` names.

```bash
dotnet run -c Release --project benchmarks/CodeFusion.Benchmarks -- --job short --filter '*HttpServerBenchmarks*'
```

`MinimalApiBenchmarks` puts the generated endpoints against the same endpoints written by hand on the raw server.

```bash
dotnet run -c Release --project benchmarks/CodeFusion.Benchmarks -- --job short --filter '*MinimalApiBenchmarks*'
```

```bash
BENCH_DB="Host=127.0.0.1;Port=5432;Username=codefusion;Password=codefusion;Database=codefusion_bench" dotnet run -c Release --project benchmarks/CodeFusion.Benchmarks -- --job short --filter '*PgBenchmarks*'
```

`DataBenchmarks` runs the compiled LINQ queries against the contest rewrite's hand-built commands for the same
queries, on the same seed (`--filter '*DataBenchmarks*'`).

`--job full` (the default) for numbers to compare; `--alloc-trace` writes an allocation trace per benchmark.

## License

MIT. See [LICENSE](LICENSE) and [SECURITY.md](SECURITY.md).
