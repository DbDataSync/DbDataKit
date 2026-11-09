using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 13c D19 (owner decision of 2026-11-09, corrected the same day): a published definition carries the Gateway's serving policy (<c>extensions.gateway</c>) and a republish replaces the whole file, <c>allow_writes</c> and
/// <c>allowed_directories</c> included: a definition that carries either is refused naming the field and the line, and a file made by hand that has them loses them when it is adopted. A publisher adopts a data source made by hand by saying <c>adopt: true</c>; the replaced file is in the history and the answer lists what changed or was dropped.
/// </summary>
public sealed partial class ManagementTests
{
    private static string Definition(string name, string? extensions = null, string access = "readonly") =>
        $"schema: connection/1\nname: {name}\nengine: duckdb\naccess: {access}\nconnection_string: 'Data Source=:memory:'\n{extensions}";

    private string ConnectionOf(string name) => Path.Combine(ConfigDirectory, "connections", name + ".yaml");

    private async Task<(HttpStatusCode Status, JsonNode Body)> PutAsAsync(ConsoleIdentity identity, PairedServer server, string name, string text, string? expected, bool adopt = false)
    {
        var body = new JsonObject { ["text"] = text, ["expected_version"] = expected };
        if (adopt) body["adopt"] = true;
        var answer = await CallAsAsync(identity, server, HttpMethod.Put, $"/api/v1/data-sources/{name}", ServerRole.Publisher, body.ToJsonString());
        return (answer.StatusCode, JsonNode.Parse(await answer.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task A_publish_carries_the_serving_policy_and_a_republish_replaces_it_whole_and_the_screen_says_whose_it_is()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);

        var first = await PutAsAsync(build1, publisher, "sales", Definition("sales", "extensions:\n  gateway:\n    batch_size: 1000\n    classifier: off\n    max_concurrent_queries: 3\n"), null);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal((1000, 3), (Options.CurrentValue.DataSources["sales"].BatchSize, Options.CurrentValue.DataSources["sales"].MaxConcurrentQueries));
        Assert.Equal(DbDataGateway.Server.Configuration.ClassifierMode.Off, Options.CurrentValue.DataSources["sales"].Classifier);
        Assert.False((bool)first.Body["adopted"]!);

        var shown = JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/sales", ServerRole.Admin)).Content.ReadAsStringAsync())!["policy"]!;
        Assert.Equal("definition", (string)shown["origin"]!);
        Assert.Equal(("1000", "off", "3"), ((string)shown["fields"]!["batch_size"]!, (string)shown["fields"]!["classifier"]!, (string)shown["fields"]!["max_concurrent_queries"]!));
        Assert.Empty(shown["blocked"]!.AsObject());

        // the next publish replaces the policy whole: the classifier is back to the default (strict in schema 2) and the limit is gone, batch size changed, nothing of the old is merged
        var second = await PutAsAsync(build1, publisher, "sales", Definition("sales", "extensions:\n  gateway:\n    batch_size: 2000\n"), (string)first.Body["version"]!);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        var options = Options.CurrentValue.DataSources["sales"];
        Assert.Equal((2000, 0, DbDataGateway.Server.Configuration.ClassifierMode.Strict), (options.BatchSize, options.MaxConcurrentQueries, options.Classifier));
        var changes = second.Body["changes"]!.AsArray();
        Assert.Contains(changes, c => (string)c!["field"]! == "extensions.gateway.classifier" && (string)c["change"]! == "dropped" && (string)c["before"]! == "off");
        Assert.Contains(changes, c => (string)c!["field"]! == "extensions.gateway.batch_size" && (string)c["change"]! == "changed" && (string)c["after"]! == "2000");

        // and a publish with no policy at all leaves the data source with none
        await PutAsAsync(build1, publisher, "sales", Definition("sales"), (string)second.Body["version"]!);
        Assert.Equal(new DbDataGateway.Server.Configuration.DataSourceOptions().BatchSize, Options.CurrentValue.DataSources["sales"].BatchSize);
        Assert.Empty(JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/sales", ServerRole.Admin)).Content.ReadAsStringAsync())!["policy"]!["fields"]!.AsObject());
    }

    [Fact]
    public async Task A_definition_that_carries_allow_writes_or_allowed_directories_is_refused_naming_the_field_and_the_line_and_nothing_is_written()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        const string extensions = "extensions:\n  gateway:\n    batch_size: 10\n    allowed_directories: ['/data']\n";

        var refused = await PutAsAsync(build1, publisher, "blocked", Definition("blocked", extensions), null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        var message = (string)refused.Body["error"]!;
        Assert.Contains("`extensions.gateway.allowed_directories`", message);
        Assert.Contains("Build does not author or publish it", message);
        Assert.Contains("line 9 of the data source text", message);
        Assert.False(File.Exists(ConnectionOf("blocked")));

        var writes = await PutAsAsync(build1, publisher, "blocked", Definition("blocked", "extensions:\n  gateway:\n    allow_writes: true\n", access: "readwrite"), null);
        Assert.Equal(HttpStatusCode.Forbidden, writes.Status);
        Assert.Contains("`extensions.gateway.allow_writes`", (string)writes.Body["error"]!);
        Assert.False(File.Exists(ConnectionOf("blocked")));
    }

    [Fact]
    public async Task A_publisher_adopts_a_data_source_made_by_hand_only_with_adopt_and_the_replaced_file_is_in_the_history_and_the_answer_lists_what_changed()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        var other = await PairPublisherAsync(build2);
        var handMade = Conn.Yaml("erp", "duckdb", "Data Source=:memory:", gateway: ["batch_size: 64", "classifier: strict", "groups: [ops]", "allow_writes: true", "allowed_directories: ['/data']"]);
        File.WriteAllText(ConnectionOf("erp"), handMade);
        File.WriteAllText(ConnectionOf("theirs"), Definition("theirs") + "managed_by: build-2\n");

        // without adopt: a 409 that says how, and the file is as it was
        var plain = await PutAsAsync(build1, publisher, "erp", Definition("erp", access: "readwrite"), null);
        Assert.Equal(HttpStatusCode.Conflict, plain.Status);
        Assert.Contains("\"adopt\": true", (string)plain.Body["error"]!);
        Assert.Equal(handMade, File.ReadAllText(ConnectionOf("erp")));

        // another client's data source is not adopted by anyone, adopt or not
        var theirs = await PutAsAsync(build1, publisher, "theirs", Definition("theirs"), null, adopt: true);
        Assert.Equal(HttpStatusCode.Forbidden, theirs.Status);
        Assert.Contains("another client", (string)theirs.Body["error"]!);

        // with adopt: the definition replaces the file whole
        var adopted = await PutAsAsync(build1, publisher, "erp", Definition("erp", "extensions:\n  gateway:\n    batch_size: 128\n", access: "readwrite"), null, adopt: true);
        Assert.Equal(HttpStatusCode.OK, adopted.Status);
        Assert.True((bool)adopted.Body["adopted"]!);
        var changes = adopted.Body["changes"]!.AsArray();
        string Change(string field) => (string)changes.Single(c => (string)c!["field"]! == field)!["change"]!;
        Assert.Equal("dropped", Change("extensions.gateway.classifier"));
        Assert.Equal("dropped", Change("extensions.gateway.groups"));
        Assert.Equal("changed", Change("extensions.gateway.batch_size"));
        Assert.Equal("added", Change("managed_by"));
        // the two fields the definition never carries are gone with the file, and the diff says so: read-only, no extra directories
        Assert.Equal("dropped", Change("extensions.gateway.allow_writes"));
        Assert.Equal("dropped", Change("extensions.gateway.allowed_directories"));
        var options = Options.CurrentValue.DataSources["erp"];
        Assert.Equal((128, false, ""), (options.BatchSize, options.AllowWrites, string.Join(",", options.AllowedDirectories)));
        Assert.DoesNotContain("groups", File.ReadAllText(ConnectionOf("erp")));
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionOf("erp")));

