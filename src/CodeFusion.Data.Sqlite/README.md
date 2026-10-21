# CodeFusion.Data.Sqlite

CodeFusion.Data's SQLite provider: the same contexts and LINQ queries as on Postgres, compiled at build time to
SQLite's SQL, run in process against one database file. Once warm, nothing is allocated per query (exactly what that
covers is under [Zero allocations, measured](#zero-allocations-measured)).

It calls SQLite directly (`e_sqlite3`, no ADO.NET layer). Reads run on the caller's thread, on reader connections
that keep their WAL snapshot until a write commits. Writes go to one writer thread, which commits whatever has
queued as one transaction (group commit), so 64 waiting writers share one commit and, with `synchronous=FULL`, one
fsync. A failed or refused command comes back as a status, not an exception.

```bash
dotnet add package CodeFusion.Data.Sqlite
```

## Example

```csharp
using CodeFusion.Data.Sqlite;

public sealed partial class AppDb : DataContext<Sqlite>
{
    public Table<Article> Articles => Table<Article>();

    public One<ArticleView> ArticleBySlug(Utf8 slug) =>
        Articles.Where(a => a.Slug == slug).Select(a => new ArticleView(a.Title, a.Body)).One();
}

// at startup: the schema as numbered steps, run once each, in order
var source = services.AddDatabase<AppDb>(new SqliteDatabase("data/app.db")
{
    Migrations =
    [
        "CREATE TABLE articles (id INTEGER PRIMARY KEY AUTOINCREMENT, slug TEXT NOT NULL UNIQUE, title TEXT NOT NULL, body TEXT NOT NULL)",
    ],
});

// in a handler, as with Postgres
return db.ArticleBySlug(slug).OrNotFound(Errors.ArticleNotFound);
```

The LINQ is the same as on Postgres: a file of queries that two projects include compiles to Postgres's SQL in the
one whose context derives from `DataContext<Postgres>` and to SQLite's in the one with `DataContext<Sqlite>`. Queries
written in SQL (`Sql<T>($"...")`) are each provider's own.

Statements outside a context are prepared once per connection:

```csharp
static readonly SqliteStatement CountBySlug = new("SELECT count(*) FROM articles WHERE slug LIKE ?1", DataType.Text);

var c = source.Read(CountBySlug);
c.Text("a%"u8);
var result = await Sqlite.Run<FirstColumn>(c.Hold(), null, aborted); // result.Value: the count

sealed class FirstColumn : IRowReader
{
    // the row is read in place: copy what you keep
    public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct => row.GetInt64(0);
}
```

## Schema

- **Keys the database draws** are `INTEGER PRIMARY KEY AUTOINCREMENT`: an insert that reads its new key back, or uses
  it in its own values, reads `sqlite_sequence`, which only AUTOINCREMENT tables have. A key written explicitly moves
  the sequence past it.
- **Instants** (`Timestamp`) are integers: microseconds since 1970, UTC. `SqlFunctions.Now()` in a write is the time its
  group began, the same for every row the group writes (as `now()` is in a Postgres transaction); in a read, the time
  of the call.
- **Booleans** are 0 and 1. **Text arrays** (`Contains` on a list) are bound as one JSON text and read with
  `json_each`.
- Foreign keys are checked unless `ForeignKeys = false`. A table in another schema (an attached database) is not
  supported.

## Options

| | Default | |
|---|---|---|
| `Synchronous` | `Full` | `Normal` skips the fsync per commit: a commit survives the process, but the last ones may roll back on power loss |
| `Readers` | cores, at least 2 | reads running at once; more callers wait for a free connection, spinning briefly |
| `StatementTimeout` | 30 s | a statement past it is interrupted (`StatementTimeout`); an interrupted write is undone |
| `MaxQueued` | 8192 | writes waiting past this are refused at once (`Busy`); `Writer.Queued` says how many wait |
| `MaxGroup` | 256 | the most writes in one transaction |
| `CheckpointFrames` | 1000 | WAL frames after which the writer checkpoints, once the group's writes have completed |
| `MmapSize`, `CacheKiB` | 256 MiB, 8 MiB | per connection |
| `CompleteOnWriterThread` | off | a write's caller continues on the writer's thread: one thread switch less, but it must never wait on another write there |
| `LeaderCommit` | off | a write that finds the writer idle commits on its caller's thread |
| `Exclusive` | off | one connection for every read and write, the file locked to this process: for one or two cores (below) |
| `EventLoopBatches` | off | with `Exclusive`, behind CodeFusion.Http's event loops: one transaction per loop wake-up (below) |

## One core

`Exclusive = true` gives the file to this process alone (`PRAGMA locking_mode=EXCLUSIVE`) and runs every read and
write on one connection: SQLite keeps the WAL index in the process's memory, and reads and the writer take turns
instead of reading beside it. On one core, where they take turns anyway, that is much faster under a mixed load:

| 1 vCPU, 2 GiB | default | `Exclusive` |
|---|---|---|
| point reads, 1 thread | 1.51 to 1.71 M/s | 1.46 to 1.70 M/s |
| 64 writers, `Normal` | 63 to 65 k/s | 64 to 71 k/s |
| 16 callers, 9 reads to each write, `Normal` | 50 to 77 k ops/s | 388 to 486 k ops/s |
| the same, `Full` | 74 to 75 k ops/s | 120 to 198 k ops/s |

(`benchmarks/CodeFusion.Data.Sqlite.Throughput`: 100,000 rows, reads and updates by key, in a container pinned to one
CPU of a Colima VM, linux-arm64; three rounds alternating the two, two with `Full`.) Reads and the writer take turns,
each waiting for at most one of the other's. On more cores it costs: reads no longer run beside each other, so 4
threads on an 8-core Mac read 1.2 to 1.8 M/s where the default reads 6.4 M/s. The runs, and the ones before them,
are in [docs/benchmarks](../../docs/benchmarks/README.md).

Behind CodeFusion.Http's event loops (`HttpTransport.EventLoop`), `EventLoopBatches = true` goes one step further: a
write made on a loop runs there at once, inside one transaction per loop wake-up, and every write of the wake-up commits
together when the server has handled its events. Each write completes after that COMMIT, on the loop, so its answer goes
out with the wake-up's others. A write has a savepoint of its own, so one that fails fails alone. `AddDatabase` hands
the server the batch's end; nothing else to wire. Writes off a loop still go to the writer thread. A batch opened after
the batch's end ran in that wake-up (by another database's completions, or a handler resumed as answers go out) asks the
loop for another round, so it still ends in its wake-up. A read on the loop of a table the batch has written and not
committed yet runs inside its transaction and sees the writes; it completes with them, after the one COMMIT, or fails
with them. A read of tables the batch hasn't written answers at once, from committed rows. What each statement reads and
writes is what SQLite reports as it prepares it (triggers, foreign-key actions and views included), so raw SQL counts as
generated SQL does. So a wake-up commits once (unless an error makes SQLite roll its transaction back, which fails every
write it held), and no answer goes out with a write its COMMIT then loses. A handler that reads a written table and then
writes waits for the read, so its write joins the batch the completions open: one more COMMIT for the wake-up, not one
per request. While a batch holds the connection, other threads waiting for it park instead of spinning.

