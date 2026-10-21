# CodeFusion.ContestBench

The rig that scores contest entries the way the two contests do, on Google Cloud (project in your gcloud config,
zone europe-west1-b). Results, the VM ledger and built binaries go to `artifacts/contest/`.

It is a project under `benchmarks/`, not a script in `scripts/`, because the same program also runs on the VMs: it is
published for linux-x64 and is the target's switch and monitor hooks (`cbench agent ...`). The contests' checkouts
are found next to this repository (`../multicore-server-challenge`, `../twelve-dollar-codefusion`), or where
`CBENCH_MULTICORE_REPO` and `CBENCH_TWELVE_REPO` say.

```bash
cbench() { dotnet run --project benchmarks/CodeFusion.ContestBench -c Release -- "$@"; }
cbench image multicore            # Ubuntu 24.04 + box/setup-box.sh + full and dev seeds -> image family cbench-multicore
cbench image twelve               # Ubuntu 24.04 + nginx (bench/nginx.conf) + seed/feed.db  -> cbench-twelve
cbench image load                 # loadgen (multicore main), k6, sysctl.conf               -> cbench-load
cbench rig up multicore --load 5  # target + 5 load boxes from the images
cbench deploy multicore pr8 ../multicore-server-challenge:85715f2:submissions/csharp-kestrel-xeonfx
cbench search multicore pr8       # the full scoring search; --quick for iterating, --fixed N for one hold at N users
cbench results                    # the table
cbench rig down                   # delete every rig VM; the images stay
```

What can cost money, and what stops it:
- Every VM is created with `--max-run-duration` (8 hours, `--hours N` for up to 48) and `--instance-termination-action=DELETE`:
  Google deletes it then, whatever happened on the Mac.
- Ctrl-C deletes the VMs that run created before it exits. A `rig up` that fails deletes the VMs it created (ones
  already up stay); an `image` deletes its builder whether it succeeds or fails.
- `rig status` lists the VMs and adds up the ledger. A VM that went without the rig (a preempted spot box, or one
  deleted at its max run duration) gets a "gone" row at the time the zone's operations give.
- The images stay: one per family, 95 GB of disks (8.7 GB stored) as of 2026-10-21. At about $0.05 per GB-month
  that is $0.45 to $4.75 a month, depending on which size is billed. To delete them:
  `gcloud compute images delete $(gcloud compute images list --no-standard-images --filter=labels.purpose=contest-bench --format='value(name)')`.

Scoring rules the rig keeps:
- An entry is deployed from a commit: `repo:rev:path`, or a folder in a git checkout with nothing uncommitted or
  untracked in it (its HEAD). Only tracked files go to the VM. The deploy records the commit and a build id (a hash of
  the archive); the switch prints both, and every phase and result row carries them.
- A label is one build of one entry. A search resumed under a label refuses when the entry was deployed again from
  other sources since; before each phase it runs, it checks the box still runs that build, and switches back when
  another entry's search ran in between.
- The entry is warm before every phase measured: after a switch, a crash restart or a resume, the 1,000-user warm-up
  runs again first (`rewarm-<phase>`).
- A phase where the entry crashed, or 1% of requests failed, is a fail even when the load boxes were busy (a dead
  entry makes k6 spin on refused connections). Busy load boxes void only a pass or a latency fail.
- The search log's first line has the options the search ran with.
- Entry names and labels are letters, digits, '.', '_' and '-', starting with a letter or digit.
- ssh keeps the VMs' host keys in `artifacts/contest/known_hosts` (taken on first contact, dropped when a new VM gets
  the address). The rig's key stays on the Mac: the first multicore load box makes its own key to drive the others.


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
- A run where a load box averages over 75% CPU (or falls behind, for loadgen) is LG-limited and doesn't count, unless
  it failed on a crash or errors.
- `--quick` runs shorter phases at 10% resolution: for iterating, never a score.
- `cbench agent pgstat on|reset|dump|off` turns on pg_stat_statements for profiling runs only (a server setting the
  rules don't give entries).
- Every VM carries the labels `purpose=contest-bench,owner=codefusion`; `rig status` prints the VMs and the cost from the
  ledger (Cloud Billing catalog list prices for europe-west1, 2026-10-21).
