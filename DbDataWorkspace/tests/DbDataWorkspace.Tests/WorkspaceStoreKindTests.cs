using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;

namespace DbDataWorkspace.Tests;

/// <summary>The Store model kind (plan 33 SC21a): a project starts as one model file, and a draft is checked with the model reader the Store itself uses, file and line included.</summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceStoreKindTests
{
    private static async Task<string> DraftAsync(WorkspaceHarness server, string project = "territories")
    {
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = project, ["kind"] = "store", ["title"] = "Sales \"territories\"" }.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Check(WorkspaceHarness server, string project, string id) => server.AsAda(HttpMethod.Get, $"/api/v1/projects/{project}/workspaces/{id}/check");

    [Fact]
    public async Task A_store_project_starts_as_one_sound_model_file_named_for_the_project()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server, "sales-2026");

        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/sales-2026/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("models/sales_2026.yaml", files);       // a project id with '-' is a plain identifier as a model name
        var check = await Check(server, "sales-2026", id);
        Assert.Equal("store", (string)check.Body!["kind"]!);
        Assert.True((bool)check.Body!["ok"]!, check.Body!.ToJsonString());
    }

    [Fact]
    public async Task A_damaged_model_names_its_code_file_and_line()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);
        var text = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/territories/workspaces/{id}/files/models/territories.yaml")).Body!["content"]!;
        // a key that names an attribute the entity does not declare
        var broken = text.Replace("key: [code]", "key: [nothing]");
        Assert.NotEqual(text, broken);
        Assert.Equal(HttpStatusCode.OK, (await server.AsAda(HttpMethod.Put, $"/api/v1/projects/territories/workspaces/{id}/files/models/territories.yaml", new JsonObject { ["content"] = broken }.ToJsonString())).Response.StatusCode);

        var check = await Check(server, "territories", id);
        Assert.False((bool)check.Body!["ok"]!);
        var finding = check.Body!["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.StartsWith("DBD-", (string)finding["code"]!);
        Assert.Equal("models/territories.yaml", (string)finding["file"]!);
        Assert.True((int)finding["line"]! > 0);
    }

    [Fact]
    public async Task A_draft_with_no_model_and_two_files_for_one_model_are_errors()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var id = await DraftAsync(server);
        var text = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/territories/workspaces/{id}/files/models/territories.yaml")).Body!["content"]!;

        await server.AsAda(HttpMethod.Put, $"/api/v1/projects/territories/workspaces/{id}/files/models/copy.yaml", new JsonObject { ["content"] = text }.ToJsonString());
        var twice = await Check(server, "territories", id);
        Assert.Contains(twice.Body!["diagnostics"]!.AsArray(), d => (string)d!["code"]! == "DBD-WS-STORE-DUPLICATE" && (string)d["message"]!.ToString() is { } m && m.Contains("models/copy.yaml") && (string)d["file"]! == "models/territories.yaml");

        await server.AsAda(HttpMethod.Delete, $"/api/v1/projects/territories/workspaces/{id}/files/models/copy.yaml");
        await server.AsAda(HttpMethod.Delete, $"/api/v1/projects/territories/workspaces/{id}/files/models/territories.yaml");
        var none = await Check(server, "territories", id);
        Assert.False((bool)none.Body!["ok"]!);
        Assert.Equal("DBD-WS-STORE-EMPTY", (string)none.Body!["diagnostics"]![0]!["code"]!);
    }

    [Fact]
    public void The_model_name_of_a_project_id_is_always_an_identifier()
    {
        Assert.Equal("orders", StoreProjectKind.ModelName("orders"));
        Assert.Equal("my_models", StoreProjectKind.ModelName("my-models"));
        Assert.Equal("m_2026_sales", StoreProjectKind.ModelName("2026-sales"));
    }
}
