using System;
using CodeFusion.Http;

namespace CodeFusion.Http.Tests;

/// <summary>
/// The transport a test's server uses unless the test names one: Sockets, or what CODEFUSION_HTTP_TRANSPORT names
/// (EventLoop), so a second run of the whole suite covers the event loops with every test written for the server.
/// CI runs both.
/// </summary>
internal static class TestTransport
{
    public static readonly HttpTransport Default =
        Environment.GetEnvironmentVariable("CODEFUSION_HTTP_TRANSPORT") is { Length: > 0 } name
            ? Enum.Parse<HttpTransport>(name, ignoreCase: true)
            : HttpTransport.Sockets;

    /// <summary>Sets the default transport; two loops, so connections are spread over more than one.</summary>
    public static HttpServerOptions Apply(HttpServerOptions o)
    {
        o.Transport = Default;
        if (Default == HttpTransport.EventLoop) o.EventLoops = 2;
        return o;
    }

    /// <summary>Sets <paramref name="transport"/>, skipping the test where it doesn't run.</summary>
    public static void Use(HttpServerOptions o, HttpTransport transport)
    {
        Xunit.Assert.SkipWhen(transport == HttpTransport.EventLoop && !EventLoop.Supported, "event loops run on Linux and macOS");
        o.Transport = transport;
    }

    /// <summary>As <see cref="Use"/>, with an empty batch hook on the event loop, so its calls are measured too.</summary>
    public static void UseWithHook(HttpServerOptions o, HttpTransport transport)
    {
        Use(o, transport);
        if (transport == HttpTransport.EventLoop) o.OnEventLoopBatch = static _ => { };
    }
}
