using System.Net;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Server;
using DbDataKit.Connections;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 13c D19 (owner decision of 2026-11-09), Build's side (owner decision corrected the same day): the serving policy of the connection file is published with the definition and each publish sends the policy the file has now (the Gateway replaces its copy
/// completely); a data source made by hand on the Gateway is replaced only by a publish that says <c>adopt</c>, and the Gateway's answer (what changed or was dropped) comes back to the caller.
/// </summary>
public sealed partial class BuildServerProxyTests
{
    private void WriteConnectionWithPolicy(string? gateway)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConnectionFile)!);
        File.WriteAllText(ConnectionFile, "schema: connection/1\nname: sqlserver\nengine: sqlserver\n" + (gateway is null ? "" : "extensions:\n  gateway:\n" + gateway));
    }

    [Fact]
    public async Task The_serving_policy_of_the_connection_file_is_published_with_the_data_source_and_each_publish_sends_exactly_the_policy_the_file_has_now()
    {
        await Pair();

        WriteConnectionWithPolicy("    batch_size: 1000\n    classifier: strict\n    compression:\n      enabled: true\n");
        var (first, loaded) = await PublishedText();
        Assert.StartsWith("schema: connection/2\n", first);
        var block = ServingPolicy.BlockOf(first)!;
        Assert.Equal(["batch_size", "classifier", "compression"], block.Entries.Select(e => e.Key.Value));
        Assert.Equal("1000", ((DbDataKit.Config.Yaml.YamlScalar)block.Get("batch_size")!).Value);
        Assert.Equal(AccessMode.Readonly, loaded.Access);                 // what is published stays read-only whatever the policy says

        // the project changes the policy: the next publish carries the new fields and none of the old
        WriteConnectionWithPolicy("    max_concurrent_queries: 4\n");
        var (second, _) = await PublishedText();
        Assert.Equal(["max_concurrent_queries"], ServingPolicy.BlockOf(second)!.Entries.Select(e => e.Key.Value));
        Assert.DoesNotContain("classifier", second);
        Assert.Equal(second, (await PublishedText()).Text);              // unchanged: the same text again

        // with grants as well (connection/2), and with no policy in the file: none is sent
        File.WriteAllText(ConnectionFile, "schema: connection/2\nname: sqlserver\nengine: sqlserver\ngrants:\n  groups: [Finance]\nextensions:\n  gateway:\n    batch_size: 5\n");
        var (both, bothLoaded) = await PublishedText();
        Assert.StartsWith("schema: connection/2\n", both);
        Assert.Equal(["Finance"], bothLoaded.Grants!.Groups);
        Assert.Equal(["batch_size"], ServingPolicy.BlockOf(both)!.Entries.Select(e => e.Key.Value));
        WriteConnectionWithPolicy(null);
        Assert.Null(ServingPolicy.BlockOf((await PublishedText()).Text));
    }

    [Fact]
    public void The_text_of_a_data_source_never_carries_allow_writes_even_if_a_policy_block_were_handed_to_it_with_only_published_fields()
    {
        var policy = ServingPolicy.BlockOf("schema: connection/1\nname: w\nengine: sqlserver\nextensions:\n  gateway:\n    batch_size: 9\n    compression:\n      enabled: true\n")!;
        var text = ProxyPublisher.DataSourceText("w", "sqlserver", "secret:x", null, policy);
        Assert.DoesNotContain("allow_writes", text);
        Assert.EndsWith("extensions:\n  gateway:\n    batch_size: 9\n    compression:\n      enabled: true\n", text);
        Assert.True(ConnectionDefinitionLoader.LoadDatabaseConnection(text, "w.yaml").Ok);
    }

    [Fact]
    public async Task A_data_source_made_by_hand_is_replaced_only_by_a_publish_that_says_adopt_and_what_the_gateway_answers_comes_back()
    {
        await Pair();
        WriteConnectionWithPolicy("    batch_size: 128\n");
        dataSourceStatus = 403;      // made by hand on the Gateway: this client may not read it

        // without adopt: refused as before, and the refusal offers the second way (publishing with adopt)
        var refused = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver", data_source = "warehouse" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var offer = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["adopt"]!;
        Assert.True((bool)offer["publish_adopt"]!);
        Assert.DoesNotContain(seen, r => r.Method == "PUT" && r.Path == "/api/v1/definitions/data-source/warehouse");

        // with adopt: the write goes with adopt: true, and the Gateway's answer (changes) is the caller's
        seen.Clear();
        var adopted = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver", data_source = "warehouse", adopt = true });
        Assert.True(adopted.StatusCode == HttpStatusCode.OK, await adopted.Content.ReadAsStringAsync());
        var write = JsonNode.Parse(seen.Last(r => r.Method == "PUT" && r.Path == "/api/v1/definitions/data-source/warehouse").Body)!;
        Assert.True((bool)write["adopt"]!);
        var answer = JsonNode.Parse(await adopted.Content.ReadAsStringAsync())!;
        Assert.True(answer.AsObject().ContainsKey("adopted"));
    }
}
