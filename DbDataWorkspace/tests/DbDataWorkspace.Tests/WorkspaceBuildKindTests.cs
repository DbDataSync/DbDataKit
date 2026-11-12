using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.State;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The Build project kind (plan 33 SC20c): a new project starts as Build's own starter template (made by the real <c>dbdatabuild</c>) and a draft is checked by it, offline. The program is <c>DBDATAKIT_TEST_DBDATABUILD</c>, or the
/// one a build of <c>DbDataBuild/src/DbDataBuild.Cli</c> leaves in the repository; the tests that need it skip without it (<c>DBDATAKIT_TEST_REQUIRE_BUILD=true</c> makes that a failure).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildKindTests
{
    internal static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_DBDATABUILD") is { Length: > 0 } given) return File.Exists(given) ? given : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DbDataBuild", "src", "DbDataBuild.Cli", "bin", "Debug", "net10.0", OperatingSystem.IsWindows() ? "dbdatabuild.exe" : "dbdatabuild");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static string Need()
    {
        var found = Locate();
        Assert.False(found is null && string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_BUILD"), "true", StringComparison.OrdinalIgnoreCase), "dbdatabuild is required but was not found.");
        if (found is null) Assert.Skip("dbdatabuild is not built (DbDataBuild/src/DbDataBuild.Cli) and DBDATAKIT_TEST_DBDATABUILD is not set");
        return found;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server, string project = "shop")
    {
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = project, ["kind"] = "build", ["title"] = "Shop models" }.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    [Fact]
    public async Task A_build_project_starts_as_the_starter_template_titled_for_it_and_its_draft_checks_sound()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Need());
        var id = await DraftAsync(server);

        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("dbdatabuild.yml", files);
        Assert.Contains("models/marts/orders.sql", files);
        Assert.Contains("README.md", files);
        var config = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/files/dbdatabuild.yml")).Body!["content"]!;
        Assert.Contains("Shop models", config);

        var check = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/check");
        Assert.Equal("build", (string)check.Body!["kind"]!);
        Assert.True((bool)check.Body!["ok"]!, check.Body!.ToJsonString());
    }

    [Fact]
    public async Task A_damaged_model_file_is_reported_with_its_code_file_and_line_by_the_tool_itself()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Need());
        var id = await DraftAsync(server);
        var original = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/files/models/marts/orders.yml")).Body!["content"]!;
        var write = await server.AsAda(HttpMethod.Put, $"/api/v1/projects/shop/workspaces/{id}/files/models/marts/orders.yml", new JsonObject { ["content"] = original + "\ngarbage: [\n" }.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, write.Response.StatusCode);

        var check = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/check");
        Assert.False((bool)check.Body!["ok"]!);
        var finding = check.Body!["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("DDB-101", (string)finding["code"]!);
        Assert.Equal("models/marts/orders.yml", (string)finding["file"]!);
        Assert.True((int)finding["line"]! > 0);
    }

    [Fact]
    public async Task The_check_runs_in_the_draft_and_writes_nothing_there()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Need());
        var id = await DraftAsync(server);
        await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/check");
        var changes = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/changes");
        Assert.Equal(HttpStatusCode.OK, changes.Response.StatusCode);
        Assert.Empty(changes.Body!["changes"]!.AsArray());
    }

    [Fact]
    public async Task Without_the_program_a_project_cannot_be_made_and_a_check_says_why()
    {
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Path.Combine(Path.GetTempPath(), "no-such-dbdatabuild"));
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","kind":"build"}""");
        Assert.Equal(HttpStatusCode.Conflict, made.Response.StatusCode);
        Assert.Contains("dbdatabuild is not beside it or on its PATH", (string)made.Body!["error"]!);
        Assert.False(Directory.Exists(Path.Combine(server.Directory, "projects", "shop")));
        Assert.Null(server.Store().FindProject("shop"));
    }

    [Fact]
    public async Task A_build_project_added_in_place_is_checked_the_same_and_says_so_when_the_tool_is_gone()
    {
        // an app project exists beside it, so the kinds are told apart by the project's row, not by its files
        await using var server = await WorkspaceHarness.StartAsync(buildExecutable: Path.Combine(Path.GetTempPath(), "no-such-dbdatabuild"));
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders"}""")).Response.StatusCode);
        var store = server.Store();
        var folder = Directory.CreateTempSubdirectory("ddw-inplace-").FullName;
        LibGit2Sharp.Repository.Init(folder);
        File.WriteAllText(Path.Combine(folder, "dbdatabuild.yml"), "schema: dbdatabuild.project/2\n");
        DbDataKit.Config.Git.GitWorkspaceOperations.Commit(folder, "first", new DbDataKit.Config.Git.GitAuthor("t", "t@x"));
        Assert.True(store.AddProject("shop", "build", null, folder, "root"));
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var check = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{id}/check");
        Assert.False((bool)check.Body!["ok"]!);
        Assert.Equal("DBD-WS-BUILD-MISSING", (string)check.Body!["diagnostics"]![0]!["code"]!);
    }
}
