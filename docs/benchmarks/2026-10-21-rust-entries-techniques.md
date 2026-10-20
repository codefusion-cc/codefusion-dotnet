# What the best Rust entries do, and which of it CodeFusion should take

- **Question:** what makes the top Rust entries in both contests fast, which of their ideas the CodeFusion entries
  don't have yet, where each would land, and what it should gain. The owner's bar is beating the best Rust entry in
  each contest outright.
- **Code:** read at each PR's head, with `git show`, nothing built or run.
  - Multicore (arjaythedev/multicore-server-challenge): #11 rust-mio-foovoo bc78b77, #7 rust-tokio-Scooter1337
    851da7f, #25 rust-urus-markk116 88e2060, #1 rust-hyper-cknutson12 e93ab19, and examples/rust at 97bae8f.
  - $12 (arjaythedev/twelve-dollar-server-challenge): #82 rust-epoll-scooter1337 95aa405 (io_uring v2), #19 bd517fa,
    #90 rust-actix-web-abeni-csa 2c1db8a, #114 rust-axum-mjc cf07ad8, #17 rust-axum-tbsvttr bc77641, and #74
    csharp-epoll-gichamba 2d589c4 for comparison.
  - CodeFusion: feat/codefusion-packages 35fcf86, perf/http-transport f00a731, the multicore rewrite in mcsc-zeroalloc,
    and the $12 draft csharp-codefusion-XeonFX.
- **Machine:** none. This is a code study. Every number below that an entry claims comes from its author's own
  machine, not from a scoring box. The rig runs that check them are still to come.
- **Method:** read each entry's server, database and auth code, and its README. Then check each idea against what
  CodeFusion and its open branches already do.

## Ideas ranked, with where they land and what they should gain

The gains are estimates from the profiles and the entries' own measurements. None is measured on the rig yet. Each one
gets a rig run and an iteration file before it's kept.

