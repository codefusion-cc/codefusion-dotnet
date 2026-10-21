using System.Reflection;
using System.Runtime.CompilerServices;
using CodeFusion.Data.Postgres;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>CodeFusion.Data.Postgres checks, as it loads, that the CodeFusion packages it uses are its version, and the
/// ones loaded here are.</summary>
public sealed class PackageVersionTests
{
    [Fact]
    public void ThePostgresPackageChecksItsVersionAsItLoads()
    {
        var check = typeof(PgSettings).Assembly.GetType("CodeFusion.Data.Postgres.CodeFusionVersions");
        Assert.NotNull(check);
        Assert.NotNull(check.GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic)?.GetCustomAttribute<ModuleInitializerAttribute>());
    }

    [Fact]
    public void TheCodeFusionAssembliesLoadedHereAreOneVersion() =>
        Assert.Equal(VersionOf(typeof(DataContext).Assembly), VersionOf(typeof(PgSettings).Assembly));

    static string? VersionOf(Assembly a) => a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
