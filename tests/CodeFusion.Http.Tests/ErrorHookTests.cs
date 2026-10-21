using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using CodeFusion.Http;
using Xunit;

// As in ServerTests: each test's timeout fails it if the server hangs, and the tests talk to their own server over
// loopback without the test's cancellation token.
#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Http.Tests;

/// <summary>Where the server's own failures go: <see cref="HttpServerOptions.OnError"/>, never the console directly.</summary>
public class ErrorHookTests
{
    const string Boom = "GET /boom HTTP/1.1\r\nHost: x\r\n\r\n", Fine = "GET /fine HTTP/1.1\r\nHost: x\r\n\r\n";

    /// <summary>A server with no <see cref="HttpServerOptions.OnHandlerError"/>, so a handler's exception goes to
    /// <paramref name="onError"/>.</summary>
    static HttpServer Start(Action<string, Exception?>? onError)
    {
        var s = new HttpServer(TestTransport.Apply(new HttpServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            OnError = onError,
        }), ThrowOnBoom);
        s.Start();
        return s;
    }

    static ValueTask ThrowOnBoom(HttpExchange x)
    {
        if (x.Path.SequenceEqual("/boom"u8)) throw new InvalidOperationException("boom");
        TestServer.Write(x, "fine");
        return default;
    }

    /// <summary>A 500 for /boom, then an answer on the same connection.</summary>
    static async Task BoomThenFine(HttpServer s)
    {
        using var c = await Client.ConnectAsync(s.LocalEndPoint);
        await c.SendAsync(Boom);
        Assert.Equal(500, (await c.ReadAsync()).Status);
        await c.SendAsync(Fine);
        Assert.Equal("fine", (await c.ReadAsync()).Text);
    }

    [Fact(Timeout = 10_000)]
    public async Task AHandlersExceptionGoesToOnErrorWhenNoOnHandlerErrorIsSet()
    {
        var seen = new List<(string What, Exception? Error)>();
        var s = Start((what, e) => { lock (seen) seen.Add((what, e)); });
        try
        {
            await BoomThenFine(s);
        }
        finally
        {
            await s.StopAsync(TimeSpan.FromSeconds(1));
        }
        await ServerChecks.AssertStoppedCleanAsync(s);
        var (what, error) = Assert.Single(seen);
        Assert.Equal("http handler failed", what);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(error).Message);
    }

    [Fact(Timeout = 10_000)]
    public async Task AnOnErrorThatThrowsIsCountedAndTheConnectionGoesOn()
    {
        var s = Start((_, _) => throw new InvalidOperationException("the hook failed"));
        try
        {
            await BoomThenFine(s);
        }
        finally
        {
            await s.StopAsync(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(1, s.CallbackFailures);
        Assert.Equal(0, s.FailedConnections);
        ServerChecks.AssertPoolsBalance(s);
    }

    [Fact(Timeout = 10_000)]
    public async Task ANullOnErrorDropsThem()
    {
        var s = Start(null);
        try
        {
            await BoomThenFine(s);
        }
        finally
        {
            await s.StopAsync(TimeSpan.FromSeconds(1));
        }
        await ServerChecks.AssertStoppedCleanAsync(s);
    }

    [Fact]
    public void TheServerRefusesNullOptionsOrHandler()
    {
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new HttpServer(null!, ThrowOnBoom)).ParamName);
        Assert.Equal("handler", Assert.Throws<ArgumentNullException>(() => new HttpServer(new HttpServerOptions(), null!)).ParamName);
    }
}

/// <summary>The default hook writes to the process's stderr, which this swaps for a moment: in the collection that
/// runs alone.</summary>
[Collection(Quiet.Name)]
public class ErrorHookStderrTests
{
    [Fact]
    public void TheDefaultWritesTheLineAndTheExceptionToStderr()
    {
        Action<string, Exception?> onError = new HttpServerOptions().OnError!;
        var e = new InvalidOperationException("boom");
        TextWriter was = Console.Error;
        using var w = new StringWriter();
        Console.SetError(w);
        try
        {
            onError("http connection failed", e);
            onError("http accept works again", null);
        }
        finally
        {
            Console.SetError(was);
        }
        Assert.Equal($"http connection failed: {e}{Environment.NewLine}http accept works again{Environment.NewLine}", w.ToString());
    }
}
