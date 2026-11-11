using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A Gateway as far as publishing an app goes: real pairing and token check over HTTPS (the code a Gateway makes with <c>server pair --max-role publisher</c>), and the two calls the Workspace server makes, <c>POST /api/v1/apps/check</c> and
/// <c>PUT /api/v1/apps/{name}</c>, with the Gateway's role rule (publisher or admin). It records what it was sent and can be told to refuse, in a sentence of its own.
/// </summary>
internal sealed class FakeGateway : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string directory;
    public Uri Url { get; }
    public PairingCodes Codes { get; }
    public ConcurrentQueue<JsonObject> Published { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public string? RefuseWith { get; set; }

    /// <summary>The entity models it serves (plan 33 SC21b): name to the version and the data source; and the data sources it has, which a model must name.</summary>
    public ConcurrentDictionary<string, (int Version, string DataSource, string Text)> Models { get; } = new();
    public HashSet<string> DataSources { get; } = ["mdm"];
    public string InstanceId { get; } = "fake-gateway-1";

    private FakeGateway(WebApplication app, Uri url, string directory, PairingCodes codes) => (this.app, Url, this.directory, Codes) = (app, url, directory, codes);

    public static async Task<FakeGateway> StartAsync(string product = "dbdatagateway")
    {
        var directory = Directory.CreateTempSubdirectory("ddw-gateway-").FullName;
        var certificate = DirectoryCertificate.Load(directory);
        var clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        var codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.UseDbDataHost(host => { host.Product = product; host.Version = "9.9.9"; });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        const string instance = "fake-gateway-1";
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(instance, PairingProof.CertificateFingerprint(certificate), clients, codes));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instance, clients, new MemoryReplayCache()));
        var gateway = new FakeGatewayState();
        app.MapPost("/api/v1/apps/check", async (HttpContext context) => await gateway.Handle(context, check: true));
        app.MapPut("/api/v1/apps/{name}", async (HttpContext context) => await gateway.Handle(context, check: false));
        app.MapGet("/api/v1/entity-models/{name}", async (string name, HttpContext context) => await gateway.Models(context, name));
        app.MapPut("/api/v1/entity-models/{name}", async (string name, HttpContext context) => await gateway.Models(context, name));
        await app.StartAsync();
        var made = new FakeGateway(app, new Uri(app.Urls.First().Replace("[::1]", "127.0.0.1")), directory, codes);
        gateway.Owner = made;
        return made;
    }

    private sealed class FakeGatewayState
    {
        public FakeGateway Owner = null!;

        public async Task Models(HttpContext context, string name)
        {
            var token = context.ManagementToken();
            Owner.Calls.Enqueue($"{context.Request.Method} {context.Request.Path} as {token.SubjectId} ({token.SubjectName}) with {string.Join("+", ServerRoles.Names(token.Roles))}");
            async Task Reply(int status, object body)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(body);
            }
            if (!ManagementAuthorization.Authorize(token, ServerRole.Publisher).Allowed && !ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) { await Reply(403, new { error = "Publishing models needs the publisher role." }); return; }
            if (context.Request.Method == "GET")
            {
                if (!Owner.Models.TryGetValue(name, out var held)) { await Reply(404, new { error = $"There is no published entity model '{name}'." }); return; }
                await Reply(200, new { name, current_version = held.Version, model = held.Text, data_source = held.DataSource });
                return;
            }
            var body = (await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body))!;
            if (Owner.RefuseWith is { } refusal) { await Reply(400, new { error = refusal }); return; }
            var dataSource = (string)body["data_source"]!;
            if (!Owner.DataSources.Contains(dataSource)) { await Reply(409, new { error = $"'{dataSource}' is not a data source of this server; publish the data source the model's tables are on first." }); return; }
            var expected = (int?)body["expected_version"];
            var was = Owner.Models.TryGetValue(name, out var existing) ? existing.Version : (int?)null;
            if (was != expected) { await Reply(409, new { error = "The model changed since it was read; read it again and publish on that.", current_version = was }); return; }
            var text = (string)body["model"]!;
            var unchanged = was is not null && existing.Text == text;
            var version = unchanged ? was!.Value : (was ?? 0) + 1;
            Owner.Models[name] = (version, dataSource, text);
            Owner.Published.Enqueue(body);
            await Reply(200, new { name, version, created = was is null, unchanged });
        }

        public async Task Handle(HttpContext context, bool check)
        {
            var token = context.ManagementToken();
            Owner.Calls.Enqueue($"{context.Request.Method} {context.Request.Path} as {token.SubjectId} ({token.SubjectName}) with {string.Join("+", ServerRoles.Names(token.Roles))}");
            async Task Reply(int status, object body)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(body);
            }
            if (!ManagementAuthorization.Authorize(token, ServerRole.Publisher).Allowed && !ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed)
            {
                await Reply(403, new { error = "Publishing apps needs the publisher role." });
                return;
            }
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body);
            if (Owner.RefuseWith is { } refusal) { await Reply(400, new { error = refusal }); return; }
            if (check) { await Reply(200, new { diagnostics = Array.Empty<object>(), version = 1 }); return; }
            Owner.Published.Enqueue(body!);
            await Reply(200, new { app = (string?)context.Request.RouteValues["name"], version = Owner.Published.Count, hash = "abc123" });
        }
    }

    public string NewCode(ServerRole ceiling = ServerRole.Publisher) => Codes.Create(ServerRoles.Close(ceiling)).Code;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
