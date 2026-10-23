using System.Diagnostics;
using System.Net;
using DbDataProxy.WorkerHost;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// dbdataproxy-worker.exe --port <n> [--parent <pid>]; the shared secret arrives in DBPROXY_WORKER_SECRET.
// Published as a 32-bit process so the proxy can reach 32-bit-only ODBC drivers through it.

int port = ArgValue(args, "--port") is { } p ? int.Parse(p) : 0;
int? parentPid = ArgValue(args, "--parent") is { } pp ? int.Parse(pp) : null;
string secret = Environment.GetEnvironmentVariable("DBPROXY_WORKER_SECRET")
    ?? throw new InvalidOperationException("DBPROXY_WORKER_SECRET is not set; the worker is started by the proxy.");

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddSimpleConsole();
builder.Services.AddSingleton(new WorkerSecret(secret));
builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = 64 * 1024 * 1024).AddFlightServer<WorkerFlightServer>();
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MinResponseDataRate = null;
    k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapFlightEndpoint();

if (parentPid is { } pid)
{
    // Do not outlive the proxy: if it dies without stopping us, shut down.
    _ = Task.Run(async () =>
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException) { /* parent already gone */ }
        app.Lifetime.StopApplication();
    });
}

await app.RunAsync();

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