        // the file made by hand is in the history as it was, then the adoption
        var history = new GitCommitService(ConfigDirectory).GetHistory("connections/erp.yaml", 10);
        Assert.Equal(["Data source erp adopted by build-1: the definition replaces the file made by hand", "Data source erp as it was before build-1 adopted it"], history.Take(2).Select(c => c.Message).ToArray());
        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "erp" && (string)a["outcome"]! == "adopted by build-1" && (string)a["consoleId"]! == "build-1");

        // it is the client's now: the next publish needs no adopt, and the other client still cannot touch it
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsAsync(build2, other, "erp", Definition("erp"), null, adopt: true)).Status);
        var again = await PutAsAsync(build1, publisher, "erp", Definition("erp", access: "readwrite"), (string)adopted.Body["version"]!);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.False((bool)again.Body["adopted"]!);
    }

    [Fact]
    public async Task An_administrator_hands_over_a_data_source_with_policy_only_with_adopt_and_is_told_what_the_next_publish_replaces()
    {
        await StartAsync();
        var admin = await PairAsync();
        await PairPublisherAsync(build1);
        File.WriteAllText(ConnectionOf("limited"), Conn.Yaml("limited", "duckdb", "Data Source=:memory:", gateway: ["batch_size: 64", "allow_writes: true"]));
        var before = File.ReadAllText(ConnectionOf("limited"));

        var refused = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/limited/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var advice = await refused.Content.ReadAsStringAsync();
        Assert.Contains("replaces with the policy of its definition", advice);
        Assert.Contains("\\\"adopt\\\": true", advice);
        Assert.Equal(before, File.ReadAllText(ConnectionOf("limited")));

        var done = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/limited/adopt", ServerRole.Admin, JsonSerializer(new { client = "build-1", adopt = true }));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var answer = JsonNode.Parse(await done.Content.ReadAsStringAsync())!;
        Assert.Equal(["batch_size", "allow_writes"], answer["replaced_by_next_publish"]!.AsArray().Select(f => (string)f!).ToArray());
        var history = new GitCommitService(ConfigDirectory).GetHistory("connections/limited.yaml", 10);
        Assert.Contains(history, c => c.Message.StartsWith("Data source limited as it was before", StringComparison.Ordinal));
    }
}
