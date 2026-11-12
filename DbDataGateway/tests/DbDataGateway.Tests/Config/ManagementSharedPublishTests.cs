using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;
using DbDataKit.Serving.Semantic;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 30 PB02: a Build server publishes a data source and a semantic model through the shared publish API (<c>PUT /api/v1/definitions/{kind}/{name}</c>) and gets what the per-kind routes give it: the owner rules and the client's stamp,
/// the serving policy check, the data source made by hand that is replaced only with <c>adopt</c>, a model refused when a data source does not answer or the client did not make it, the audit entries and the model served at once.
/// </summary>
public sealed partial class ManagementTests
{
    private static string DefinitionBody(string manifest, int? expected = 0, string[]? dependsOn = null, object? adopt = null, string? signature = null) =>
        JsonSerializer(new Dictionary<string, object?> { ["manifest"] = manifest, ["expected_version"] = expected, ["depends_on"] = dependsOn, ["adopt"] = adopt, ["signature"] = signature });

    private static string ModelManifest(string catalog, string? managedBy, string source) =>
        JsonSerializer(new { catalog, managedBy, sources = new Dictionary<string, string> { ["sqlserver_sales"] = source }, ssas = new Dictionary<string, string>() });

    private string StampedSales(string name, string client) => SalesData(name) + $"managed_by: {client}\n";

    private async Task<(HttpStatusCode Status, JsonNode Body)> DefineAsync(ConsoleIdentity who, PairedServer server, string kind, string name, string body)
    {
        var response = await CallAsAsync(who, server, HttpMethod.Put, $"/api/v1/definitions/{kind}/{name}", ServerRole.Publisher, body);
        return (response.StatusCode, await BodyAsync(response));
    }

    [Fact]
    public async Task A_publishing_client_publishes_a_data_source_through_the_shared_api_under_the_owner_rules_and_the_file_is_served_at_once()
    {
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);

