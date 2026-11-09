using System.Net;
using DbDataKit.Web.Trust;
using LibGit2Sharp;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The Workspace server's management API (plan 22a, GP31): a console creates a project, a person makes a workspace in it and sees it listed, and what happens to it is recorded in the <c>workspace</c> state module. The done-when of the slice
/// runs on SQLite, PostgreSQL and SQL Server; the roles are checked on every call.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceManagementTests
{
    private const string Orders = """{"id":"orders","title":"Orders"}""";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_server_starts_pairs_and_lists_a_workspace_made_in_a_project_and_the_state_module_records_it(string engine)
    {
        await using var server = await WorkspaceHarness.StartAsync(engine);

        var made = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", Orders);
        Assert.Equal(HttpStatusCode.Created, made.Response.StatusCode);
        Assert.Equal(("orders", "app"), ((string)made.Body!["id"]!, (string)made.Body["kind"]!));

        var workspace = await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"new-orders"}""");
        Assert.Equal(HttpStatusCode.Created, workspace.Response.StatusCode);
        Assert.Equal("ada-new-orders", (string)workspace.Body!["id"]!);
        Assert.True(File.Exists(Path.Combine(server.Directory, "workspaces", "orders", "ada-new-orders", "README.md")));

        var listed = (await server.AsAda(HttpMethod.Get, "/api/v1/projects/orders/workspaces")).Body!["workspaces"]!.AsArray();
        Assert.Equal(["ada-new-orders"], listed.Select(w => (string)w!["id"]!));
        Assert.True((bool)listed[0]!["mine"]!);
        var projects = (await server.AsAda(HttpMethod.Get, "/api/v1/projects")).Body!["projects"]!.AsArray();
        Assert.Equal(1, (int)projects.Single()!["workspaces"]!);

        // the state module has the draft and its creation
        var store = server.Store();
        var row = Assert.Single(store.Workspaces("orders"));
        Assert.Equal(("ada-new-orders", "ada", "ws/ada-new-orders"), (row.Id, row.Owner, row.Branch));
        var events = (await server.AsAda(HttpMethod.Get, "/api/v1/projects/orders/workspaces/ada-new-orders/events")).Body!["events"]!.AsArray();
        Assert.Equal(["created"], events.Select(e => (string)e!["kind"]!));

        Assert.Equal(HttpStatusCode.NoContent, (await server.AsAda(HttpMethod.Delete, "/api/v1/projects/orders/workspaces/ada-new-orders")).Response.StatusCode);
        Assert.Empty(store.Workspaces("orders"));
        Assert.Equal(["abandoned", "created"], store.Events("orders", "ada-new-orders").Select(e => e.Kind));
        Assert.Equal(0, (int)(await server.AsAda(HttpMethod.Get, "/api/v1/projects")).Body!["projects"]![0]!["workspaces"]!);
    }

    [Fact]
    public async Task Committing_pushing_and_merging_are_recorded_and_a_merge_ends_the_workspace()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", Orders);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        File.AppendAllText(Path.Combine(server.Directory, "workspaces", "orders", id, "README.md"), "More.\n");

        var committed = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"Say more"}""");
        Assert.True((bool)committed.Body!["committed"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/merge")).Response.StatusCode);
        var merged = await server.Send("ann", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/merge");
        Assert.True((bool)merged.Body!["merged"]!);

        Assert.Contains("More.", File.ReadAllText(Path.Combine(server.Directory, "projects", "orders", "README.md")));
        var store = server.Store();
        Assert.Empty(store.Workspaces("orders"));
        var history = store.Events("orders", id);
        Assert.Equal(["merged", "committed", "created"], history.Select(e => e.Kind));
        Assert.Equal(("Say more", "ann"), (history[1].Detail, history[0].Actor));
        using var repo = new Repository(Path.Combine(server.Directory, "projects", "orders"));
        Assert.Equal("Say more", repo.Head.Tip.Message.Trim());
    }

    [Fact]
    public async Task Making_a_project_is_an_administrators_and_is_checked_and_audited()
    {
        await using var server = await WorkspaceHarness.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Post, "/api/v1/projects", Orders)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", "{}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"Has Spaces"}""")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"x","kind":"agent"}""")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", Orders)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", Orders)).Response.StatusCode);

        Assert.Equal(["project.create", "project.create", "project.create", "project.create"], server.Audit().Select(a => a.Command));
        Assert.Contains(server.Audit(), a => a.Outcome == "ok" && a.Target == "orders" && a.UserId == "root");
        Assert.Contains(server.Audit(), a => a.Outcome.StartsWith("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_viewer_reads_and_cannot_make_or_abandon_and_a_token_scoped_to_one_project_sees_only_that_one()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", Orders);
        await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"billing"}""");
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;

        Assert.Equal(HttpStatusCode.OK, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, "/api/v1/projects/orders/workspaces")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/events")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"mine"}""")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Send("bob", ServerRole.Author, HttpMethod.Delete, $"/api/v1/projects/orders/workspaces/{id}")).Response.StatusCode);   // only the owner

        var scoped = new Dictionary<string, ServerRole> { ["billing"] = ServerRole.Author };
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Send("nobody", ServerRole.None, HttpMethod.Get, "/api/v1/projects")).Response.StatusCode);   // no role anywhere: nothing
        var seen = (await server.Send("sue", ServerRole.None, HttpMethod.Get, "/api/v1/projects", projects: scoped)).Body!["projects"]!.AsArray();
        Assert.Equal(["billing"], seen.Select(p => (string)p!["id"]!));       // the project it holds a role on, and not orders
        Assert.Equal(HttpStatusCode.NotFound, (await server.Send("sue", ServerRole.Viewer, HttpMethod.Get, "/api/v1/projects/nope/workspaces")).Response.StatusCode);
    }

    [Fact]
    public async Task The_publishers_key_is_kept_as_a_reference_only_and_by_an_administrator()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        const string key = "AAAAC3NzaC1lZDI1NTE5AAAAIGkZ0q";

        var set = await server.AsAdmin(HttpMethod.Put, "/api/v1/publisher-keys/main-group", """{"secret_ref":"secret:dbdata:workspace:publisher:main-group"}""");
        Assert.Equal(HttpStatusCode.OK, set.Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAdmin(HttpMethod.Put, "/api/v1/publisher-keys/main-group", $$"""{"secret_ref":"{{key}}"}""")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AsAdmin(HttpMethod.Put, "/api/v1/publisher-keys/main-group", "{}")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Put, "/api/v1/publisher-keys/main-group", """{"secret_ref":"secret:x"}""")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.AsAda(HttpMethod.Get, "/api/v1/publisher-keys")).Response.StatusCode);

        var keys = (await server.AsAdmin(HttpMethod.Get, "/api/v1/publisher-keys")).Body!["keys"]!.AsArray();
        Assert.Equal("secret:dbdata:workspace:publisher:main-group", (string)keys.Single()!["secret_ref"]!);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(server.Directory, "management-audit.log")));
        Assert.Equal(HttpStatusCode.NoContent, (await server.AsAdmin(HttpMethod.Delete, "/api/v1/publisher-keys/main-group")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAdmin(HttpMethod.Delete, "/api/v1/publisher-keys/main-group")).Response.StatusCode);
        Assert.Contains(server.Audit(), a => a.Command == "publisher-key.remove");
    }
}
