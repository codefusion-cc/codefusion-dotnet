using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Npgsql;
using Xunit;

// The timeouts turn a read that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051, xUnit1031

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// Read groups against a real Postgres (CODEFUSION_TEST_DB), the scenarios of the contest server's Npgsql reader
/// tests: reads queued together run as one batch with one Sync, each gets its own rows, and a failure stays with its
/// own read.
/// </summary>
public sealed class PgReaderTests : IAsyncDisposable
{
    static readonly PgStatement Times10 = new("SELECT $1::bigint * 10", PgType.Int8);
    static readonly PgStatement TxId = new("SELECT txid_current()");
    static readonly PgStatement Divide = new("SELECT 10 / $1::bigint", PgType.Int8);
    static readonly PgStatement FailsOnRow3 = new("SELECT 10 / (3 - g) FROM generate_series(1, 5) g");
    static readonly PgStatement Seven = new("SELECT 7");
    static readonly PgStatement OneOverZero = new("SELECT 1 / 0");
    static readonly PgStatement Pid = new("SELECT pg_backend_pid()");
    static readonly PgStatement Slow = new("SELECT 1 FROM pg_sleep(5)");
    static readonly PgStatement One = new("SELECT 1");
    static readonly PgStatement PlusOne = new("SELECT $1::bigint + 1", PgType.Int8);

    NpgsqlDataSource? _admin;
    PgReader? _reader;

    /// <summary>A reader whose loops are not started yet, so reads queued now share its first group.</summary>
    PgReader NewReader()
    {
        var settings = PgTestDb.Settings();
        _admin = PgTestDb.Admin();
        return _reader = new PgReader(settings);
    }

    static readonly PgRowHandler First = static (in PgRow r, object? _) => r.Index == 0 ? r.GetInt64(0) : throw new InvalidOperationException("a second row");

    static async Task<long> Read(PgReader reader, PgStatement s, params long[] args)
    {
        var cmd = reader.Command(s);
        foreach (long a in args) cmd.Int8(a);
        var res = (await cmd.Run(First, null)).ThrowIfFailed();
        Assert.Equal(1, res.Rows);
        return res.Value;
    }

    static Task<PgResult> Try(PgReader reader, PgStatement s, params long[] args)
    {
        var cmd = reader.Command(s);
        foreach (long a in args) cmd.Int8(a);
        return cmd.Run(First, null).AsTask();
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadsQueuedTogetherRunInOneTransactionAndEachGetsItsOwnRow()
    {
        var reader = NewReader();
        var values = Enumerable.Range(0, 10).Select(i => Read(reader, Times10, i)).ToArray();
        // statements of one implicit transaction (one Sync) see the same transaction id
        var txids = Enumerable.Range(0, 5).Select(_ => Read(reader, TxId)).ToArray();
        reader.Start(1);

        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 10L), await Task.WhenAll(values));
        Assert.Single((await Task.WhenAll(txids)).Distinct());
        Assert.Equal(1, reader.Counters.Groups);
        Assert.Equal(15, reader.Counters.GroupMax);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailingReadFailsAloneAndTheReadsAfterItRunAgain()
    {
        var reader = NewReader();
        var tasks = new[] { 1L, 0, 2, 5 }.Select(d => Try(reader, Divide, d)).ToArray();
        reader.Start(1);

        Assert.Equal(10, (await tasks[0]).Value);
        var e = await tasks[1];
        Assert.Equal(PgStatus.ServerError, e.Status);
        Assert.Equal(PgErrorCodes.DivisionByZero, e.SqlState);
        Assert.Contains("division by zero", e.Error!.MessageText);
        Assert.Equal(5, (await tasks[2]).Value);
        Assert.Equal(2, (await tasks[3]).Value);
        Assert.Equal(1, reader.Counters.Reruns);
    }

    static readonly PgStatement Elements = new("SELECT array_to_string($1::text[], ',', '-'), cardinality($1::text[])", PgType.TextArray);

    [Fact(Timeout = 30_000)]
    public async Task AnElementRefusedMidArrayNeverSendsAMiscountedArray()
    {
        var r = NewReader();
        r.Start(1);
        var box = new PgTestDb.Box();
        // the command either refuses to go on or sends exactly the elements that went in, never one counted for the
        // refused element: Postgres would read past the array
        if (BuildWithARefusedElement(r, box) is { } run)
        {
            var res = await run;
            Assert.True(res.IsOk, res.ToString());
            Assert.Equal("a,b|2", box.Value);
        }
    }

