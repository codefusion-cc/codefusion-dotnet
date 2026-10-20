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

    /// <summary>Linux (epoll) and macOS (kqueue).</summary>
    public static bool EventLoopsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Sets the default transport; two loops, so connections are spread over more than one.</summary>
    public static HttpServerOptions Apply(HttpServerOptions o)
    {
        o.Transport = Default;
        if (Default == HttpTransport.EventLoop) o.EventLoops = 2;
        return o;
    }
}
