using System;
using System.Buffers.Text;
using System.Globalization;
using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;
using CodeFusion.Http;
using CodeFusion.TechEmpower;

// The six TechEmpower tests as TFB's requirements state them, on port 8080. DATABASE_URL defaults to TFB's
// database; DB_READERS and DB_WRITERS set the Postgres loops: two readers per core and one writer by default. A
// group's updates now run as one statement (#22), so more writers may pay; they haven't been measured since
// (docs/PERFORMANCE-BACKLOG.md). Before, concurrent groups over the same rows deadlocked (2026-10-21-tfb-pg-loops.md).
using var allocations = AllocationTrace.StartIfAsked();
using var gcReport = GcReport.StartIfAsked();
var builder = HttpApp.CreateBuilder(args);
builder.Options.ServerName = "CodeFusion";
string url = HttpAppBuilder.Setting("DATABASE_URL") ?? "postgres://benchmarkdbuser:benchmarkdbpass@tfb-database:5432/hello_world";
int readers = Loops("DB_READERS", 2 * Environment.ProcessorCount), writers = Loops("DB_WRITERS", 1);
builder.AddDatabase<TfbDb>(Postgres.FromUrl(url), readers, writers);
builder.AddPerConnection(static () => new Scratch());
var app = builder.Build();

app.MapGet("/plaintext", static () => Pages.HelloWorld);

// a new object per request, written by the generated serializer
app.MapGet("/json", static () => Reply.Ok(new HelloMessage(Pages.HelloWorldText)));

app.MapGet("/db", static (TfbDb db) => db.WorldById(Pages.RandomId()));

// each row its own query, all of a request's queries sent together
app.MapGet("/queries", static async (QueryCount count, TfbDb db) =>
{
    var worlds = await db.Each(count.Clamped, static (db, _) => db.WorldById(Pages.RandomId()));
    return worlds.Failed ? Reply.Failed(worlds.Error!) : Reply.Ok(worlds);
});

app.MapGet("/fortunes", static async (TfbDb db, Scratch scratch) => new FortunesPage(await db.AllFortunes(), scratch));

// each row read by its own query, then every new number written: the request's reads go together, and its writes
// commit in the same group
app.MapGet("/updates", static async (QueryCount count, TfbDb db, Scratch scratch) =>
{
    int n = count.Clamped;
    var read = await db.Each(n, static (db, _) => db.WorldById(Pages.RandomId()));
    if (read.Failed) return Reply.Failed(read.Error!);
    if (read.Count != n) return Reply.Failed(Pages.Missing);
    var worlds = scratch.Worlds;
    for (int i = 0; i < n; i++) worlds[i] = read[i] with { RandomNumber = Pages.RandomId() };
    var written = await db.Each(n, worlds, static (db, worlds, i) => db.SetRandomNumber(worlds[i].Id, worlds[i].RandomNumber));
    return written.IsOk ? Reply.Ok(new ArraySegment<WorldRow>(worlds, 0, n)) : Reply.Failed(Pages.WriteFailed);
});

app.Run();

static int Loops(string name, int fallback) =>
    HttpAppBuilder.Setting(name) is { Length: > 0 } s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;

/// <summary>TFB's JSON test object: <c>{"message":"Hello, World!"}</c>.</summary>
readonly record struct HelloMessage(Utf8 Message);

/// <summary>
/// The <c>queries</c> parameter as TFB reads it: 1 to 500, with a missing, empty or non-numeric value read as 1 and
/// anything above 500 as 500.
/// </summary>
[FromQuery]
partial struct QueryCount
{
    public Utf8? Queries;

    public readonly int Clamped
    {
        get
        {
            if (Queries is not { } q || q.IsEmpty) return 1;
            if (!Utf8Parser.TryParse(q.Span, out long n, out int used) || used != q.Length)
                // a number too long for a long is still more than 500
                return q.Span.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0 ? 500 : 1;
            return n < 1 ? 1 : n > 500 ? 500 : (int)n;
        }
    }
}
