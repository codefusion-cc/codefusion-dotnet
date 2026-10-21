using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// Where e_sqlite3 comes from: the app's own directory when it ships one there (an app that builds SQLite itself),
/// else the package's native asset through deps.json. Nowhere else.
/// </summary>
public sealed class NativeLibraryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cf-sqlite-native-" + Guid.NewGuid().ToString("N")[..8]);

    public NativeLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The package's library for this machine, as the test build copied it.</summary>
    static string PackageLibrary() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"))
            .Single(f => Path.GetFileName(f).Contains("e_sqlite3", StringComparison.Ordinal));

    [Fact]
    public void ALibraryInTheGivenDirectoryIsLoaded()
    {
        File.Copy(PackageLibrary(), Path.Combine(_dir, Sqlite3.LibraryFileName));
        nint handle = Sqlite3.LoadFrom(_dir);
        Assert.NotEqual(0, handle);
        Assert.True(NativeLibrary.TryGetExport(handle, "sqlite3_libversion_number", out _));
    }

    [Fact]
    public void ALibraryInTheGivenDirectoryCanSayItsBuild()
    {
        // the build check looks the function up in the library the resolver loaded, not by name (which misses it)
        File.Copy(PackageLibrary(), Path.Combine(_dir, Sqlite3.LibraryFileName));
        Assert.NotNull(Sqlite3.CompileOptionUsedIn(Sqlite3.LoadFrom(_dir)));
        Assert.Null(Sqlite3.CompileOptionUsedIn(0));
    }

    [Fact]
    public void ADirectoryWithoutOneLoadsNothing()
    {
        Assert.Equal(0, Sqlite3.LoadFrom(_dir));
        // a file of another name is not taken for it
        File.Copy(PackageLibrary(), Path.Combine(_dir, "not-" + Sqlite3.LibraryFileName));
        Assert.Equal(0, Sqlite3.LoadFrom(_dir));
    }

    [Fact]
    public void TheFileNameIsThePlatforms() =>
        Assert.Equal(OperatingSystem.IsWindows() ? "e_sqlite3.dll" : OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so", Sqlite3.LibraryFileName);
}
