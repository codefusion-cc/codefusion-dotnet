using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data;
using CodeFusion.Data.Sqlite;

// CodeFusion.Data.Sqlite.Throughput <default|exclusive> <normal|full> <seconds per case>
// One line of results: point reads on 1 and 4 threads, 64 writers and 1, and 16 callers doing 9 reads per write.
if (args is not [var mode and ("default" or "exclusive"), var sync and ("normal" or "full"), var time])
{
    Console.Error.WriteLine("usage: <default|exclusive> <normal|full> <seconds per case>");
    return 2;
}
double seconds = double.Parse(time, CultureInfo.InvariantCulture);
string dir = Path.Combine(Path.GetTempPath(), "cf-sqlite-throughput-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
Directory.CreateDirectory(dir);
var source = new SqliteDatabase(Path.Combine(dir, "throughput.db"))
{
    Synchronous = sync == "full" ? SqliteSync.Full : SqliteSync.Normal,
    Exclusive = mode == "exclusive",
    Migrations =
    [
        "CREATE TABLE kv (k INTEGER PRIMARY KEY, v INTEGER NOT NULL, s TEXT NOT NULL)",
        "INSERT INTO kv WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100000) SELECT i, i, 'some text of a row ' || i FROM n",
    ],
}.OpenSource();
try
{
    var bench = new Bench(source, seconds);
    bench.Reads(1);
    await bench.Writes(8); // warm
    double reads1 = bench.Reads(1), reads4 = bench.Reads(4), writes64 = await bench.Writes(64), writes1 = await bench.Writes(1), mixed = await bench.Mixed(16);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{mode,-9} {sync,-6} reads 1 thread {reads1 / 1e6,5:F2} M/s | 4 threads {reads4 / 1e6,5:F2} M/s | writes 64 {writes64 / 1e3,6:F1} k/s | 1 {writes1 / 1e3,5:F1} k/s | 9 reads : 1 write x16 {mixed / 1e3,6:F1} k ops/s"));
    return 0;
}
finally
{
    source.Stop();
    Directory.Delete(dir, recursive: true);
}

sealed class Bench(SqliteDataSource source, double seconds)
{
    static readonly SqliteStatement Get = new("SELECT v, s FROM kv WHERE k = ?1", DataType.Int64);
    static readonly SqliteStatement Bump = new("UPDATE kv SET v = v + 1 WHERE k = ?1", DataType.Int64);

    long Until() => Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);

    void Read(int k)
    {
        var c = source.Read(Get);
        c.Int64(k);
        var r = Sqlite.Run<Row>(c.Hold(), null, default);
        if (!r.IsCompleted || r.Result.Rows != 1) throw new InvalidOperationException("a read failed");
    }

    async Task Write(int k)
    {
        var c = source.Write(Bump);
        c.Int64(k);
        if (!(await Sqlite.Run<Row>(c.Hold(), null, default).ConfigureAwait(false)).IsOk) throw new InvalidOperationException("a write failed");
    }

    /// <summary>Point reads by key from threads of their own, per second.</summary>
    public double Reads(int threads)
    {
        long total = 0, until = Until();
        var all = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var random = new Random(t);
            long n = 0;
            while (Stopwatch.GetTimestamp() < until)
            {
                for (int i = 0; i < 256; i++) Read(random.Next(1, 100_001));
                n += 256;
            }
            Interlocked.Add(ref total, n);
        })).ToList();
        all.ForEach(t => t.Start());
        all.ForEach(t => t.Join());
        return total / seconds;
    }

    /// <summary>Updates by key from loops that each await their write, per second.</summary>
    public async Task<double> Writes(int loops)
    {
        long total = 0, until = Until();
        await Task.WhenAll(Enumerable.Range(0, loops).Select(l => Task.Run(async () =>
        {
            var random = new Random(l);
            long n = 0;
            for (; Stopwatch.GetTimestamp() < until; n++) await Write(random.Next(1, 100_001)).ConfigureAwait(false);
            Interlocked.Add(ref total, n);
        }))).ConfigureAwait(false);
        return total / seconds;
    }

    /// <summary>Loops of 9 reads and an awaited write, operations per second.</summary>
    public async Task<double> Mixed(int loops)
    {
        long total = 0, until = Until();
        await Task.WhenAll(Enumerable.Range(0, loops).Select(l => Task.Run(async () =>
        {
            var random = new Random(l);
            long n = 0;
            for (; Stopwatch.GetTimestamp() < until; n += 10)
            {
                for (int i = 0; i < 9; i++) Read(random.Next(1, 100_001));
                await Write(random.Next(1, 100_001)).ConfigureAwait(false);
            }
            Interlocked.Add(ref total, n);
        }))).ConfigureAwait(false);
        return total / seconds;
    }

    sealed class Row : IRowReader
    {
        public static long Read<TRow>(scoped in TRow row, object? state) where TRow : IDataRow, allows ref struct => row.GetInt64(0) + row.GetBytes(1).Length;
    }
}
