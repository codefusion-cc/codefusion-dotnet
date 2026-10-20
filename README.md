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