| # | Idea | Package | Already on a branch? | Expected gain, multicore | Expected gain, $12 |
|---|---|---|---|---|---|
| 1 | Linux epoll transport: one read per ready socket, a short read means the socket is drained, `LibraryImport` + `SuppressGCTransition` for `recv`/`send`, a configurable loop count (`cpus/4` as foovoo does) | CodeFusion.Http | **Mostly, on perf/http-transport**: epoll and kqueue loops with one receive per request (89d366a), `EventLoops` = ProcessorCount, Sockets is still the default. Missing: direct `recv`/`send` (it still goes through `Socket`), sends batched per wake-up, and fewer loops than cores | Small: the server is about 28% of the box at PR #8's ceiling, and Postgres about 70%. Maybe 5-10% less server CPU, so 2-4% more users | The branch measured +21% on health and +5% on the mix (arm64 Colima). The rest of #74's design (direct syscalls, batched sends) is worth maybe another 10-20% on the mix; #74 is 26% ahead on health |
| 2 | One SQLite transaction per loop wake-up: every request of the wake-up, reads included, on one `locking_mode=EXCLUSIVE` connection, one `COMMIT`, then all the sends | CodeFusion.Data.Sqlite, plus an end-of-wake-up hook in CodeFusion.Http's event loop | No. feat/sqlite-provider (sqlite-connector session) has a writer thread with group commit | n/a | Removes the writer-thread hop and its wake-up on about 17% of requests (likes, posts). Maybe 10-20% on the mix; it's the design of both entries ahead of us (#82, #74) |
| 3 | Pipelined Postgres read groups: send the next group before the last one's ReadyForQuery (foovoo allows 4096 outstanding), release each read at its CommandComplete | CodeFusion.Data.Postgres (PgReader, PgLoops, PgConnection) | No. Today each loop thread sends a group of up to 32, then blocks in `recv` until ReadyForQuery (PgReader.Run), so each backend idles for a round trip per group | Enables #4. foovoo measured 72k users on 4 backends against 68k on 16 (+6%). Maybe 5-15% with #4 | n/a |
| 4 | Fewer threads and backends on 8 vCPU: 2 HTTP loops, 8 Postgres connections (6-8 read, 2 write), bcrypt on 1-2 threads | app settings (`DB_POOL_SIZE`, `DB_WRITERS`, `EventLoops`) | Settings exist. Can be measured on the rig now, without code | 0-6% on its own. More with #3 (less Postgres context switching and lock contention) | n/a (already one CPU) |
| 5 | One `SO_REUSEPORT` listener per loop | CodeFusion.Http (event loop) | No | Under 2%: accepts happen in the ramp, so it shows in ramp-time p99 with 120k+ connections | ~0 (one loop) |
| 6 | SQLite built `-O3 -march=native`, `HAVE_FDATASYNC=1`, GCC PGO trained on a seed-shaped database | the $12 entry's build.sh, and Data.Sqlite's documented build | No (the draft builds `-O2` without fdatasync) | n/a | SQLite is 34% of the draft's profile; 5-10% of that is 2-3% |
| 7 | One send per connection per wake-up with every reply in it; pause reading at 64 KiB of pending output | CodeFusion.Http (event loop) | No | Small: the load generator keeps one request in flight per connection | Small for the same reason, unless batching the sends after the wake-up also cuts the wake-up cost (#74 does it; untested for us) |
| 8 | bcrypt threads at SCHED_BATCH and nice 10 | CodeFusion.Security.Bcrypt (`BcryptQueueOptions`) | No. `ThreadPriority` does nothing on Linux without privileges | Tail latency during login bursts: 0-3% more users at the p99 edge | ~0 (bcrypt isn't in the $12 API) |
| 9 | Route each read to the connection with the least estimated queued Postgres time (a fixed cost per statement; the feed is heavy) | CodeFusion.Data.Postgres | No | Small on its own; it matters with only 8 backends (#4) | n/a |
| 10 | $12 SQL: the like as one `INSERT … SELECT WHERE EXISTS(post) ON CONFLICT DO NOTHING`, probing for the post only when nothing changed; create without `RETURNING` | the $12 entry, through Data.Sqlite's query generator | No (the draft checks the post first, then runs `INSERT … RETURNING`) | n/a | About 3% on the mix: one statement less per like |
| 11 | Writes that expect errors (sign-up, user update) never share a group | CodeFusion.Data.Postgres (a flag on the statement) | No | Insurance against a group rerun on a taken name; ~0 on the workload | n/a |
| 12 | io_uring transport: multishot accept and recv, provided buffer ring, DEFER_TASKRUN, one `io_uring_enter` per loop | CodeFusion.Http (a second Linux transport) | No | Unknown; only after #1 | #82 (89k) against #74 (57k) on the mix, but that's Rust against C#. Unknown for us |

Order: for $12, finish #1 (on perf/http-transport), then #2 and #7, then #6 and #10. For multicore, measure #4 now,
then #3 with #4, then #9 and #5. Each step gets its own rig run and iteration file. #2 changes when a reply may go
out (commit, then send), so its tests hold the commit and make it fail, as tbsvttr's tests/commit_fault.c does.

- **Conclusion:** both contests' leaders win on transport and batching, not on SQL. On multicore, Postgres is 70% of
  the box at PR #8's ceiling, so the gains are in using Postgres better: pipelining, fewer backends and routing. On
  $12, the kernel is 52% of the draft's profile, so the gains are the event-loop transport and the per-wake-up
  transaction. Two entries break rules (below), so they can't be the bar until fixed: $12 #90 has no scripts and lax
  JWT checks, and #74 accepts tokens without `exp`.

## The entries in detail

### Multicore contest (Conduit on Postgres 18, 8 vCPU)

### Architecture and HTTP

| Entry | Threads / loop | Listeners | HTTP read / write per request |
|---|---|---|---|
| pr-11 rust-mio-foovoo | `cpus/4` mio (edge epoll) loops = **2** on 8 vCPU (main.rs:70-74), std threads, no runtime | one `SO_REUSEPORT` listener per loop, backlog 65535 (server.rs:171-182) | one shared 16 KiB scratch read per loop; short read (`n<16K`) means drained, so no EAGAIN read (server.rs:444-445); one `writev` header+body (628-643); per-conn `inbuf` only on partial reads; idle conn about 100 B |
| pr-7 rust-tokio-Scooter1337 | tokio, `cores.div_ceil(4)` = **2** workers (main.rs:59-63) | one, no REUSEPORT, backlog 16384 | idle task waits on `readable()` with no buffer; 1 KiB from thread-local size-class free lists (http.rs:17-72,117-135); one `try_write_vectored` (377-392) |
| pr-25 rust-urus-markk116 | own green-thread runtime (smarm), schedulers = cores | in external `urus` crate (git host outside repo) | not checkable; serde `to_vec` |
| pr-1 rust-hyper-cknutson12 | tokio, cores workers, hyper http1 | one, backlog 16384 | hyper defaults, `Full<Bytes>` |
| examples/rust | axum + sqlx pool of 10 | | serde, one round trip per statement |

All set TCP_NODELAY. Only foovoo sets SO_REUSEPORT. None pin CPUs, busy-poll or size socket buffers. foovoo raises
`RLIMIT_NOFILE` soft to hard (main.rs:51-60).

### Postgres access

| | foovoo pr-11 | Scooter pr-7 | markk116 pr-25 | cknutson12 pr-1 |
|---|---|---|---|---|
| Connections | **8**: per loop 3 read + 1 write (main.rs:76-95) | **8**: 6 read + 2 write (main.rs:73-74) | 40 (32 read, 8 write) | 48 (16 read, 32 write) |
| Client | own, on `postgres-protocol` framing | vendored tokio-postgres + `query_group_encoded` (vendor MODIFICATIONS.md) | own `smarm-pg` (external) | stock tokio-postgres |
| Statements | 27 named, Parsed once at connect, Bind+Execute only; **binary** in and out (pg.rs:272, 673-678) | `prepare_typed` per conn | lazy named, binary | prepared per conn |
| Session SETs | jit off, parallel 0, statement 9 s, lock 5 s, `force_generic_plan` (main.rs:102-113) | generic plan, jit off | as startup options | generic plan, jit off |
| Read batching | groups of one epoll iteration share a Sync, max 8 (pg.rs:290-328); **up to 4096 unanswered groups pipelined per conn** (server.rs:32); each member released at its CommandComplete, before RFQ (pg.rs:389-409) | 0 µs wait, up to 32 per Sync (writer.rs:178-241); batch failure: each read retried alone | `Db::batch`: one request's statements behind one Sync | none |
| Read routing | least **estimated Postgres time**, fixed cost per statement (sql.rs:336-364, server.rs:739-755) | least-loaded, rotating start (db.rs:159-188) | fewest in flight | least-loaded scan |
| Group commit | one Sync in flight per write conn; up to 32 held writes as Bind/Execute… + one Sync = one implicit txn; results released at RFQ (pg.rs:341-365, 434-459) | 1 ms wait, up to 128: `BEGIN`, pipelined statements each with own Sync, `COMMIT` (3 round trips) (writer.rs:194-285) | none across requests | none (Postgres's own flush grouping over 32 backends) |
| Error isolation | first ErrorResponse blamed on current member; others re-sent alone with own Sync; 40P01/40001 retried once (pg.rs:412-485) | any error: ROLLBACK, all re-run alone; failed COMMIT not retried | n/a | n/a |
| Solo writes | `USER_INSERT`, `USER_UPDATE` (unique violation expected), `TAGS_INSERT` never share a Sync (pg.rs:169-171, 347-357) | | | |
| Reconnect | background thread, fails only in-flight groups with 503 (server.rs:808-828) | 250 ms loop, never replays | supervisor | none |

SQL tricks seen (pick-up list):
- One statement per request; list page + capped count in one: `WITH c AS (count) SELECT c.n, p.* FROM c LEFT JOIN LATERAL (page) p` (foovoo sql.rs:67-87).
- Count cap via `OFFSET 999 LIMIT 1` existence probe (foovoo sql.rs:56-63, Scooter sql.rs:50). **CodeFusion has it** (Data/Sql.cs:66).
- Ids first, per-row subqueries only for the page (foovoo sql.rs:124,136). CodeFusion has it.
- Feed: LATERAL per followee `LIMIT limit+offset`; `following=true` constant (foovoo sql.rs:160-168). CodeFusion has the LATERAL.
- Favorite count = snapshot count ± the CTE's own row (foovoo sql.rs:199-240, pr-1 sql.rs:76-96). CodeFusion's CTE design is close.
- `count(*)` + `bool_or(user_id=$1)` in one LATERAL for count and favorited (Scooter sql.rs:48,55).
- Article create: MATERIALIZED CTE reads existing tags and inserts only the missing ones, so hot tag rows aren't touched (Scooter sql.rs:102-117). pr-1's `ON CONFLICT DO UPDATE` locks hot tags on every create; CodeFusion uses `DO NOTHING` (Sql.cs:100), fine.
- Create as one group INSERT(nextval) / tags / links / SELECT by `(SELECT currval(...))` under one Sync (foovoo handlers.rs:505-515, sql.rs:279-306).

### bcrypt (all cost 10, all verify every login)

| | Implementation | Threads | Batching |
|---|---|---|---|
| foovoo | own 4-lane interleaved scalar EksBlowfish, bit-exact (bcrypt2.rs:1-9, 293, 432) | 2, **SCHED_BATCH + nice 10** (pool.rs:186-193) | primary waits up to 200 ms for company; second worker takes only full batches of 4 or jobs >500 ms old (pool.rs:49-123); queue 256, 503 past it |
| Scooter | libxcrypt crypt-bcrypt.c + `bcrypt-batch` 2/3/4 lanes interleaved per Feistel round, not SIMD (native/bcrypt-batch-impl.h:1-86) | `cores/8` = 1 via spawn_blocking | up to 4 within 160 ms |
| markk116 | bcrypt crate core with preemption points, inline in request actor | all schedulers | none |
| cknutson12 | bcrypt crate | spawn_blocking, semaphore cores-1 | none |

CodeFusion.Security.Bcrypt already interleaves 4 and waits 200 ms. Its thread priority option is `ThreadPriority`
(BcryptQueueOptions.cs:35), which on Linux does nothing without privileges; SCHED_BATCH/nice is the working equivalent.

### Memory
foovoo and Scooter and pr-1 use mimalloc; markk116 the system allocator. foovoo still allocates per request (owned
`Vec<P>` + `Bytes` of the Bind kept for resends, pg.rs:266-287); CodeFusion allocates 0 B, already ahead.

### Claimed numbers (none from the scoring box)
- foovoo: 76k users, 20.1k req/s, p95 9.4 ms on 4 vCPU M1 Pro Lima VM (README:160-163); 68k with 16 backends vs 72k with 4 (README:106-108); Postgres about 70%, app 22% (bcrypt 9%); shared Syncs averaged 2.6 requests.
- Scooter: 76k users, 19,964 req/s, p95 92 ms, 105 MiB RSS, Xeon E5-2690 v3 (README:26).
- markk116: "101k equivalent users" at think x0.1; own estimate about 60k real users (README:113-141).
- cknutson12: 17.1k req/s at 7k VUs, think x0.1, Docker on a laptop.
- For scale, CodeFusion's 21abce6 (Kestrel+Npgsql) passed 125k on c4-highcpu-8 with Postgres 70% of 98% CPU.

### Rule check
No caching, no non-durable writes, no skipped verification in any of the four. Notes:
- foovoo skips the `alg` check when the header is byte-identical to the standard HS256 header (auth.rs:56-61): still HMAC-verified, fine.
- foovoo runs the create's read-back inside the transaction rather than "after the commit" (SPEC:191); the reply still waits for the commit.
- markk116: `claims.exp < t` accepts `exp == now` (auth.rs:82); its HTTP and Postgres code live on an outside git host (Cargo.lock), so they can't be reviewed from the PR.

### $12 contest (feed on SQLite, 1 vCPU / 2 GB)

### Architecture and HTTP

| Entry | Threads / loop | Accept / receive | Send |
|---|---|---|---|
| pr-82 rust-epoll-scooter1337 (io_uring v2) | `ceil(8*65535/hardNOFILE)` rings, max 8, all under one `Mutex<App>` (uring.rs:20-21, 922-997): one effective worker on 1 vCPU. Ring: CQ 16384, SQ 4096, `COOP_TASKRUN + SINGLE_ISSUER + DEFER_TASKRUN`, no SQPOLL (uring.rs:271-277) | multishot accept straight into a sparse fixed-file table (361-369); multishot recv with a **provided buffer ring** of 4096×2 KiB (278-291, 379-385); parse in place from the kernel buffer, copy only partial tails (533-615) | one `Send` per connection per batch carrying all pipelined replies (386-399); one `io_uring_enter` per loop: waits for min(32, 2×open) CQEs with a 250 µs timeout under load (780-795) |
| pr-19 (older) | same design; NBUFS 1024, SQ 1024, CQ 8192; serde for JSON input | | |
| pr-74 csharp-epoll-gichamba | one thread, level-triggered epoll, `epoll_wait(512,-1)` (Server.cs:93-148) | `accept4` loop; EMFILE removes the listener until a close (152-177); one `read(fd,64K)` into one pinned shared buffer (183-237) | every reply of a wake-up goes into one 1 MiB `Out`; after COMMIT one `send(MSG_NOSIGNAL)` per reply (394-437) |
| pr-114 rust-axum-mjc | tokio current_thread + forked hyper/tokio; idle sockets leave hyper: std `TcpStream` in a private epoll fd that is the only `AsyncFd` (idle.rs:31-126); active capped 8192 | plain nonblocking read, registers with Tokio only after WouldBlock (socket.rs:30-106) | hyper `writev(true)` |
| pr-17 rust-axum-tbsvttr | tokio current_thread, stock axum; DB task on same thread | stock | stock; `Connection: close` past 40k conns |
| pr-90 rust-actix-web-abeni-csa | actix, `web::block` per DB call | stock | sonic_rs to fresh Vec |

Other HTTP details: Scooter's JSON escaping 16 bytes at a time with SSE2/NEON (main.rs:16-82) and a validating
single-pass JSON cursor for JWT header, claims and body (lufp.rs; about 2× faster than serde, +5% on creates only,
LUFP.md:9-20). Backpressure: recv cancelled at 64 KiB pending output, resumed at 32 KiB (uring.rs:488-501). A 32-bit
generation in user_data rejects completions for reused slots (266-268). gichamba and foovoo cache the Date header per second.

### SQLite

| | Scooter pr-82 | gichamba pr-74 | mjc pr-114 | tbsvttr pr-17 | abeni pr-90 |
|---|---|---|---|---|---|
| Build | 3.53.4, `THREADSAFE=0`, MEMSTATUS=0, DQS=0, OMIT_{LOAD_EXTENSION,DEPRECATED,SHARED_CACHE,PROGRESS_CALLBACK}, LIKE_DOESNT_MATCH_BLOBS, USE_ALLOCA, **HAVE_FDATASYNC=1**; **-O3 -march=native + GCC PGO trained on a synthetic DB** (build.rs:9-37, bench/train_sqlite.c) | `clang -O2 -march=native`, THREADSAFE=0, same omits + OMIT_AUTOINIT, MAX_EXPR_DEPTH=0, HAVE_FDATASYNC, static `.a` + `DirectPInvoke` (build.sh:17-22, Feed.csproj:30-32) | bundled, `-USQLITE_ENABLE_MEMORY_MANAGEMENT` | -O3, THREADSAFE=1, omits | bundled defaults |
| Connections | **one**, `locking_mode=EXCLUSIVE` | **one**, EXCLUSIVE | one worker thread on `unix-excl` VFS (WAL index on heap) + checkpoint conn | one, EXCLUSIVE | r2d2 readers (8) + mutex writer |
| Pragmas | WAL, NORMAL, mmap 1 GiB, cache 500 pages, temp MEMORY, `wal_autocheckpoint=0` (db.rs:176) | WAL, NORMAL, mmap 1 GiB, cache 16 MiB, autocheckpoint 0 (Db.cs:39-45) | WAL, NORMAL, autocheckpoint 0, cache 32 MiB, mmap 0 | WAL, NORMAL, cache 8 MiB, mmap 256 MiB | WAL, NORMAL, 64 MiB cache per conn |
| Batching | **every request of one loop iteration, reads included, in one lazy deferred `BEGIN`; one COMMIT; sends queued only after it** (db.rs:238-295, uring.rs:837, 744-775); commit failure turns every reply into 500 | same: whole wake-up in one `BEGIN`…`COMMIT`, then SendBatch (Server.cs:137-146, 334-341) | reads: ≤1024 within 1 ms in one `BEGIN DEFERRED`; writes: ≤1024 within 1 ms in `BEGIN IMMEDIATE`, **SAVEPOINT per write** (db.rs:583-998) | `yield_now` then drain ≤256; lazy `BEGIN IMMEDIATE` on first write (main.rs:162-348) | none |
| Checkpoint | wal_hook counts frames; **RESTART at ≥4096 frames** after commit (db.rs:275-290; 1000 in pr-19) | wal_hook; PASSIVE at 1000 **after the batch's sends** (Db.cs:83-97, Server.cs:147) | PASSIVE after 1 s idle on its own thread | auto | auto |
| Statements | `prepare_v3` PERSISTENT, reset + clear_bindings | PERSISTENT, columns read as spans into the reply | PERSISTENT | `prepare_cached` 16 | `prepare_cached` |
| SQL | like = one `INSERT…SELECT WHERE EXISTS(post) ON CONFLICT DO NOTHING`, exists probe only when nothing changed (db.rs:469-505); create = INSERT + `last_insert_rowid` + SELECT created_at, **no RETURNING** (430-468); feed range scan when the 20 ids span <256 (335-412) | like = `INSERT … WHERE EXISTS`, exists only on no insert (Db.cs:170-192) | feed split into 3 range queries, fallback >256 | | |

Memory: Scooter uses the system allocator, a 64-byte per-connection record, buffers only on demand, output pool of
256×16 KiB; zero allocations per warmed request. gichamba: NativeAOT, workstation non-concurrent GC, `stackalloc` JWT,
ArrayPool; classic `DllImport` without `SuppressGCTransition`. mjc: jemalloc with background threads.

In-process settings: Scooter raises NOFILE soft to hard, backlog 4096, TCP_NODELAY on the listener (inherited);
gichamba TCP_NODELAY on the listener, backlog 65535; mjc TCP_NODELAY per accept, many listeners via HOST/PORT ranges
(bind.rs). Nobody sets REUSEPORT, busy poll or affinity.

### Claimed numbers
- Scooter v2 (WSL i9-13900H): mixed 75.0k → 81.2k req/s at 64 conns, 83.9k → 96.0k at 1,024 (RESEARCH.md:21-29); Xeon 1 CPU: feed 40.1k, single post 157.4k, create 13.7k (README:45-52); 15k-user hold, p99 2-3.4 ms, 61 MiB.
- gichamba (Azure F1s): 64k users held, p99 53 ms, CPU 39%; 64k was the conntrack ceiling (README:38-86).
- mjc: 240k users, 25.3k req/s, p95/p99 478/618 ms on a 1 vCPU VM (README:244-258).
- tbsvttr: 70k users 1-minute hold, p99 23.7 ms; mixed 45.6k req/s (README:82-165).
- CodeFusion draft (same M1 Colima rig): mix **34.2k** vs gichamba 57.5k vs Scooter 89.3k; profile 52% kernel, 34% SQLite, 8% own code; two receives per request and twice the wake-up time of #74.

### Rule check ($12)
- **pr-90 abeni-csa**: no `install.sh`/`build.sh`/`start.sh` (rule 10: can't run); missing or non-Bearer header returns "invalid or expired token" instead of `missing bearer token` (main.rs:177-184); token without `exp` accepted (`if let Some(exp)`, main.rs:225). README describes a different program than the code.
- **pr-74 gichamba**: token without `exp` or with `"exp": null` accepted (Jwt.cs:149-151, 188); SPEC says verify signature and exp.
- **pr-114 mjc**: build pulls forked hyper/tokio from GitHub (pinned by rev); reads in a batch share a snapshot taken up to 1 ms earlier (still a live SQLite read). Borderline, not a break.
- Scooter: a batch's reads share one deferred-transaction snapshot; with one connection and EXCLUSIVE locking nothing else can write, so not stale. Feed range path is rediscovered per request with a general fallback. Compliant.
- No entry caches data or responses, and every group commit sends after COMMIT.

