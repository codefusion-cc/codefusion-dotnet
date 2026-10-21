using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// The default connection limit leaves descriptors for the rest of the process: past the descriptor limit accept
/// fails and so does everything else that opens one, and the runtime has ended the process there ($12 entry, 65,535).
/// </summary>
public class DescriptorLimitTests
{
    [Theory]
    [InlineData(-1L, int.MaxValue)]          // RLIM_INFINITY on Linux
    [InlineData(long.MaxValue, int.MaxValue)] // and on macOS
    [InlineData(0L, int.MaxValue)]
    [InlineData((long)int.MaxValue, int.MaxValue)]
    [InlineData(1L, 1)]
    [InlineData(4L, 2)]
    [InlineData(256L, 128)]                  // half kept back, below 2048
    [InlineData(2047L, 1024)]
    [InlineData(2048L, 1024)]
    [InlineData(2049L, 1025)]                // 1024 kept back from here on
    [InlineData(4096L, 3072)]
    [InlineData(65535L, 64511)]              // the contest box's LimitNOFILE
    [InlineData(1048576L, 1047552)]
    public void TheDefaultLimitKeepsAReserve(long softLimit, int connections)
        => Assert.Equal(connections, FileDescriptors.MaxConnections(softLimit));

    [Fact]
    public void AServerWithoutAMaxConnectionsTakesItsFromTheDescriptorLimit()
    {
        long soft = FileDescriptors.SoftLimit();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) Assert.True(soft > 0, $"soft limit {soft}");
        var unset = new HttpServer(new HttpServerOptions(), static _ => default);
        Assert.Equal(FileDescriptors.MaxConnections(soft), unset.ConnectionLimit);
        var set = new HttpServer(new HttpServerOptions { MaxConnections = 5 }, static _ => default);
        Assert.Equal(5, set.ConnectionLimit);
    }

    // The sample app under `ulimit -n 256`: past its 128 connections a new one is closed at once, the ones it holds
    // are still answered, and once they go new ones are taken again. Without the default it ran out of descriptors.
    [Fact(Timeout = 60_000)]
    public async Task PastTheDescriptorLimitNewConnectionsAreClosedAndTheServerGoesOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "ulimit is a Unix shell's");
        var start = new ProcessStartInfo("bash", ["-c", "ulimit -n 256 && exec dotnet \"$0\"", SignalTests.SampleApp()])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["PORT"] = "0", ["HOST"] = "127.0.0.1" },
        };
        using Process app = Process.Start(start)!;
        var clients = new List<Client>();
        try
        {
            string? line = await app.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            Assert.StartsWith("listening ", line, StringComparison.Ordinal);
            var ep = new IPEndPoint(IPAddress.Loopback, int.Parse(line!["listening ".Length..], System.Globalization.CultureInfo.InvariantCulture));

            int served = 0, closed = 0;
            for (int i = 0; i < 260; i++)
            {
                Client c = await Client.ConnectAsync(ep);
                clients.Add(c);
                if (await Answered(c)) served++;
                else closed++;
            }
            Assert.Equal(128, served); // the rest of the descriptors are the runtime's and the app's
            Assert.Equal(260 - served, closed);
            // the connections it holds are still served
            Assert.True(await Answered(clients[0]), "a held connection went unanswered");
            foreach (Client c in clients) c.Dispose();
            clients.Clear();
            // and with them gone, new ones are taken again (their closes take a moment to reach the server)
            long until = Environment.TickCount64 + 10_000;
            while (true)
            {
                using Client again = await Client.ConnectAsync(ep);
                if (await Answered(again)) break;
                Assert.True(Environment.TickCount64 < until, "no connection was taken again");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            if (app.HasExited) Assert.Fail($"the app ended: {await app.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken)}");
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
            if (!app.HasExited) app.Kill();
        }
    }

    /// <summary>A GET answered, or the connection closed (the end of the stream or a reset).</summary>
    static async Task<bool> Answered(Client c)
    {
        try
        {
            await c.SendAsync("GET /fast HTTP/1.1\r\nHost: x\r\n\r\n");
            return (await c.ReadAsync()).Status == 200;
        }
        catch (Exception e) when (e is SocketException or EndOfStreamException or IOException)
        {
            return false;
        }
    }
}
