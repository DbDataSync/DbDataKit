using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Authoring.Server;
using DbDataKit.State;

namespace DbDataWorkspace.Tests;

/// <summary>The project-kind seam (plan 33 SC20a): the kinds are a registry the server reads, a project starts as its kind's template, and a draft is checked by its kind's validators.</summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceKindsTests
{
    [Fact]
    public void The_registry_and_the_state_module_name_the_same_kinds()
    {
        Assert.Equal(WorkspaceModule.ProjectKinds.Order(StringComparer.Ordinal), StandardProjectKinds.Create().Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_kind_name_is_unique_in_a_registry()
    {
        Assert.Throws<ArgumentException>(() => new ProjectKinds([new AppProjectKind(), new AppProjectKind()]));
    }

    [Fact]
    public async Task The_kinds_are_listed_with_what_each_publishes_and_where()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var answer = await server.AsAda(HttpMethod.Get, "/api/v1/project-kinds");
        Assert.Equal(HttpStatusCode.OK, answer.Response.StatusCode);
        var kinds = answer.Body!["kinds"]!.AsArray();
        var app = kinds.Single(k => (string)k!["name"]! == "app")!;
        Assert.True((bool)app["buildable"]!);
        Assert.Equal("gateway", (string)app["publishes"]![0]!["product"]!);
        Assert.Equal("app", (string)app["publishes"]![0]!["what"]!);
        Assert.Equal(WorkspaceModule.ProjectKinds.Count, kinds.Count);
        Assert.False((bool)app["produces"]!);
        // what a kind makes to publish, and where it goes (plan 33 SC20d, SC21b, SC22c)
        foreach (var (name, product, what) in new[] { ("build", "runner", "package"), ("build", "gateway", "semantic-model"), ("sync", "runner", "package"), ("store", "gateway", "model") })
        {
            var kind = kinds.Single(k => (string)k!["name"]! == name)!;
            Assert.True((bool)kind["produces"]!, name);
            Assert.False((bool)kind["buildable"]!, name);
            Assert.Contains(kind["publishes"]!.AsArray(), p => (string)p!["product"]! == product && (string)p["what"]! == what);
        }
    }

    [Fact]
    public async Task A_project_of_a_kind_the_server_does_not_know_is_refused_naming_the_kinds()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"odd","kind":"spreadsheet"}""");
        Assert.Equal(HttpStatusCode.BadRequest, made.Response.StatusCode);
        Assert.Contains("app", (string)made.Body!["error"]!);
    }

    [Fact]
    public async Task A_draft_is_checked_by_its_kinds_validator_and_a_finding_names_the_file()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders"}""")).Response.StatusCode);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;

        var sound = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/check");
        Assert.Equal(HttpStatusCode.OK, sound.Response.StatusCode);
        Assert.Equal("app", (string)sound.Body!["kind"]!);
        Assert.True((bool)sound.Body!["ok"]!, sound.Body!.ToJsonString());

        var write = await server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders/workspaces/{id}/files/app.yaml", new JsonObject { ["content"] = "name: Not A Valid Name\nversion: 1\n" }.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, write.Response.StatusCode);
        var broken = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/check");
        Assert.False((bool)broken.Body!["ok"]!);
        var first = broken.Body!["diagnostics"]!.AsArray()[0]!;
        Assert.Equal("error", (string)first["severity"]!);
        Assert.Equal("app.yaml", (string)first["file"]!);
        Assert.StartsWith("DBD-", (string)first["code"]!);
    }

    [Fact]
    public async Task Checking_a_draft_needs_a_role_in_its_project()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders"}""")).Response.StatusCode);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, "/api/v1/projects/orders/workspaces/nobody-none/check")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, "/api/v1/projects/ghost/workspaces/x/check")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/check")).Response.StatusCode);
    }
}
