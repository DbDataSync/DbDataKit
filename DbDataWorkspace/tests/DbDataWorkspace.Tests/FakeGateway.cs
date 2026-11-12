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

    /// <summary>What its catalog reader answers per data source (plan 33 SC20j): the engine and the tables with their columns, as <c>POST /api/v1/data-sources/{name}/schema</c> does.</summary>
    public ConcurrentDictionary<string, JsonObject> Schemas { get; } = new();

    /// <summary>The data source documents the paired client published as definitions (plan 33 SW01): name to the version and the document.</summary>
    public ConcurrentDictionary<string, (int Version, string Text)> DataSourceDocs { get; } = new();

    /// <summary>The slots bound in production (plan 33 SW01), as <c>kind/name/slot</c>, and who bound them.</summary>
    public ConcurrentDictionary<string, string> Bindings { get; } = new();

    /// <summary>The data sources a publish went over through a bound slot (no adopt), as <c>data source for model by client</c>.</summary>
    public ConcurrentQueue<string> SlotUses { get; } = new();

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
        app.MapGet("/api/v1/definitions/{kind}", async (string kind, HttpContext context) => await gateway.Definitions(context, kind, null));
        app.MapGet("/api/v1/definitions/{kind}/{name}", async (string kind, string name, HttpContext context) => await gateway.Definitions(context, kind, name));
        app.MapGet("/api/v1/definitions/{kind}/{name}/versions/{number}", async (string kind, string name, HttpContext context) => await gateway.Definitions(context, kind, name));
        app.MapPut("/api/v1/definitions/{kind}/{name}", async (string kind, string name, HttpContext context) => await gateway.Definitions(context, kind, name));
        app.MapPut("/api/v1/bindings/{channel}/{kind}/{name}", async (string channel, string kind, string name, HttpContext context) => await gateway.Bindings(context, channel, kind, name));
        app.MapGet("/api/v1/trust/{kind}", async (string kind, HttpContext context) => await gateway.Definitions(context, "trust", kind));
        app.MapPost("/api/v1/data-sources/{name}/schema", async (string name, HttpContext context) => await gateway.Schema(context, name));
        await app.StartAsync();
        var made = new FakeGateway(app, new Uri(app.Urls.First().Replace("[::1]", "127.0.0.1")), directory, codes);
        gateway.Owner = made;
        return made;
    }

    private sealed class FakeGatewayState
    {
        public FakeGateway Owner = null!;

        /// <summary>The Gateway's schema call (plan 33 SC20j): the shape of a data source's catalog, a hand-made data source only with <c>adopt</c> naming it.</summary>
        public async Task Schema(HttpContext context, string name)
        {
            var token = context.ManagementToken();
            Owner.Calls.Enqueue($"{context.Request.Method} {context.Request.Path} as {token.SubjectId} ({token.SubjectName}) with {string.Join("+", ServerRoles.Names(token.Roles))}");
            async Task Reply(int status, object body)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(body);
            }
            if (!ManagementAuthorization.Authorize(token, ServerRole.Publisher).Allowed && !ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) { await Reply(403, new { error = "Reading a catalog needs the publisher role." }); return; }
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body);
            if (!Owner.Schemas.TryGetValue(name, out var schema)) { await Reply(404, new { error = $"There is no data source '{name}'." }); return; }
            if (Owner.HandMade.Contains(name))
            {
                if ((string?)body?["adopt"] != name) { await Reply(409, new { error = $"The data source '{name}' was made by hand. Ask again with \"adopt\".", adopt = new { data_source = name, made_by = "hand", publish_adopt = true, field = "adopt" } }); return; }
                Owner.Adoptions.Enqueue($"{name} schema-read by {token.Client.Id}");
            }
            await Reply(200, schema);
        }

        private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

        private async Task<(ValidatedToken Token, bool Allowed)> Begin(HttpContext context, Func<int, object, Task> reply)
        {
            var token = context.ManagementToken();
            Owner.Calls.Enqueue($"{context.Request.Method} {context.Request.Path} as {token.SubjectId} ({token.SubjectName}) with {string.Join("+", ServerRoles.Names(token.Roles))}");
            var allowed = ManagementAuthorization.Authorize(token, ServerRole.Publisher).Allowed || ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed;
            if (!allowed) await reply(403, new { error = "Publishing needs the publisher role." });
            return (token, allowed);
        }

        /// <summary>
        /// The shared publish API as far as the Workspace goes (plan 33 SW01): the reads of a definition, the owned list, and <c>PUT definitions/{kind}/{name}</c> for <c>data-source</c>, <c>entity-model</c> and <c>semantic-model</c> with the Gateway's rules in its words
        /// (the data source it has, the one made by hand that goes only with <c>adopt</c> or a bound slot, the version a publish is made over). The real Gateway is run against the same code in the Gateway's own tests.
        /// </summary>
        public async Task Definitions(HttpContext context, string kind, string? name)
        {
            async Task Reply(int status, object body)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(body);
            }
            var (token, allowed) = await Begin(context, Reply);
            if (!allowed) return;
            var client = token.Client.Id;

            if (kind == "trust") { await Reply(404, new { error = "This Gateway keeps no trust store (a stand-in)." }); return; }
            if (name is null)
            {
                // a caller that only publishes sees the names it owns: the data sources it published and the ones it made (here: every one of the Gateway's that is not made by hand)
                var names = kind == "data-source" ? Owner.DataSourceDocs.Keys.Order(StringComparer.Ordinal).ToArray() : [];
                await Reply(200, new { kind, definitions = names.Select(n => new { name = n, owner = client }).ToArray() });
                return;
            }
            if (context.Request.Method == "GET")
            {
                if (kind == "data-source" && Owner.HandMade.Contains(name)) { await Reply(403, new { error = $"The data source '{name}' was made by hand or by another client, not by this client." }); return; }
                var held = kind switch
                {
                    "data-source" => Owner.DataSourceDocs.TryGetValue(name, out var d) ? (d.Version, d.Text) : ((int, string)?)null,
                    "entity-model" => Owner.Models.TryGetValue(name, out var m) ? (m.Version, System.Text.Json.JsonSerializer.Serialize(new { model = m.Text, dataSource = m.DataSource, managedBy = client }, Web)) : ((int, string)?)null,
                    "semantic-model" => Owner.SemanticModels.TryGetValue(name, out var sm) ? (sm.Version, System.Text.Json.JsonSerializer.Serialize(new { catalog = sm.Catalog, managedBy = client, sources = sm.Sources, ssas = new { } }, Web)) : ((int, string)?)null,
                    _ => null,
                };
                if (held is null) { await Reply(404, new { error = $"There is no {kind} '{name}'." }); return; }
                var path = context.Request.Path.Value ?? "";
                if (path.Contains("/versions/", StringComparison.Ordinal)) await Reply(200, new { kind, name, version = held.Value.Item1, manifest = held.Value.Item2 });
                else await Reply(200, new { kind, name, latest_version = held.Value.Item1, channels = new[] { new { channel = "prod", active = (int?)held.Value.Item1 } } });
                return;
            }

            var body = (await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body))!;
            if (Owner.RefuseWith is { } refusal) { await Reply(400, new { error = refusal }); return; }
            var manifestText = (string)body["manifest"]!;
            var manifest = kind == "data-source" ? null : JsonNode.Parse(manifestText) as JsonObject;   // a data source is the connection/2 file as text, the models are JSON
            var expected = (int?)body["expected_version"];
            var slots = ((manifest?["slots"] as JsonArray)?.Select(x => (string?)x).Where(x => x is not null).Select(x => x!).ToList()) ?? [];
            var adopt = body["adopt"] switch { JsonArray list => list.Select(a => (string?)a).ToList(), JsonValue one => [(string?)one], _ => [] };

            // a data source the version names as a slot is refused until the channel binds it; one made by hand that is not a slot goes only with `adopt` naming it; the refusals are the Gateway's words
            async Task<bool> HandMadeAsync(string dataSource, string model, string modelKind)
            {
                if (slots.Contains(dataSource))
                {
                    if (!Owner.Bindings.ContainsKey($"{modelKind}/{model}/{dataSource}"))
                    {
                        await Reply(409, new { error = $"The {modelKind} '{model}' version 1 needs the slot '{dataSource}' bound in channel 'prod', and it has no binding there.", code = "unboundslot", message = "unbound" });
                        return false;
                    }
                    if (Owner.HandMade.Contains(dataSource)) Owner.SlotUses.Enqueue($"{dataSource} for {model} by {client}");
                    return true;
                }
                if (!Owner.HandMade.Contains(dataSource)) return true;
                if (!adopt.Contains(dataSource))
                {
                    await Reply(400, new { error = $"'{dataSource}' is not a data source this client made: it was made by hand or by an administrator. Publish again with \"adopt\": \"{dataSource}\".", valid = false, adopt = new { data_source = dataSource, made_by = "hand", publish_adopt = true, field = "adopt" } });
                    return false;
                }
                Owner.Adoptions.Enqueue($"{dataSource} for {model} by {client} ({token.SubjectName})");
                return true;
            }

            switch (kind)
            {
                case "data-source":
                {
                    if (!manifestText.Contains($"managed_by: {client}", StringComparison.Ordinal)) { await Reply(400, new { error = $"The file of a data source a publishing client sends says whose it is: add managed_by: {client}." }); return; }
                    if (Owner.HandMade.Contains(name)) { await Reply(409, new { error = $"The data source '{name}' was made by hand on this Gateway." }); return; }
                    var was = Owner.DataSourceDocs.TryGetValue(name, out var existing) ? existing.Version : (int?)null;
                    if (was != expected && !(was is null && expected == 0)) { await Reply(409, new { error = "The data source changed since it was read.", code = "version_conflict", current_version = was }); return; }
                    var unchanged = was is not null && existing.Text == manifestText;
                    var version = unchanged ? was!.Value : (was ?? 0) + 1;
                    Owner.DataSourceDocs[name] = (version, manifestText);
                    Owner.DataSources.Add(name);
                    Owner.Published.Enqueue(body);
                    await Reply(200, new { kind, name, version, created = was is null, changed = !unchanged });
                    return;
                }
                case "entity-model":
                {
                    var dataSource = (string)manifest!["dataSource"]!;
                    var text = (string)manifest["model"]!;
                    if (!Owner.DataSources.Contains(dataSource)) { await Reply(400, new { error = $"'{dataSource}' is not a data source of this server; publish the data source the model's tables are on first.", valid = false }); return; }
                    if (!await HandMadeAsync(dataSource, name, kind)) return;
                    var was = Owner.Models.TryGetValue(name, out var existing) ? existing.Version : (int?)null;
                    if ((was ?? 0) != (expected ?? 0)) { await Reply(409, new { error = "The model changed since it was read; read it again and publish on that.", code = "version_conflict", current_version = was }); return; }
                    var unchanged = was is not null && existing.Text == text && existing.DataSource == dataSource;
                    var version = unchanged ? was!.Value : (was ?? 0) + 1;
                    Owner.Models[name] = (version, dataSource, text);
                    Owner.Published.Enqueue(body);
                    await Reply(200, new { kind, name, version, created = was is null, unchanged, adopted = Owner.HandMade.Contains(dataSource) && adopt.Contains(dataSource) ? dataSource : null, slots_used = Owner.HandMade.Contains(dataSource) && slots.Contains(dataSource) ? new[] { dataSource } : null });
                    return;
                }
                case "semantic-model":
                {
                    var sources = (JsonObject)manifest!["sources"]!;
                    foreach (var (source, dataSource) in sources)
                        if (!Owner.DataSources.Contains((string)dataSource!)) { await Reply(400, new { error = $"Source '{source}' maps to '{(string)dataSource!}', which is not a data source of this server.", valid = false }); return; }
                    foreach (var dataSource in sources.Select(x => (string)x.Value!).Distinct())
                        if (!await HandMadeAsync(dataSource, name, kind)) return;
                    var was = Owner.SemanticModels.TryGetValue(name, out var existing) ? existing.Version : (int?)null;
                    if ((was ?? 0) != (expected ?? 0)) { await Reply(409, new { error = "The model changed since it was read; read it again and publish on that.", code = "version_conflict", current_version = was }); return; }
                    var catalog = (string)manifest["catalog"]!;
                    var unchanged = was is not null && existing.Catalog == catalog && existing.Sources.ToJsonString() == sources.ToJsonString();
                    var version = unchanged ? was!.Value : (was ?? 0) + 1;
                    Owner.SemanticModels[name] = (version, catalog, (JsonObject)sources.DeepClone());
                    Owner.Published.Enqueue(body);
                    var adopted = sources.Select(x => (string)x.Value!).Distinct().Where(x => Owner.HandMade.Contains(x) && adopt.Contains(x)).ToArray();
                    var used = sources.Select(x => (string)x.Value!).Distinct().Where(x => Owner.HandMade.Contains(x) && slots.Contains(x)).ToArray();
                    await Reply(200, new { kind, name, version, created = was is null, unchanged, adopted = adopted.Length == 0 ? null : adopted, slots_used = used.Length == 0 ? null : used });
                    return;
                }
                default:
                    await Reply(404, new { error = $"The kind '{kind}' is not registered here." });
                    return;
            }
        }

        /// <summary>The binding call: a publishing client binds only a data source it made; one made by hand is an administrator's to bind (the Gateway's words).</summary>
        public async Task Bindings(HttpContext context, string channel, string kind, string name)
        {
            async Task Reply(int status, object body)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(body);
            }
            var (token, allowed) = await Begin(context, Reply);
            if (!allowed) return;
            var body = (await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body))!;
            foreach (var (slot, value) in (JsonObject)body["slots"]!)
            {
                var dataSource = (string)value!;
                if (Owner.HandMade.Contains(dataSource))
                {
                    await Reply(403, new { error = $"'{dataSource}' was made by hand or by an administrator, not by this client, and a publishing client binds only the data sources it made. An administrator binds it once, for this {kind}: PUT /api/v1/bindings/{channel}/{kind}/{name}." });
                    return;
                }
                Owner.Bindings[$"{kind}/{name}/{slot}"] = token.Client.Id;
            }
            await Reply(200, new { kind, name, channel });
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