    static Task<PgResult>? BuildWithARefusedElement(PgReader r, PgTestDb.Box box)
    {
        var cmd = r.Command(Elements).BeginTextArray().Element("a".AsSpan());
        bool refused;
        try
        {
            cmd.Element("\ud800".AsSpan());
            refused = false;
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        Assert.True(refused);
        try
        {
            return cmd.Element("b".AsSpan()).EndTextArray().Run(static (in PgRow row, object? s) =>
            {
                ((PgTestDb.Box)s!).Value = row.Text(0) + "|" + row.GetInt64(1);
                return 0;
            }, box).AsTask();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadsAfterAFailingOneAreParsedAgainIfTheirParseWasSkipped()
    {
        // each statement is new to the connection, so its Parse is in the group; the error makes Postgres skip them
        var reader = NewReader();
        var a = new PgStatement("SELECT 1 / 0 + $1::bigint", PgType.Int8);
        var b = new PgStatement("SELECT 2 + $1::bigint", PgType.Int8);
        var c = new PgStatement("SELECT 3 + $1::bigint", PgType.Int8);
        var tasks = new[] { Try(reader, a, 0), Try(reader, b, 0), Try(reader, c, 0) };
        reader.Start(1);
        Assert.Equal(PgErrorCodes.DivisionByZero, (await tasks[0]).SqlState);
        Assert.Equal(2, (await tasks[1]).Value);
        Assert.Equal(3, (await tasks[2]).Value);
        Assert.Equal(3, await Read(reader, c, 0)); // and still prepared afterwards
    }

    [Fact(Timeout = 30_000)]
    public async Task AStatementWhoseParseFailedIsParsedAgainOnceItCan()
    {
        var reader = NewReader();
        string table = "pg_reader_" + Guid.NewGuid().ToString("N");
        var later = new PgStatement($"SELECT count(*) FROM {table}");
        reader.Start(1);
        Assert.Equal("42P01", (await Try(reader, later)).SqlState); // no such table: the Parse fails
        await _admin!.Scalar($"CREATE TABLE {table} (x int); INSERT INTO {table} VALUES (1)");
        try
        {
            Assert.Equal(1, await Read(reader, later));
        }
        finally
        {
            await _admin!.Scalar($"DROP TABLE {table}");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AStatementFailingAfterTheRowItsReadTookFailsThatReadAndNoOther()
    {
        var reader = NewReader();
        // rows 1 and 2 go out before row 3 divides by zero
        var failing = reader.Command(FailsOnRow3).Run(static (in PgRow r, object? _) => r.GetInt64(0), null).AsTask();
        var next = Read(reader, Seven);
        var last = reader.Command(FailsOnRow3).Run(static (in PgRow r, object? _) => r.GetInt64(0), null).AsTask(); // its error comes at the batch's end
        reader.Start(1);

        var f = await failing;
        Assert.Equal((PgStatus.ServerError, PgErrorCodes.DivisionByZero), (f.Status, f.SqlState));
        Assert.Equal(2, f.Rows); // the handler saw them; the read fails all the same
        Assert.Equal(7, await next);
        Assert.Equal(PgErrorCodes.DivisionByZero, (await last).SqlState);
        long pid = await Read(reader, Pid);
        Assert.Equal(pid, await Read(reader, Pid)); // the loop kept its connection
    }

    [Fact(Timeout = 30_000)]
    public async Task AReadWhoseClientLeftIsDroppedWithoutRunning()
    {
        var reader = NewReader();
        // run, these would fail with a division by zero
        var gone = reader.Command(OneOverZero).Run(First, null, new CancellationToken(canceled: true));
        using var leaves = new CancellationTokenSource();
        var leaving = reader.Command(OneOverZero).Run(First, null, leaves.Token).AsTask();
        var staying = Read(reader, Seven);
        leaves.Cancel(); // after it queued, before a loop takes it
        reader.Start(1);

        Assert.Equal(PgStatus.Canceled, (await gone).Status);
        Assert.Equal(PgStatus.Canceled, (await leaving).Status);
        Assert.Equal(7, await staying);
        Assert.Equal(0, reader.Queued);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABugInOneReadsHandlerFailsOnlyThatRead()
    {
        var reader = NewReader();
        var before = Read(reader, One);
        var buggy = reader.Command(Seven).Run(static (in PgRow _, object? _) => throw new InvalidOperationException("bug"), null).AsTask();
        var after = Read(reader, One);
        reader.Start(1);

        Assert.Equal(1, await before);
        var b = await buggy;
        Assert.Equal(PgStatus.HandlerFailed, b.Status);
        Assert.IsType<InvalidOperationException>(b.Exception);
        Assert.Throws<InvalidOperationException>(() => b.ThrowIfFailed());
        Assert.Equal(1, await after);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABrokenConnectionFailsItsReadsAndTheNextGroupReconnects()
    {
        var reader = NewReader();
        reader.Start(1);
        long pid = await Read(reader, Pid);
        var slow = Try(reader, Slow);
        await Task.Delay(300);
        await _admin!.Scalar($"SELECT pg_terminate_backend({pid})");

        var s = await slow;
        Assert.Equal(PgStatus.Broken, s.Status);
        Assert.Equal(PgErrorCodes.AdminShutdown, Assert.IsType<PgException>(s.Exception!.InnerException).SqlState);
        Assert.NotEqual(pid, await Read(reader, Pid));
    }

    [Fact(Timeout = 30_000)]
    public async Task AConnectionPostgresEndedWhileIdleIsReplacedAndTheNextReadSucceeds()
    {
        _admin = PgTestDb.Admin();
        var reader = _reader = new PgReader(PgTestDb.Settings().Set("idle_session_timeout", "100ms"));
        reader.Start(1);
        long pid = await Read(reader, Pid);
        await Task.Delay(400); // Postgres ends the session (FATAL 57P05) and closes the socket
        await PgTestDb.Asleep(reader);
        Assert.NotEqual(pid, await Read(reader, Pid));

        LoopChecks.Stop(reader);

        // a session that would idle on (a reader without the timeout, so no timer races the steps below): an
        // administrator ends it (FATAL 57P01), and the backend is gone before the next read, which goes to a new one
        reader = _reader = new PgReader(PgTestDb.Settings());
        reader.Start(1);
        long second = await Read(reader, Pid);
        await PgTestDb.Asleep(reader);
        Assert.Equal(true, await _admin.Scalar($"SELECT pg_terminate_backend({second}, 10000)"));
        long third = await Read(reader, Pid);
        Assert.NotEqual(second, third);
        Assert.Equal(7, await Read(reader, Seven));
    }

    [Fact(Timeout = 30_000)]
    public async Task AStatementThatChangesTheClientEncodingEndsItsConnectionAndTheNextReadIsUtf8Again()
    {
        var reader = NewReader();
        reader.Start(1);
        long pid = await Read(reader, Pid);
        // the SET itself ran; Postgres reports the new encoding before ReadyForQuery, and the client hangs up
        await Try(reader, new PgStatement("SET client_encoding TO 'LATIN1'"));
        var box = new PgTestDb.Box();
        var res = await reader.Command(new PgStatement("SELECT 'ä€'::text")).Run(static (in PgRow r, object? b) => { ((PgTestDb.Box)b!).Value = r.Text(0); return 0; }, box);
        Assert.True(res.IsOk, res.ToString());
        Assert.Equal("ä€", box.Value);
        Assert.NotEqual(pid, await Read(reader, Pid));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("DEALLOCATE ALL")]
    [InlineData("DISCARD ALL")]
    [InlineData("DEALLOCATE {0}")]
    [InlineData("/* its own */ deallocate prepare \"{0}\"")]
    public async Task AStatementThatDropsTheConnectionsPreparedStatementsEndsItAndTheNextReadParsesAgain(string sql)
    {
        var reader = NewReader();
        reader.Start(1);
        long pid = await Read(reader, Pid);
        Assert.Equal(7, await Read(reader, Seven));
        // the statements the connection parsed are gone with it, not bound later by a name the server forgot
        var dropped = await Try(reader, new PgStatement(string.Format(System.Globalization.CultureInfo.InvariantCulture, sql, Seven.ToString().Split(':')[0])));
        Assert.Equal(PgStatus.Broken, dropped.Status);
        Assert.Equal(7, await Read(reader, Seven));
        Assert.NotEqual(pid, await Read(reader, Pid));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("myplan", "DEALLOCATE myplan")]
    [InlineData("myplan", "deallocate prepare MyPlan")]
    [InlineData("\"Plan\"", "DEALLOCATE \"Plan\"")]
    [InlineData("s999999", "DEALLOCATE s999999")] // the client's form, but no statement it prepared here
    public async Task ADeallocateOfTheAppsOwnPreparedStatementKeepsTheConnection(string name, string sql)
    {
        var reader = NewReader();
        reader.Start(1);
        long pid = await Read(reader, Pid);
        Assert.Equal(7, await Read(reader, Seven));
        Assert.True((await Try(reader, new PgStatement($"PREPARE {name} AS SELECT 1"))).IsOk);
        var res = await Try(reader, new PgStatement(sql));
        Assert.True(res.IsOk, res.ToString());
        Assert.Equal(7, await Read(reader, Seven));
        Assert.Equal(pid, await Read(reader, Pid));
    }

    [Fact(Timeout = 60_000)]
    public async Task ReadsPastTheQueueLimitAreRefusedAtOnce()
    {
        var reader = NewReader();
        var queued = Enumerable.Range(0, PgReader.MaxQueued).Select(_ => Try(reader, One)).ToArray();
        var refused = reader.Command(One).Run(First, null);

        Assert.True(refused.IsCompletedSuccessfully, "refused without waiting for a loop");
        Assert.Equal(PgStatus.Busy, refused.Result.Status);
        Assert.Equal(1, reader.Counters.Refused);
        Assert.Throws<PgBusyException>(() => refused.Result.ThrowIfFailed());
        Assert.Equal(PgReader.MaxQueued, reader.Queued);
        reader.Start(2);
        Assert.All(await Task.WhenAll(queued), v => Assert.Equal(1, v.Value));
        Assert.Equal(1, await Read(reader, One));
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentReadsFromManyRequestsEachGetTheirOwnAnswer()
    {
        var reader = NewReader();
        reader.Start(4);
        var results = await Task.WhenAll(Enumerable.Range(0, 2000).Select(i => Task.Run(() => Read(reader, PlusOne, i))));
        Assert.Equal(Enumerable.Range(1, 2000).Select(i => (long)i), results);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheConnectionCarriesTheSessionSettings()
    {
        var reader = NewReader();
        reader.Start(1);
        var show = new PgStatement("SELECT current_setting('plan_cache_mode') || ' ' || current_setting('jit') || ' ' || " +
            "current_setting('max_parallel_workers_per_gather') || ' ' || current_setting('statement_timeout') || ' ' || current_setting('client_encoding')");
        var box = new PgTestDb.Box();
        (await reader.Command(show).Run(static (in PgRow r, object? b) => { ((PgTestDb.Box)b!).Value = r.Text(0); return 0; }, box)).ThrowIfFailed();
        Assert.Equal("force_generic_plan off 0 30s UTF8", box.Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task APageOfRowsComesWithItsCountAndNullsAndTypesReadRight()
    {
        var reader = NewReader();
        reader.Start(1);
        var page = new PgStatement(
            "SELECT g, g % 2 = 0, CASE WHEN g % 3 = 0 THEN NULL ELSE 'n' || g END, convert_to('[\"t' || g || '\"]', 'UTF8'), " +
            "timestamptz '2000-01-01 00:00:00.123456+00' + g * interval '1 day', count(*) OVER () FROM generate_series(1, $1::int8) g ORDER BY g",
            PgType.Int8);
        var rows = new System.Collections.Generic.List<string>();
        var res = await reader.Command(page).Int8(20).Run(static (in PgRow r, object? s) =>
        {
            ((System.Collections.Generic.List<string>)s!).Add($"{r.Index}:{r.GetInt64(0)} {r.GetBoolean(1)} {(r.IsNull(2) ? "null" : r.Text(2))} {r.Text(3)} " +
                $"{r.GetDateTime(4):O} {r.GetUnixMicroseconds(4)} {r.FieldCount}");
            return r.GetInt64(5);
        }, rows);
        Assert.Equal(PgStatus.Ok, res.Status);
        Assert.Equal(20, res.Rows);
        Assert.Equal(20, res.Value); // the count from the last row
        Assert.Equal("0:1 False n1 [\"t1\"] 2000-01-02T00:00:00.1234560Z 946771200123456 6", rows[0]);
        Assert.Equal("2:3 False null [\"t3\"] 2000-01-04T00:00:00.1234560Z 946944000123456 6", rows[2]);
        Assert.Equal(20, rows.Count);

        var none = await reader.Command(page).Int8(0).Run(static (in PgRow _, object? _) => throw new InvalidOperationException(), null);
        Assert.Equal((PgStatus.Ok, 0, 0L), (none.Status, none.Rows, none.Value));
    }

    [Fact(Timeout = 30_000)]
    public async Task InfinityAndOldTimestampsReadAsTheirMicrosecondsAndDateTimeRefusesThem()
    {
        var reader = NewReader();
        reader.Start(1);
        var s = new PgStatement("SELECT 'infinity'::timestamptz, '-infinity'::timestamptz, '1970-01-01 00:00:00+00'::timestamptz");
        var box = new PgTestDb.Box();
        var res = await reader.Command(s).Run(static (in PgRow r, object? b) =>
        {
            ((PgTestDb.Box)b!).Value = $"{r.GetUnixMicroseconds(0) == long.MaxValue} {r.GetUnixMicroseconds(1) == long.MinValue} {r.GetUnixMicroseconds(2)}";
            return r.GetDateTime(0).Ticks;
        }, box);
        Assert.Equal("True True 0", box.Value);
        Assert.Equal(PgStatus.HandlerFailed, res.Status);
        Assert.IsType<InvalidCastException>(res.Exception);
    }

    /// <summary>What <paramref name="get"/> reads from the one column <paramref name="sql"/> selects: the value as
    /// text, or the exception's type.</summary>
    static async Task<string> ReadAs(PgReader reader, string sql, Func<PgRow, string> get)
    {
        var box = new PgTestDb.Box();
        var res = await reader.Command(new PgStatement(sql)).Run(static (in PgRow r, object? b) =>
        {
            var (box, get) = ((PgTestDb.Box, Func<PgRow, string>))b!;
            box.Value = get(r);
            return 0;
        }, (box, get));
        return res.IsOk ? box.Value! : res.Exception!.GetType().Name;
    }

    static string Invariant(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    [Theory(Timeout = 30_000)]
    [InlineData("SELECT 7::int2", "int", "7")]
    [InlineData("SELECT 7::int4", "int", "7")]
    [InlineData("SELECT 7::int8", "int", "7")]
    [InlineData("SELECT 1.5::float4", "int", "InvalidCastException")]          // not 1069547520, its bits
    [InlineData("SELECT 2.5::float8", "int", "InvalidCastException")]
    [InlineData("SELECT 'abcdefgh'::text", "int", "InvalidCastException")]    // eight bytes, not an int8
    [InlineData("SELECT now()", "int", "InvalidCastException")]
    [InlineData("SELECT 1.5::numeric", "int", "InvalidCastException")]
    [InlineData("SELECT 1.5::float4", "double", "1.5")]
    [InlineData("SELECT 2.5::float8", "double", "2.5")]
    [InlineData("SELECT 7::int2", "double", "7")]                             // widened: every int2 and int4 is a double
    [InlineData("SELECT 2147483647::int4", "double", "2147483647")]
    [InlineData("SELECT 1::int8", "double", "InvalidCastException")]          // not 5E-324: past 2^53 an int8 isn't one
    [InlineData("SELECT 1.5::numeric", "double", "InvalidCastException")]
    [InlineData("SELECT true", "bool", "True")]
    [InlineData("SELECT 1::int2", "bool", "InvalidCastException")]
    [InlineData("SELECT '\\x01'::bytea", "bool", "InvalidCastException")]
    [InlineData("SELECT '2000-01-01 00:00:01+00'::timestamptz", "timestamp", "946684801000000")]
    [InlineData("SELECT '2000-01-01 00:00:01'::timestamp", "timestamp", "946684801000000")]
    [InlineData("SELECT 1::int8", "timestamp", "InvalidCastException")]
    [InlineData("SELECT 1.5::float8", "timestamp", "InvalidCastException")]
    [InlineData("SELECT '{\"a\":1}'::jsonb", "json", "{\"a\": 1}")]
    [InlineData("SELECT '{\"a\":1}'::json", "json", "{\"a\":1}")]
    [InlineData("SELECT chr(1) || '[2]'", "json", "\u0001[2]")]            // text: as it is, never cut as if it were jsonb
    [InlineData("SELECT convert_to('[1]', 'UTF8')", "json", "[1]")]               // bytea holding JSON, as the generated statements build it
    [InlineData("SELECT 1::int8", "json", "InvalidCastException")]
    [InlineData("SELECT 1::int8", "bytes", "8")]                              // the raw bytes of any type
    public async Task AGetterReadsOnlyTheTypesItCanReadExactlyAndRefusesTheRest(string sql, string getter, string expected)
    {
        var reader = NewReader();
        reader.Start(1);
        Func<PgRow, string> get = getter switch
        {
            "int" => static r => r.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "double" => static r => Invariant(r.GetDouble(0)),
            "bool" => static r => r.GetBoolean(0).ToString(),
            "timestamp" => static r => r.GetUnixMicroseconds(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "json" => static r => Encoding.UTF8.GetString(r.GetJson(0)),
            _ => static r => r.GetBytes(0).Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        Assert.Equal(expected, await ReadAs(reader, sql, get));
    }

    static readonly PgStatement WhatTimestamp = new("SELECT CASE WHEN isfinite($1) THEN to_char($1 AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US BC') "
        + "ELSE $1::text END, isfinite($1)", PgType.Timestamptz);

    /// <summary>What Postgres made of <paramref name="unixMicroseconds"/> bound as a timestamptz.</summary>
    static async Task<(PgResult Result, string? Text)> Bound(PgReader reader, long unixMicroseconds)
    {
        var box = new PgTestDb.Box();
        var res = await reader.Command(WhatTimestamp).Timestamptz(unixMicroseconds).Run(static (in PgRow r, object? b) =>
        {
            ((PgTestDb.Box)b!).Value = $"{r.Text(0)} {r.GetBoolean(1)}";
            return 0;
        }, box);
        return (res, box.Value);
    }

    const long UnixToPg = 946684800000000;

    [Fact(Timeout = 30_000)]
    public async Task InfinityIsWrittenAsInfinityAndAnInstantPastPostgresRangeIsRefusedByIt()
    {
        var reader = NewReader();
        reader.Start(1);
        Assert.Equal("infinity False", (await Bound(reader, long.MaxValue)).Text);
        Assert.Equal("-infinity False", (await Bound(reader, long.MinValue)).Text);
        Assert.Equal("1970-01-01 00:00:00.000000 AD True", (await Bound(reader, 0)).Text);
        // the last microsecond a long holds since 1970, and Postgres's first instant
        Assert.Equal("294247-01-10 04:00:54.775806 AD True", (await Bound(reader, long.MaxValue - 1)).Text);
        Assert.Equal("4714-11-24 00:00:00.000000 BC True", (await Bound(reader, -210866803200000000)).Text);
        // before it, down to where the offset to 2000 would reach -infinity or wrap
        foreach (long outside in new[] { -210866803200000001, long.MinValue + UnixToPg + 1, long.MinValue + UnixToPg, long.MinValue + UnixToPg - 1, long.MinValue + 1 })
        {
            var (res, _) = await Bound(reader, outside);
            Assert.Equal(PgStatus.ServerError, res.Status);
            Assert.Equal("22008", res.SqlState); // timestamp out of range
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TheYearsPastWhatALongOfMicrosecondsSince1970HoldsAreRefusedInsteadOfWrapping()
    {
        var reader = NewReader();
        reader.Start(1);
        var s = new PgStatement("SELECT '294247-01-10 04:00:54.775806+00'::timestamptz, '294247-01-10 04:00:54.775807+00'::timestamptz, "
            + "'294276-12-31 23:59:59.999999+00'::timestamptz");
        var box = new PgTestDb.Box();
        var res = await reader.Command(s).Run(static (in PgRow r, object? b) =>
        {
            var box = (PgTestDb.Box)b!;
            box.Value = $"{r.GetUnixMicroseconds(0)} {r.GetPgMicroseconds(1)} {r.GetPgMicroseconds(2)}";
            foreach (int c in new[] { 1, 2 })
                try
                {
                    r.GetUnixMicroseconds(c); // the first is long.MaxValue, +infinity's; the second wraps
                }
                catch (InvalidCastException)
                {
                    box.Calls++;
                }
            return 0;
        }, box);
        Assert.True(res.IsOk, res.ToString());
        Assert.Equal($"{long.MaxValue - 1} {long.MaxValue - UnixToPg} 9223371331199999999", box.Value);
        Assert.Equal(2, box.Calls);
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null) LoopChecks.Stop(_reader);
        if (_admin is not null) await _admin.DisposeAsync();
    }
}
