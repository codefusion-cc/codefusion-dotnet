# Past the descriptor limit, with and without the default connection limit

- **Question:** does `HttpServerOptions.MaxConnections` defaulting to the soft RLIMIT_NOFILE less 1024 keep the server
  up past the limit, without the entry setting a cap of its own? What does the server do there without it? The $12
  entry died past 65,535 descriptors on GCP (2026-10-21-twelve-entry-fd-limit-crash.md).
- **Code:** unfixed: image twelve-cf-tables, built by the SQLite session with no connection cap. Fixed: twelve-cf-proof,
  the current submission with its `MaxConnections` line removed, on build/twelve-entry b664b30 with
  fix/accept-past-resource-limits' EventLoop.cs, HttpServer.cs and FileDescriptors.cs applied.
- **Machine:** cf-bench Colima VM (Apple M-series host, aarch64, 4 vCPU); the server on CPU 0 with 2 GiB and no swap,
  the load generator on CPUs 1-3; .NET 10.0.13.
- **Method:** twelve-dollar-codefusion's tools/bench/conns.sh, `--ulimit nofile=8192:8192`, 10,000 load.js users at
  1,000 a second, a 20 s settle, a 60 to 90 s hold. Unfixed runs one after the other; per-thread CPU from
  /proc/<pid>/task/*/stat during the hold. No other session used the VM meanwhile, but a test suite ran on the host
  Mac during the default-limit run, so its requests per second and CPU are indicative, not an A/B; what it shows is
  that the server stayed up and didn't spin.

| Run | Connections held | Requests answered | Server CPU over the hold | End |
|---|---|---|---|---|
| unfixed, 1 | 8,189 descriptors in use | 315 req/s | 99.95% | "Out of memory." |
| unfixed, 2 | 8,190 | none | 99.98% | "Out of memory." |
| unfixed, 3 (perf attached) | 8,190 | | | exit 139 (SIGSEGV) at the hold |
| unfixed, `DOTNET_EnableWriteXorExecute=0` | 8,191 | 692 req/s | 64.5% | "Out of memory.", PAL_SEHException |
| unfixed, `DOTNET_GCHeapHardLimit=0x50000000` | 8,190 | none | 99.97% | "Out of memory.", PAL_SEHException |
| default limit | 7,168 (8,192 - 1,024); 7,246 descriptors at most | 1,324 req/s, p99 52.5 ms | 15.1% | alive, nothing printed |

- **Memory and CPU:** RSS about 100 MiB in every run. In the unfixed runs the heartbeat thread used the most CPU
  (166 ticks in 5 s), and no dump was written: createdump needs descriptors too. With the default, the 2,832 users
  past the limit got their connections closed at once (the /feed errors), and the rest were served.
- **Conclusion:** keep the default. Without it, once the process is out of descriptors the server spins, stops
  answering and the runtime aborts, on any of these settings. Neither W^X off nor a GC hard limit prevents it, so the
  runtime fails for want of a descriptor somewhere this run didn't name. With the default, the server never
  reaches the limit. The SQLite session's run at 65,535 with the same cap held 64,511 connections with no abort.
