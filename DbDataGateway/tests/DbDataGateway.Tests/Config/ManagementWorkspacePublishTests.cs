using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 33 SW01: the Workspace server's publish (<see cref="WorkspaceGatewayPublish"/>, the code <c>WorkspacePublishing</c> runs for a Store project's models and a Build project's models and data source documents) against a real Gateway's shared publish API, end to end:
/// the Gateway's own hooks, owner rules, slot refusal, signature policy and audit answer it, with a real pairing and token check; only the transport is the harness's.
/// </summary>
public sealed partial class ManagementTests
{
    /// <summary>The Workspace's call as the shared API's caller makes it: relative to <c>/api/v1/</c>, as the paired publisher client.</summary>
    private sealed class WorkspaceLink(ManagementTests owner, ConsoleIdentity who, PairedServer server) : IDefinitionsLink
    {
        public string Name => "the Gateway";

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct) =>
            await owner.CallAsAsync(who, server, method, "/api/v1/" + path, ServerRole.Publisher, content is null ? null : await content.ReadAsStringAsync(ct));
    }

    private string BuildFolder(string model, string dataSource, params (string Name, string Document)[] documents)
    {
        var folder = Path.Combine(dataDirectory, "build-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(folder, "models"));
        Directory.CreateDirectory(Path.Combine(folder, "data-sources"));
        File.WriteAllText(Path.Combine(folder, "models", model + ".catalog.json"), CatalogText);
        foreach (var (name, document) in documents) File.WriteAllText(Path.Combine(folder, "data-sources", name + ".yaml"), document);
        var manifest = new JsonObject
        {
            ["schema"] = "gateway-publish/1",
            ["models"] = new JsonArray(new JsonObject { ["name"] = model, ["file"] = $"models/{model}.catalog.json", ["sources"] = new JsonObject { ["sqlserver_sales"] = dataSource }, ["data_sources"] = new JsonArray((JsonNode?)dataSource) }),
            ["data_sources"] = new JsonArray([.. documents.Select(d => (JsonNode?)new JsonObject { ["name"] = d.Name, ["engine"] = "duckdb", ["file"] = $"data-sources/{d.Name}.yaml", ["access"] = "readonly" })]),
        };
        File.WriteAllText(Path.Combine(folder, "gateway.json"), manifest.ToJsonString());
        return folder;
    }

    [Fact]
    public async Task A_workspace_publishes_a_build_projects_data_source_and_model_through_the_shared_routes_and_the_gateway_serves_them_at_once()
    {
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var steps = new List<string>();
        var publish = new WorkspaceGatewayPublish(new WorkspaceLink(this, build1, first), build1, steps.Add);
        var folder = BuildFolder("sales_catalog", "sales-ro", ("sales-ro", SalesData("sales-ro")));

        var (models, sources) = await publish.PublishSemanticModelsAsync(folder, null);

        // the data source document went first as this client's own, with no login; the model over it needs no slot and no adopt
        Assert.Equal("sales-ro", (string)sources[0]!["name"]!);
        Assert.True((bool)sources[0]!["created"]!);
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionFile("sales-ro")));
        Assert.DoesNotContain("secret", File.ReadAllText(ConnectionFile("sales-ro")));
        Assert.Equal(("sales_catalog", 1, true), ((string)models[0]!["model"]!, (int)models[0]!["version"]!, (bool)models[0]!["created"]!));
        Assert.Equal("sales-ro", app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>().Find("semantic:sales_catalog")!.ServingSource("sqlserver_sales"));

        // the same build again changes nothing; another version of the model goes over the version the Gateway has
        var (again, _) = await publish.PublishSemanticModelsAsync(folder, null);
        Assert.Equal((1, true), ((int)again[0]!["version"]!, (bool)again[0]!["unchanged"]!));
        File.WriteAllText(Path.Combine(folder, "models", "sales_catalog.catalog.json"), CatalogText.Replace("Governed sales", "Sales"));
        var (changed, _) = await publish.PublishSemanticModelsAsync(folder, null);
        Assert.Equal(2, (int)changed[0]!["version"]!);

        // a publisher sees the names it owns, and only those
        Assert.Equal(["sales-ro"], await new DbDataKit.State.Api.DefinitionsClient(new WorkspaceLink(this, build1, first)).NamesAsync("data-source"));
        var second = await PairPublisherAsync(build2);
        Assert.Empty((await new DbDataKit.State.Api.DefinitionsClient(new WorkspaceLink(this, build2, second)).NamesAsync("data-source"))!);
        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "semantic-model publish" && (string)a["target"]! == "sales_catalog" && (string)a["consoleId"]! == "build-1");
    }

    [Fact]
    public async Task A_workspace_publish_over_a_data_source_made_by_hand_is_refused_naming_who_binds_it_and_after_the_administrator_binds_the_slot_it_goes_with_no_confirmation()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(SalesData("by-hand"), null))).StatusCode);
        var handMade = File.ReadAllText(ConnectionFile("by-hand"));
        var publish = new WorkspaceGatewayPublish(new WorkspaceLink(this, build1, first), build1, _ => { });
        // the build's document of that name is not sent over the one made by hand
        var folder = BuildFolder("sales_catalog", "by-hand", ("by-hand", SalesData("by-hand")));

        var refused = await Assert.ThrowsAsync<WorkspacePublishException>(() => publish.PublishSemanticModelsAsync(folder, null));
        Assert.Equal(409, refused.Status);
        Assert.Contains("An administrator of the Gateway binds it once (PUT /api/v1/bindings/prod/semantic-model/sales_catalog", refused.Message);
        Assert.Equal("by-hand", (string)refused.Adopt!["data_source"]!);   // the screen still has what the confirmation (adopt) needs
        Assert.Equal(handMade, File.ReadAllText(ConnectionFile("by-hand")));
        Assert.Null(app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));

        Assert.Equal(HttpStatusCode.OK, (await BindAsync(admin, "semantic-model", "sales_catalog", "by-hand", "by-hand")).Status);
        var (models, sources) = await publish.PublishSemanticModelsAsync(folder, null);
        Assert.Equal("by-hand", (string)models[0]!["slots_used"]![0]!);
        Assert.Null(models[0]!["adopted"]);
        Assert.True((bool)sources[0]!["left_alone"]!);
        Assert.Equal(handMade, File.ReadAllText(ConnectionFile("by-hand")));
        Assert.Equal(1, app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));
        Assert.DoesNotContain(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source adopt");
    }

    [Fact]
    public async Task A_workspace_publishes_a_store_model_on_a_data_source_it_made_binding_the_slot_itself_and_on_one_made_by_hand_only_once_an_administrator_binds_it_or_with_adopt()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/data-sources/mine", ServerRole.Publisher, Put(Plain("mine"), null))).StatusCode);   // made through the per-kind route: a file, not a definition
        var publish = new WorkspaceGatewayPublish(new WorkspaceLink(this, build1, first), build1, _ => { });
        var entities = app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedEntityModels>();
        string Model(string name, string dataSource) => TerritoriesModel.Replace("name: territories", $"name: {name}", StringComparison.Ordinal).Replace("connection: by-hand", $"connection: {dataSource}", StringComparison.Ordinal);

        // its own: the slot is refused until bound, the Workspace binds it as the client that made the data source, and the version goes
        var own = await publish.PublishEntityModelsAsync([("mine.yaml", Model("mine", "mine"))], null, null);
        Assert.Equal(("mine", 1, true), ((string)own[0]!["model"]!, (int)own[0]!["version"]!, (bool)own[0]!["created"]!));
        Assert.NotNull(entities.Find("mine"));

        // one made by hand: refused naming who binds it, then bound by an administrator, then published with no confirmation
        var refused = await Assert.ThrowsAsync<WorkspacePublishException>(() => publish.PublishEntityModelsAsync([("territories.yaml", Model("territories", "by-hand"))], null, null));
        Assert.Contains("An administrator of the Gateway binds it once (PUT /api/v1/bindings/prod/entity-model/territories", refused.Message);
        Assert.Null(entities.Find("territories"));
        Assert.Equal(HttpStatusCode.OK, (await BindAsync(admin, "entity-model", "territories", "by-hand", "by-hand")).Status);
        var bound = await publish.PublishEntityModelsAsync([("territories.yaml", Model("territories", "by-hand"))], null, null);
        Assert.Equal("by-hand", (string)bound[0]!["slots_used"]![0]!);
        Assert.NotNull(entities.Find("territories"));

        // the other way, until it is retired (SC21f): the person's confirmation for this publish
        var adopted = await publish.PublishEntityModelsAsync([("again.yaml", Model("again", "by-hand"))], null, ["by-hand"]);
        Assert.Equal("by-hand", (string)adopted[0]!["adopted"]!);
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && ((string)a["outcome"]!).Contains("for the model again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workspace_publish_is_signed_with_the_publisher_key_when_the_group_requires_signatures_and_says_how_to_trust_the_key_when_it_does_not()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var publish = new WorkspaceGatewayPublish(new WorkspaceLink(this, build1, first), build1, _ => { });
        foreach (var kind in new[] { "data-source", "semantic-model" })
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, $"/api/v1/settings/signatures/{kind}", ServerRole.Admin, "{\"required\":true}")).StatusCode);
        var folder = BuildFolder("sales_catalog", "sales-ro", ("sales-ro", SalesData("sales-ro")));

        // the group requires signed versions and does not trust the key: the sentence names the call that trusts it, and nothing was written
        var untrusted = await Assert.ThrowsAsync<WorkspacePublishException>(() => publish.PublishSemanticModelsAsync(folder, null));
        Assert.Contains("requires signed versions of data-source and does not trust this client's key", untrusted.Message);
        Assert.Contains("PUT /api/v1/trust/data-source/keys", untrusted.Message);
        Assert.False(File.Exists(ConnectionFile("sales-ro")));

        foreach (var kind in new[] { "data-source", "semantic-model" })
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, $"/api/v1/trust/{kind}/keys", ServerRole.Admin, JsonSerializer(new { public_key = DbDataKit.State.Api.Url.Encode(build1.Key.PublicKey), label = "workspace" }))).StatusCode);
        var (models, _) = await publish.PublishSemanticModelsAsync(folder, null);
        Assert.Equal(1, (int)models[0]!["version"]!);
        var version = await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/definitions/semantic-model/sales_catalog/versions/1", ServerRole.Admin));
        Assert.StartsWith("ed25519:", (string)version["signature"]!);
    }
}
