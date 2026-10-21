using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodeFusion.Http;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// The CodeFusion packages this app loads (Core, Data, Data.Sqlite, Http) are one version, and each package but Core
/// checks so as it loads: the shared build writes a module initializer into it (src/Directory.Build.props).
/// </summary>
public sealed class PackageVersionTests
{
    [Fact]
    public void TheCodeFusionAssembliesLoadedHereAreOneVersion() =>
        PackageVersions.Check(typeof(SqliteDataSource).Assembly, typeof(Utf8).Assembly, typeof(DataContext).Assembly, typeof(HttpServer).Assembly);

    [Theory]
    [InlineData(typeof(HttpServer), "CodeFusion.Http")]
    [InlineData(typeof(DataContext), "CodeFusion.Data")]
    [InlineData(typeof(SqliteDataSource), "CodeFusion.Data.Sqlite")]
    public void EachPackageChecksItsVersionAsItLoads(Type inPackage, string rootNamespace)
    {
        var check = inPackage.Assembly.GetType(rootNamespace + ".CodeFusionVersions");
        Assert.NotNull(check);
        var method = check.GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<ModuleInitializerAttribute>());
    }

    [Fact]
    public void CoreChecksNothing() => Assert.Null(typeof(Utf8).Assembly.GetType("CodeFusion.CodeFusionVersions"));
}
