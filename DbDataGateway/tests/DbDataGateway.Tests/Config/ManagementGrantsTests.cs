using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Data;
using Grpc.Core;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 22a GP03 and the owner's decision of 2026-11-08 (plan 20 task 13, groups on Build-published data sources): who may read a data source is part of the definition a Build project publishes. A publish replaces the grants
/// completely; nothing the Gateway added survives, because the Gateway has no grants of its own to add; it enforces exactly what the definition says. A change in the grant list moves access exactly: the groups removed lose it, the
/// groups added gain it, and a publish with no change leaves it as it was.
/// </summary>
public sealed partial class ManagementTests
{
    private static string Published(string name, string? grants, int format = 2) =>
        $"schema: connection/{format}\nname: {name}\nengine: duckdb\naccess: readonly\nconnection_string: 'Data Source=:memory:'\n{grants}";

    private static GatewayPrincipal Person(params string[] groups) =>
        new("Ada Lovelace", "home-token", [], defaultDataSource: null, credentials: null, homeUserId: "ada", homeGroups: groups);

    private bool MayUse(string source, params string[] groups)
    {
        try { app.Services.GetRequiredService<DataSourceCatalog>().Resolve(Person(groups), source); return true; }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied) { return false; }
    }

    private async Task<string> PublishAsync(ConsoleIdentity identity, PairedServer publisher, string name, string text, string? expected)
    {
        var answer = await CallAsAsync(identity, publisher, HttpMethod.Put, $"/api/v1/data-sources/{name}", ServerRole.Publisher, Put(text, expected));
        Assert.True(answer.StatusCode == HttpStatusCode.OK, $"publish: {answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
        return (string)JsonNode.Parse(await answer.Content.ReadAsStringAsync())!["version"]!;
    }

    [Fact]
    public async Task A_republish_with_another_grant_list_replaces_it_exactly_and_one_without_a_change_leaves_access_as_it_was()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);

        // the first publish: finance and auditors may use it, analysts may not (names match ignoring case)
        var first = await PublishAsync(build1, publisher, "retail", Published("retail", "grants:\n  groups: ['Finance', 'auditors']\n"), null);
        Assert.Equal(["Finance", "auditors"], Options.CurrentValue.DataSources["retail"].Groups);
        Assert.False(Options.CurrentValue.DataSources["retail"].Everyone);
        Assert.True(MayUse("retail", "finance"));
        Assert.True(MayUse("retail", "AUDITORS"));
        Assert.False(MayUse("retail", "analysts"));
        Assert.False(MayUse("retail"));

        // the Gateway shows them and changes nothing: they are the definition's
        var shown = JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/retail", ServerRole.Admin)).Content.ReadAsStringAsync())!["grants"]!;
        Assert.Equal("definition", (string)shown["source"]!);
        Assert.Equal(["Finance", "auditors"], shown["groups"]!.AsArray().Select(g => (string)g!).ToArray());
        Assert.False((bool)shown["everyone"]!);
        var listed = JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Admin)).Content.ReadAsStringAsync())!["data_sources"]!.AsArray();
        Assert.Equal("definition", (string)listed.Single(d => (string)d!["name"]! == "retail")!["grants"]!["source"]!);

        // an administrator's own addition to the file (the Gateway's side) ...
        var file = Path.Combine(ConfigDirectory, "connections", "retail.yaml");
        var edited = File.ReadAllText(file) + "extensions:\n  gateway:\n    batch_size: 500\n";
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/retail", ServerRole.Admin, Put(edited, first))).StatusCode);
        Assert.Equal(500, Options.CurrentValue.DataSources["retail"].BatchSize);
        var current = (string)JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/retail", ServerRole.Admin)).Content.ReadAsStringAsync())!["version"]!;

        // ... does not survive a republish with a changed list: auditors stay, finance loses it, analysts gain it, and the file is the definition and nothing else
        var changed = Published("retail", "grants:\n  groups: ['auditors', 'analysts']\n");
        var second = await PublishAsync(build1, publisher, "retail", changed, current);
        Assert.Equal(["auditors", "analysts"], Options.CurrentValue.DataSources["retail"].Groups);
        Assert.Equal(new DbDataGateway.Server.Configuration.DataSourceOptions().BatchSize, Options.CurrentValue.DataSources["retail"].BatchSize);
        Assert.False(MayUse("retail", "finance"));
        Assert.True(MayUse("retail", "auditors"));
        Assert.True(MayUse("retail", "analysts"));
        Assert.Equal(changed + "managed_by: build-1\n", File.ReadAllText(file));

        // the same publish again changes nothing: the same file, the same access
        var before = File.ReadAllText(file);
        var third = await PublishAsync(build1, publisher, "retail", changed, second);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(["auditors", "analysts"], Options.CurrentValue.DataSources["retail"].Groups);
        Assert.False(MayUse("retail", "finance"));
        Assert.True(MayUse("retail", "analysts"));

        // everyone, deliberately, then a publish with no grants at all (the project dropped them): closed again, the definition says so
        var open = await PublishAsync(build1, publisher, "retail", Published("retail", "grants: { everyone: true }\n"), third);
        Assert.True(Options.CurrentValue.DataSources["retail"].Everyone);
        Assert.Empty(Options.CurrentValue.DataSources["retail"].Groups);
        Assert.True(MayUse("retail"));
        await PublishAsync(build1, publisher, "retail", Published("retail", null, format: 1), open);
        Assert.False(Options.CurrentValue.DataSources["retail"].Everyone);
        Assert.False(MayUse("retail", "auditors", "analysts", "finance"));
    }

    [Fact]
    public async Task A_definition_says_who_in_grants_alone_and_never_in_two_places()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);

        // the grants are the definition's, and so is the serving policy (plan 13c D19); who may use the data source is `grants`, never groups of extensions.gateway
        var both = Published("mixed", "grants:\n  groups: [a]\nextensions:\n  gateway:\n    batch_size: 10\n");
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/mixed", ServerRole.Publisher, Put(both, null))).StatusCode);
        Assert.Equal(10, Options.CurrentValue.DataSources["mixed"].BatchSize);
        var groups = Published("grouped", "extensions:\n  gateway:\n    groups: [a]\n");
        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/grouped", ServerRole.Publisher, Put(groups, null));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("says who may use the data source; a definition says it in `grants`", await refused.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "grouped.yaml")));

        // grants and extensions.gateway.groups in one file: refused naming the file's line, and nothing is written
        var twice = await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/twice", ServerRole.Admin, Put(Published("twice", "grants:\n  groups: [a]\nextensions:\n  gateway:\n    groups: [b]\n"), null));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        var body = await twice.Content.ReadAsStringAsync();
        Assert.Contains("`grants` and `extensions.gateway.groups` both say who may use the data source", body);
        Assert.Contains("connections/twice.yaml", body.Replace("\\\\", "/").Replace('\\', '/'));      // the body is JSON: a Windows path comes in as connections\\twice.yaml
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "twice.yaml")));

        // grants in a connection/1 file: the stored-format refusal names the file, the key and the fix
        var old = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/old", ServerRole.Publisher, Put(Published("old", "grants: { everyone: true }\n", format: 1), null));
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        var oldBody = await old.Content.ReadAsStringAsync();
        Assert.Contains("`grants` is a section of format 2", oldBody);
        Assert.Contains("schema: connection/2", oldBody);
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "old.yaml")));

        // a hand-made file keeps its own policy, and the screen says it is the file's
        var made = await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/byhand", ServerRole.Admin, Put(Conn.Yaml("byhand", "duckdb", "Data Source=:memory:", gateway: ["groups: [ops]"]), null));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.True(MayUse("byhand", "ops"));
        var shown = JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/byhand", ServerRole.Admin)).Content.ReadAsStringAsync())!["grants"]!;
        Assert.Equal(("file", "ops"), ((string)shown["source"]!, (string)shown["groups"]![0]!));
    }
}
