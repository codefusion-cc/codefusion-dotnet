using System;
using Xunit;

namespace CodeFusion.Tests;

/// <summary>
/// The CodeFusion packages share internals, so a package refuses to load beside another CodeFusion package of a
/// different version, and says which and what to do.
/// </summary>
public sealed class PackageVersionsTests
{
    [Fact]
    public void OneVersionThroughoutPasses() =>
        Assert.Null(PackageVersions.Mismatch("CodeFusion.Data.Sqlite", "1.2.3", [("CodeFusion.Core", "1.2.3"), ("CodeFusion.Data", "1.2.3")]));

    [Fact]
    public void NothingElseLoadedPasses() => Assert.Null(PackageVersions.Mismatch("CodeFusion.Http", "1.2.3", []));

    [Theory]
    [InlineData("1.2.4")]
    [InlineData("1.2.2")]
    [InlineData("1.2.3-beta")]
    [InlineData("")]
    public void AnyOtherVersionIsRefusedWithWhatToDo(string other)
    {
        string? why = PackageVersions.Mismatch("CodeFusion.Data.Sqlite", "1.2.3", [("CodeFusion.Core", "1.2.3"), ("CodeFusion.Data", other)]);
        Assert.NotNull(why);
        Assert.Contains($"CodeFusion.Data {other}", why, StringComparison.Ordinal);
        Assert.Contains("update CodeFusion.Data to 1.2.3", why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssemblyOfAnotherVersionThrows()
    {
        // the runtime's own library stands in for a CodeFusion package of another version
        var e = Assert.Throws<InvalidOperationException>(() => PackageVersions.Check(typeof(Utf8).Assembly, typeof(object).Assembly));
        Assert.Contains("must all be the same version", e.Message, StringComparison.Ordinal);
    }
}
