using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.State;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Compile, lowered text and the offline sample of a Build draft (plan 33 SC20l): the product's own program on a copy (compile) or read only, the draft untouched. These run the stand-in <see cref="FakeBuild"/> (a shell script) for the mechanics:
/// what is compared with what, what is refused, and that nothing is written into the draft.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildCompileTests
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
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        await Put(server, id, "models/marts/orders.sql", "SELECT 1 AS id\n");
        await Put(server, id, "rendered/stale.sql", "-- old\n");
        return id;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Put(WorkspaceHarness server, string id, string path, string text) =>
        server.AsAda(HttpMethod.Put, $"/api/v1/projects/shop/workspaces/{id}/files/{path}", new JsonObject { ["content"] = text }.ToJsonString());

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Post(WorkspaceHarness server, string id, string route, string json = "{}") =>
        server.AsAda(HttpMethod.Post, $"/api/v1/projects/shop/workspaces/{id}/{route}", json);

    [Fact]
    public async Task A_compile_shows_what_it_would_add_change_and_remove_against_the_committed_plan_and_writes_nothing_in_the_draft()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        var compiled = await Post(server, id, "compile");
        Assert.Equal(HttpStatusCode.OK, compiled.Response.StatusCode);
        Assert.True((bool)compiled.Body!["ok"]!, compiled.Body!.ToJsonString());
        var files = compiled.Body["files"]!.AsArray().ToDictionary(f => (string)f!["path"]!, f => f!);
        Assert.Equal("added", (string)files["rendered/sqlserver/orders.sql"]["status"]!);
        Assert.Contains("SELECT 1 AS id", (string)files["rendered/sqlserver/orders.sql"]["content"]!);
        Assert.Equal("removed", (string)files["rendered/stale.sql"]["status"]!);
        Assert.Equal("unchanged", (string)files["rendered/refresh.plan.yaml"]["status"]!);
        Assert.Null(files["rendered/refresh.plan.yaml"]["content"]);

        // the draft is as it was: the compile ran on a copy
        var list = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.DoesNotContain("rendered/sqlserver/orders.sql", list);
        Assert.Contains("rendered/stale.sql", list);
    }

    [Fact]
    public async Task A_project_with_errors_is_not_compiled_and_the_tools_findings_name_the_file_and_line()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);
        await Put(server, id, "models/marts/orders.sql", "SELECT * FROM broken\n");

        var compiled = await Post(server, id, "compile");
        Assert.False((bool)compiled.Body!["ok"]!);
        Assert.Empty(compiled.Body["files"]!.AsArray());
        var finding = Assert.Single(compiled.Body["diagnostics"]!.AsArray());
        Assert.Equal(("DDB-301", "models/marts/orders.sql", 1), ((string)finding!["code"]!, (string)finding["file"]!, (int)finding["line"]!));
    }

    [Fact]
    public async Task One_model_is_lowered_read_only_and_the_path_must_be_a_model_file()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        var lowered = await Post(server, id, "lowered", """{"path":"models/marts/orders.sql"}""");
        Assert.Equal(HttpStatusCode.OK, lowered.Response.StatusCode);
        var file = Assert.Single(lowered.Body!["files"]!.AsArray());
        Assert.Equal("rendered/sqlserver/orders.sql", (string)file!["path"]!);
        Assert.Contains("-- lowered", (string)file["content"]!);

        foreach (var bad in new[] { "{}", """{"path":"../etc/passwd"}""", """{"path":"models/../dbdatabuild.yml"}""", """{"path":"-rf"}""", """{"path":"README.md"}""" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(server, id, "lowered", bad)).Response.StatusCode);
    }

    [Fact]
    public async Task The_sample_runs_on_generated_rows_with_the_asked_rows_seed_and_models_and_refuses_out_of_range_values()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);

        var sample = await Post(server, id, "sample", """{"models":["models/marts/orders.sql"],"rows":7,"seed":3,"limit":5}""");
        Assert.Equal(HttpStatusCode.OK, sample.Response.StatusCode);
        Assert.True((bool)sample.Body!["ok"]!, sample.Body!.ToJsonString());
        var table = Assert.Single(sample.Body["data"]!["tables"]!.AsArray())!;
        Assert.Equal(("marts.orders", 7), ((string)table["name"]!, (int)table["row_count"]!));
        Assert.Contains("models/marts/orders.sql limit:5", (string)table["warnings"]![0]!);

        Assert.Equal(HttpStatusCode.OK, (await Post(server, id, "sample")).Response.StatusCode);
        foreach (var bad in new[] { """{"rows":0}""", """{"rows":100000}""", """{"limit":0}""", """{"models":["x.sql"]}""", """{"models":"models/a.sql"}""" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(server, id, "sample", bad)).Response.StatusCode);
    }

    [Fact]
    public async Task A_model_that_does_not_run_says_so_with_the_tools_finding()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        var id = await DraftAsync(server);
        await Put(server, id, "models/marts/orders.sql", "SELECT * FROM broken\n");
        var sample = await Post(server, id, "sample");
        Assert.False((bool)sample.Body!["ok"]!);
        Assert.Equal("DDB-301", (string)sample.Body["diagnostics"]![0]!["code"]!);
    }

    [Fact]
    public async Task A_kind_with_nothing_to_compile_and_a_missing_program_answer_409()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Fake());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders"}""")).Response.StatusCode);
        var app = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var refused = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{app}/compile", "{}");
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("nothing to compile", (string)refused.Body!["error"]!);

        var tool = new BuildTool(Path.Combine(Path.GetTempPath(), "no-such-dbdatabuild"));
        await Assert.ThrowsAsync<DbDataKit.Authoring.ProjectKindUnavailableException>(() => new BuildProjectKind(tool).CompileAsync(Path.GetTempPath(), CancellationToken.None));
    }
}
