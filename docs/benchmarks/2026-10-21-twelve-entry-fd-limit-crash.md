# The $12 entry at its descriptor limit: why it crashed at 80,000 users

- **Question:** on GCP (c4-highcpu-2 held to 1 vCPU and 2 GB, systemd `LimitNOFILE=65535`) the entry passed 40,000 users
  and died with SIGSEGV in the 80,000-user probe, with nothing in the journal. Why, and what keeps it up?
- **Code:** `cf-tables` (codefusion-dotnet 150f0e6), the crashed build; `cf-fdcap`, the entry on b664b30 with a
  connection cap below the descriptor limit (below).
- **Machine:** the Colima VM cf-bench, as in [twelve-entry-connection-memory](2026-10-21-twelve-entry-connection-memory.md),
  with the server's limit set as GCP's: `--ulimit nofile=65535:65535`.
- **Method:** tools/bench/conns.sh, ramping 2,000 users a second to 60,000 and then 66,000, 10 s to settle, 15 s holds;
  core dumps on, then gdb attached to the server, stopping on SIGABRT and printing every thread's stack.

## Runs

| Run | Build | Limit | Users | What happened |
|---|---|---|---|---|
| 1 | cf-tables | 65,535 | 60,000, then 70,000 | 60,000 held (p99 1.7 s on the busy Mac); died during the ramp past about 62,000: exit 139, nothing on stderr, no dump |
| 2 | cf-tables | 4,096 | 3,000, then 5,000 | held 4,095 descriptors and kept answering; no crash |
| 3 | cf-tables | 65,535 | 60,000, then 66,000 | 65,533 descriptors open; the server at 99.9% CPU completing nothing; then `terminate called after throwing an instance of 'PAL_SEHException'`, `Out of memory.` |
| 4 | cf-tables + gdb | 65,535 | 60,000, then 66,000 | the same; gdb caught the abort |
| 5 | cf-fdcap | 65,535 | 60,000, then 66,000 | held 64,511 connections; p99 41 ms; the users past the cap refused (4.1% of requests); no abort |

- **The stack (run 4):** SIGABRT on the `http event loop` thread: a JIT-compiled frame, then libcoreclr, `__cxa_rethrow`,
  `std::terminate`, `abort`. A managed exception the runtime had to carry through native frames ended the process. At
  that moment the runtime's own `.NET DebugPipe` thread was in `open64`. With no descriptor left, the runtime can't open
  what a code path needs, and the process dies; on GCP the same end came as SIGSEGV. The runtime's dump couldn't be
  written either, since createdump needs descriptors too.
- **Before it dies, the server spins:** at 65,533 of 65,535 descriptors, 99.9% CPU and no request completed in the hold.

## Fix in the entry

- `FileLimit.Raise()` raises the soft limit to the hard one at start and logs both.
- `HttpServerOptions.MaxConnections` is the limit less 1,024 descriptors, kept for the runtime, SQLite's files and a
  connection accepted past the cap (CodeFusion.Http closes it at once). Logged at start:
  `open files: 65535 (the hard limit); connections: 64511`.
- Run 5 is the check: the same 66,000 users over the same limit, and the server held its cap and kept answering.

## Conclusion

- The GCP crash was the descriptor limit, not SQLite: 80,000 users can't fit in 65,535 descriptors. The cap turns a dead
  server into refused connections.
- Whether 65,535 binds the real contest depends on how its box starts the server: from a shell on Ubuntu the hard limit
  is 524,288 or more, and .NET raises the soft limit to it. GCP's rig sets 65,535 in its systemd unit.
- For CodeFusion.Http: a default `MaxConnections` from the descriptor limit less a reserve, and the 100% CPU spin at
  EMFILE, are the transport session's to fix.
