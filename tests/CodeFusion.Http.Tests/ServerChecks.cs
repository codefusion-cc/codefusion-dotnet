using System;
using System.Threading;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// What holds once a server has stopped, whatever its test did: no connection loop threw and no exception from the
/// handler's callbacks was dropped (the server only counts and logs those, so the test would pass), every connection
/// and event loop ended, every buffer is back in its pool, and in Debug builds every connection run and handler call ended exactly
/// once (each ValueTask consumed once).
/// </summary>
internal static class ServerChecks
{
    public static async Task AssertStoppedCleanAsync(HttpServer server, BufferPool? arena = null)
    {
        // the stop gives up on connections after its own waits, which a loaded machine can outlast
        long until = Environment.TickCount64 + 5000;
        while ((server.Connections > 0 || server.EventLoopsRunning > 0) && Environment.TickCount64 < until) await Task.Delay(5);
        int failed = Volatile.Read(ref server.FailedConnections);
        Assert.True(failed == 0, $"{failed} connection loop(s) threw (\"http connection failed\" on stderr)");
        int dropped = Volatile.Read(ref server.CallbackFailures);
        Assert.True(dropped == 0, $"{dropped} exception(s) from the handler's callbacks were dropped");
        Assert.True(server.Connections == 0, $"{server.Connections} connection(s) still open after the stop");
        Assert.True(server.EventLoopsRunning == 0, $"{server.EventLoopsRunning} event loop(s) still running after the stop");
        AssertPoolsBalance(server, arena);
#if DEBUG
        Assert.True(server.RunsStarted == server.RunsEnded, $"{server.RunsStarted} connection runs started, {server.RunsEnded} ended");
        Assert.True(server.HandlersStarted == server.HandlersEnded, $"{server.HandlersStarted} handler calls started, {server.HandlersEnded} ended");
#endif
    }

    /// <summary>Every buffer rented is back: the request and response pools (in Debug builds, the larger arrays from
    /// ArrayPool.Shared too) and the app's arena chunks.</summary>
    public static void AssertPoolsBalance(HttpServer server, BufferPool? arena = null)
    {
        (int request, int response, int chunks) = (server.RequestBuffers.Out, server.ResponseBuffers.Out, arena?.Out ?? 0);
        Assert.True((request, response, chunks) == (0, 0, 0), $"buffers out after the stop: {request} request, {response} response, {chunks} arena");
    }
}

/// <summary>Stops an app at the end of a test and checks its server as <see cref="ServerChecks"/> does. Declare it
/// after the app (<c>await using var check = new AppCheck(app)</c>), so it is disposed first.</summary>
internal sealed class AppCheck(HttpApp app) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        if (app.Server is { } server) await ServerChecks.AssertStoppedCleanAsync(server, app.ArenaChunks);
    }
}
