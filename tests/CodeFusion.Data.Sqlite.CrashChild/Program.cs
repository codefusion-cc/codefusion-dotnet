using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Sqlite;

// crash-child <mid-group|after-commit|batches> <synchronous: 1 NORMAL, 2 FULL> <path>
bool batches = args[0] == "batches";
var s = new SqliteDatabase(args[2])
{
    Exclusive = batches,
    EventLoopBatches = batches,
    Synchronous = (SqliteSync)int.Parse(args[1], CultureInfo.InvariantCulture),
    Readers = 1,
    Migrations = ["CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID"],
}.OpenSource();
var put = new SqliteStatement("INSERT INTO kv (k, v) VALUES (?1, ?2)", DataType.Text, DataType.Int64);
var many = new SqliteStatement(
    "INSERT INTO kv (k, v) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?2) SELECT ?1 || i, i FROM n", DataType.Text, DataType.Int64);

ValueTask<DataResult> Write(SqliteStatement statement, string key, long value)
{
    var c = s.Write(statement);
    c.Text(key);
    c.Int64(value);
    return Sqlite.Run<Nothing>(c.Hold(), null, default);
}

for (int i = 0; i < 100; i++)
    if (!(await Write(put, "committed-" + i, i)).IsOk) return 3;
if (batches)
{
    // an event loop that never stops: each wake-up writes, ends its batch, then answers what completed
    new Thread(() => Wakeups(s, put, many)) { IsBackground = true }.Start();
    Thread.Sleep(Timeout.Infinite);
}
else if (args[0] == "mid-group")
{
    // one write that runs for seconds: the kill lands inside its group's transaction
    _ = Write(many, "crash-", 50_000_000);
    Thread.Sleep(50);
}
else if (!(await Write(put, "marker", 1)).IsOk) return 4;
Console.Out.WriteLine("READY");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);
return 0;

// Each wake-up k: ten writes b{k}-0..9, a unit of two (pair-a-k, pair-b-k), and 20,000 rows big{k}-*, all in the
// wake-up's transaction. "OPEN k" before they run; "ANSWERED <key>" the moment each completes, as a handler that
// answers then would; "READY" after the fourth wake-up.
static void Wakeups(SqliteDataSource s, SqliteStatement put, SqliteStatement many)
{
    EventLoopThread.Enter(0);
    for (int k = 0; ; k++)
    {
        Console.Out.WriteLine($"OPEN {k}");
        Console.Out.Flush();
        for (int i = 0; i < 10; i++)
        {
            var c = s.Write(put);
            c.Text($"b{k}-{i}");
            c.Int64(k);
            Answer(Sqlite.Run<Nothing>(c.Hold(), null, default), $"b{k}-{i}");
        }
        var unit = s.Write(put);
        unit.Text($"pair-a-{k}");
        unit.Int64(k);
        unit.Then(put);
        unit.Text($"pair-b-{k}");
        unit.Int64(k);
        Answer(Sqlite.Run<Nothing>(unit.Hold(), null, default), $"pair-{k}");
        var big = s.Write(many);
        big.Text($"big{k}-");
        big.Int64(20_000);
        Answer(Sqlite.Run<Nothing>(big.Hold(), null, default), $"big{k}");
        s.EndEventLoopBatch(0);
        if (k == 3)
        {
            Console.Out.WriteLine("READY");
            Console.Out.Flush();
        }
    }
}

// A handler: its answer goes out as soon as its write completes.
#pragma warning disable CA1849, VSTHRD100
static async void Answer(ValueTask<DataResult> write, string name)
{
    if (!(await write).IsOk) return;
    Console.Out.WriteLine("ANSWERED " + name);
    Console.Out.Flush();
}
#pragma warning restore CA1849, VSTHRD100

sealed class Nothing : IRowReader
{
    public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct => 0;
}
