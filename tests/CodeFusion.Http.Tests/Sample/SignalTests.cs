using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>Run() in a process of its own: SIGTERM lets the request in flight finish, then the app stops.</summary>
public class SignalTests
{
    static string SampleApp()
    {
        // tests/CodeFusion.Http.Tests/bin/<Configuration>/net10.0 -> tests/CodeFusion.Http.SampleApp/bin/<Configuration>/net10.0
        string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string configuration = Path.GetFileName(Path.GetDirectoryName(here)!);
        string tests = Path.GetFullPath(Path.Combine(here, "..", "..", "..", ".."));
        return Path.Combine(tests, "CodeFusion.Http.SampleApp", "bin", configuration, "net10.0", "CodeFusion.Http.SampleApp.dll");
    }

    [Fact(Timeout = 60_000)]
    public async Task SigtermFinishesTheRequestInFlightThenStops()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM is a Unix signal");
        var start = new ProcessStartInfo("dotnet", [SampleApp()])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["PORT"] = "0", ["HOST"] = "127.0.0.1" },
        };
        using Process app = Process.Start(start)!;
        try
        {
            string? line = await app.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            Assert.StartsWith("listening ", line, StringComparison.Ordinal);
            var ep = new IPEndPoint(IPAddress.Loopback, int.Parse(line!["listening ".Length..], System.Globalization.CultureInfo.InvariantCulture));

            using Client slow = await Client.ConnectAsync(ep);
            await slow.SendAsync("GET /slow HTTP/1.1\r\nHost: x\r\n\r\n");
            await Task.Delay(100, TestContext.Current.CancellationToken); // the handler is waiting now
            using (Process kill = Process.Start("kill", ["-TERM", app.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!)
                await kill.WaitForExitAsync(TestContext.Current.CancellationToken);

            Response r = await slow.ReadAsync();
            Assert.Equal((200, """{"slow":true}""", "close"), (r.Status, r.Text, r.Header("Connection")));
            await app.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, app.ExitCode);
            Assert.Equal("stopped", await app.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SocketException>(async () => await Client.ConnectAsync(ep));
        }
        finally
        {
            if (!app.HasExited) app.Kill();
        }
    }
}
