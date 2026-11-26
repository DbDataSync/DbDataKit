using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The hand-over of what one paired client owns to another (<c>PUT /api/v1/settings/handovers/{client}</c>, administrators): a console takes over what a Workspace server's publisher key published, and from then on publishes over it
/// as the person, with no adoption asked; the Gateway's audit records the change of owner of each definition, and the client handed over no longer changes what it made.
/// </summary>
public sealed partial class ManagementTests
{
    private readonly ConsoleIdentity workspaceKey = new("workspace-0a1b2c", ConsoleSigningKey.Generate());

    private async Task<(HttpStatusCode Status, JsonNode Body)> AsConsoleAsync(PairedServer server, HttpMethod method, string path, ServerRole role, string? body = null)
    {
        var response = await CallAsync(server, method, path, role, body);
        return (response.StatusCode, await BodyAsync(response));
    }

    [Fact]
    public async Task An_administrator_hands_what_a_workspace_key_published_to_the_console_which_then_publishes_over_it_as_the_person_with_no_adoption()
    {
        await StartAsync();
        var console1 = await PairAsync();
        var workspace = await PairPublisherAsync(workspaceKey);
        Assert.True((await DefineAsync(workspaceKey, workspace, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", workspaceKey.Id)))).Status == HttpStatusCode.OK);
        var model = await DefineAsync(workspaceKey, workspace, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(CatalogText, workspaceKey.Id, "sales-ro"), dependsOn: ["data-source:sales-ro"]));
        Assert.True(model.Status == HttpStatusCode.OK, model.Body.ToJsonString());

        // before: the console publishes as a person who holds the publisher role, and the Workspace's data source is not its own
        var before = await AsConsoleAsync(console1, HttpMethod.Put, "/api/v1/definitions/data-source/sales-ro", ServerRole.Publisher, DefinitionBody(StampedSales("sales-ro", "console-1"), expected: 1));
        Assert.Equal(HttpStatusCode.Forbidden, before.Status);

        // the hand-over is an administrator's, to a paired client other than the one handed over
        Assert.Equal(HttpStatusCode.Forbidden, (await AsConsoleAsync(console1, HttpMethod.Put, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Publisher, "{\"to\":\"console-1\"}")).Status);
        var nobody = await AsConsoleAsync(console1, HttpMethod.Put, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Admin, "{\"to\":\"console-9\"}");
        Assert.Equal((HttpStatusCode.NotFound, "There is no paired client 'console-9'."), (nobody.Status, (string)nobody.Body["error"]!));
        Assert.Equal(HttpStatusCode.BadRequest, (await AsConsoleAsync(console1, HttpMethod.Put, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Admin, $"{{\"to\":\"{workspaceKey.Id}\"}}")).Status);

        var handed = await AsConsoleAsync(console1, HttpMethod.Put, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Admin, "{\"to\":\"console-1\"}");
        Assert.True(handed.Status == HttpStatusCode.OK, handed.Body.ToJsonString());
        Assert.Equal(("console-1", "Ada", false), ((string)handed.Body["handover"]!["to"]!, (string)handed.Body["handover"]!["by"]!, (bool)handed.Body["unchanged"]!));
        var moved = handed.Body["definitions"]!.AsArray().Select(d => $"{d!["kind"]}/{d["name"]}").ToList();
        Assert.Contains("data-source/sales-ro", moved);
        Assert.Contains("semantic-model/sales_catalog", moved);
        var again = await AsConsoleAsync(console1, HttpMethod.Put, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Admin, "{\"to\":\"console-1\"}");
        Assert.True((bool)again.Body["unchanged"]!);
        var listed = await AsConsoleAsync(console1, HttpMethod.Get, "/api/v1/settings/handovers", ServerRole.Viewer);
        Assert.Equal(workspaceKey.Id, (string)listed.Body["handovers"]![0]!["from"]!);

        // after: the console's publish over the Workspace's data source and model goes through with no adopt, and each version records the person
        var source = await AsConsoleAsync(console1, HttpMethod.Put, "/api/v1/definitions/data-source/sales-ro", ServerRole.Publisher, DefinitionBody(StampedSales("sales-ro", "console-1"), expected: 1));
        Assert.True(source.Status == HttpStatusCode.OK, source.Body.ToJsonString());
        Assert.Equal((2, false), ((int)source.Body["version"]!, (bool)source.Body["adopted"]!));
        Assert.Contains("managed_by: console-1", File.ReadAllText(ConnectionFile("sales-ro")));
        var republished = await AsConsoleAsync(console1, HttpMethod.Put, "/api/v1/definitions/semantic-model/sales_catalog", ServerRole.Publisher,
            DefinitionBody(ModelManifest(CatalogText, "console-1", "sales-ro"), expected: 1, dependsOn: ["data-source:sales-ro"]));
        Assert.True(republished.Status == HttpStatusCode.OK, republished.Body.ToJsonString());
        var described = await AsConsoleAsync(console1, HttpMethod.Get, "/api/v1/definitions/data-source/sales-ro", ServerRole.Viewer);
        Assert.Equal("console-1", (string)described.Body["owner"]!);
        Assert.Equal("Ada", (string)described.Body["versions"]![0]!["published_by"]!);

        // the client handed over owns nothing now: it neither reads nor changes what it made
        Assert.Equal(HttpStatusCode.Forbidden, (await DefineAsync(workspaceKey, workspace, "data-source", "sales-ro", DefinitionBody(StampedSales("sales-ro", workspaceKey.Id), expected: 2))).Status);
        Assert.Empty((await BodyAsync(await CallAsAsync(workspaceKey, workspace, HttpMethod.Get, "/api/v1/definitions/data-source", ServerRole.Publisher)))["definitions"]!.AsArray());

        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        Assert.Contains(audit, a => (string)a["command"]! == "definition.handover" && (string)a["target"]! == $"client/{workspaceKey.Id}" && (string)a["outcome"]! == "handed to console-1");
        Assert.Contains(audit, a => (string)a["command"]! == "definition.owner" && (string)a["target"]! == "data-source/sales-ro" && (string)a["outcome"]! == $"handed from {workspaceKey.Id} to console-1");
        Assert.Contains(audit, a => (string)a["command"]! == "definition.owner" && (string)a["target"]! == "semantic-model/sales_catalog");

        // the hand-over ends by an administrator's call; what carries the Workspace's id is its own again
        Assert.Equal(HttpStatusCode.OK, (await AsConsoleAsync(console1, HttpMethod.Delete, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await AsConsoleAsync(console1, HttpMethod.Delete, $"/api/v1/settings/handovers/{workspaceKey.Id}", ServerRole.Admin)).Status);
    }
}
