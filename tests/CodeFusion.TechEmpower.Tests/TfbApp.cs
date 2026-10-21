using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace CodeFusion.TechEmpower.Tests;

/// <summary>
/// TFB's database (World with 10,000 rows, Fortune with its 12) in a database of its own on CODEFUSION_TEST_DB's
/// server, and the app started against it as TFB starts it: its own process, DATABASE_URL, a port.
/// </summary>
public sealed class TfbApp : IAsyncLifetime
{
    public const string Database = "codefusion_tfb_test";

    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CODEFUSION_TEST_DB");

    readonly System.Collections.Generic.List<Process> _apps = [];
    readonly StringBuilder _output = new();

    public NpgsqlDataSource Db { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public IPEndPoint EndPoint { get; private set; } = null!;

    public static void Require() => Assert.SkipWhen(ConnectionString is null, "CODEFUSION_TEST_DB is not set");

    public async ValueTask InitializeAsync()
    {
        if (ConnectionString is null) return;
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString);
        await using (var server = NpgsqlDataSource.Create(admin.ConnectionString))
        {
            await Exec(server, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await Exec(server, $"CREATE DATABASE {Database}");
        }
        var b = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = Database };
        Db = NpgsqlDataSource.Create(b.ConnectionString);
        await Exec(Db, Schema);
        EndPoint = await StartAsync($"postgres://{b.Username}:{b.Password}@{b.Host}:{b.Port}/{Database}");
        Http = new HttpClient { BaseAddress = new Uri($"http://{EndPoint}") };
    }

    /// <summary>The app on a free port, once it answers.</summary>
    public async Task<IPEndPoint> StartAsync(string databaseUrl)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var start = new ProcessStartInfo("dotnet", [AppPath()]) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DATABASE_URL"] = databaseUrl;
        start.Environment["HOST"] = "127.0.0.1";
        start.Environment["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["DB_READERS"] = "2";
        var app = Process.Start(start)!;
        app.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        app.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        app.BeginOutputReadLine();
        app.BeginErrorReadLine();
        lock (_apps) _apps.Add(app);
        var ep = new IPEndPoint(IPAddress.Loopback, port);
        for (int i = 0; i < 200; i++)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(ep);
                return ep;
            }
            catch (SocketException)
            {
                if (app.HasExited) throw new InvalidOperationException("the app exited: " + Output);
                await Task.Delay(50);
            }
        }
        throw new TimeoutException("the app didn't listen: " + Output);
    }

    public string Output
    {
        get
        {
            lock (_output) return _output.ToString();
        }
    }

    static string AppPath()
    {
        // tests/CodeFusion.TechEmpower.Tests/bin/<configuration>/net10.0 → benchmarks/CodeFusion.TechEmpower/bin/<configuration>/net10.0
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string configuration = here.Parent!.Name;
        string root = here.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, "benchmarks", "CodeFusion.TechEmpower", "bin", configuration, "net10.0", "CodeFusion.TechEmpower.dll");
    }

    public static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> RandomNumberOf(int id)
    {
        await using var cmd = Db.CreateCommand($"SELECT randomnumber FROM world WHERE id = {id}");
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        foreach (var app in _apps)
        {
            if (!app.HasExited)
            {
                app.Kill();
                await app.WaitForExitAsync();
            }
            app.Dispose();
        }
        if (Db is not null) await Db.DisposeAsync();
    }

    /// <summary>TFB's create-postgres.sql for World and Fortune (toolset/databases/postgres).</summary>
    const string Schema = """
        CREATE TABLE World (id integer NOT NULL, randomNumber integer NOT NULL default 0, PRIMARY KEY (id));
        INSERT INTO World (id, randomnumber) SELECT x.id, least(floor(random() * 10000 + 1), 10000) FROM generate_series(1,10000) as x(id);
        CREATE TABLE Fortune (id integer NOT NULL, message varchar(2048) NOT NULL, PRIMARY KEY (id));
        INSERT INTO Fortune (id, message) VALUES (1, 'fortune: No such file or directory');
        INSERT INTO Fortune (id, message) VALUES (2, 'A computer scientist is someone who fixes things that aren''t broken.');
        INSERT INTO Fortune (id, message) VALUES (3, 'After enough decimal places, nobody gives a damn.');
        INSERT INTO Fortune (id, message) VALUES (4, 'A bad random number generator: 1, 1, 1, 1, 1, 4.33e+67, 1, 1, 1');
        INSERT INTO Fortune (id, message) VALUES (5, 'A computer program does what you tell it to do, not what you want it to do.');
        INSERT INTO Fortune (id, message) VALUES (6, 'Emacs is a nice operating system, but I prefer UNIX. — Tom Christaensen');
        INSERT INTO Fortune (id, message) VALUES (7, 'Any program that runs right is obsolete.');
        INSERT INTO Fortune (id, message) VALUES (8, 'A list is only as strong as its weakest link. — Donald Knuth');
        INSERT INTO Fortune (id, message) VALUES (9, 'Feature: A bug with seniority.');
        INSERT INTO Fortune (id, message) VALUES (10, 'Computers make very fast, very accurate mistakes.');
        INSERT INTO Fortune (id, message) VALUES (11, '<script>alert("This should not be displayed in a browser alert box.");</script>');
        INSERT INTO Fortune (id, message) VALUES (12, 'フレームワークのベンチマーク');
        """;
}

[CollectionDefinition(nameof(TfbTests), DisableParallelization = true)]
public sealed class TfbTests : ICollectionFixture<TfbApp>;
