using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;

// The timeouts turn a command that never completes into a failure instead of a hung run.
#pragma warning disable xUnit1069, xUnit1051, xUnit1031

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// The wire protocol against a scripted server: batching, framing at every split, errors at every position,
/// asynchronous messages anywhere, malformed and truncated replies, drops and restarts.
/// </summary>
public sealed class PgProtocolTests : IDisposable
{
    /// <summary>Distinct statements, so each one's Parse can be told apart.</summary>
    static readonly PgStatement Echo = new("echo", PgType.Int8), Echo2 = new("echo 2", PgType.Int8), Echo3 = new("echo 3", PgType.Int8);

    readonly List<PgLoops> _started = [];

    /// <summary>Rows the handler saw, copied out of the receive buffer.</summary>
    sealed class Seen
    {
        public readonly List<byte[]?[]> Rows = [];
    }

    static readonly PgRowHandler Collect = static (in PgRow r, object? s) =>
    {
        var cols = new byte[]?[r.FieldCount];
        for (int c = 0; c < cols.Length; c++) cols[c] = r.IsNull(c) ? null : r.GetBytes(c).ToArray();
        ((Seen)s!).Rows.Add(cols);
        return r.FieldCount > 0 && !r.IsNull(0) && r.GetBytes(0).Length == 8 ? r.GetInt64(0) : 0;
    };

    /// <summary>Echo's rows: the parameter back as int8, then a NULL, then the parameter as text (one row per unit of it, up to 3).</summary>
    static IEnumerable<byte[]?[]> EchoRows(Bind b)
    {
        long v = b.Int8(0);
        for (int i = 0; i < Math.Clamp(v, 1, 3); i++) yield return [ServerWriter.BE64(v), null, Encoding.UTF8.GetBytes($"v{v} ż {i}")];
    }

    PgReader Reader(FakeServer server)
    {
        var r = new PgReader(server.Settings());
        _started.Add(r);
        return r;
    }

    PgWriter Writer(FakeServer server)
    {
        var w = new PgWriter(server.Settings());
        _started.Add(w);
        return w;
    }

    public void Dispose()
    {
        LoopChecks.Stop(_started);
    }

    static Task<PgResult> Read(PgReader r, long v, Seen? seen = null, PgStatement? s = null) =>
        r.Command(s ?? Echo).Int8(v).Run(Collect, seen ?? new Seen()).AsTask();