        // the manifest is the whole file, signed as sent: it says whose it is, and the Gateway checks the line where the route added it
        var unstamped = await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(SalesData("sales-ro")));
        Assert.Equal(HttpStatusCode.BadRequest, unstamped.Status);
        Assert.Contains("managed_by: build-1", (string)unstamped.Body["error"]!);
        var other = await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", "build-2")));
        Assert.Equal(HttpStatusCode.Forbidden, other.Status);
        Assert.Equal("The file names another client in managed_by.", (string)other.Body["error"]!);
        Assert.False(File.Exists(ConnectionFile("sales-ro")));

        var created = await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", "build-1")));
        Assert.True(created.Status == HttpStatusCode.OK, created.Body.ToJsonString());
        Assert.True((bool)created.Body["created"]!);
        Assert.False((bool)created.Body["adopted"]!);
        Assert.Equal(1, (int)created.Body["version"]!);
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionFile("sales-ro")));
        Assert.True(Options.CurrentValue.DataSources.ContainsKey("sales-ro"));   // applied at once, like the route's write

        // the same file again is no new version, and the file stays
        var again = await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", "build-1"), expected: 1));
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(1, (int)again.Body["version"]!);
        Assert.False((bool)again.Body["created"]!);

        // another client neither reads, changes nor takes the data source
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/definitions/data-source/sales-ro", ServerRole.Publisher)).StatusCode);
        var taken = await DefineAsync(build2, second, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", "build-2"), expected: 1));
        Assert.Equal(HttpStatusCode.Forbidden, taken.Status);

        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "data-source write" && (string)a["target"]! == "sales-ro" && (string)a["outcome"]! == "created" && (string)a["consoleId"]! == "build-1");
    }

    [Fact]
    public async Task A_data_source_made_by_hand_is_replaced_through_the_shared_api_only_with_adopt_and_the_answer_says_what_changed()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null))).StatusCode);

        // reading it is refused (the way a client learns it is not its own), and a publish over it says how to take it
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/definitions/data-source/by-hand", ServerRole.Publisher)).StatusCode);
        var refused = await DefineAsync(build1, first, "data-source", "by-hand", DefinitionBody(Conn.Yaml("by-hand", "duckdb", "Data Source=:memory:") + "managed_by: build-1\n"));
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("\"adopt\": true", (string)refused.Body["error"]!);
        Assert.DoesNotContain("managed_by", File.ReadAllText(ConnectionFile("by-hand")));

        var adopted = await DefineAsync(build1, first, "data-source", "by-hand", DefinitionBody(Conn.Yaml("by-hand", "duckdb", "Data Source=:memory:", gateway: ["batch_size: 5"]) + "managed_by: build-1\n", adopt: true));
        Assert.True(adopted.Status == HttpStatusCode.OK, adopted.Body.ToJsonString());
        Assert.True((bool)adopted.Body["adopted"]!);
        Assert.NotEmpty(adopted.Body["changes"]!.AsArray());
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionFile("by-hand")));
        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && (string)a["outcome"]! == "adopted by build-1");
    }

    [Fact]
    public async Task A_model_is_published_through_the_shared_api_over_the_clients_own_data_sources_that_answer_and_is_served_at_once()
    {
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        var models = app.Services.GetRequiredService<SemanticModels>();
        Assert.True((await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", "build-1")))).Status == HttpStatusCode.OK);

        // the refusals are the route's, in its words
        var wrongOwner = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-2", "sales-ro")));
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.Status);
        Assert.Contains("carries its own client id ('build-1')", (string)wrongOwner.Body["error"]!);
        var upper = await DefineAsync(build1, first, "semantic-model", "Sales_Catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "sales-ro")));
        Assert.Equal(HttpStatusCode.BadRequest, upper.Status);
        Assert.Contains("lower-case name ('sales_catalog')", (string)upper.Body["error"]!);
        var missing = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "nowhere")));
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.False((bool)missing.Body["valid"]!);
        Assert.Contains("maps to 'nowhere', which is not a data source of this server", (string)missing.Body["error"]!);
        Assert.Null(models.Find("sales_catalog"));

        // a data source another client made is never read by this client's model
        Assert.True((await DefineAsync(build2, second, "data-source", "theirs", DefinitionBody(StampedSales("theirs", "build-2")))).Status == HttpStatusCode.OK);
        var theirs = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "theirs")));
        Assert.Equal(HttpStatusCode.BadRequest, theirs.Status);
        Assert.Contains("which this client did not make", (string)theirs.Body["error"]!);

        // an unreadable table is named
        var emptyData = Conn.Yaml("empty", "duckdb", "Data Source=:memory:") + "managed_by: build-1\n";
        Assert.True((await DefineAsync(build1, first, "data-source", "empty", DefinitionBody(emptyData))).Status == HttpStatusCode.OK);
        var unreadable = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "empty")));
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.Status);
        Assert.Contains("could not be read on data source 'empty'", (string)unreadable.Body["error"]!);

        var ok = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "sales-ro"), dependsOn: ["data-source:sales-ro"]));
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToJsonString());
        Assert.Equal(1, (int)ok.Body["version"]!);
        Assert.True((bool)ok.Body["created"]!);
        Assert.Equal("sales-ro", models.Find("semantic:sales_catalog")!.ServingSource("sqlserver_sales"));   // served here at once
        Assert.Equal(1, app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));

        // the same content again is no new version; a change is the next, over the version the client saw
        var same = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "sales-ro"), expected: 1, dependsOn: ["data-source:sales-ro"]));
        Assert.Equal(1, (int)same.Body["version"]!);
        Assert.False((bool)same.Body["created"]!);
        Assert.True((bool)same.Body["unchanged"]!);
        var stale = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText.Replace("Governed sales", "Sales"), "build-1", "sales-ro"), expected: 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);

        // the owner is the one the model names: another client neither reads nor replaces it, and the per-kind route agrees on whose it is
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/definitions/semantic-model/sales_catalog", ServerRole.Publisher)).StatusCode);
        var replaced = await DefineAsync(build2, second, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-2", "theirs"), expected: 1));
        Assert.Equal(HttpStatusCode.Forbidden, replaced.Status);
        var viaRoute = await BodyAsync(await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher));
        Assert.Equal(1, (int)viaRoute["current_version"]!);

        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "semantic-model publish" && (string)a["target"]! == "sales_catalog" && (string)a["outcome"]! == "created" && (string)a["consoleId"]! == "build-1");
        Assert.Contains(audit, a => (string)a["command"]! == "semantic-model publish" && (string)a["target"]! == "sales_catalog" && (string)a["outcome"]! == "unchanged");
    }

    [Fact]
    public async Task A_model_on_a_data_source_made_by_hand_is_published_through_the_shared_api_only_with_adopt_and_the_adoption_is_recorded()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(SalesData("by-hand"), null))).StatusCode);

        var refused = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "by-hand")));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.True(((string)refused.Body["error"]!).Contains("publish again with \"adopt\": \"by-hand\"", StringComparison.Ordinal), refused.Body.ToJsonString());
        Assert.Equal("by-hand", (string)refused.Body["adopt"]!["data_source"]!);

        var adopted = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, "build-1", "by-hand"), adopt: "by-hand"));
        Assert.True(adopted.Status == HttpStatusCode.OK, adopted.Body.ToJsonString());
        Assert.Equal("by-hand", (string)adopted.Body["adopted"]![0]!);
        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && ((string)a["outcome"]!).Contains("for the model sales_catalog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_signed_version_is_required_for_a_model_only_where_an_administrator_said_so_and_the_clients_key_is_the_one_the_group_trusts()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var stamped = StampedSales("sales-ro", "build-1");

        // the per-kind routes publish unsigned versions, so an unsigned one is taken until an administrator requires signatures for the kind
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(stamped))).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/settings/signatures/data-source", ServerRole.Admin, "{\"required\":true}")).StatusCode);
        var unsigned = await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", "build-1") + "# changed\n", expected: 1));
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.Status);
        Assert.Contains("requires signed versions of data-source", (string)unsigned.Body["error"]!);

        // the key a client pairs with is trusted by an administrator, and then its signed publish is verified
        var key = build1.Key;
        var keyBody = JsonSerializer(new { public_key = DbDataKit.State.Api.Url.Encode(key.PublicKey), label = "build-1" });
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/trust/data-source/keys", ServerRole.Admin, keyBody)).StatusCode);
        var text = StampedSales("sales-ro", "build-1") + "# changed\n";
        var signed = await DefineAsync(build1, first, "data-source", "sales-ro", DefinitionBody(text, expected: 1, signature: DefinitionSigning.Sign(key, "data-source", "sales-ro", text, [])));
        Assert.True(signed.Status == HttpStatusCode.OK, signed.Body.ToJsonString());
        Assert.Equal("verified", (string)signed.Body["signature"]!);
    }

    private static string EntityManifest(string? managedBy, string dataSource) => JsonSerializer(new { model = TerritoriesModel, dataSource, managedBy });

    [Fact]
    public async Task An_entity_model_is_published_through_the_shared_api_on_a_data_source_the_client_made_or_adopts_and_is_served_at_once()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build2, second, "data-source", "theirs", DefinitionBody(Plain("theirs") + "managed_by: build-2\n"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build1, first, "data-source", "mine", DefinitionBody(Plain("mine") + "managed_by: build-1\n"))).Status);

        var wrongOwner = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityManifest("build-2", "mine")));
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.Status);
        var nowhere = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityManifest("build-1", "nowhere")));
        Assert.Equal(HttpStatusCode.BadRequest, nowhere.Status);
        Assert.Contains("'nowhere' is not a data source of this server", (string)nowhere.Body["error"]!);
        var theirs = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityManifest("build-1", "theirs")));
        Assert.Equal(HttpStatusCode.BadRequest, theirs.Status);
        Assert.Contains("ask an administrator to hand it over", (string)theirs.Body["error"]!);
        var hand = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityManifest("build-1", "by-hand")));
        Assert.Equal(HttpStatusCode.BadRequest, hand.Status);
        Assert.Equal("by-hand", (string)hand.Body["adopt"]!["data_source"]!);

        var ok = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityManifest("build-1", "mine"), dependsOn: ["data-source:mine"]));
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToJsonString());
        Assert.True((bool)ok.Body["created"]!);
        Assert.NotNull(app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedEntityModels>().Find("territories"));   // served here at once

        var adopted = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityManifest("build-1", "by-hand"), expected: 1, adopt: "by-hand"));
        Assert.True(adopted.Status == HttpStatusCode.OK, adopted.Body.ToJsonString());
        Assert.Equal("by-hand", (string)adopted.Body["adopted"]!);
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && ((string)a["outcome"]!).Contains("for the model territories", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/definitions/entity-model/territories", ServerRole.Publisher)).StatusCode);
    }
}
