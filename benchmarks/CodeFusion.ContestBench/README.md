# CodeFusion.ContestBench

The rig that scores contest entries the way the two contests do, on Google Cloud (project in your gcloud config,
zone europe-west1-b). Results, the VM ledger and built binaries go to `artifacts/contest/`.

```bash
cbench() { dotnet run --project benchmarks/CodeFusion.ContestBench -c Release -- "$@"; }
cbench image multicore            # Ubuntu 24.04 + box/setup-box.sh + full and dev seeds -> image family cbench-multicore
cbench image twelve               # Ubuntu 24.04 + nginx (bench/nginx.conf) + seed/feed.db  -> cbench-twelve
cbench image load                 # loadgen (multicore main), k6, sysctl.conf               -> cbench-load
cbench rig up multicore --load 5  # target + 5 load boxes from the images
cbench deploy multicore pr8 /Volumes/Dev/Projects/multicore-server-challenge:85715f2:submissions/csharp-kestrel-xeonfx
cbench search multicore pr8       # the full scoring search; --quick for iterating, --fixed N for one hold at N users
cbench results                    # the table
cbench rig down                   # delete every rig VM; the images stay
```

| | Multicore | $12 |
|---|---|---|
| Target | c4-highcpu-8 (8 vCPU, 16 GB), the shape PR #8's README used | c4-highcpu-2 held to 1 vCPU and 2 GB by the kernel command line (`nr_cpus=1 mem=2G`), no swap: the owner wants Intel C4 for scored boxes, and C4 starts at 2 vCPU / 4 GB. The switch checks nproc, MemTotal and swap before every search |
| Box setup | the repo's `box/setup-box.sh` (Postgres 18 + `pg-conf.sh`, Nginx + `site.conf`, sysctls, `challenge.service`) | Ubuntu's nginx with `bench/nginx.conf` and the limits its header names; nothing else tuned |
| Load | the repo's `loadgen` on five n2-highcpu-4 spot boxes (the project has 32 vCPUs), up to 60,000 users each | k6 `bench/load.js` on n2-highmem-4 spot boxes |
| Search | loadgen's own `search` (the scorer's orchestrator), with `cbench agent switch` and `cbench agent monitor` as its hooks | in C# here: warm-up 1,000 users for 2 minutes, from 2,500 doubling, narrowing to 250, 5-minute holds stepping down 250 |
| Fresh state | `CREATE DATABASE conduit TEMPLATE conduit_seed`, Postgres restarted, page cache dropped | a fresh copy of `seed/feed.db`, page cache dropped |

Notes:
- Multicore searches lift loadgen's `-max` from the scorer's 120,000 to what the load boxes can drive, so entries above
  the cap still differ; the table shows both.
- $12 probes are 2-minute load.js runs (the README doesn't give their length); a run passes on k6's thresholds over all
  its requests. Several k6 boxes split the users, and their samples merge exactly (3 significant digits).
- A run where a load box averages over 75% CPU (or falls behind, for loadgen) is LG-limited and doesn't count.
- `--quick` runs shorter phases at 10% resolution: for iterating, never a score.
- `cbench agent pgstat on|reset|dump|off` turns on pg_stat_statements for profiling runs only (a server setting the
  rules don't give entries).
- Every VM carries the labels `purpose=contest-bench,owner=codefusion`; `rig status` prints the VMs and the cost from the
  ledger (Cloud Billing catalog list prices for europe-west1, 2026-10-21).
