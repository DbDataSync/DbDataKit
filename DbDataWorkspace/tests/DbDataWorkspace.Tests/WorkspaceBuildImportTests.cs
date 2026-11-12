using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The mapped models of a Build draft's catalog cache (plan 33 SC20k): <c>dbdatabuild project import --from-cache --write</c> on a copy of the draft, the draft untouched, the text of each file it wrote passed back for the author to write through the file routes.
/// The stand-in <see cref="FakeBuild"/> for the mechanics; the real program for the shape and the type mapping.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildImportTests
{
    private static string Fake()
    {
        var path = FakeBuild.Create();
        if (path is null) Assert.Skip("the stand-in dbdatabuild is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","kind":"build"}""")).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Put(WorkspaceHarness server, string id, string path, string text) =>
        server.AsAda(HttpMethod.Put, $"/api/v1/projects/shop/workspaces/{id}/files/{path}", new JsonObject { ["content"] = text }.ToJsonString());

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Import(WorkspaceHarness server, string id, string json) =>
        server.AsAda(HttpMethod.Post, $"/api/v1/projects/shop/workspaces/{id}/import-preview", json);

    private const string Cache = "schema: catalog-cache/1\ngenerated_at: 2026-11-12T10:00:00Z\nsource: {server: prod-gateway, product: gateway}\nconnections:\n  sqlserver:\n    engine: sqlserver\n    tables:\n      - schema: crm\n        name: accounts\n        kind: table\n        columns:\n          - {name: id, type: int, nullable: false, key: true}\n          - {name: name, type: 'nvarchar(80)', nullable: false}\n          - {name: balance, type: 'decimal(18,2)', nullable: true}\n          - {name: opened, type: 'datetime2(3)', nullable: true}\n          - {name: area, type: geography, nullable: true}\n";

    [Fact]
    public async Task The_models_come_back_with_the_tools_account_and_the_text_it_wrote_and_the_draft_is_untouched()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);

        var answer = await Import(server, id, """{"connection":"sqlserver","tables":["src.*"]}""");
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        Assert.True((bool)answer.Body!["ok"]!, answer.Body.ToJsonString());
        var data = answer.Body["data"]!;
        Assert.Equal("new", (string)data["mapped_models"]![0]!["status"]!);
        var file = data["files"]![0]!;
        Assert.Equal("models/src/events.yml", (string)file["path"]!);
        Assert.Contains("kind: mapped", (string)file["content"]!);
        Assert.False((bool)file["exists"]!);

        var list = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.DoesNotContain("models/src/events.yml", list);      // it ran on a copy
    }

    [Fact]
    public async Task A_draft_with_no_cache_and_a_connection_the_cache_does_not_have_are_answered_with_findings()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        var none = await Import(server, id, """{"connection":"sqlserver"}""");
        Assert.False((bool)none.Body!["ok"]!);
        Assert.Contains("no catalog-cache.yaml", (string)none.Body["diagnostics"]![0]!["message"]!);

        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);
        var wrong = await Import(server, id, """{"connection":"nocache"}""");
        Assert.False((bool)wrong.Body!["ok"]!);
        var finding = Assert.Single(wrong.Body["diagnostics"]!.AsArray())!;
        Assert.Equal("DBD-WS-BUILD-IMPORT", (string)finding["code"]!);
        Assert.Contains("no entry for the connection `nocache`", (string)finding["message"]!);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"connection":"-x"}""")]
    [InlineData("""{"connection":"a/b"}""")]
    [InlineData("""{"connection":"sqlserver","tables":["--write"]}""")]
    [InlineData("""{"connection":"sqlserver","tables":["nodot"]}""")]
    [InlineData("""{"connection":"sqlserver","tables":["a/b.c"]}""")]
    [InlineData("""{"connection":"sqlserver","tables":[1]}""")]
    public async Task The_connection_and_the_patterns_are_checked_before_anything_runs(string body)
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        Assert.Equal(HttpStatusCode.BadRequest, (await Import(server, id, body)).Response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_ask_because_it_runs_on_a_copy_and_reads_no_database()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);

        var viewer = await server.Send("vic", ServerRole.Viewer, HttpMethod.Post, $"/api/v1/projects/shop/workspaces/{id}/import-preview", """{"connection":"sqlserver"}""");
        Assert.Equal(HttpStatusCode.OK, viewer.Response.StatusCode);
    }

    [Fact]
    public async Task The_real_program_makes_the_descriptor_from_the_cached_types_and_leaves_out_what_has_no_logical_type()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: WorkspaceBuildKindTests.Need());
        var id = await DraftAsync(server);
        Assert.True((await Put(server, id, "catalog-cache.yaml", Cache)).Response.IsSuccessStatusCode);

        var answer = await Import(server, id, """{"connection":"sqlserver","tables":["crm.*"]}""");
        Assert.True((bool)answer.Body!["ok"]!, answer.Body.ToJsonString());
        var data = answer.Body["data"]!;
        var model = data["mapped_models"]!.AsArray().Single(m => (string)m!["name"]! == "crm.accounts")!;
        Assert.Equal("new", (string)model["status"]!);
        string Logical(string column) => (string?)model["columns"]!.AsArray().Single(c => (string)c!["name"]! == column)!["logical_type"] ?? "(none)";
        Assert.Equal(("INTEGER", "VARCHAR(80)", "DECIMAL(18, 2)", "TIMESTAMP", "(none)"), (Logical("id"), Logical("name"), Logical("balance"), Logical("opened"), Logical("area")));
        Assert.Contains(data["catalog_notes"]!.AsArray(), n => ((string)n!).Contains("no index and no foreign key"));
        var file = Assert.Single(data["files"]!.AsArray())!;
        Assert.Equal("models/crm/accounts.yml", (string)file["path"]!);
        var text = (string)file["content"]!;
        Assert.Contains("grain: [id]", text);
        Assert.DoesNotContain("area", text);

        // writing it through the file route gives a draft that checks sound
        Assert.True((await Put(server, id, "models/crm/accounts.yml", text)).Response.IsSuccessStatusCode);
        var again = await Import(server, id, """{"connection":"sqlserver","tables":["crm.*"]}""");
        Assert.Equal("unchanged", (string)again.Body!["data"]!["mapped_models"]!.AsArray().Single(m => (string)m!["name"]! == "crm.accounts")!["status"]!);
    }
}
