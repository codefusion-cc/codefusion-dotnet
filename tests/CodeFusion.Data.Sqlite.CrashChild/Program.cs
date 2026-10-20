using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data;
using CodeFusion.Data.Sqlite;

// crash-child <mid-group|after-commit> <synchronous: 1 NORMAL, 2 FULL> <path>
var s = new SqliteDatabase(args[2])
{
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
if (args[0] == "mid-group")
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

sealed class Nothing : IRowReader
{
    public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct => 0;
}