    [Fact(Timeout = 20_000)]
    public async Task AGroupGoesOutAsOneSendWithOneSyncAndEachStatementIsParsedOncePerConnection()
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        var tasks = Enumerable.Range(1, 10).Select(i => Read(r, i)).ToArray();
        r.Start(1);
        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), results.Select(x => x.Value));

        var c = Assert.Single(server.Connections);
        var batch = Assert.Single(c.Batches);
        Assert.Equal(1, batch.Count(m => m.Type == 'P'));
        Assert.Equal(10, batch.Count(m => m.Type == 'B'));
        Assert.Equal(10, batch.Count(m => m.Type == 'E'));
        int sendsBefore = c.Sends;

        Assert.Equal(11, (await Read(r, 11)).Value);
        Assert.Equal(2, c.Batches.Count);
        Assert.DoesNotContain(c.Batches[1], m => m.Type == 'P'); // parsed once on this connection
        Assert.Equal(sendsBefore + 1, c.Sends);
    }

    [Fact(Timeout = 20_000)]
    public async Task TheStartupCarriesTheSessionSettings()
    {
        var server = new FakeServer { Rows = EchoRows };
        var settings = server.Settings().Set("search_path", "s1").Set("statement_timeout", "30s").Set("search_path", "s2");
        var r = new PgReader(settings);
        _started.Add(r);
        r.Start(1);
        Assert.True((await r.Command(Echo).Int8(1).Run(null, null)).IsOk);
        var p = Assert.Single(server.Connections).StartupParameters;
        Assert.Equal("u", p["user"]);
        Assert.Equal("d", p["database"]);
        Assert.Equal("UTF8", p["client_encoding"]);
        Assert.Equal("s2", p["search_path"]);
        Assert.Equal("30s", p["statement_timeout"]);
    }

    /// <summary>A group of three reads, each with its rows, answered with the reply cut by <paramref name="cut"/>.</summary>
    static async Task<(PgResult[] Results, Seen[] Seen)> GroupWithReply(Func<byte[], IEnumerable<byte[]>> cut, int chunk = int.MaxValue,
        Func<List<byte[]>, List<byte[]>>? messages = null)
    {
        var server = new FakeServer { Rows = EchoRows, Chunk = chunk };
        server.Respond = (c, batch) =>
        {
            // the Auto reply, message by message, so a test can rearrange or add to it
            var auto = new FakeServer { Rows = EchoRows };
            var probe = new FakeConnection(auto);
            foreach (string p in c.Prepared) probe.Prepared.Add(p);
            auto.Auto(probe, batch);
            foreach (string p in probe.Prepared) c.Prepared.Add(p);
            var all = new byte[probe.Available];
            probe.Receive(all);
            var list = Split(all);
            if (messages is not null) list = messages(list);
            foreach (var piece in cut([.. list.SelectMany(m => m)])) c.Write(piece);
        };
        var r = new PgReader(server.Settings());
        try
        {
            var seen = new[] { new Seen(), new Seen(), new Seen() };
            var tasks = new[] { Read(r, 1, seen[0], Echo), Read(r, 2, seen[1], Echo2), Read(r, 3, seen[2], Echo3) };
            r.Start(1);
            return (await Task.WhenAll(tasks), seen);
        }
        finally
        {
            LoopChecks.Stop(r);
        }
    }

    /// <summary>Server bytes as whole messages.</summary>
    static List<byte[]> Split(byte[] all)
    {
        var list = new List<byte[]>();
        for (int p = 0; p < all.Length;)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(all.AsSpan(p + 1));
            list.Add(all[p..(p + 1 + len)]);
            p += 1 + len;
        }
        return list;
    }

    static void AssertEchoed(PgResult[] results, Seen[] seen)
    {
        for (int i = 0; i < 3; i++)
        {
            long v = i + 1;
            Assert.Equal(PgStatus.Ok, results[i].Status);
            Assert.Equal(v, results[i].Value);
            Assert.Equal((int)v, results[i].Rows);
            Assert.Equal(Enumerable.Range(0, (int)v).Select(k => $"v{v} ż {k}"), seen[i].Rows.Select(row => Encoding.UTF8.GetString(row[2]!)));
            Assert.All(seen[i].Rows, row => Assert.Null(row[1]));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AReplySplitAtEveryPositionGivesTheSameAnswers()
    {
        var (whole, wholeSeen) = await GroupWithReply(b => [b]);
        AssertEchoed(whole, wholeSeen);
        int length = 0;
        await GroupWithReply(b => { length = b.Length; return [b]; });
        for (int at = 1; at < length; at++)
        {
            int k = at;
            var (results, seen) = await GroupWithReply(b => [b[..k], b[k..]]);
            AssertEchoed(results, seen);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task EveryMessageFedOneByteAtATimeGivesTheSameAnswers()
    {
        // the startup and authentication too
        var (results, seen) = await GroupWithReply(b => [b], chunk: 1);
        AssertEchoed(results, seen);
    }

    [Fact(Timeout = 60_000)]
    public async Task NoticesAndParameterStatusAnywhereChangeNothing()
    {
        int count = 0;
        await GroupWithReply(b => [b], messages: m => { count = m.Count; return m; });
        for (int at = 0; at <= count; at++)
        {
            int k = at;
            var (results, seen) = await GroupWithReply(b => [b], messages: m =>
            {
                m.Insert(k, new ServerWriter().Notice("hello").Parameter("application_name", "x").Message('A', [0, 0, 0, 1, (byte)'c', 0, 0]).ToArray());
                return m;
            });
            AssertEchoed(results, seen);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AnErrorAtEveryPositionFailsOnlyTheStatementItHitsAndTheRestRunAgain()
    {
        // the first batch's reply: per read ParseComplete, ParameterDescription, RowDescription, BindComplete, its
        // rows, CommandComplete; then ReadyForQuery
        int[] rows = [1, 2, 3];
        var regions = new List<int>(); // for each message before ReadyForQuery, the read it belongs to
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 5 + rows[i]; k++) regions.Add(i);

        for (int at = 0; at <= regions.Count; at++)
        {
            int k = at, batches = 0;
            var server = new FakeServer { Rows = EchoRows };
            server.Respond = (c, batch) =>
            {
                if (batches++ > 0)
                {
                    server.Auto(c, batch);
                    return;
                }
                var probe = new FakeConnection(new FakeServer { Rows = EchoRows });
                probe.Prepared.UnionWith(c.Prepared);
                new FakeServer { Rows = EchoRows }.Auto(probe, batch);
                var all = new byte[probe.Available];
                probe.Receive(all);
                var messages = Split(all);
                var reply = new ServerWriter();
                for (int m = 0; m < k; m++)
                {
                    reply.Raw(messages[m]);
                    if (messages[m][0] == (byte)'1') c.Prepared.Add(PrepareNames[regions[m]]);
                }
                reply.Error("ERROR", "22012", "division by zero").Ready(); // Sync ended the failed implicit transaction: idle
                c.Write(reply.ToArray());
            };
            var r = Reader(server);
            var seen = new[] { new Seen(), new Seen(), new Seen() };
            var tasks = new[] { Read(r, 1, seen[0], Echo), Read(r, 2, seen[1], Echo2), Read(r, 3, seen[2], Echo3) };
            r.Start(1);
            var results = await Task.WhenAll(tasks);

            int failing = k < regions.Count ? regions[k] : -1; // past the last statement: the commit, no read to blame
            for (int i = 0; i < 3; i++)
            {
                if (i == failing)
                {
                    Assert.Equal(PgStatus.ServerError, results[i].Status);
                    Assert.Equal("22012", results[i].SqlState);
                }
                else
                {
                    Assert.Equal(PgStatus.Ok, results[i].Status);
                    Assert.Equal(i + 1, results[i].Value);
                    Assert.Equal(i + 1, seen[i].Rows.Count);
                }
            }
            // the reads after the failing one ran again in one more batch; a statement whose Parse the error skipped was parsed again
            var c0 = Assert.Single(server.Connections);
            Assert.Equal(failing is >= 0 and < 2 ? 2 : 1, c0.Batches.Count);
            LoopChecks.Stop(r);
        }
    }

    static readonly string[] PrepareNames = [Name(Echo), Name(Echo2), Name(Echo3)];

    /// <summary>The name a statement is parsed under, read from the Parse the client sends.</summary>
    static string Name(PgStatement s) => "s" + s.ToString()[1..s.ToString().IndexOf(':')];

    [Fact(Timeout = 20_000)]
    public async Task AFailedParseIsSentAgainNextTime()
    {
        int batches = 0;
        var server = new FakeServer { Rows = EchoRows };
        server.Respond = (c, batch) =>
        {
            if (batches++ == 0) c.Write(new ServerWriter().Error("ERROR", "42P01", "relation does not exist").Ready().ToArray());
            else server.Auto(c, batch);
        };
        var r = Reader(server);
        r.Start(1);
        Assert.Equal("42P01", (await Read(r, 1)).SqlState);
        Assert.Equal(PgStatus.Ok, (await Read(r, 2)).Status);
        var c = Assert.Single(server.Connections);
        Assert.Contains(c.Batches[0], m => m.Type == 'P');
        Assert.Contains(c.Batches[1], m => m.Type == 'P');
    }

    public static TheoryData<string, byte[]> Malformed => new()
    {
        { "length below 4", new ServerWriter().Raw((byte)'2', 0, 0, 0, 3).ToArray() },
        { "length over 1 GB", new ServerWriter().Raw((byte)'D', 0x7f, 0xff, 0xff, 0xff).ToArray() },
        { "unknown message", new ServerWriter().Parsed(20).Empty('2').Empty('Q').ToArray() },
        { "DataRow wider than its RowDescription", new ServerWriter().Parsed(20).Empty('2').DataRow([ServerWriter.BE64(1), ServerWriter.BE64(2)]).Complete("SELECT 1").Ready().ToArray() },
        { "DataRow after NoData", new ServerWriter().Parsed().Empty('2').DataRow([ServerWriter.BE64(1)]).Complete("SELECT 1").Ready().ToArray() },
        { "RowDescription before ParameterDescription", new ServerWriter().Empty('1').RowDescription(20).Message('t', [0, 0]).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "BindComplete before the Describe's answer", new ServerWriter().Empty('1').Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "ParameterDescription of the wrong length", new ServerWriter().Empty('1').Message('t', [0, 2, 0, 0, 0, 20]).Message('n', []).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "RowDescription cut inside a column", new ServerWriter().Empty('1').Message('t', [0, 0]).Message('T', [0, 1, (byte)'c', 0, 0, 0]).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "RowDescription with bytes after its columns", new ServerWriter().Empty('1').Message('t', [0, 0]).Message('T', [0, 0, 7]).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "RowDescription of -1 columns", new ServerWriter().Empty('1').Message('t', [0, 0]).Message('T', [0xff, 0xff]).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "NoData with a body", new ServerWriter().Empty('1').Message('t', [0, 0]).Message('n', [1]).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "a second RowDescription", new ServerWriter().Parsed(20).RowDescription(20).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "BindComplete with a body", new ServerWriter().Parsed(20).Message('2', [1]).ToArray() },
        { "DataRow before BindComplete", new ServerWriter().Parsed(20).DataRow([[1]]).Empty('2').Complete("SELECT 1").Ready().ToArray() },
        { "column longer than the row", new ServerWriter().Parsed(20).Empty('2').Message('D', [0, 1, 0, 0, 0, 9, 1, 2]).Complete("SELECT 1").Ready().ToArray() },
        { "bytes after the last column", new ServerWriter().Parsed(20).Empty('2').Message('D', [0, 1, 0, 0, 0, 1, 7, 7]).Complete("SELECT 1").Ready().ToArray() },
        { "negative column count", new ServerWriter().Parsed(20).Empty('2').Message('D', [0xff, 0xff]).Complete("SELECT 1").Ready().ToArray() },
        { "column length -2", new ServerWriter().Parsed(20).Empty('2').Message('D', [0, 1, 0xff, 0xff, 0xff, 0xfe]).Complete("SELECT 1").Ready().ToArray() },
        { "BindComplete twice", new ServerWriter().Parsed(20).Empty('2').Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "ParseComplete for a statement parsed already", new ServerWriter().Parsed(20).Empty('1').Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "ReadyForQuery before the statement ended", new ServerWriter().Parsed(20).Empty('2').Ready().ToArray() },
        { "CommandComplete before BindComplete", new ServerWriter().Parsed(20).Complete("SELECT 0").Ready().ToArray() },
        { "two errors", new ServerWriter().Error("ERROR", "22012", "x").Error("ERROR", "22012", "y").Ready().ToArray() },
        { "unterminated error field", new ServerWriter().Message('E', [(byte)'C', (byte)'2']).ToArray() },
        { "ReadyForQuery without status", new ServerWriter().Parsed(20).Empty('2').Empty('Z').ToArray() },
        { "truncated message, then closed", new ServerWriter().Parsed(20).Raw((byte)'2', 0, 0).ToArray() },
        { "closed before any reply", [] },
        { "empty ErrorResponse", new ServerWriter().Message('E', []).ToArray() },
        { "DataRow after an error in the rows", new ServerWriter().Parsed(20).Empty('2').DataRow([[1]]).Error("ERROR", "22012", "x").DataRow([[1]]).Ready().ToArray() },
        { "CommandComplete after an error in the rows", new ServerWriter().Parsed(20).Empty('2').DataRow([[1]]).Error("ERROR", "22012", "x").Complete("SELECT 1").Ready().ToArray() },
        { "timestamps switched to floats mid-session", new ServerWriter().Parsed(20).Empty('2').Parameter("integer_datetimes", "off").Complete("SELECT 0").Ready().ToArray() },
        { "DataRow of one byte", new ServerWriter().Parsed(20).Empty('2').Message('D', [0]).Complete("SELECT 1").Ready().ToArray() },
        { "DataRow cut inside a column length", new ServerWriter().Parsed(20).Empty('2').Message('D', [0, 1, 0, 0]).Complete("SELECT 1").Ready().ToArray() },
        { "ParseComplete with a body", new ServerWriter().Message('1', [1]).Empty('2').Complete("SELECT 0").Ready().ToArray() },
        { "DataRow after an error", new ServerWriter().Error("ERROR", "22012", "x").DataRow([[1]]).Ready().ToArray() },
        { "CommandComplete after an error", new ServerWriter().Error("ERROR", "22012", "x").Complete("SELECT 0").Ready().ToArray() },
        { "ParseComplete after an error", new ServerWriter().Error("ERROR", "22012", "x").Empty('1').Ready().ToArray() },
        { "BindComplete after an error", new ServerWriter().Error("ERROR", "22012", "x").Empty('2').Ready().ToArray() },
        { "ParameterStatus with a name and no value", new ServerWriter().Parsed(20).Empty('2').Message('S', [.. "integer_datetimes"u8, 0]).Complete("SELECT 0").Ready().ToArray() },
    };

    /// <summary>A stray message once every statement ended: the reads have their answers already.</summary>
    public static TheoryData<string, byte[]> StrayAfterTheEnd => new()
    {
        { "DataRow after the last statement", new ServerWriter().Parsed(20).Empty('2').Complete("SELECT 0").DataRow([[1]]).Ready().ToArray() },
        { "CommandComplete after the last statement", new ServerWriter().Parsed(20).Empty('2').Complete("SELECT 0").Complete("SELECT 0").Ready().ToArray() },
        { "ParseComplete after the last statement", new ServerWriter().Parsed(20).Empty('2').Complete("SELECT 0").Empty('1').Ready().ToArray() },
        { "BindComplete after the last statement", new ServerWriter().Parsed(20).Empty('2').Complete("SELECT 0").Empty('2').Ready().ToArray() },
    };

    [Theory(Timeout = 20_000)]
    [MemberData(nameof(StrayAfterTheEnd))]
    public async Task AStrayMessageAfterTheLastStatementKeepsTheAnswersAndDropsTheConnection(string what, byte[] reply)
    {
        int batches = 0;
        var server = new FakeServer { Rows = EchoRows };
        server.Respond = (c, batch) =>
        {
            if (batches++ > 0)
            {
                server.Auto(c, batch);
                return;
            }
            c.Write(reply);
        };
        var r = Reader(server);
        r.Start(1);
        var seen = new Seen();
        var answered = await Read(r, 1, seen);
        Assert.True(answered.Status == PgStatus.Ok, what);
        Assert.Empty(seen.Rows);
        Assert.Equal(2, (await Read(r, 2)).Value);
        Assert.Equal(2, server.Connections.Length); // the stream wasn't trusted after it
    }

    [Theory(Timeout = 20_000)]
    [MemberData(nameof(Malformed))]
    public async Task AMalformedOrTruncatedReplyBreaksTheConnectionAndTheNextGroupReconnects(string what, byte[] reply)
    {
        int batches = 0;
        var server = new FakeServer { Rows = EchoRows };
        server.Respond = (c, batch) =>
        {
            if (batches++ > 0)
            {
                server.Auto(c, batch);
                return;
            }
            c.Write(reply);
            c.Close(); // a reply the client waits on forever would hang the test instead of failing it
        };
        var r = Reader(server);
        r.Start(1);
        var broken = await Read(r, 1);
        Assert.True(broken.Status == PgStatus.Broken, what);
        Assert.IsType<PgConnectionException>(broken.Exception);
        Assert.Null(broken.Error); // not the server's error

        Assert.Equal(2, (await Read(r, 2)).Value);
        Assert.True(server.Connections[0].Disposed); // the loop let go of the broken one before it reconnected
        Assert.Equal(2, server.Connections.Length);
    }

    [Fact(Timeout = 20_000)]
    public async Task AProtocolErrorAfterAReadsStatementEndedKeepsItsAnswerAndReplacesTheConnection()
    {
        int batches = 0;
        var server = new FakeServer { Rows = EchoRows };
        server.Respond = (c, batch) =>
        {
            if (batches++ > 0)
            {
                server.Auto(c, batch);
                return;
            }
            c.Write(new ServerWriter().Parsed(20, 25, 25).Empty('2').DataRow([ServerWriter.BE64(1), null, [65]]).Complete("SELECT 1")
                .Complete("SELECT 1").Ready().ToArray()); // a CommandComplete for no statement
        };
        var r = Reader(server);
        r.Start(1);
        var first = await Read(r, 1);
        Assert.Equal((PgStatus.Ok, 1L), (first.Status, first.Value));
        Assert.Equal(2, (await Read(r, 2)).Value);
        Assert.Equal(2, server.Connections.Length);
    }

    [Fact(Timeout = 20_000)]
    public async Task AConnectionDroppedMidGroupAnswersTheReadsThatEndedAndFailsTheRest()
    {
        int batches = 0;
        var server = new FakeServer { Rows = EchoRows };
        server.Respond = (c, batch) =>
        {
            if (batches++ > 0)
            {
                server.Auto(c, batch);
                return;
            }
            // the first read whole, half of the second, then the connection is gone
            c.Prepared.Add(PrepareNames[0]);
            c.Write(new ServerWriter().Parsed(20, 25, 25).Empty('2').DataRow([ServerWriter.BE64(1), null, [65]]).Complete("SELECT 1")
                .Parsed(20, 25, 25).Empty('2').DataRow([ServerWriter.BE64(2), null, [65]]).ToArray());
            c.Close();
        };
        var r = Reader(server);
        var tasks = new[] { Read(r, 1, null, Echo), Read(r, 2, null, Echo2), Read(r, 3, null, Echo3) };
        r.Start(1);
        var results = await Task.WhenAll(tasks);
        Assert.Equal(PgStatus.Ok, results[0].Status);
        Assert.Equal(PgStatus.Broken, results[1].Status);
        Assert.Equal(PgStatus.Broken, results[2].Status);
        Assert.Equal(PgStatus.Ok, (await Read(r, 4, null, Echo2)).Status);
        Assert.Equal(2, server.Connections.Length);
    }

    [Fact(Timeout = 20_000)]
    public async Task AServerRestartFailsTheGroupInFlightThenReadsWorkOnceItIsBack()
    {
        var server = new FakeServer { Rows = EchoRows };
        var settings = server.Settings();
        settings.ReconnectDelay = TimeSpan.FromMilliseconds(20);
        var r = Reader(settings);
        r.Start(1);
        Assert.Equal(PgStatus.Ok, (await Read(r, 1)).Status);

        // shutting down: FATAL to every session, then the sockets close and nothing accepts for a while
        server.Respond = (c, _) =>
        {
            c.Write(new ServerWriter().Error("FATAL", "57P01", "terminating connection due to administrator command").ToArray());
            c.Close();
        };
        var lost = await Read(r, 2);
        Assert.Equal(PgStatus.Broken, lost.Status);
        Assert.Equal("57P01", Assert.IsType<PgException>(lost.Exception!.InnerException).SqlState);

        server.Down = true;
        server.Respond = null;
        Assert.Equal(PgStatus.Broken, (await Read(r, 3)).Status); // can't connect
        server.Down = false;
        await Task.Delay(100); // past the wait before the next try
        Assert.Equal(4, (await Read(r, 4)).Value);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("FATAL")]
    [InlineData("closed")]
    [InlineData("reset")]
    public async Task AConnectionTheServerEndedWhileItIdledIsReplacedBeforeTheNextGroupGoesOut(string how)
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        r.Start(1);
        Assert.Equal(PgStatus.Ok, (await Read(r, 1)).Status);
        await PgTestDb.Asleep(r); // the loop is asleep, waiting for the next group
        var idle = server.Connections[0];
        if (how == "FATAL") idle.Write(new ServerWriter().Error("FATAL", "57P05", "terminating connection due to idle-session timeout").ToArray());
        if (how is "FATAL" or "closed") idle.Close();
        if (how == "reset") idle.Reset();
        // nothing of the group was sent on the dead connection, so it runs on a new one, a write as safely as a read
        Assert.Equal(2, (await Read(r, 2)).Value);
        Assert.Equal(2, server.Connections.Length);
        Assert.Empty(idle.Batches.Skip(1));
        Assert.True(idle.Disposed);
    }

    [Fact(Timeout = 20_000)]
    public async Task NoticesAndParameterStatusWhileIdleKeepTheConnection()
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        r.Start(1);
        Assert.Equal(PgStatus.Ok, (await Read(r, 1)).Status);
        await PgTestDb.Asleep(r);
        server.Connections[0].Write(new ServerWriter().Notice("idle").Parameter("TimeZone", "UTC").ToArray());
        Assert.Equal(2, (await Read(r, 2)).Value);
        Assert.Single(server.Connections);
    }

    [Fact(Timeout = 20_000)]
    public async Task AFailedConnectIsTriedAgainOnlyAfterAWaitThatGrowsAndCommandsMeanwhileFailAtOnce()
    {
        var server = new FakeServer { Rows = EchoRows, Down = true };
        var settings = server.Settings();
        settings.ReconnectDelay = TimeSpan.FromMilliseconds(40);
        settings.MaxReconnectDelay = TimeSpan.FromSeconds(10);
        var r = Reader(settings);
        r.Start(1);
        // reads until four connects were tried, each with when it started and ended and the connects after it: a
        // connect happened within its read, so two connects lie at most from the first's start to the second's end
        // apart. However slow the machine, that span holds the wait between them, which is all this checks.
        var reads = new List<(long Start, long End, int Connects)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (server.Connects < 4 && sw.ElapsedMilliseconds < 10_000)
        {
            long start = sw.ElapsedTicks;
            var res = await Read(r, 1);
            reads.Add((start, sw.ElapsedTicks, server.Connects));
            Assert.Equal(PgStatus.Broken, res.Status);
            Assert.IsType<System.Net.Sockets.SocketException>(res.Exception!.InnerException ?? res.Exception);
            await Task.Delay(2);
        }
        Assert.Equal(4, server.Connects);
        var tried = Enumerable.Range(1, 4).Select(c => reads.First(x => x.Connects == c)).ToList();
        for (int k = 1; k < tried.Count; k++)
        {
            // the k-th wait is 40 ms doubled k - 1 times, a quarter shorter at the least
            double apart = (tried[k].End - tried[k - 1].Start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double wait = 0.75 * 40 * (1 << (k - 1));
            Assert.True(apart >= wait, $"connect {k + 1} came {apart:F1} ms after connect {k}, before its wait of {wait} ms");
        }
        // a group between two tries fails at once, with no connect of its own
        Assert.True(reads.Count > server.Connects, $"{reads.Count} reads, {server.Connects} connects");

        server.Down = false;
        var back = System.Diagnostics.Stopwatch.StartNew();
        PgResult ok;
        do
        {
            await Task.Delay(10);
            ok = await Read(r, 5);
        }
        while (!ok.IsOk && back.ElapsedMilliseconds < 5000);
        Assert.Equal(5, ok.Value);
        // connected: the wait starts over at the first failure after it, so a try 80 ms later, past a 40 ms wait at
        // most a quarter longer, connects
        await PgTestDb.Asleep(r); // the next group finds the connection closed and connects
        int before = server.Connects;
        server.Connections.Single().Close();
        server.Down = true;
        Assert.Equal(PgStatus.Broken, (await Read(r, 6)).Status);
        Assert.Equal(before + 1, server.Connects);
        server.Down = false;
        await Task.Delay(80);
        Assert.Equal(7, (await Read(r, 7)).Value);
        Assert.Equal(before + 2, server.Connects);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("search_path", "x\0user\0postgres")]   // a NUL would end the value and start keys of its own
    [InlineData("a\0b", "v")]
    [InlineData("", "v")]
    [InlineData("client_encoding", "LATIN1")]          // the client reads and writes UTF-8 only
    [InlineData("Client_Encoding", "UTF8")]
    [InlineData("user", "postgres")]                   // the settings' own User and Database say these
    [InlineData("database", "postgres")]
    [InlineData("replication", "database")]            // a replication session runs no ordinary statements
    public async Task AStartupParameterThatWouldChangeWhoOrHowTheClientTalksIsRefusedBeforeItIsSent(string name, string value)
    {
        var server = new FakeServer { Rows = EchoRows };
        var settings = server.Settings();
        settings.Parameters.Add(new(name, value)); // the list itself, past Set's checks
        var r = Reader(settings);
        r.Start(1);
        var res = await Read(r, 1);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.IsType<PgConnectionException>(res.Exception);
        Assert.Empty(server.Connections.SelectMany(c => c.StartupParameters));
    }

    [Theory]
    [InlineData("client_encoding")]
    [InlineData("CLIENT_ENCODING")]
    [InlineData("user")]
    [InlineData("database")]
    [InlineData("replication")]
    public void SetRefusesTheParametersTheClientSetsItself(string name) =>
        Assert.Throws<ArgumentException>(() => new PgSettings().Set(name, "x"));

    [Fact(Timeout = 20_000)]
    public async Task AServerThatReportsAnotherClientEncodingAtStartupIsRefused()
    {
        var server = new FakeServer { StartupReply = new ServerWriter().Auth(0).Parameter("client_encoding", "LATIN1").Ready().ToArray() };
        var settings = server.Settings();
        settings.Password = null;
        var r = Reader(settings);
        r.Start(1);
        var res = await Read(r, 1);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.Contains("LATIN1", res.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 20_000)]
    public async Task AClientEncodingChangedMidSessionBreaksTheConnection()
    {
        var server = new FakeServer { Rows = EchoRows };
        int batches = 0;
        server.Respond = (c, batch) =>
        {
            if (batches++ == 0) c.Write(new ServerWriter().Parameter("client_encoding", "SQL_ASCII").ToArray());
            server.Auto(c, batch);
        };
        var r = Reader(server);
        r.Start(1);
        var res = await Read(r, 1);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.Contains("SQL_ASCII", res.Exception!.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await Read(r, 2)).Value);
        Assert.Equal(2, server.Connections.Length);
    }

    /// <summary>Answers like <see cref="FakeServer.Auto"/>, but the first batch's ReadyForQuery reports <paramref name="status"/>.</summary>
    static void FirstReadyIs(FakeServer server, char status)
    {
        int batches = 0;
        server.Respond = (c, batch) =>
        {
            if (batches++ > 0)
            {
                server.Auto(c, batch);
                return;
            }
            var probe = new FakeConnection(server);
            probe.Prepared.UnionWith(c.Prepared);
            server.Auto(probe, batch);
            c.Prepared.UnionWith(probe.Prepared);
            var bytes = new byte[probe.Available];
            probe.Receive(bytes);
            bytes[^1] = (byte)status;
            c.Write(bytes);
        };
    }

    [Theory(Timeout = 20_000)]
    [InlineData('T')]
    [InlineData('E')]
    public async Task AWriteGroupThatEndsInATransactionIsBrokenAndItsConnectionReplaced(char status)
    {
        var server = new FakeServer { Rows = EchoRows };
        FirstReadyIs(server, status);
        var w = Writer(server);
        var first = w.Command(Ins).Int8(1).Run(Collect, new Seen()).AsTask();
        var second = w.Command(Ins).Int8(2).Run(Collect, new Seen()).AsTask();
        w.Start(1);
        // still in a transaction after Sync: nothing of the group committed, so no write may be Ok
        foreach (var res in new[] { await first, await second })
        {
            Assert.Equal(PgStatus.Broken, res.Status);
            Assert.Contains($"'{status}'", res.Exception!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(3, (await w.Command(Ins).Int8(3).Run(Collect, new Seen())).Value);
        Assert.Equal(2, server.Connections.Length);
        Assert.True(server.Connections[0].Disposed);
    }

    [Fact(Timeout = 20_000)]
    public async Task AReadGroupThatEndsInATransactionKeepsItsAnswersAndReplacesTheConnection()
    {
        var server = new FakeServer { Rows = EchoRows };
        FirstReadyIs(server, 'T');
        var r = Reader(server);
        var tasks = new[] { Read(r, 1), Read(r, 2) };
        r.Start(1);
        Assert.Equal([1L, 2L], (await Task.WhenAll(tasks)).Select(x => x.Value)); // each read ended before the Sync
        Assert.Equal(3, (await Read(r, 3)).Value);
        Assert.Equal(2, server.Connections.Length);
        Assert.True(server.Connections[0].Disposed);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("BEGIN")]
    [InlineData("START TRANSACTION")]
    [InlineData("SAVEPOINT")]
    [InlineData("RELEASE")]
    [InlineData("PREPARE TRANSACTION")]
    public async Task AWriteWhoseStatementControlsTheTransactionBreaksItsGroup(string tag)
    {
        var server = new FakeServer();
        server.Respond = (c, batch) =>
        {
            c.Prepared.Add(Name(Ins));
            // the first write's statement ended (or split) the group's transaction; the server carries on
            c.Write(new ServerWriter().Parsed().Empty('2').Complete(tag).Empty('2').Complete("INSERT 0 1").Ready().ToArray());
        };
        var w = Writer(server);
        var first = w.Command(Ins).Int8(1).Run(Collect, new Seen()).AsTask();
        var second = w.Command(Ins).Int8(2).Run(Collect, new Seen()).AsTask();
        w.Start(1);
        foreach (var res in new[] { await first, await second })
        {
            Assert.Equal(PgStatus.Broken, res.Status);
            Assert.Contains(tag, res.Exception!.Message, StringComparison.Ordinal);
        }
        server.Respond = null;
        server.Rows = EchoRows;
        Assert.Equal(3, (await w.Command(Ins).Int8(3).Run(Collect, new Seen())).Value);
        Assert.True(server.Connections[0].Disposed);
    }

    [Fact(Timeout = 20_000)]
    public async Task ACommandTagThatOnlyStartsLikeATransactionsIsAnOrdinaryWrite()
    {
        var server = new FakeServer();
        server.Respond = (c, batch) =>
        {
            c.Prepared.Add(Name(Ins));
            c.Write(new ServerWriter().Parsed().Empty('2').Complete("PREPARE").Ready().ToArray()); // SQL PREPARE, not of a transaction
        };
        var w = Writer(server);
        w.Start(1);
        var res = await w.Command(Ins).Int8(1).Run(null, null);
        Assert.Equal(PgStatus.Ok, res.Status);
    }

    [Fact(Timeout = 20_000)]
    public async Task ACancelledReadIsNeverSent()
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        using var cts = new CancellationTokenSource();
        var gone = r.Command(Echo).Int8(1).Run(null, null, new CancellationToken(true));
        Assert.True(gone.IsCompletedSuccessfully);
        Assert.Equal(PgStatus.Canceled, gone.Result.Status);
        var leaving = r.Command(Echo).Int8(2).Run(null, null, cts.Token).AsTask();
        var staying = Read(r, 3);
        cts.Cancel();
        r.Start(1);
        Assert.Equal(PgStatus.Canceled, (await leaving).Status);
        Assert.Equal(3, (await staying).Value);
        var binds = server.Connections.Single().Batches.SelectMany(b => b).Where(m => m.Type == 'B').Select(m => m.Bind().Int8(0));
        Assert.Equal([3L], binds);
        Assert.Equal(0, r.Queued);
    }

    [Fact(Timeout = 20_000)]
    public async Task AHandlerThatThrowsFailsItsReadAndTheConnectionCarriesOn()
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        var before = Read(r, 1);
        var bad = r.Command(Echo).Int8(3).Run(static (in PgRow row, object? _) => row.Index == 1 ? throw new FormatException("bug") : 0, null).AsTask();
        var after = Read(r, 2);
        r.Start(1);
        Assert.Equal(1, (await before).Value);
        var b = await bad;
        Assert.Equal(PgStatus.HandlerFailed, b.Status);
        Assert.Equal("bug", Assert.IsType<FormatException>(b.Exception).Message);
        Assert.Equal(2, (await after).Value);
        Assert.Single(server.Connections);
    }

    [Fact(Timeout = 20_000)]
    public async Task AWrongTypeReadFailsItsReadInsteadOfMisreading()
    {
        var server = new FakeServer { Rows = _ => [[[1, 2, 3]]] };
        var r = Reader(server);
        r.Start(1);
        var res = await r.Command(Echo).Int8(1).Run(static (in PgRow row, object? _) => row.GetInt64(0), null);
        Assert.Equal(PgStatus.HandlerFailed, res.Status);
        Assert.IsType<InvalidCastException>(res.Exception);
        var nul = await r.Command(Echo).Int8(1).Run(static (in PgRow row, object? _) => row.GetBytes(5).Length, null);
        Assert.IsType<IndexOutOfRangeException>(nul.Exception);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentCallersOnSeveralLoopsEachGetTheirOwnAnswer()
    {
        var server = new FakeServer { Rows = b => [[ServerWriter.BE64(b.Int8(0) * 3)]] };
        var r = Reader(server);
        r.Start(4);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(async () =>
        {
            var mine = new long[500];
            for (int i = 0; i < mine.Length; i++)
                mine[i] = (await r.Command(Echo).Int8(t * 1000 + i).Run(static (in PgRow row, object? _) => row.GetInt64(0), null)).Value;
            return mine;
        })));
        for (int t = 0; t < 16; t++) Assert.Equal(Enumerable.Range(0, 500).Select(i => (t * 1000L + i) * 3), results[t]);
        Assert.InRange(server.Connections.Length, 1, 4);
    }

    [Fact(Timeout = 20_000)]
    public async Task ACommandIsBuiltOnceAndRunOnce()
    {
        var r = Reader(new FakeServer { Rows = EchoRows });
        Assert.Throws<InvalidOperationException>(() => { r.Command(Echo).Run(null, null); });          // too few parameters
        Assert.True(Refuses(r.Command(Echo), static c => c.Int8(1).Int8(2)));                        // too many
        Assert.True(Refuses(r.Command(Echo), static c => c.Element("x"u8)));                         // no array open
        Assert.Throws<InvalidOperationException>(() => { r.Command(Echo).BeginTextArray().Run(null, null); });
        r.Start(1);
        var cmd = r.Command(Echo).Int8(1);
        var first = cmd.Run(null, null);
        bool thrown = false;
        try { _ = cmd.Run(null, null); } catch (InvalidOperationException) { thrown = true; }
        Assert.True(thrown, "a second run of one command");
        Assert.Equal(PgStatus.Ok, (await first).Status);
    }

    delegate PgCommand Build(PgCommand command);

    /// <summary>Whether <paramref name="build"/> refuses to go on with <paramref name="command"/>. A refused command
    /// keeps its pooled job (it is a ref struct, nothing can give it back for it): Hold gives it back here.</summary>
    static bool Refuses(PgCommand command, Build build)
    {
        try
        {
            build(command);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            try
            {
                command.Hold().Discard();
            }
            catch (InvalidOperationException)
            {
                // unfinished: Hold gave the job back as it refused
            }
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task AStoppedReaderRunsWhatQueuedThenRefuses()
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        var queued = Read(r, 1);
        r.Complete();
        Assert.Equal(PgStatus.Stopped, (await Read(r, 2)).Status);
        r.Start(1);
        Assert.Equal(1, (await queued).Value);
        // the loop ends on its own thread after answering; give it time without betting on how much
        for (int i = 0; i < 1000 && !server.Connections.Single().Terminated; i++) await Task.Delay(10);
        Assert.True(server.Connections.Single().Terminated, "the loop said goodbye and closed its connection");
    }

    // ---------- writes ----------

    static readonly PgStatement Ins = new("insert", PgType.Int8);

    [Fact(Timeout = 20_000)]
    public async Task AWritesRowsReachItsHandlerOnlyAfterTheCommit()
    {
        var server = new FakeServer { Rows = EchoRows };
        int batches = 0;
        var gate = new ManualResetEventSlim();
        server.Respond = (c, batch) =>
        {
            batches++;
            var all = new FakeConnection(new FakeServer { Rows = EchoRows });
            all.Prepared.UnionWith(c.Prepared);
            new FakeServer { Rows = EchoRows }.Auto(all, batch);
            c.Prepared.UnionWith(all.Prepared);
            var bytes = new byte[all.Available];
            all.Receive(bytes);
            // everything up to ReadyForQuery, then the commit's ReadyForQuery only once the test lets it go
            c.Write(bytes[..^6]);
            ThreadPool.QueueUserWorkItem(_ => { gate.Wait(); c.Write(bytes[^6..]); });
        };
        var w = Writer(server);
        var seen = new Seen();
        var task = w.Command(Ins).Int8(2).Run(Collect, seen).AsTask();
        w.Start(1);
        await Task.Delay(200);
        Assert.Empty(seen.Rows);
        Assert.False(task.IsCompleted);
        gate.Set();
        var res = await task;
        Assert.Equal(PgStatus.Ok, res.Status);
        Assert.Equal(2, res.Rows);
        Assert.Equal(1, res.Runs);
        Assert.Equal(2, seen.Rows.Count);
        Assert.Equal(1, batches);
    }

    [Fact(Timeout = 20_000)]
    public async Task WhenTheCommitFailsEachWriteRunsAloneAndNoAttemptsRowsLeak()
    {
        var server = new FakeServer { Rows = EchoRows };
        int batches = 0;
        server.Respond = (c, batch) =>
        {
            if (batches++ > 0)
            {
                server.Auto(c, batch);
                return;
            }
            // every statement ran, then COMMIT failed (a deferred constraint)
            var all = new FakeConnection(new FakeServer { Rows = EchoRows });
            new FakeServer { Rows = EchoRows }.Auto(all, batch);
            c.Prepared.UnionWith(all.Prepared);
            var bytes = new byte[all.Available];
            all.Receive(bytes);
            c.Write([.. bytes[..^6], .. new ServerWriter().Error("ERROR", "23505", "duplicate key").Ready('I').ToArray()]);
        };
        var w = Writer(server);
        var seen = new[] { new Seen(), new Seen() };
        var a = w.Command(Ins).Int8(1).Run(Collect, seen[0]).AsTask();
        var b = w.Command(Ins).Int8(3).Run(Collect, seen[1]).AsTask();
        w.Start(1);
        var (ra, rb) = (await a, await b);
        Assert.Equal((PgStatus.Ok, 2), (ra.Status, ra.Runs));
        Assert.Equal((PgStatus.Ok, 2), (rb.Status, rb.Runs));
        Assert.Single(seen[0].Rows);          // only the committed attempt's rows
        Assert.Equal(3, seen[1].Rows.Count);
        Assert.Equal(3, batches);             // the group, then each write alone
    }

    [Fact(Timeout = 20_000)]
    public async Task AMalformedRowOfACommittedWriteFailsThatWriteAndStillCompletesEveryOne()
    {
        var server = new FakeServer();
        server.Respond = (c, batch) =>
        {
            c.Prepared.Add(Name(Ins));
            // two writes; the first one's row claims a column longer than the row
            c.Write(new ServerWriter().Parsed(20).Empty('2').Message('D', [0, 1, 0, 0, 0, 9, 1]).Complete("INSERT 0 1")
                .Empty('2').DataRow([ServerWriter.BE64(7)]).Complete("INSERT 0 1").Ready().ToArray());
        };
        var w = Writer(server);
        var bad = w.Command(Ins).Int8(1).Run(Collect, new Seen()).AsTask();
        var good = w.Command(Ins).Int8(2).Run(Collect, new Seen()).AsTask();
        w.Start(1);
        var b = await bad;
        Assert.Equal(PgStatus.HandlerFailed, b.Status);
        Assert.IsType<PgConnectionException>(b.Exception);
        Assert.Equal((PgStatus.Ok, 7L), ((await good).Status, (await good).Value));
    }

    [Fact(Timeout = 20_000)]
    public async Task ANullArrayIsSqlNullAndAnEmptyOneIsEmpty()
    {
        var server = new FakeServer { Rows = _ => [] };
        var r = Reader(server);
        var s = new PgStatement("arrays", PgType.TextArray, PgType.TextArray);
        r.Start(1);
        string?[]? none = null;
        Assert.True((await r.Command(s).TextArray(none).TextArray(System.Array.Empty<string>()).Run(null, null)).IsOk);
        var bind = server.Connections.Single().Batches.Single().Single(m => m.Type == 'B').Bind();
        Assert.Null(bind.Params[0]);
        Assert.Equal([], bind.TextArray(1)!);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABigWriteGroupGoesOutWholeInPieces()
    {
        var server = new FakeServer { Rows = b => [[new byte[100_000]]] };
        var w = Writer(server);
        var big = new string('x', 300_000);
        var stmt = new PgStatement("insert text", PgType.Text);
        var tasks = Enumerable.Range(0, 20).Select(_ => w.Command(stmt).Text(big).Run(null, null).AsTask()).ToArray();
        w.Start(1);
        Assert.All(await Task.WhenAll(tasks), res => Assert.Equal((PgStatus.Ok, 1), (res.Status, res.Rows)));
        var batch = Assert.Single(server.Connections.Single().Batches);
        Assert.All(batch.Where(m => m.Type == 'B'), m => Assert.Equal(big, m.Bind().Text(0)));
    }

    // ---- Startup, parameters, rows and errors: the edges mutation testing found untested ----

    PgReader Reader(PgSettings settings)
    {
        var r = new PgReader(settings);
        _started.Add(r);
        return r;
    }

    static byte[] Mechanisms(params string[] names) => [.. names.SelectMany(ServerWriter.Z), 0];

    public static TheoryData<string, byte[]> BadStartups => new()
    {
        { "BackendKeyData of 7 bytes", new ServerWriter().Auth(0).Message('K', [0, 0, 0, 42, 0, 0, 7]).Ready().ToArray() },
        { "ParameterStatus without its last NUL", new ServerWriter().Auth(0).Message('S', [(byte)'a', 0, (byte)'b']).Ready().ToArray() },
        { "ParameterStatus without a NUL", new ServerWriter().Auth(0).Message('S', [(byte)'a']).Ready().ToArray() },
        { "ParameterStatus with a name and no value", new ServerWriter().Auth(0).Message('S', [.. "integer_datetimes"u8, 0]).Ready().ToArray() },
        { "timestamps kept as floats", new ServerWriter().Auth(0).Parameter("integer_datetimes", "off").Ready().ToArray() },
        { "an authentication request of 2 bytes", new ServerWriter().Message('R', [0, 0]).ToArray() },
        { "SASL continue before SASL", new ServerWriter().Auth(11, "r=abc,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8).ToArray() },
        { "SASL final before SASL", new ServerWriter().Auth(12, "v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="u8).ToArray() },
        { "SASL final before the client's proof", new ServerWriter().Auth(10, Mechanisms("SCRAM-SHA-256")).Auth(12, "v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="u8).ToArray() },
        { "no SCRAM-SHA-256 offered", new ServerWriter().Auth(10, Mechanisms("SCRAM-SHA-256-PLUS")).ToArray() },
    };

    [Theory(Timeout = 20_000)]
    [MemberData(nameof(BadStartups))]
    public async Task AMalformedOrHostileStartupFailsTheConnection(string what, byte[] reply)
    {
        var server = new FakeServer { StartupReply = reply };
        var r = Reader(server);
        r.Start(1);
        var res = await Read(r, 1);
        Assert.True(res.Status == PgStatus.Broken, what);
        Assert.IsType<PgConnectionException>(res.Exception);
        Assert.True(server.Connections[0].Disposed);
    }

    /// <summary>The server's side of a startup as fixed bytes, at most <paramref name="chunk"/> per receive, then
    /// closed; what the client sends is dropped.</summary>
    sealed class Scripted(byte[] reply, int chunk) : PgTransport
    {
        int _at;

        public override int Receive(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, chunk), reply.Length - _at);
            reply.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        public override int ReceiveWaiting(Span<byte> buffer) => 0;
        public override void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null) { }
        public override void Dispose() { }
    }

    static PgSettings Scripted_(byte[] reply, int chunk = int.MaxValue, int receiveBuffer = 256 << 10) =>
        new() { User = "u", Database = "d", ReceiveBufferSize = receiveBuffer, Transport = _ => new Scripted(reply, chunk) };

    /// <summary>Found by fuzzing: five bytes announcing a 1 GB message made the client allocate the gigabyte before
    /// any of it came. Without TLS anyone on the path can send them.</summary>
    [Fact]
    public void AnAnnouncedGigabyteIsNotAllocatedBeforeItArrives()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PgConnectionException>(() => PgConnection.Open(Scripted_([(byte)'R', 0x3F, 0xFF, 0xFF, 0xFF])));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 << 20);
    }

    /// <summary>A server that, while the client's large batch goes out, never reads and keeps sending: each time the
    /// send blocks, <see cref="Flood"/> more bytes wait (notices, so each is a valid message).</summary>
    sealed class Flooding(long flood) : PgTransport
    {
        readonly byte[] _startup = new ServerWriter().Auth(0).Parameter("integer_datetimes", "on").Ready().ToArray();
        readonly byte[] _notice = new ServerWriter().Notice(new string('n', 60_000)).ToArray();
        int _at, _noticeAt;
        long _sent;

        public long Sent => _sent;

        public override int Receive(Span<byte> buffer)
        {
            int n = Math.Min(buffer.Length, _startup.Length - _at);
            if (n == 0) throw new TimeoutException("nothing more comes");
            _startup.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        public override int ReceiveWaiting(Span<byte> buffer)
        {
            if (_sent >= flood) return 0;
            int n = Math.Min(buffer.Length, _notice.Length - _noticeAt);
            _notice.AsSpan(_noticeAt, n).CopyTo(buffer);
            _noticeAt = (_noticeAt + n) % _notice.Length;
            _sent += n;
            return n;
        }

        public override void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null)
        {
            // the socket takes nothing more of a large batch until the replies waiting are taken in
            if (whileBlocked is not null)
                while (_sent < flood) whileBlocked();
        }

        public override void Dispose() { }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(1L << 20, 0)]       // under the limit: all of it taken in
    [InlineData(64L << 20, 1)]      // a server that only writes: refused at the limit, not grown without one
    public void RepliesTakenInWhileABatchGoesOutAreCapped(long flood, int refused)
    {
        var transport = new Flooding(flood);
        var settings = new PgSettings { User = "u", Database = "d", Transport = _ => transport };
        settings.MaxTakeIn = 4 << 20;
        using var conn = PgConnection.Open(settings);
        var big = new PgStatement("big", PgType.Text);
        var job = PgJob.Rent(big);
        try
        {
            job.Text(new string('x', 100_000)); // past the 64 KB sent in one go
            job.Seal();
            conn.Begin();
            conn.Add(job, 0);
            if (refused == 0) conn.Flush();
            else
            {
                var e = Assert.Throws<PgConnectionException>(conn.Flush);
                Assert.Contains("MB", e.Message, StringComparison.Ordinal);
                Assert.InRange(transport.Sent, 4 << 20, (4 << 20) + (64 << 10) * 2);
            }
        }
        finally
        {
            job.Discard();
        }
    }

    /// <summary>Found by fuzzing: a ParameterStatus holding only a name and its NUL threw ArgumentOutOfRangeException
    /// from the slice for its value, instead of breaking the connection as malformed.</summary>
    [Fact]
    public void AParameterStatusWithoutAValueIsMalformed() =>
        Assert.Throws<PgConnectionException>(() => PgConnection.Open(Scripted_(
            new ServerWriter().Auth(0).Message('S', [.. "integer_datetimes"u8, 0]).Ready().ToArray())));

    /// <summary>A message far larger than the receive buffer still arrives whole: the buffer grows as it comes.</summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1000)]
    [InlineData(1)]
    public void AMessageLargerThanTheBufferGrowsItAsItArrives(int chunk)
    {
        string text = new('m', chunk == 1 ? 70_000 : 3 << 20);
        byte[] reply = new ServerWriter().Error("FATAL", "28P01", text).ToArray();
        var e = Assert.Throws<PgException>(() => PgConnection.Open(Scripted_(reply, chunk, receiveBuffer: 64)));
        Assert.Equal(text, e.MessageText);
    }

    [Fact(Timeout = 20_000)]
    public async Task AServerThatAsksForACleartextPasswordDuringScramGetsNone()
    {
        var server = new FakeServer { StartupReply = new ServerWriter().Auth(10, Mechanisms("SCRAM-SHA-256")).Auth(3).ToArray() };
        var settings = server.Settings();
        settings.AllowWeakPasswordAuth = true; // even then: it chose SCRAM first
        var r = Reader(settings);
        r.Start(1);
        Assert.Equal(PgStatus.Broken, (await Read(r, 1)).Status);
        var sent = Assert.Single(server.Connections[0].Passwords); // the SCRAM client-first only
        Assert.StartsWith("SCRAM-SHA-256\0", Encoding.ASCII.GetString(sent), StringComparison.Ordinal);
    }

    [Fact(Timeout = 20_000)]
    public async Task APasswordHoldingNulIsNeverSent()
    {
        var server = new FakeServer { StartupReply = new ServerWriter().Auth(3).ToArray() };
        var settings = server.Settings();
        settings.AllowWeakPasswordAuth = true;
        settings.Password = "se\0cret";
        var r = Reader(settings);
        r.Start(1);
        var res = await Read(r, 1);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.IsType<PgConnectionException>(res.Exception);
        Assert.Empty(server.Connections[0].Passwords);
    }

    [Fact(Timeout = 20_000)]
    public async Task ScramWorksWhenTheServerNamesTheMechanismTwice()
    {
        var server = new FakeServer { Rows = EchoRows, Auth = "scram", Mechanisms = ["SCRAM-SHA-256", "SCRAM-SHA-256"] };
        var r = Reader(server);
        r.Start(1);
        Assert.Equal(1, (await Read(r, 1)).Value);
    }

    static readonly PgStatement AllTypes = new("all types", PgType.Int4, PgType.Bool, PgType.Bool, PgType.Float8, PgType.Timestamptz,
        PgType.Text, PgType.Text, PgType.Int4);

    [Fact(Timeout = 20_000)]
    public async Task EveryParameterTypeGoesOutInPostgresBinaryFormat()
    {
        var server = new FakeServer();
        var r = Reader(server);
        r.Start(1);
        const long unixUs = 1_760_000_000_123_456;
        var res = await r.Command(AllTypes).Int4(-7).Bool(true).Bool(false).Float8(-1.5).Timestamptz(unixUs)
            .Text("zażółć").Text("raw"u8).Null().Run(null, null);
        Assert.Equal(PgStatus.Ok, res.Status);
        var p = server.Connections.Single().Batches[0].Single(m => m.Type == 'B').Bind().Params;
        Assert.Equal([0xff, 0xff, 0xff, 0xf9], p[0]);
        Assert.Equal([1], p[1]);
        Assert.Equal([0], p[2]);
        Assert.Equal(ServerWriter.BE64(BitConverter.DoubleToInt64Bits(-1.5)), p[3]);
        Assert.Equal(ServerWriter.BE64(unixUs - 946_684_800_000_000), p[4]); // Postgres counts from 2000-01-01
        Assert.Equal("zażółć", Encoding.UTF8.GetString(p[5]!));
        Assert.Equal("raw"u8.ToArray(), p[6]);
        Assert.Null(p[7]);
    }

    static readonly PgStatement Big = new("big", PgType.Text, PgType.Text, PgType.TextArray);

    [Fact(Timeout = 20_000)]
    public async Task ParametersFarLargerThanTheBufferGoOutWhole()
    {
        var server = new FakeServer();
        var r = Reader(server);
        r.Start(1);
        string text = string.Concat(Enumerable.Repeat("żółw ", 60_000)); // 300,000 chars, 480,000 bytes
        byte[] bytes = Enumerable.Repeat((byte)'x', 300_000).ToArray();
        string?[] array = [.. Enumerable.Range(0, 2000).Select(i => i % 7 == 0 ? null : $"élément {i}")];
        Assert.Equal(PgStatus.Ok, (await r.Command(Big).Text(text).Text(bytes).TextArray(array).Run(null, null)).Status);
        var bind = server.Connections.Single().Batches[0].Single(m => m.Type == 'B').Bind();
        Assert.Equal(text, bind.Text(0));
        Assert.Equal(bytes, bind.Params[1]);
        Assert.Equal(array, bind.TextArray(2));
    }

    static readonly PgStatement Ts = new("ts", PgType.Timestamptz);

    [Fact(Timeout = 20_000)]
    public async Task InfinityGoesOutAsPostgresInfinityAndAnInstantNoOffsetCanReachIsOnePastIt()
    {
        var server = new FakeServer();
        var r = Reader(server);
        r.Start(1);
        const long C = 946684800000000; // 1970 to 2000
        long[] sent = [long.MaxValue, long.MinValue, 0, long.MaxValue - 1, long.MinValue + C + 2, long.MinValue + C + 1, long.MinValue + C, long.MinValue + 1];
        foreach (long v in sent) Assert.True((await r.Command(Ts).Timestamptz(v).Run(null, null)).IsOk);
        var binds = server.Connections.Single().Batches.SelectMany(b => b).Where(m => m.Type == 'B')
            .Select(m => BinaryPrimitives.ReadInt64BigEndian(m.Bind().Params.Single()));
        // ±infinity as Postgres keeps them; the rest moved to 2000, except where that would reach -infinity or wrap:
        // there one past -infinity, which Postgres refuses as out of range
        Assert.Equal([long.MaxValue, long.MinValue, -C, long.MaxValue - 1 - C, long.MinValue + 2, long.MinValue + 1, long.MinValue + 1, long.MinValue + 1], binds);
    }

    async Task<PgResult> OneRow(byte[]?[] row, PgRowHandler handler, params uint[] types)
    {
        var r = Reader(new FakeServer { Rows = _ => [row], Columns = types.Length == 0 ? null : _ => types });
        r.Start(1);
        return await r.Command(Echo).Int8(1).Run(handler, null);
    }

    const long EpochTicks = 630822816000000000, MinUs = -EpochTicks / 10, MaxUs = (3155378975999999999 - EpochTicks) / 10;

    [Fact(Timeout = 20_000)]
    public async Task RowGettersRefuseNullsAndWrongSizesAndDateTimesPastItsRange()
    {
        static void Throws(PgResult res)
        {
            Assert.Equal(PgStatus.HandlerFailed, res.Status);
            Assert.IsType<InvalidCastException>(res.Exception);
        }
        Throws(await OneRow([null], static (in PgRow r, object? _) => r.GetInt64(0), 20));
        Throws(await OneRow([[1, 0]], static (in PgRow r, object? _) => r.GetBoolean(0) ? 1 : 0, 16)); // a bool of two bytes
        Throws(await OneRow([[0, 0, 0, 1]], static (in PgRow r, object? _) => r.GetDateTime(0).Ticks, 1184));
        Throws(await OneRow([ServerWriter.BE64(MinUs - 1)], static (in PgRow r, object? _) => r.GetDateTime(0).Ticks, 1184));
        Throws(await OneRow([ServerWriter.BE64(MaxUs + 1)], static (in PgRow r, object? _) => r.GetDateTime(0).Ticks, 1184));
        var min = await OneRow([ServerWriter.BE64(MinUs)], static (in PgRow r, object? _) => r.GetDateTime(0).Ticks, 1184);
        Assert.Equal((PgStatus.Ok, DateTime.MinValue.Ticks), (min.Status, min.Value));
        var max = await OneRow([ServerWriter.BE64(MaxUs)], static (in PgRow r, object? _) => r.GetDateTime(0).Ticks, 1184);
        Assert.Equal((PgStatus.Ok, EpochTicks + MaxUs * 10), (max.Status, max.Value));
    }

    [Fact(Timeout = 20_000)]
    public async Task JsonbIsReadWithoutItsVersionByteAndAnUnknownVersionIsRefused()
    {
        var ok = await OneRow([[1, .. "[1]"u8]], static (in PgRow r, object? _) => r.GetJson(0).SequenceEqual("[1]"u8) ? 1 : 0, 3802);
        Assert.Equal(1, ok.Value);
        var json = await OneRow([[1, .. "[1]"u8]], static (in PgRow r, object? _) => r.GetJson(0).Length, 114); // json: every byte its own
        Assert.Equal(4, json.Value);
        var v2 = await OneRow([[2, .. "[1]"u8]], static (in PgRow r, object? _) => r.GetJson(0).Length, 3802);
        Assert.Equal(PgStatus.HandlerFailed, v2.Status);
        Assert.IsType<InvalidCastException>(v2.Exception);
    }

    [Fact(Timeout = 20_000)]
    public async Task ARowWithManyColumnsReadsEveryOne()
    {
        byte[]?[] row = [.. Enumerable.Range(0, 70).Select(i => ServerWriter.BE64(i))]; // past twice the 32 a connection starts with
        var res = await OneRow(row, static (in PgRow r, object? _) =>
        {
            long sum = 0;
            for (int c = 0; c < r.FieldCount; c++) sum += r.GetInt64(c);
            return sum * 100 + r.FieldCount;
        });
        Assert.Equal(2415 * 100 + 70, res.Value);
    }

    [Fact(Timeout = 20_000)]
    public async Task ARowLargerThanTheReceiveBufferArrivesWhole()
    {
        byte[] big = new byte[5000];
        new Random(7).NextBytes(big);
        var server = new FakeServer { Rows = _ => [[big, ServerWriter.BE64(9)]] };
        var settings = server.Settings();
        settings.ReceiveBufferSize = 64;
        var r = Reader(settings);
        r.Start(1);
        var seen = new Seen();
        Assert.Equal(PgStatus.Ok, (await Read(r, 1, seen)).Status);
        Assert.Equal(big, seen.Rows.Single()[0]);
    }

    static byte[] Field(char f, string v) => [(byte)f, .. Encoding.UTF8.GetBytes(v), 0];

    async Task<PgResult> FailWith(byte[] errorBody)
    {
        var server = new FakeServer();
        server.Respond = (c, _) => c.Write(new ServerWriter().Message('E', errorBody).Ready().ToArray());
        var r = Reader(server);
        r.Start(1);
        return await Read(r, 1);
    }

    [Fact(Timeout = 20_000)]
    public async Task AnErrorResponseKeepsWhatTheServerSaid()
    {
        var both = (await FailWith([.. Field('S', "FEHLER"), .. Field('V', "ERROR"), .. Field('C', "22012"), .. Field('M', "x"), 0])).Error!;
        Assert.Equal("ERROR", both.Severity); // the untranslated one
        Assert.Equal("FEHLER", (await FailWith([.. Field('S', "FEHLER"), .. Field('C', "22012"), .. Field('M', "x"), 0])).Error!.Severity);
        Assert.Equal("ERROR", (await FailWith([.. Field('C', "22012"), .. Field('M', "x"), 0])).Error!.Severity);

        var empty = await FailWith([.. Field('V', "ERROR"), .. Field('C', "22012"), .. Field('M', ""), 0]);
        Assert.Equal(PgStatus.ServerError, empty.Status);
        Assert.Equal("", empty.Error!.MessageText);

        Assert.Equal("XX", (await FailWith([.. Field('V', "ERROR"), .. Field('C', "XX"), .. Field('M', "x"), 0])).SqlState);
        // a known code is the constant itself: reading it allocates no string
        Assert.Same(PgErrorCodes.UniqueViolation, (await FailWith([.. Field('V', "ERROR"), .. Field('C', "23505"), .. Field('M', "x"), 0])).SqlState);
    }

    [Fact(Timeout = 20_000)]
    public async Task AReadIsAnsweredWhenItsRepliesArriveWithoutWaitingForTheRestOfItsGroup()
    {
        var firstAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool restCameAfterTheAnswer = false;
        var server = new FakeServer { Rows = EchoRows };
        server.Respond = (c, batch) =>
        {
            var probe = new FakeConnection(new FakeServer { Rows = EchoRows });
            new FakeServer { Rows = EchoRows }.Auto(probe, batch);
            var all = new byte[probe.Available];
            probe.Receive(all);
            var messages = Split(all);
            int firstEnd = messages.FindIndex(m => m[0] == (byte)'C') + 1;
            c.Write([.. messages.Take(firstEnd).SelectMany(m => m)]);
            _ = Task.Run(async () =>
            {
                restCameAfterTheAnswer = await Task.WhenAny(firstAnswered.Task, Task.Delay(3000)) == firstAnswered.Task;
                c.Write([.. messages.Skip(firstEnd).SelectMany(m => m)]);
            });
        };
        var r = Reader(server);
        var first = Read(r, 1, null, Echo);
        var second = Read(r, 2, null, Echo2);
        r.Start(1);
        Assert.Equal(1, (await first).Value);
        firstAnswered.SetResult();
        Assert.Equal(2, (await second).Value);
        Assert.True(restCameAfterTheAnswer);
    }

    [Fact(Timeout = 20_000)]
    public async Task FortyQueuedReadsGoOutInGroupsOfAtMost32AndEachIsAnswered()
    {
        var server = new FakeServer { Rows = EchoRows };
        var r = Reader(server);
        var tasks = Enumerable.Range(1, 40).Select(i => Read(r, i)).ToArray();
        r.Start(1);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), (await Task.WhenAll(tasks)).Select(x => x.Value));
        Assert.Equal([PgReader.MaxGroup, 40 - PgReader.MaxGroup], server.Connections.Single().Batches.Select(b => b.Count(m => m.Type == 'B')));
    }

    [Fact(Timeout = 20_000)]
    public async Task FortyWritesInOneGroupAreEachAnswered()
    {
        var server = new FakeServer { Rows = EchoRows };
        var w = Writer(server);
        var tasks = Enumerable.Range(1, 40).Select(i => w.Command(Echo).Int8(i).Run(Collect, new Seen()).AsTask()).ToArray(); // past the 32 answers a connection starts with
        w.Start(1);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), (await Task.WhenAll(tasks)).Select(x => x.Value));
        Assert.Equal(40, server.Connections.Single().Batches.Single().Count(m => m.Type == 'B'));
    }

    static readonly PgStatement OneText = new("one text", PgType.Text);

    [Fact]
    public void ALoneSurrogateIsRefusedInsteadOfSentAsAReplacementCharacter()
    {
        var r = Reader(new FakeServer());
        var cmd = r.Command(OneText);
        bool refused;
        try
        {
            cmd.Text("a\ud800b".AsSpan());
            refused = false;
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        Assert.True(refused);
        // the command went back with its pooled job: Hold refuses it
        try
        {
            cmd.Hold();
            refused = false;
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        Assert.True(refused);
    }

    [Fact(Timeout = 20_000)]
    public async Task ALongStartupParameterGoesOutWhole()
    {
        var server = new FakeServer { Rows = EchoRows };
        var settings = server.Settings().Set("application_name", new string('a', 5000));
        var r = Reader(settings);
        r.Start(1);
        Assert.Equal(1, (await Read(r, 1)).Value);
        Assert.Equal(new string('a', 5000), server.Connections[0].StartupParameters["application_name"]);
    }

    [Fact(Timeout = 20_000)]
    public async Task RowsAWriteReturnsReachItsHandlerWholeOnceItCommitted()
    {
        byte[] big = new byte[70_000];
        new Random(3).NextBytes(big);
        var server = new FakeServer { Rows = _ => [[[1, 2, 3]], [big], [[4]]] };
        var w = Writer(server);
        w.Start(1);
        var seen = new Seen();
        var res = await w.Command(Echo).Int8(1).Run(Collect, seen);
        Assert.Equal(PgStatus.Ok, res.Status);
        Assert.Equal([[1, 2, 3], big, [4]], seen.Rows.Select(row => row[0]!));
    }

    /// <summary>One random parameter: its type, the call that binds it, and what the server must decode.</summary>
    sealed record Param(PgType Type, object? Value);

    static Param RandomParam(Random rng)
    {
        switch (rng.Next(9))
        {
            case 0: return new(PgType.Int4, rng.Next(int.MinValue, int.MaxValue));
            case 1: return new(PgType.Int8, rng.NextInt64(long.MinValue, long.MaxValue));
            case 2: return new(PgType.Bool, rng.Next(2) == 1);
            case 3: return new(PgType.Float8, rng.NextDouble() * 1e9 - 5e8);
            case 4: return new(PgType.Timestamptz, rng.NextInt64(-1L << 50, 1L << 50));
            case 5: return new(PgType.Text, RandomText(rng, rng.Next(4) == 0 ? 70_000 : 300));
            case 6: return new(PgType.Text, Enumerable.Range(0, rng.Next(rng.Next(4) == 0 ? 70_000 : 300)).Select(_ => (byte)rng.Next(32, 127)).ToArray());
            case 7: return new(PgType.Int8, null);
            default: return new(PgType.TextArray, Enumerable.Range(0, rng.Next(40)).Select(_ => rng.Next(5) == 0 ? null : RandomText(rng, 40)).ToArray());
        }
    }

    static string RandomText(Random rng, int maxChars)
    {
        var sb = new StringBuilder();
        int n = rng.Next(maxChars);
        while (sb.Length < n)
            sb.Append(rng.Next(4) switch { 0 => (char)rng.Next(32, 127), 1 => (char)rng.Next(0xA0, 0x800), 2 => (char)rng.Next(0x800, 0xD800), _ => "😀" });
        return sb.ToString();
    }

    static void Bind(ref PgCommand c, Param p)
    {
        switch (p.Value)
        {
            case null: c = c.Null(); break;
            case int v: c = c.Int4(v); break;
            case long v when p.Type == PgType.Timestamptz: c = c.Timestamptz(v); break;
            case long v: c = c.Int8(v); break;
            case bool v: c = c.Bool(v); break;
            case double v: c = c.Float8(v); break;
            case string v: c = c.Text(v.AsSpan()); break;
            case byte[] v: c = c.Text(v); break;
            case string?[] v: c = c.TextArray(v); break;
        }
    }

    static void Check(Param p, Bind b, int i)
    {
        switch (p.Value)
        {
            case null: Assert.Null(b.Params[i]); break;
            case int v: Assert.Equal(v, BinaryPrimitives.ReadInt32BigEndian(b.Params[i])); break;
            case long v when p.Type == PgType.Timestamptz: Assert.Equal(v - 946_684_800_000_000, b.Int8(i)); break;
            case long v: Assert.Equal(v, b.Int8(i)); break;
            case bool v: Assert.Equal([v ? (byte)1 : (byte)0], b.Params[i]!); break;
            case double v: Assert.Equal(v, BitConverter.Int64BitsToDouble(b.Int8(i))); break;
            case string v: Assert.Equal(v, b.Text(i)); break;
            case byte[] v: Assert.Equal(v, b.Params[i]); break;
            case string?[] v: Assert.Equal(v, b.TextArray(i)); break;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RandomParametersOfEveryTypeAndSizeDecodeAsTheyWereBound()
    {
        // Seeded: the sizes cross the pooled buffers' ends at many points, and a failure replays.
        var server = new FakeServer();
        var r = Reader(server);
        r.Start(1);
        var rng = new Random(20261020);
        for (int round = 0; round < 150; round++)
        {
            Param[] ps = [.. Enumerable.Range(0, rng.Next(1, 9)).Select(_ => RandomParam(rng))];
            var statement = new PgStatement($"random {round}", [.. ps.Select(p => p.Type)]);
            var res = await Send(r, statement, ps);
            Assert.Equal(PgStatus.Ok, res.Status);
            var bind = server.Connections.Single().Batches[^1].Single(m => m.Type == 'B').Bind();
            for (int i = 0; i < ps.Length; i++) Check(ps[i], bind, i);
        }

        static ValueTask<PgResult> Send(PgReader r, PgStatement statement, Param[] ps)
        {
            var c = r.Command(statement);
            foreach (var p in ps) Bind(ref c, p);
            return c.Run(null, null);
        }
    }
}
