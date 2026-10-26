using System.Net;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataConsole.Tests;

/// <summary>
/// A server a console can pair with, as small as one can be and still be the real thing at the edges: Kestrel over HTTPS with a certificate of its own, the shared pairing and key-rotation endpoints, the
/// management API version header, and the shared host's <c>/health</c> and <c>/version</c>. What the console does with it is what it would do with a Build, Sync or Proxy server.
/// </summary>
internal sealed class PairableServer : IAsyncDisposable
{
    public const string InstanceId = "fake-instance-1";
    private readonly WebApplication app;

    public Uri Url { get; }
    public string Directory { get; }
    public string Fingerprint { get; }
    public PairedClientsFile Clients { get; }
    public PairingCodes Codes { get; }

    private PairableServer(WebApplication app, Uri url, string directory, string fingerprint)
    {
        (this.app, Url, Directory, Fingerprint) = (app, url, directory, fingerprint);
        Clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        Codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));
    }

    public static async Task<PairableServer> StartAsync(int port = 0, string? directory = null)
    {
        directory ??= System.IO.Directory.CreateTempSubdirectory("ddk-pairable").FullName;
        var certificate = DirectoryCertificate.Load(directory);
        var fingerprint = PairingProof.CertificateFingerprint(certificate);
        var clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        var codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.UseDbDataHost(host =>
        {
            host.Product = "fake-server";
            host.Version = "9.9.9";
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(InstanceId, fingerprint, clients, codes));
        app.MapKeyRotation(new KeyRotationService(InstanceId, clients, new MemoryReplayCache()));

        // A management API behind the token check, as a real server has: /api/echo says who is calling and what they sent, /api/admin-only needs the admin role.
        var validator = new ConsoleTokenValidator(InstanceId, clients, new MemoryReplayCache());
        app.UseWebSockets();
        app.UseManagementAuthentication(validator);
        app.UseManagementAuthentication(validator, prefix: "/hubs/");
        // a realtime channel, as the product servers have: it says who is connected and sends back what it is sent
        app.Map("/hubs/echo", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.Json(new { subject = context.ManagementToken().SubjectId });
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var who = context.ManagementToken().SubjectName;
            var buffer = new byte[4096];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                var reply = System.Text.Encoding.UTF8.GetBytes($"{who}: {System.Text.Encoding.UTF8.GetString(buffer, 0, received.Count)}");
                await socket.SendAsync(reply, System.Net.WebSockets.WebSocketMessageType.Text, true, context.RequestAborted);
            }
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            return Results.Empty;
        });
        app.MapMethods("/api/echo", ["GET", "POST", "PUT", "DELETE"], async (HttpContext context) =>
        {
            var token = context.ManagementToken();
            using var reader = new StreamReader(context.Request.Body);
            return Results.Json(new
            {
                subject = token.SubjectId, name = token.SubjectName, client = token.Client.Name, roles = ServerRoles.Names(token.Roles),
                method = context.Request.Method, query = context.Request.QueryString.Value, body = await reader.ReadToEndAsync(), contentType = context.Request.ContentType,
            });
        });
        app.MapGet("/api/admin-only", (HttpContext context) =>
            ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed ? Results.Json(new { ok = true }) : Results.Json(new { error = "needs admin" }, statusCode: 403));
        await app.StartAsync();
        return new PairableServer(app, new Uri(app.Urls.First().Replace("[::1]", "127.0.0.1")), directory, fingerprint);
    }

    /// <summary>A single-use code, as <c>server pair</c> prints it; the client it pairs may grant at most viewer.</summary>
    public string NewCode() => Codes.Create(ServerRoles.Close(ServerRole.Viewer)).Code;

    /// <summary>A single-use code for a client that may grant more.</summary>
    public string NewCodeWith(ServerRole ceiling) => Codes.Create(ServerRoles.Close(ceiling)).Code;

    public async Task StopAsync()
    {
        await app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
