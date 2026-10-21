# The TechEmpower rig's first run: aspnetcore's json test, to check the tooling

- **Question:** does `scripts/TechEmpowerRig.cs` run TFB's sequence end to end and record what it should (levels,
  score, latency, server CPU and RSS, each VM's load, dotnet-counters)? Not a measurement: the h2o image was being
  compiled on the app VM at the same time.
- **Code:** perf/techempower at 7656835 (rig script uncommitted then, committed in ba052ee); TFB at 57d92fbec6f8.
- **Machine:** GCP europe-west1-b, compact placement. App c4-highcpu-8, Postgres c4-highcpu-8 (TFB's postgres:18
  image and config), wrk n2-highcpu-16 (Ubuntu 24.04, kernel 7.0.0-1014-gcp, wrk 4.1.0 from apt). The project's
  quotas (32 vCPUs in all regions, 24 of C4 per region) set these sizes.
- **Method:** TFB's json sequence: primer, warmup at 512, 15 s at 16 to 512 connections; aspnetcore's own dockerfile
  (Platform, .NET 10), host network.

| Connections | req/s | Mean latency | p99 |
|---|---|---|---|
| 16 | 201,047 | 93 µs | 633 µs |
| 32 | 293,545 | 154 µs | 1.71 ms |
| 64 | 537,165 | 126 µs | 656 µs |
| 128 | 619,571 | 256 µs | 1.27 ms |
| 256 | 449,990 | 822 µs | 6.62 ms |
| 512 | 589,822 | 0.88 ms | 2.11 ms |

- **Memory and CPU:** server 5.9 cores at its busiest 15 s, 146 MB RSS. dotnet-counters: no collection in any
  generation; at most 14 MB/s allocated (about 23 B per request).
- **Conclusion:** the pipeline works. Found and fixed before the real runs: vmstat on Ubuntu 24.04 has a `gu` column,
  so the busy share read 100% on every VM (the id column is found from the header now); the allocation rate is now
  the busiest 15 s against the best level. The numbers above are not comparable with later runs (the build shared
  the app VM).
