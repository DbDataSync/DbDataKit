using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataKit.Deploy.Tests;

public sealed class GatewayDefinitionsPublishTests
{
    private static readonly ConsoleIdentity Publisher = new("console-1", ConsoleSigningKey.Generate());

    internal static string Territories(string dataSource) =>
        $"schemaVersion: 2\nname: territories\nstorage: {{connection: {dataSource}}}\nentities:\n  territory:\n    key: [code]\n    attributes:\n      code: {{type: VARCHAR(10), required: true}}\n      name: {{type: VARCHAR(100), required: true}}\n";

    [Fact]
    public async Task A_model_on_a_data_source_the_caller_made_is_published_with_the_caller_as_its_owner()
    {
        using var link = new FakeLink
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("GET", "definitions/data-source") => (200, new JsonObject { ["definitions"] = new JsonArray(new JsonObject { ["name"] = "mine" }) }),
                ("PUT", "definitions/entity-model/territories") => (201, new JsonObject { ["version"] = 1, ["created"] = true }),
                _ => null,
            },
        };

        var published = await new GatewayDefinitionsPublish(link, Publisher, _ => { }).PublishEntityModelsAsync([("territories.yaml", Territories("mine"))], null, null);

        var sent = JsonNode.Parse(link.Calls.Single(c => c.Method == "PUT").Body!)!;
        var manifest = JsonNode.Parse((string)sent["manifest"]!)!;
        Assert.Equal(("console-1", "mine", null), ((string?)manifest["managedBy"], (string?)manifest["dataSource"], manifest["slots"]));
        Assert.Equal("data-source:mine", (string?)sent["depends_on"]![0]);
        Assert.Equal(("territories", 1, true), ((string?)published[0]!["model"], (int)published[0]!["version"]!, (bool)published[0]!["created"]!));
    }

    [Fact]
    public async Task A_model_deployed_from_a_build_carries_its_build_record_and_is_signed_over_it_when_the_gateway_trusts_the_key()
    {
        const string record = """{"schema":"build-record/1","build":"b1"}""";
        using var link = new FakeLink
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("GET", "definitions/data-source") => (200, new JsonObject { ["definitions"] = new JsonArray(new JsonObject { ["name"] = "mine" }) }),
                ("GET", "trust/entity-model") => (200, new JsonObject { ["required"] = false, ["keys"] = new JsonArray(new JsonObject { ["key_id"] = Publisher.Key.Fingerprint }) }),
                ("PUT", "definitions/entity-model/territories") => (201, new JsonObject { ["version"] = 1, ["created"] = true }),
                _ => null,
            },
        };

        await new GatewayDefinitionsPublish(link, Publisher, _ => { }, default, record).PublishEntityModelsAsync([("territories.yaml", Territories("mine"))], null, null);

        var sent = JsonNode.Parse(link.Calls.Single(c => c.Method == "PUT").Body!)!;
        Assert.Equal(record, (string?)sent["build"]);
        var signature = ((string)sent["signature"]!).Split(':');
        Assert.True(ConsoleSigningKey.Verify(Publisher.Key.PublicKey, DbDataKit.State.Api.DefinitionSigning.Message("entity-model", "territories", (string)sent["manifest"]!, [], record), DbDataKit.State.Api.Url.Decode(signature[2])!));
    }

    [Fact]
    public async Task A_slot_the_gateway_lets_the_caller_bind_is_bound_and_the_model_published_again()
    {
        var tries = 0;
        using var link = new FakeLink
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("GET", "definitions/data-source") => (200, new JsonObject { ["definitions"] = new JsonArray() }),
                ("PUT", "definitions/entity-model/territories") => ++tries == 1
                    ? (409, new JsonObject { ["error"] = "The slot 'by-hand' is not bound in prod.", ["code"] = "unboundslot" })
                    : (200, new JsonObject { ["version"] = 2, ["slots_used"] = new JsonArray("by-hand") }),
                ("PUT", "bindings/prod/entity-model/territories") => (200, new JsonObject()),
                _ => null,
            },
        };
        var steps = new List<string>();

        var published = await new GatewayDefinitionsPublish(link, Publisher, steps.Add).PublishEntityModelsAsync([("territories.yaml", Territories("by-hand"))], null, null);

        Assert.Equal(2, (int)published[0]!["version"]!);
        Assert.Contains("Bound 'by-hand' for territories on the Gateway.", steps);
    }

    [Fact]
    public async Task A_slot_only_an_administrator_binds_is_refused_with_the_call_and_the_data_source_for_an_adoption()
    {
        using var link = new FakeLink
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("GET", "definitions/data-source") => (200, new JsonObject { ["definitions"] = new JsonArray() }),
                ("PUT", "definitions/entity-model/territories") => (409, new JsonObject { ["error"] = "The slot 'by-hand' is not bound in prod.", ["code"] = "unboundslot" }),
                ("PUT", "bindings/prod/entity-model/territories") => (403, new JsonObject { ["error"] = "Only an administrator binds a data source made by hand." }),
                _ => null,
            },
        };

        var refused = await Assert.ThrowsAsync<DeployException>(() => new GatewayDefinitionsPublish(link, Publisher, _ => { }).PublishEntityModelsAsync([("territories.yaml", Territories("by-hand"))], null, null));

        Assert.Equal(409, refused.Status);
        Assert.Contains("The publisher could not bind it itself (Only an administrator binds a data source made by hand.)", refused.Message, StringComparison.Ordinal);
        Assert.Contains("An administrator of the Gateway binds it once (PUT /api/v1/bindings/prod/entity-model/territories", refused.Message, StringComparison.Ordinal);
        Assert.Equal(("by-hand", "adopt"), ((string?)refused.Adopt!["data_source"], (string?)refused.Adopt["field"]));
    }

    [Fact]
    public async Task A_confirmation_that_names_another_data_source_is_refused_before_the_model_is_sent()
    {
        using var link = new FakeLink { Answer = (_, path, _) => path == "definitions/data-source" ? (200, new JsonObject { ["definitions"] = new JsonArray() }) : null };

        var refused = await Assert.ThrowsAsync<DeployException>(() => new GatewayDefinitionsPublish(link, Publisher, _ => { }).PublishEntityModelsAsync([("territories.yaml", Territories("by-hand"))], null, ["erp"]));

        Assert.Equal((400, "The confirmation names 'erp', and territories goes on 'by-hand': confirm the data source it goes on."), (refused.Status, refused.Message));
        Assert.DoesNotContain(link.Calls, c => c.Method == "PUT");
    }

    [Fact]
    public async Task A_build_without_its_gateway_manifest_has_no_semantic_model_to_send()
    {
        var folder = Directory.CreateTempSubdirectory("deploy-gateway-").FullName;
        try
        {
            using var link = new FakeLink();
            var refused = await Assert.ThrowsAsync<DeployException>(() => new GatewayDefinitionsPublish(link, Publisher, _ => { }).PublishSemanticModelsAsync(folder, null));
            Assert.Equal("This build has no semantic model to publish (gateway.json is not in it).", refused.Message);
            Assert.Empty(await new GatewayDefinitionsPublish(link, Publisher, _ => { }).PublishViewsAsync(folder));
            Assert.Empty(link.Calls);
        }
        finally { Directory.Delete(folder, true); }
    }
}
