using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// A publishing client puts a model on a data source it did not make only when the request says <c>adopt: &lt;data source&gt;</c> and the data source was made by hand (plan 33 SC21f); the Gateway records who, when and which, in its audit and in the
/// data source's metadata (<c>adoptions</c> of the data source's read). A data source another server client made is never adopted this way.
/// </summary>
public sealed partial class ManagementTests
{
    private const string TerritoriesModel = "schemaVersion: 2\nname: territories\nstorage: {connection: by-hand}\nentities:\n  territory:\n    key: [code]\n    attributes:\n      code: {type: VARCHAR(10), required: true}\n      name: {type: VARCHAR(100), required: true}\n";

    private static string EntityBody(string dataSource, object? adopt = null, int? expected = null) =>
        JsonSerializer(new Dictionary<string, object?> { ["model"] = TerritoriesModel, ["data_source"] = dataSource, ["expected_version"] = expected, ["adopt"] = adopt });

    [Fact]
    public async Task A_model_goes_on_a_hand_made_data_source_only_with_adopt_naming_it_and_the_gateway_records_who_when_and_which()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));

        // without it: refused in words that say how, and with the data source named for a screen to ask about
        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/entity-models/territories", ServerRole.Publisher, EntityBody("by-hand"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = await BodyAsync(refused);
        Assert.Contains("\"adopt\": \"by-hand\"", (string)said["error"]!);
        Assert.Equal(("by-hand", "hand"), ((string)said["adopt"]!["data_source"]!, (string)said["adopt"]!["made_by"]!));

        // naming another data source is not consent to this one
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/entity-models/territories", ServerRole.Publisher, EntityBody("by-hand", adopt: "elsewhere"))).StatusCode);
        Assert.Empty((await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Admin)))["adoptions"]!.AsArray());

        var adopted = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/entity-models/territories", ServerRole.Publisher, EntityBody("by-hand", adopt: "by-hand"));
        Assert.True(adopted.StatusCode == HttpStatusCode.OK, await adopted.Content.ReadAsStringAsync());
        Assert.Equal("by-hand", (string)(await BodyAsync(adopted))["adopted"]!);

        var record = Assert.Single((await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Admin)))["adoptions"]!.AsArray())!;
        Assert.Equal(("by-hand", "build-1", "Ada", "territories", "entity-model"), ((string)record["data_source"]!, (string)record["client"]!, (string)record["by"]!, (string)record["model"]!, (string)record["kind"]!));
        Assert.True(DateTimeOffset.TryParse((string)record["at"]!, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(5));
        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && ((string)a["outcome"]!).StartsWith("adopted by build-1 (Ada)", StringComparison.Ordinal) && (string)a["consoleId"]! == "build-1");

        // the data source file is the definition and is not changed by it: still made by hand
        Assert.DoesNotContain("managed_by", File.ReadAllText(ConnectionPath("by-hand")));
    }

    [Fact]
    public async Task A_data_source_another_client_made_is_never_adopted_this_way_and_a_publishers_own_needs_no_adopt()
    {
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Publisher, Put(Plain("by-hand"), null));

        var refused = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/entity-models/territories", ServerRole.Publisher, EntityBody("by-hand", adopt: "by-hand"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("another client's data source", (string)(await BodyAsync(refused))["error"]!);
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/entity-models/territories", ServerRole.Publisher, EntityBody("by-hand"))).StatusCode);
    }

    [Fact]
    public async Task A_semantic_model_reads_a_hand_made_data_source_only_when_adopt_lists_it_and_the_adoption_is_recorded()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/sales-hand", ServerRole.Admin, Put(SalesData("sales-hand"), null));

        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-hand"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("sales-hand", (string)(await BodyAsync(refused))["adopt"]!["data_source"]!);

        var body = JsonNode.Parse(Publish(CatalogText, "sales-hand"))!.AsObject();
        body["adopt"] = new JsonArray("sales-hand");
        var adopted = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, body.ToJsonString());
        Assert.True(adopted.StatusCode == HttpStatusCode.OK, await adopted.Content.ReadAsStringAsync());
        Assert.Equal("sales-hand", (string)(await BodyAsync(adopted))["adopted"]![0]!);
        var record = Assert.Single((await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/sales-hand", ServerRole.Admin)))["adoptions"]!.AsArray())!;
        Assert.Equal(("build-1", "sales_catalog", "semantic-model"), ((string)record["client"]!, (string)record["model"]!, (string)record["kind"]!));
    }
}
