# The $12 contest's statements on the SQLite provider: 7 µs of database per request

- **Question:** how much of a one-core box the database takes at the $12 contest's load, and so whether sharding or
  more SQLite work could move its score.
- **Code:** codefusion-sqlite-prototype 298cb2d, `checks twelve`.
- **Machine:** Apple M1 Pro (macOS), and the 1 vCPU / 2 GiB Colima VM of
  [the prototype's VM run](2026-10-20-sqlite-prototype-1vcpu.md). .NET 10.0.13, SQLite 3.50.4.
- **Method:** the contest's schema with its seed's size (50,000 users, 500,000 posts, 2 million likes), its statements
  timed one by one, then weighted by bench/load.js's mix (each user loop: `GET /feed`, `GET /posts/:id`, a like 15% of
  the time, a new post 2%).

| Statement | Mac M1 Pro | 1 vCPU VM |
|---|---|---|
| `GET /feed` (20 newest posts, author, like count) | 11.6 µs | 15.0 µs |
| `GET /posts/:id` | 1.13 µs | 1.48 µs |
| like (insert, ON CONFLICT DO NOTHING), 16 writers | 15.0 µs | 24.5 µs |
| new post, 16 writers | 14.7 µs | 17.7 µs |
| database per request at the mix | 7.0 µs | 9.4 µs |

- **Memory and CPU:** at 30,000 users (1,460 loops/s, 3,170 requests/s) the database takes about 22 ms per second on
  the Mac and 30 ms on the VM: 2 to 3% of the core.
- **Conclusion:** the score is decided by the HTTP side (connections, JWT checks, JSON), not by SQLite. Sharding can't
  be used there (one file, a fixed schema) and isn't needed, so the provider was split into 2a (one file) and 2b
  (sharding), and the contest doesn't wait for 2b.
