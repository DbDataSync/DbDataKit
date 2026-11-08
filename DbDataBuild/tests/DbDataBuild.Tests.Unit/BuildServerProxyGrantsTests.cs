using System.Net;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Server;
using DbDataKit.Connections;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 22a GP03 (owner decision of 2026-11-08): who may read a data source is authored in the project's connection file (<c>grants:</c>, <c>schema: connection/2</c>) and published with the definition. Each publish sends the
/// grants the file has now and nothing else (the Gateway replaces its copy completely); a connection with none publishes the <c>connection/1</c> file it always did.
/// </summary>
public sealed partial class BuildServerProxyTests
{
    private string ConnectionFile => Path.Combine(project, "connections", "sqlserver.yaml");

    private void WriteConnection(string? grants, int format = 2)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConnectionFile)!);
        File.WriteAllText(ConnectionFile, $"schema: connection/{format}\nname: sqlserver\nengine: sqlserver\n{grants}");
    }

    private async Task<(string Text, DbDataKit.Connections.ConnectionDefinition Loaded)> PublishedText()
    {
        seen.Clear();
        var reply = await Publish("ada", ServerRole.Approver, "edge", new { connection = "sqlserver", data_source = "warehouse" });
        Assert.True(reply.StatusCode == HttpStatusCode.OK, await reply.Content.ReadAsStringAsync());
        var text = (string)JsonNode.Parse(seen.Last(r => r.Method == "PUT" && r.Path == "/api/v1/data-sources/warehouse").Body)!["text"]!;
        return (text, ConnectionDefinitionLoader.Load(text, "warehouse.yaml").Require());
    }

    [Fact]
    public async Task The_grants_of_the_connection_file_are_published_with_the_data_source_and_each_publish_sends_exactly_the_ones_the_file_has_now()
    {
        await Pair();

        WriteConnection("grants:\n  groups: [Finance, auditors]\n");
        var (first, loaded) = await PublishedText();
        Assert.StartsWith("schema: connection/2\n", first);
        Assert.Equal(["Finance", "auditors"], loaded.Grants!.Groups);
        Assert.False(loaded.Grants.Everyone);

        // the project changes the list: the next publish carries the new list and none of the old
        WriteConnection("grants:\n  groups: [auditors, analysts]\n");
        var (second, changed) = await PublishedText();
        Assert.Equal(["auditors", "analysts"], changed.Grants!.Groups);
        Assert.DoesNotContain("Finance", second);

        // nothing changed in the project: the same text again
        Assert.Equal(second, (await PublishedText()).Text);

        // everyone, deliberately; then the grants are taken out of the file: a connection/1 file with no grants goes
        WriteConnection("grants:\n  everyone: true\n");
        Assert.True((await PublishedText()).Loaded.Grants!.Everyone);
        WriteConnection(null, format: 1);
        var (none, bare) = await PublishedText();
        Assert.StartsWith("schema: connection/1\n", none);
        Assert.Null(bare.Grants);

        // a connection with no file at all (named after its engine) publishes as it always did
        File.Delete(ConnectionFile);
        Assert.StartsWith("schema: connection/1\n", (await PublishedText()).Text);
    }

    [Fact]
    public void The_text_of_a_data_source_quotes_group_names_and_says_an_empty_grant_list_is_empty()
    {
        var odd = ProxyPublisher.DataSourceText("w", "sqlserver", "secret:x", new GrantsSpec { Groups = ["it's", "a b"] });
        Assert.Equal(["it's", "a b"], ConnectionDefinitionLoader.Load(odd, "w.yaml").Require().Grants!.Groups);
        var empty = ConnectionDefinitionLoader.Load(ProxyPublisher.DataSourceText("w", "sqlserver", "secret:x", new GrantsSpec()), "w.yaml").Require();
        Assert.Empty(empty.Grants!.Groups);
        Assert.False(empty.Grants.Everyone);
        Assert.StartsWith("schema: connection/1\n", ProxyPublisher.DataSourceText("w", "sqlserver", "secret:x", null));
    }
}