## Failures

| What | What the caller sees |
|---|---|
| A unique or primary key conflict | `UniqueViolation` (409); the rest of the group commits |
| One statement of a unit (`Then`) fails | the whole unit is undone (a savepoint each), the rest of the group commits |
| Disk full or an I/O error | that write fails; if SQLite rolled the transaction back, the group runs once more without it |
| The process dies | no write that completed is lost; none of an open group's writes exist after the next open |
| Another process holds the write lock | the writer waits `BusyTimeout`, then the group fails `Busy` |
| Writer queue full | `Busy` at once |

## Zero allocations, measured

CodeFusion.Data's allocation test runs here unchanged: 14,600 reads and writes of the Conduit queries from 4 callers
allocate 2.9 KB in the whole process, the test's own tasks, and twice as many allocate the same
([the run](../../docs/benchmarks/2026-10-21-sqlite-provider-allocation.md)). The provider's own tests measure the same
for Exclusive mode, event-loop batches and writes refused as unique violations.

What that covers, exactly: a query that runs, or ends as a unique violation, once warm. Busy and a timeout take
the unique violation's path. What still allocates:

- **The first time**: a connection prepares each statement on its first use, and the pools and queues fill up to
  what the load needs (at most 4,096 pooled commands).
- **Large commands**: more than 64 parameters or 64 columns in a row; a command buffer past 64 KB isn't pooled. Rows
  a write returns, or a read that joined an event-loop batch, are kept till the COMMIT: past 64 KB in buffers rented
  from the shared array pool, so nothing once warm, but held in memory till the batch ends.
- **Other failures**: a statement SQLite refuses for any other reason carries a `SqliteException` with its message.
- **Your code**: replies, handlers and what you do with the rows.

## Limits

One process writes the file: a second writer process works (SQLite makes it wait) but breaks up the groups. Reads and
writes are only as concurrent as SQLite is: any number of reads, one write transaction at a time.
