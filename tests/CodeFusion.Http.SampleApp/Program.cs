using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CodeFusion;
using CodeFusion.Http;

// Listens on PORT (0 picks one) with EVENT_LOOPS loops if set, says where, and answers until SIGTERM. GET /slow says it has started, then answers
// 500 ms after the SIGTERM (or after 30 s without one), so a test stops the app while it is in flight whatever the
// machine's load; the OnStopping line says the app stopped after it.
using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, static _ => Signals.Sigterm.TrySetResult());
var builder = HttpApp.CreateBuilder(args);
builder.ShutdownTimeout = TimeSpan.FromSeconds(10);
if (Environment.GetEnvironmentVariable("EVENT_LOOPS") is { } loops) builder.Options.EventLoops = int.Parse(loops, System.Globalization.CultureInfo.InvariantCulture);
builder.OnStopping(static () => Console.WriteLine("stopped"));
var app = builder.Build();
app.MapGet("/slow", static async () =>
{
    Console.WriteLine("slow started");
    await Task.WhenAny(Signals.Sigterm.Task, Task.Delay(30_000));
    await Task.Delay(500);
    return Reply.Ok(new Done(true));
});
app.MapGet("/fast", static () => Reply.Ok(new Done(false)));
app.Start();
Console.WriteLine($"listening {app.LocalEndPoint.Port}");
app.Run();

readonly record struct Done(bool Slow);

static class Signals
{
    public static readonly TaskCompletionSource Sigterm = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
