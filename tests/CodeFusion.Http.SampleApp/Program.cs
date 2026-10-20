using System;
using System.Threading.Tasks;
using CodeFusion;
using CodeFusion.Http;

// Listens on PORT (0 picks one), says where, and answers until SIGTERM. GET /slow takes 500 ms, so a test can stop
// the app while one is in flight; the OnStopping line says the app stopped after it.
var builder = HttpApp.CreateBuilder(args);
builder.ShutdownTimeout = TimeSpan.FromSeconds(10);
builder.OnStopping(static () => Console.WriteLine("stopped"));
var app = builder.Build();
app.MapGet("/slow", static async () =>
{
    await Task.Delay(500);
    return Reply.Ok(new Done(true));
});
app.MapGet("/fast", static () => Reply.Ok(new Done(false)));
app.Start();
Console.WriteLine($"listening {app.LocalEndPoint.Port}");
app.Run();

readonly record struct Done(bool Slow);
