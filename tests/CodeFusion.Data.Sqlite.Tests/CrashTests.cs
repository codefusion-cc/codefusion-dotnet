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

    /// <summary>
    /// <see cref="SqliteDatabase.EventLoopBatches"/> under a kill: the child is an event loop whose every wake-up writes
    /// ten rows, a unit of two and 20,000 more in one transaction, and answers each write the moment it completes. It is
    /// killed 0 to 3 ms after a wake-up starts: while its writes run, while its batch commits, or as it answers. Reopened, the file has every
    /// write that was answered (a 201 means committed), and of each wake-up all of it or none of it: nothing unanswered
    /// is half there.
    /// </summary>
    [Theory]
    [InlineData(SqliteSync.Normal, 0)]
    [InlineData(SqliteSync.Normal, 1)]
    [InlineData(SqliteSync.Normal, 3)]
    [InlineData(SqliteSync.Full, 0)]
    [InlineData(SqliteSync.Full, 1)]
    [InlineData(SqliteSync.Full, 3)]
    public async Task AKilledEventLoopLosesNoAnsweredWriteAndLeavesNoWakeUpHalfDone(SqliteSync sync, int delayMs)
    {
        string path = _db.File();
        string host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CodeFusion.Data.Sqlite.CrashChild.dll"));
        start.ArgumentList.Add("batches");
        start.ArgumentList.Add(((int)sync).ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var child = Process.Start(start)!;
        var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var answered = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        int lastOpen = -1;
        bool ready = false;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                string? line;
                while ((line = await child.StandardOutput.ReadLineAsync(wait.Token)) is not null)
                {
                    if (line.StartsWith("ANSWERED ", StringComparison.Ordinal)) answered.Add(line["ANSWERED ".Length..]);
                    else if (line == "READY") ready = true;
                    else if (line.StartsWith("OPEN ", StringComparison.Ordinal))
                    {
                        lastOpen = int.Parse(line["OPEN ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                        if (ready)
                        {
                            // the wake-up is starting: kill it while its writes run, as it commits, or as it answers
                            if (delayMs > 0) Thread.Sleep(delayMs);
                            child.Kill(entireProcessTree: true);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("the child didn't get to a wake-up after READY in 30 s: " + await stderr);
            }
        }
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        // what it answered before the kill and still had in its output
        foreach (var line in (await child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Split('\n'))
            if (line.StartsWith("ANSWERED ", StringComparison.Ordinal)) answered.Add(line["ANSWERED ".Length..].TrimEnd('\r'));
        Assert.True(ready, "the child ended before it was ready: " + await stderr);
        Assert.True(lastOpen >= 4);

        var s = _db.Open(new SqliteDatabase(path) { Synchronous = sync, Readers = 1, Migrations = Schema });
        Assert.Equal(100, Kv.Count(s, "committed-%"));
        int whole = 0;
        for (int k = 0; k <= lastOpen; k++)
        {
            long writes = Kv.Count(s, $"b{k}-%"), pairs = Kv.Count(s, $"pair-%-{k}"), big = Kv.Count(s, $"big{k}-%");
            bool all = writes == 10 && pairs == 2 && big == 20_000, none = writes == 0 && pairs == 0 && big == 0;
            Assert.True(all || none, $"wake-up {k} is half there: {writes} writes, {pairs} of the unit, {big} big rows");
            for (int i = 0; i < 10; i++)
                if (answered.Contains($"b{k}-{i}")) Assert.True(all, $"b{k}-{i} was answered but isn't there");
            if (answered.Contains($"pair-{k}") || answered.Contains($"big{k}")) Assert.True(all, $"wake-up {k} was answered but isn't there");
            if (all) whole++;
        }
        Assert.True(whole >= 4, $"only {whole} wake-ups committed before the kill"); // the four before READY at least
        var check = s.Read(new SqliteStatement("PRAGMA integrity_check"));
        var rows = new System.Collections.Generic.List<string?[]>();
        Assert.True(Kv.Done(Sqlite.Run<Dump>(check.Hold(), rows, default)).IsOk);
        Assert.Equal("ok", rows[0][0]);
    }
}
