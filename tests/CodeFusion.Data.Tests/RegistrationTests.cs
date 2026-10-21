using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using Conduit.Data;
using Xunit;
// inside namespace CodeFusion.Data.*, "Postgres" is the namespace CodeFusion.Data.Postgres first
using PostgresProvider = CodeFusion.Data.Postgres.Postgres;

namespace CodeFusion.Data.Tests;

/// <summary>AddDatabase registers what CodeFusion.Http's app builder hands its handlers.</summary>
[Collection(nameof(ConduitCollection))]
public sealed class RegistrationTests(ConduitFixture fixture)
{
    /// <summary>What an app builder records: the services, in order, and what runs when the app stops.</summary>
    sealed class Registry : IServiceRegistry
    {
        public readonly List<object> Singletons = new();
        public readonly List<Func<object>> PerConnection = new();
        public readonly List<Action> Stopping = new();

        public void AddSingleton<TService>(TService instance) where TService : class => Singletons.Add(instance);

        public void AddPerConnection<TService>(Func<TService> factory) where TService : class, IRequestScoped => PerConnection.Add(factory);

        public void OnStopping(Action onStopping) => Stopping.Add(onStopping);
        public void OnEventLoopBatch(Action<int> onBatch) => Batches.Add(onBatch);
        public readonly List<Action<int>> Batches = [];
    }

    [Fact]
    public async Task AddDatabaseRegistersAContextPerConnectionAndStopsTheLoopsWithTheApp()
    {
        _ = fixture;
        TestDb.Require();
        var b = new Npgsql.NpgsqlConnectionStringBuilder(TestDb.ConnectionString!);
        string url = $"postgres://{Uri.EscapeDataString(b.Username!)}:{Uri.EscapeDataString(b.Password!)}@{b.Host}:{b.Port}/{b.Database}";
        var database = PostgresProvider.FromUrl(url);
        database.Settings.Set("search_path", ConduitFixture.SchemaName);
        var registry = new Registry();
        var source = registry.AddDatabase<ConduitDb>(database, readers: 2, writers: 1);

        Assert.Same(source, Assert.Single(registry.Singletons));
        var factory = Assert.Single(registry.PerConnection);
        var first = (ConduitDb)factory();
        var second = (ConduitDb)factory();
        Assert.NotSame(first, second); // one per connection
        Assert.Same(source, first.Source);

        var response = new FakeResponse();
        first.Bind(response);
        var bytes = Encoding.UTF8.GetBytes("bob");
        await first.Profile(new Utf8(bytes, 0, bytes.Length), 0).WriteAsync(response);
        Assert.Equal("""{"profile":{"username":"bob","bio":"I write \"quotes\" and naïve UTF-8 ✓","image":null,"following":false}}""", response.Text);

        // the reader's sessions start with the contest server's settings; the writer's add the lock timeout
        Assert.Contains(source.Reader is not null ? database.Settings.Parameters : [], p => p.Key == "plan_cache_mode" && p.Value == "force_generic_plan");
        Assert.Contains(database.Settings.Parameters, p => p.Key == "statement_timeout" && p.Value == "30s");

        Assert.Single(registry.Stopping)();
        response.Next();
        await first.Profile(new Utf8(bytes, 0, bytes.Length), 0).WriteAsync(response);
        Assert.Equal(500, response.Status); // stopped
    }

    // An app that builds its source itself (its own loops, error replies or failure log) registers that one.
    [Fact]
    public async Task AddDatabaseTakesASourceTheAppBuiltItself()
    {
        _ = fixture;
        TestDb.Require();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("search_path", ConduitFixture.SchemaName);
        var failures = new List<string>();
        var source = new PgDataSource(new PgReader(settings), new PgWriter(settings.Clone())) { OnFailure = (name, _) => failures.Add(name) };
        source.Reader.Start(1);
        source.Writer.Start(1);
        var registry = new Registry();
        Assert.Same(source, registry.AddDatabase<ConduitDb>(source));

        Assert.Same(source, Assert.Single(registry.Singletons));
        var factory = Assert.Single(registry.PerConnection);
        var first = (ConduitDb)factory();
        Assert.NotSame(first, factory());
        Assert.Same(source, first.Source);
        var response = new FakeResponse();
        first.Bind(response);
        var bytes = Encoding.UTF8.GetBytes("bob");
        await first.Profile(new Utf8(bytes, 0, bytes.Length), 0).WriteAsync(response);
        Assert.Equal(200, response.Status);
        Assert.StartsWith("""{"profile":{"username":"bob",""", response.Text, StringComparison.Ordinal);
        Assert.Empty(failures);

        Assert.Single(registry.Stopping)();
        response.Next();
        await first.Profile(new Utf8(bytes, 0, bytes.Length), 0).WriteAsync(response);
        Assert.Equal(500, response.Status); // stopped
        // stopping it again, as an app that also stops its own source would, is harmless
        source.Stop();

        Assert.Throws<ArgumentNullException>(() => registry.AddDatabase<ConduitDb>((PgDataSource)null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceRegistry)null!).AddDatabase<ConduitDb>(source));
    }

    [Fact]
    public void ASessionSettingOfTheCallersOwnIsKept()
    {
        TestDb.Require();
        var settings = PgSettings.Parse(TestDb.ConnectionString!).Set("statement_timeout", "5s");
        var database = PostgresProvider.FromSettings(settings);
        Assert.Contains(database.Settings.Parameters, p => p.Key == "statement_timeout" && p.Value == "5s");
        Assert.DoesNotContain(database.Settings.Parameters, p => p.Key == "statement_timeout" && p.Value == "30s");
        // the caller's settings object isn't changed
        Assert.DoesNotContain(settings.Parameters, p => p.Key == "plan_cache_mode");
        Assert.Throws<ArgumentOutOfRangeException>(() => database.Open(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => database.Open(1, 0));
    }
}
