using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Xunit;

#pragma warning disable xUnit1069, xUnit1051, xUnit1031

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>Authentication from RFC 5802/7677 and the protocol docs: fixed vectors, a scripted server, and a real
/// one per method when CODEFUSION_TEST_PG_{TRUST,CLEAR,MD5,SCRAM} name connection strings for users set up that way.</summary>
public sealed class PgAuthTests
{
    static readonly PgStatement One = new("SELECT 1::int8");

    [Fact]
    public void ScramMatchesTheRfc7677Example()
    {
        var scram = new PgScram("pencil", "user", "rOprNGfwEbeRWgbNEkqO");
        Assert.Equal("n,,n=user,r=rOprNGfwEbeRWgbNEkqO", Encoding.ASCII.GetString(scram.ClientFirst()));
        byte[] final = scram.ClientFinal("r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8);
        Assert.Equal("c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=",
            Encoding.ASCII.GetString(final));
        Assert.False(scram.Verified);
        scram.VerifyServerFinal("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="u8);
        Assert.True(scram.Verified);
    }

    const string ServerFirst = "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096";

    [Theory]
    [InlineData("v=7rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=")] // one bit off
    [InlineData("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4")]  // not base64
    [InlineData("v=")]
    [InlineData("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=")]
    [InlineData("e=invalid-proof")]
    [InlineData("")]
    public void ScramRefusesAServerFinalThatDoesntProveThePassword(string serverFinal)
    {
        var scram = new PgScram("pencil", "user", "rOprNGfwEbeRWgbNEkqO");
        scram.ClientFinal(Encoding.ASCII.GetBytes(ServerFirst));
        Assert.Throws<PgConnectionException>(() => scram.VerifyServerFinal(Encoding.ASCII.GetBytes(serverFinal)));
        Assert.False(scram.Verified);
    }

    [Fact]
    public void ScramPassesOnTheServersRefusal()
    {
        var scram = new PgScram("pencil", "user", "rOprNGfwEbeRWgbNEkqO");
        scram.ClientFinal(Encoding.ASCII.GetBytes(ServerFirst));
        var e = Assert.Throws<PgConnectionException>(() => scram.VerifyServerFinal("e=invalid-proof"u8));
        Assert.Contains("invalid-proof", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScramRefusesAServerFinalBeforeItsProof() =>
        Assert.Throws<PgConnectionException>(() => new PgScram("pencil", "user", "abc").VerifyServerFinal("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="u8));

    [Fact]
    public void ScramTakesEveryPrintableByteButNoControl()
    {
        new PgScram("pencil", "user", "abc").ClientFinal("r=abc~!z,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8); // '~' is the last printable
        Assert.Throws<PgConnectionException>(() => new PgScram("pencil", "user", "abc").ClientFinal("r=abc\u007f,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8));
        Assert.Throws<PgConnectionException>(() => new PgScram("pencil", "user", "abc").ClientFinal("r=abc\u001f,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8));
    }

    [Theory]
    [InlineData("r=xbc123,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096")]  // doesn't extend our nonce
    [InlineData("r=abc,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096")]     // adds nothing to it
    [InlineData("r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=0")]
    [InlineData("r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=-1")]
    [InlineData("r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=1048577")] // more work than any server asks for
    [InlineData("r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4x")]
    [InlineData("r=abcd,s=not*base64,i=4096")]
    [InlineData("m=ext,r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096")]
    [InlineData("s=W22ZaJ0SNY7soEsUEjb6gQ==,r=abcd,i=4096")]
    [InlineData("r=abcd,s=W22ZaJ0SNY7soEsUEjb6gQ==")]
    [InlineData("r=abcdÿ,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096")]
    public void ScramRefusesAMalformedOrHostileServerFirst(string serverFirst)
    {
        var scram = new PgScram("pencil", "user", "abc");
        Assert.Throws<PgConnectionException>(() => scram.ClientFinal(Encoding.Latin1.GetBytes(serverFirst)));
    }

    [Fact]
    public void ScramWithoutAFixedNonceUsesAFreshRandomOne()
    {
        string a = Encoding.ASCII.GetString(new PgScram("p").ClientFirst()), b = Encoding.ASCII.GetString(new PgScram("p").ClientFirst());
        Assert.StartsWith("n,,n=,r=", a);
        Assert.Equal(8 + 24, a.Length); // 18 random bytes
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Md5MatchesTheDocumentedFormula()
    {
        // "md5" + md5hex(md5hex(password + user) + salt); md5hex("postgrespostgres") = 3175bce1d3201d16594cebf9d7eb3f9d
        Assert.Equal("md568be9ed08db75f318087ab337aaea044\0", Encoding.ASCII.GetString(PgMd5.Response("postgres", "postgres", [1, 2, 3, 4])));
        Assert.Throws<PgConnectionException>(() => PgMd5.Response("u", "p", [1, 2, 3]));
    }

    static async Task<PgResult> Connect(FakeServer server, string? password, bool allowWeak = true)
    {
        var s = server.Settings();
        s.Password = password;
        s.AllowWeakPasswordAuth = allowWeak;
        var r = new PgReader(s);
        try
        {
            r.Start(1);
            return await r.Command(One).Run(null, null);
        }
        finally
        {
            LoopChecks.Stop(r);
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData("cleartext")]
    [InlineData("md5")]
    [InlineData("scram")]
    public async Task EachMethodLogsInWithTheRightPasswordAndIsRefusedWithAWrongOne(string method)
    {
        var server = new FakeServer { Auth = method, Password = "pä ss" };
        Assert.Equal(PgStatus.Ok, (await Connect(server, "pä ss")).Status);
        var wrong = await Connect(server, "pä sx");
        Assert.Equal(PgStatus.Broken, wrong.Status);
        Assert.Equal(PgErrorCodes.InvalidPassword, Assert.IsType<PgException>(wrong.Exception).SqlState);
        var none = await Connect(server, null);
        Assert.Contains("no", Assert.IsType<PgConnectionException>(none.Exception).Message);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("cleartext")]
    [InlineData("md5")]
    public async Task ACleartextOrMd5RequestIsRefusedUnlessAllowed(string method)
    {
        // a server posing as Postgres would get the password, or a hash to crack, for the asking
        var res = await Connect(new FakeServer { Auth = method }, "secret", allowWeak: false);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.Contains("AllowWeakPasswordAuth", Assert.IsType<PgConnectionException>(res.Exception).Message);
        Assert.Equal(PgStatus.Ok, (await Connect(new FakeServer { Auth = "scram" }, "secret", allowWeak: false)).Status);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("u\0options\0-c search_path=evil", "d")]
    [InlineData("u", "d\0options\0-c search_path=evil")]
    public async Task AUserOrDatabaseHoldingNulIsRefusedBeforeItReachesTheStartupPacket(string user, string database)
    {
        var server = new FakeServer();
        var s = server.Settings();
        s.User = user;
        s.Database = database;
        var r = new PgReader(s);
        r.Start(1);
        var res = await r.Command(One).Run(null, null);
        LoopChecks.Stop(r);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.Contains("NUL", res.Exception!.Message);
        Assert.Empty(server.Connections.SelectMany(c => c.StartupParameters.Keys));
    }

    [Fact(Timeout = 20_000)]
    public async Task AServerThatNeverFinishesTheStartupTimesOutWithinTheConnectTimeout()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var s = new PgSettings { Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, User = "u", ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        var r = new PgReader(s);
        r.Start(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await r.Command(One).Run(null, null);
        LoopChecks.Stop(r);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.IsType<TimeoutException>(Assert.IsType<PgConnectionException>(res.Exception).InnerException);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ScramTakesOneServerFirstMessageOnly()
    {
        var scram = new PgScram("pencil", "user", "rOprNGfwEbeRWgbNEkqO");
        scram.ClientFinal(Encoding.ASCII.GetBytes(ServerFirst));
        Assert.Throws<PgConnectionException>(() => scram.ClientFinal(Encoding.ASCII.GetBytes(ServerFirst)));
    }

    [Fact(Timeout = 20_000)]
    public async Task ScramRefusesAServerWithTheWrongSignature()
    {
        var server = new FakeServer { Auth = "scram", ForgedSignature = new byte[32] };
        var res = await Connect(server, server.Password);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.Contains("signature", Assert.IsType<PgConnectionException>(res.Exception).Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task ScramRefusesAServerThatSaysOkWithoutProvingItKnowsThePassword()
    {
        var server = new FakeServer { Auth = "scram", SkipFinal = true };
        var res = await Connect(server, server.Password);
        Assert.Equal(PgStatus.Broken, res.Status);
        Assert.Contains("signature", Assert.IsType<PgConnectionException>(res.Exception).Message);
    }

    [Fact(Timeout = 20_000)]
    public async Task ScramRefusesAServerThatAsksForAMillionIterations()
    {
        var server = new FakeServer { Auth = "scram", Iterations = PgScram.MaxIterations + 1 };
        Assert.IsType<PgConnectionException>((await Connect(server, server.Password)).Exception);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(4096, false, true)]
    [InlineData(4097, false, true)]
    [InlineData(4095, false, false)] // below Postgres's own count: a server posing as it, to crack the proof
    [InlineData(1, false, false)]
    [InlineData(4095, true, true)]
    [InlineData(1, true, true)]
    public async Task ScramBelow4096IterationsIsRefusedUnlessWeakAuthIsAllowed(int iterations, bool allowWeak, bool ok)
    {
        var server = new FakeServer { Auth = "scram", Iterations = iterations };
        var res = await Connect(server, server.Password, allowWeak);
        Assert.Equal(ok ? PgStatus.Ok : PgStatus.Broken, res.Status);
        if (!ok) Assert.Contains("AllowWeakPasswordAuth", Assert.IsType<PgConnectionException>(res.Exception).Message);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServerThatLetsAPasswordUserInWithoutAskingForItIsRefusedUnlessWeakAuthIsAllowed(bool allowWeak)
    {
        // a server posing as Postgres skips SCRAM and gets every query and its parameters, and answers them
        var server = new FakeServer { Rows = static b => [[ServerWriter.BE64(1)]] };
        var s = server.Settings();
        s.Password = "secret";
        s.AllowWeakPasswordAuth = allowWeak;
        var r = new PgReader(s);
        PgResult res;
        try
        {
            r.Start(1);
            res = await r.Command(One).Run(null, null);
        }
        finally
        {
            LoopChecks.Stop(r);
        }
        Assert.Equal(allowWeak ? PgStatus.Ok : PgStatus.Broken, res.Status);
        if (!allowWeak)
        {
            Assert.Contains("AllowWeakPasswordAuth", Assert.IsType<PgConnectionException>(res.Exception).Message);
            Assert.Empty(server.Connections[0].Batches); // nothing sent to it
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task TrustWithoutAPasswordLogsIn()
    {
        var server = new FakeServer();
        Assert.Equal(PgStatus.Ok, (await Connect(server, null, allowWeak: false)).Status);
    }

    // ---------- a real server ----------

    static PgSettings? Real(string variable)
    {
        string? cs = Environment.GetEnvironmentVariable(variable);
        Assert.SkipWhen(cs is null, $"{variable} is not set");
        return PgSettings.Parse(cs!);
    }

    static async Task<PgResult> Connect(PgSettings s)
    {
        var r = new PgReader(s);
        try
        {
            r.Start(1);
            return await r.Command(One).Run(static (in PgRow row, object? _) => row.GetInt64(0), null);
        }
        finally
        {
            LoopChecks.Stop(r);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("PGSSLMODE", "require", "", false)]
    [InlineData("PGSSLMODE", "verify-full", "", false)]
    [InlineData("PGREQUIRESSL", "1", "", false)]
    [InlineData("PGSSLNEGOTIATION", "direct", "", false)]
    [InlineData("PGSSLROOTCERT", "system", "", false)]
    [InlineData("PGGSSENCMODE", "require", "", false)]
    [InlineData("PGSSLMODE", "require", ";sslmode=disable", true)]   // the connection string's own sslmode wins, as in libpq
    [InlineData("PGREQUIRESSL", "1", ";sslmode=prefer", true)]
    [InlineData("PGSSLROOTCERT", "system", ";sslmode=disable", true)]
    [InlineData("PGGSSENCMODE", "require", ";gssencmode=disable", true)]
    [InlineData("PGSSLMODE", "prefer", "", true)]
    [InlineData("PGSSLMODE", "disable", "", true)]
    [InlineData("PGREQUIRESSL", "0", "", true)]
    [InlineData("PGSSLROOTCERT", "/etc/ca.pem", "", true)]
    public async Task AnEnvironmentVariableThatAsksForTlsMakesConnectingFailInsteadOfGoingOnInTheClear(string variable, string value, string extra, bool connects)
    {
        PgTestDb.Require();
        var s = PgSettings.Parse(PgTestDb.ConnectionString + extra);
        s.Environment = name => name == variable ? value : null;
        var res = await Connect(s);
        if (connects) Assert.Equal(1, res.Value);
        else
        {
            Assert.Equal(PgStatus.Broken, res.Status);
            var e = Assert.IsType<PgConnectionException>(res.Exception);
            Assert.Contains(variable, e.Message, StringComparison.Ordinal);
            Assert.Contains("TLS", e.Message, StringComparison.Ordinal);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("CODEFUSION_TEST_PG_TRUST")]
    [InlineData("CODEFUSION_TEST_PG_CLEAR")]
    [InlineData("CODEFUSION_TEST_PG_MD5")]
    [InlineData("CODEFUSION_TEST_PG_SCRAM")]
    public async Task ARealServerLetsTheRightPasswordInAndRefusesAWrongOne(string variable)
    {
        var s = Real(variable)!;
        s.AllowWeakPasswordAuth = true;
        Assert.Equal(1, (await Connect(s)).Value);
        if (variable == "CODEFUSION_TEST_PG_TRUST") return;
        s.Password += "x";
        var wrong = await Connect(s);
        Assert.Equal(PgStatus.Broken, wrong.Status);
        Assert.Equal(PgErrorCodes.InvalidPassword, Assert.IsType<PgException>(wrong.Exception).SqlState);
    }
}
