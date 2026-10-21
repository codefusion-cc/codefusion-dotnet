# CodeFusion.TechEmpower

The six [TechEmpower Framework Benchmarks](https://github.com/TechEmpower/FrameworkBenchmarks) tests on CodeFusion,
written the way an app is: generated endpoints, LINQ queries compiled to SQL, nothing hand-tuned around them.

| Test | Route | What it does |
|---|---|---|
| plaintext | `/plaintext` | `Hello, World!` as `text/plain`, a `ContentReply` made once |
| json | `/json` | `{"message":"Hello, World!"}`, a new struct per request through the generated writer |
| db | `/db` | one random World row, the query's `One<WorldRow>` written as it arrives |
| queries | `/queries?queries=N` | N rows, each its own query, all started together (`Each`) and gathered in order |
| fortunes | `/fortunes` | the Fortune table awaited as a list, one row added, sorted by message, HTML-escaped |
| updates | `/updates?queries=N` | N rows read together (`Each`), each given a new number, the writes started together; they commit in group commits |

`queries` is read as TFB says: missing, empty or not a number is 1, below 1 is 1, above 500 is 500. Every response
has `Server: CodeFusion` and a `Date`. Nothing is cached.

## Running it

```bash
DATABASE_URL=postgres://benchmarkdbuser:benchmarkdbpass@127.0.0.1:5432/hello_world PORT=8080 dotnet run -c Release --project benchmarks/CodeFusion.TechEmpower
```

`DB_READERS` (default: two per core) and `DB_WRITERS` (default 1) set the Postgres loops
([the rig's sweep](../../docs/benchmarks/2026-10-21-tfb-pg-loops.md); four writers once updates are batched). `codefusion.dockerfile` builds
it as TFB would, from the repository's root, with aspnetcore's runtime settings from TFB.

`tests/CodeFusion.TechEmpower.Tests` starts the app against TFB's schema in `CODEFUSION_TEST_DB`'s server and checks each
test the way TFB's verifier does: headers, bodies, the clamping cases, the fortunes page byte for byte, updates that
persist, nothing cached, a database that is down.

## The rig

`scripts/TechEmpowerRig.cs` runs TFB's sequence on Google Cloud: an app VM, a Postgres VM with TFB's own Postgres
image and settings, and a load VM with wrk. Each reference entry is built from TFB's repository at a pinned commit
with its own dockerfile and settings, and run the same way as CodeFusion: on the host's network, `tfb-database`
naming the Postgres VM.

| VM | Shape | Why |
|---|---|---|
| app | c4-highcpu-8 | what is measured: 4 Intel Emerald Rapids cores with their hyperthreads |
| Postgres | c4-highcpu-8 | TFB's Postgres 18 image and config (synchronous_commit off, 2000 connections) |
| wrk | n2-highcpu-16 | twice the app's vCPUs, so the load generator is never what limits a run |

The project's quota allows 32 vCPUs and 24 of C4 per region, which is where these sizes come from. With more,
`TFB_RIG=full` makes it app c4-highcpu-16, Postgres c4-highcpu-32 and wrk c4-highcpu-32. Citrine, TFB's own rig, is
three equal Xeon Gold 6330 machines (28 cores, 56 threads) on 40 GbE, so absolute numbers here are smaller; the
comparison is between entries run on the same VMs.

```bash
dotnet run scripts/TechEmpowerRig.cs -- create
dotnet run scripts/TechEmpowerRig.cs -- setup
dotnet run scripts/TechEmpowerRig.cs -- build aspnetcore may-minihttp h2o
dotnet run scripts/TechEmpowerRig.cs -- push
dotnet run scripts/TechEmpowerRig.cs -- bench codefusion plaintext json db queries fortunes updates
dotnet run scripts/TechEmpowerRig.cs -- gate codefusion 60 plaintext json db queries fortunes updates
dotnet run scripts/TechEmpowerRig.cs -- delete
```

Each test runs as TFB's toolset runs it: a 5 s primer at 8 connections, a 15 s warmup at 512, then 15 s per level
(plaintext pipelined 16 deep at 256, 1,024, 4,096 and 16,384 connections; json, db and fortunes at 16 to 512;
queries and updates at 1, 5, 10, 15 and 20 queries per request on 512 connections). The score is TFB's: answered
requests without errors over 15 s, the best level for plaintext, json, db and fortunes, the 20-query level for queries
and updates. Alongside it the rig records latency, the server's CPU and RSS, each VM's busiest 15 s (to see which
machine was the limit) and, for .NET entries, the GC's collections and allocation rate from dotnet-counters.

## The allocation gate

`gate` runs each test under load for a 30 s warmup and then the given seconds, with dotnet-counters reading the GC
every second. A test passes with no gen0, gen1 or gen2 collection and no byte allocated. The results go to
`artifacts/techempower/codefusion/gate.txt`; every run is written down in `docs/benchmarks/`.
