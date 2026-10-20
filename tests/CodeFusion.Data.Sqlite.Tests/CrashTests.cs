using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Data.Sqlite;
using CodeFusion.Data.Tests;
using Xunit;

namespace CodeFusion.Data.Sqlite.Tests;

/// <summary>
/// Crash recovery: a child process (CodeFusion.Data.Sqlite.CrashChild) commits 100
/// writes, then either starts a group that takes seconds or commits one more write, says so on its output, and is
/// killed. Reopened, the file has every write that completed and nothing of the group that didn't.
/// </summary>
public sealed class CrashTests : IDisposable
{
    readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("mid-group", SqliteSync.Normal)]
    [InlineData("mid-group", SqliteSync.Full)]
    [InlineData("after-commit", SqliteSync.Normal)]
    [InlineData("after-commit", SqliteSync.Full)]
    public async Task AKilledProcessLeavesEveryCommittedWriteAndNoneOfTheGroupInFlight(string mode, SqliteSync sync)
    {
        string path = _db.File();
        // the child is a program of its own, built next to these tests; the dotnet host runs it
        string host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CodeFusion.Data.Sqlite.CrashChild.dll"));
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(((int)sync).ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var child = Process.Start(start)!;
        var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        string? line;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                while ((line = await child.StandardOutput.ReadLineAsync(wait.Token)) is not null && !line.StartsWith("READY", StringComparison.Ordinal)) { }
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("the child wasn't ready in 30 s: " + await stderr);
            }
        }
        if (line is null) Assert.Fail("the child ended before it was ready: " + await stderr);
        if (mode == "mid-group") await Task.Delay(300, TestContext.Current.CancellationToken); // well inside the long group
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);

        var s = _db.Open(new SqliteDatabase(path) { Synchronous = sync, Readers = 1, Migrations = Schema });
        Assert.Equal(100, Kv.Count(s, "committed-%"));
        Assert.Equal(0, Kv.Count(s, "crash-%"));
        Assert.Equal(mode == "after-commit" ? 1 : 0, Kv.Count(s, "marker"));
        var check = s.Read(new SqliteStatement("PRAGMA integrity_check"));
        var rows = new System.Collections.Generic.List<string?[]>();
        Assert.True(Kv.Done(Sqlite.Run<Dump>(check.Hold(), rows, default)).IsOk);
        Assert.Equal("ok", rows[0][0]);
    }

    static readonly string[] Schema = ["CREATE TABLE kv (k TEXT PRIMARY KEY, v INTEGER NOT NULL) WITHOUT ROWID"];

}
