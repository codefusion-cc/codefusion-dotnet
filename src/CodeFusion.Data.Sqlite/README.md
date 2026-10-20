# CodeFusion.Data.Sqlite

CodeFusion.Data's SQLite provider: the same contexts and LINQ queries as on Postgres, compiled at build time to
SQLite's SQL, run in process against one database file. Nothing is allocated per query.

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
allocate 2.9 KB in the whole process, the test's own tasks, and twice as many allocate the same.

## Limits

One process writes the file: a second writer process works (SQLite makes it wait) but breaks up the groups. Reads and
writes are only as concurrent as SQLite is: any number of reads, one write transaction at a time.
