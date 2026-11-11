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

    /// <summary>The data sources made by hand (not by the paired client): a model goes on one only when the request says <c>adopt</c>, and the adoption is recorded (plan 33 SC21f).</summary>
    public HashSet<string> HandMade { get; } = [];
    public ConcurrentQueue<string> Adoptions { get; } = new();

    /// <summary>The semantic models it serves (plan 33 SC20e): name to the version, the catalog and the data source each source maps to.</summary>
    public ConcurrentDictionary<string, (int Version, string Catalog, JsonObject Sources)> SemanticModels { get; } = new();

    /// <summary>The instances production has, which a preview may be of.</summary>
    public HashSet<string> Instances { get; } = ["orders"];
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
        app.MapPut("/api/v1/apps/{name}/channels/{channel}", async (HttpContext context) => await gateway.Handle(context, check: false));
        app.MapPut("/api/v1/app-instances/{name}/channels/{channel}", async (string name, string channel, HttpContext context) => await gateway.PreviewInstance(context, name, channel));
        app.MapGet("/api/v1/entity-models/{name}", async (string name, HttpContext context) => await gateway.Models(context, name));
        app.MapPut("/api/v1/entity-models/{name}", async (string name, HttpContext context) => await gateway.Models(context, name));
        app.MapGet("/api/v1/semantic-models/{name}", async (string name, HttpContext context) => await gateway.Semantic(context, name));
        app.MapPut("/api/v1/semantic-models/{name}", async (string name, HttpContext context) => await gateway.Semantic(context, name));
        await app.StartAsync();
        var made = new FakeGateway(app, new Uri(app.Urls.First().Replace("[::1]", "127.0.0.1")), directory, codes);
        gateway.Owner = made;
        return made;
    }

    private sealed class FakeGatewayState
    {
        public FakeGateway Owner = null!;

        /// <summary>The Gateway's semantic-model route as far as a publish goes: the version it holds, and a PUT over that version whose sources name data sources the Gateway has.</summary>
        public async Task Semantic(HttpContext context, string name)
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
                if (!Owner.SemanticModels.TryGetValue(name, out var held)) { await Reply(404, new { error = $"There is no published model '{name}'." }); return; }
                await Reply(200, new { name, current_version = held.Version, catalog = held.Catalog });
                return;
            }
            var body = (await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body))!;
            if (Owner.RefuseWith is { } refusal) { await Reply(400, new { error = refusal }); return; }
            var sources = (JsonObject)body["sources"]!;
            foreach (var (source, dataSource) in sources)
                if (!Owner.DataSources.Contains((string)dataSource!)) { await Reply(400, new { error = $"Source '{source}' maps to '{(string)dataSource!}', which is not a data source of this server." }); return; }
            var adopt = (body["adopt"] as JsonArray)?.Select(a => (string?)a).ToList() ?? [];
            foreach (var (source, dataSource) in sources)
                if (Owner.HandMade.Contains((string)dataSource!) && !adopt.Contains((string)dataSource!)) { await Reply(400, new { error = $"Source '{source}' maps to '{(string)dataSource!}', which this client did not make; it was made by hand. Publish again with \"adopt\".", valid = false, adopt = new { data_source = (string)dataSource!, made_by = "hand", publish_adopt = true, field = "adopt" } }); return; }
            foreach (var adopted in adopt.Where(a => a is not null && Owner.HandMade.Contains(a))) Owner.Adoptions.Enqueue($"{adopted} for {name} by {token.Client.Id} ({token.SubjectName})");
            var expected = (int?)body["expected_version"];
            var was = Owner.SemanticModels.TryGetValue(name, out var existing) ? existing.Version : (int?)null;
            if (was != expected) { await Reply(409, new { error = "The model changed since it was read; read it again and publish on that.", current_version = was }); return; }
            var catalog = (string)body["catalog"]!;
            var unchanged = was is not null && existing.Catalog == catalog && existing.Sources.ToJsonString() == sources.ToJsonString();
            var version = unchanged ? was!.Value : (was ?? 0) + 1;
            Owner.SemanticModels[name] = (version, catalog, (JsonObject)sources.DeepClone());
            Owner.Published.Enqueue(body);
            await Reply(200, new { name, version, created = was is null, unchanged, adopted = adopt.Where(x => x is not null && Owner.HandMade.Contains(x)).ToArray() });
        }

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
            if (Owner.HandMade.Contains(dataSource))
            {
                if ((string?)body["adopt"] != dataSource) { await Reply(400, new { error = $"'{dataSource}' is not a data source this client made: it was made by hand or by an administrator. Publish again with \"adopt\": \"{dataSource}\" to put the model on it.", valid = false, adopt = new { data_source = dataSource, made_by = "hand", publish_adopt = true, field = "adopt" } }); return; }
                Owner.Adoptions.Enqueue($"{dataSource} for {name} by {token.Client.Id} ({token.SubjectName})");
            }
            var expected = (int?)body["expected_version"];
            var was = Owner.Models.TryGetValue(name, out var existing) ? existing.Version : (int?)null;
            if (was != expected) { await Reply(409, new { error = "The model changed since it was read; read it again and publish on that.", current_version = was }); return; }
            var text = (string)body["model"]!;
            var unchanged = was is not null && existing.Text == text;
            var version = unchanged ? was!.Value : (was ?? 0) + 1;
            Owner.Models[name] = (version, dataSource, text);
            Owner.Published.Enqueue(body);
            await Reply(200, new { name, version, created = was is null, unchanged, adopted = Owner.HandMade.Contains(dataSource) ? dataSource : null });
        }

        /// <summary>The Gateway's <c>PUT /api/v1/app-instances/{name}/channels/{channel}</c>: a preview instance of an instance that exists in production (here: the ones in <see cref="FakeGateway.Instances"/>).</summary>
        public async Task PreviewInstance(HttpContext context, string name, string channel)
        {
            var token = context.ManagementToken();
            Owner.Calls.Enqueue($"{context.Request.Method} {context.Request.Path} as {token.SubjectId} ({token.SubjectName}) with {string.Join("+", ServerRoles.Names(token.Roles))}");
            context.Response.StatusCode = !Owner.Instances.Contains(name) ? 400 : 200;
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body);
            Owner.Published.Enqueue(body!);
            await context.Response.WriteAsJsonAsync(Owner.Instances.Contains(name)
                ? new JsonObject { ["name"] = name, ["channel"] = channel, ["path"] = $"/apps/{name}~{channel}/", ["address"] = $"https://apps.fake.test:8818/apps/{name}~{channel}/" }
                : new JsonObject { ["error"] = $"There is no instance '{name}' in production to preview." });
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
