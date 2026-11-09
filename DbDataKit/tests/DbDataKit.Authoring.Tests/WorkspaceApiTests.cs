using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Authoring.Tests;

/// <summary>
/// The workspace calls on a project kind that is not Build's (plan 22a, GP30): a fake app project, served by a bare host that knows only its project folder. The tests are the ones Build's server tests run (plan 13, W02, W08, W09:
/// made, listed and abandoned by the owner, a draft that never moves the checkout, changes, commit, merge by an approver, a conflict, a push to a remote), so one behaviour serves every kind.
/// </summary>
public sealed class WorkspaceApiTests : IAsyncLifetime
{
    private const string InstanceId = "app-server-1";
    private readonly string root = Directory.CreateTempSubdirectory("authoring-").FullName;
    private readonly ConsoleSigningKey key = ConsoleSigningKey.Generate();
    private readonly Dictionary<string, AuthoringProject> served = [];
    private readonly List<string> ending = [];
    private readonly List<WorkspaceChange> changes = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string app1 = null!;

    private string ServerDir => Path.Combine(root, "server");

    private sealed class FakeApps(Dictionary<string, AuthoringProject> served) : IAuthoringProjects
    {
        public AuthoringProject? Find(string id) => served.GetValueOrDefault(id);
        public GitCredential? CredentialFor(AuthoringProject project) => null;
    }

    private static readonly Signature Who = new("Ada", "ada@example.com", DateTimeOffset.UtcNow);

    private static void Commit(string folder, string message)
    {
        using var repo = new Repository(folder);
        Commands.Stage(repo, "*");
        repo.Commit(message, Who, Who);
    }

    /// <summary>An app project: a manifest, a view spec and a page, committed.</summary>
    private static string MakeAppProject(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "views"));
        File.WriteAllText(Path.Combine(folder, "app.yaml"), "schemaVersion: 1\nname: orders\n");
        File.WriteAllText(Path.Combine(folder, "views", "orders.yaml"), "schemaVersion: 1\nkind: grid\n");
        Repository.Init(folder);
        Commit(folder, "first");
        return folder;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(ServerDir);
        app1 = MakeAppProject(Path.Combine(root, "orders-app"));
        served["orders"] = new AuthoringProject("orders", app1, null);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        var clients = new PairedClientsFile(Path.Combine(ServerDir, "server.yaml"));
        clients.Upsert(new PairedClient("console-1", "Console", key.PublicKey, ServerRoles.Close(ServerRole.Approver | ServerRole.Author)));
        app.UseManagementAuthentication(new ConsoleTokenValidator(InstanceId, clients, new MemoryReplayCache()));
        WorkspaceApi.Map(app, new FakeApps(served), new Workspaces(ServerDir, TimeProvider.System), new WorkspaceApiOptions("dbdataworkspace.local", ending.Add, changes.Add));
        await app.StartAsync();
        http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch (IOException) { }
    }

    private async Task<HttpResponseMessage> Send(string who, ServerRole role, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, new ConsoleTokenIssuer("console-1", key).Issue(new ConsoleTokenRequest(InstanceId, who, who, role)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> AsAda(HttpMethod method, string path, string? body = null) => Send("ada", ServerRole.Author, method, path, body);

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    private async Task<string> Draft(string name = "draft", string project = "orders") =>
        (string)(await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/workspaces", $$"""{"name":"{{name}}"}""")))["id"]!;

    private string FolderOf(string id, string project = "orders") => Path.Combine(ServerDir, "workspaces", project, id);

    [Fact]
    public async Task A_workspace_is_made_listed_and_abandoned_by_its_owner()
    {
        var made = await AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"new-orders"}""");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var id = (string)(await Json(made))["id"]!;
        Assert.Equal("ada-new-orders", id);
        Assert.True(File.Exists(Path.Combine(FolderOf(id), "app.yaml")));
        Assert.Equal("ws/ada-new-orders", (string)(await Json(made))["branch"]!);

        var listed = (await Json(await AsAda(HttpMethod.Get, "/api/v1/projects/orders/workspaces")))["workspaces"]!.AsArray();
        Assert.Equal([id], listed.Select(w => (string)w!["id"]!));
        Assert.True((bool)listed[0]!["mine"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await AsAda(HttpMethod.Delete, $"/api/v1/projects/orders/workspaces/{id}")).StatusCode);
        Assert.False(Directory.Exists(FolderOf(id)));
        Assert.Equal([FolderOf(id)], ending);           // the host was told before the folder went
        Assert.Empty((await Json(await AsAda(HttpMethod.Get, "/api/v1/projects/orders/workspaces")))["workspaces"]!.AsArray());
        using var repo = new Repository(app1);
        Assert.Null(repo.Branches["ws/ada-new-orders"]);
    }

    [Fact]
    public async Task A_name_that_is_not_valid_or_is_taken_is_refused_and_so_is_a_viewer()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"Has Spaces"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("vic", ServerRole.Viewer, HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"mine"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("vic", ServerRole.Viewer, HttpMethod.Get, "/api/v1/projects/orders/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Get, "/api/v1/projects/nope/workspaces")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_abandons_commits_or_pushes_a_workspace()
    {
        var id = await Draft();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Delete, $"/api/v1/projects/orders/workspaces/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"x"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Delete, "/api/v1/projects/orders/workspaces/nope")).StatusCode);
        Assert.True(Directory.Exists(FolderOf(id)));
    }

    [Fact]
    public async Task A_draft_does_not_change_what_the_projects_own_checkout_holds()
    {
        var id = await Draft();
        var original = File.ReadAllText(Path.Combine(app1, "views", "orders.yaml"));

        File.AppendAllText(Path.Combine(FolderOf(id), "views", "orders.yaml"), "title: Orders\n");
        Commit(FolderOf(id), "a draft");

        Assert.Equal(original, File.ReadAllText(Path.Combine(app1, "views", "orders.yaml")));
        using var main = new Repository(app1);
        Assert.Equal("first", main.Head.Tip.Message.Trim());
        Assert.Equal("a draft", main.Branches["ws/ada-draft"].Tip.Message.Trim());
    }

    [Fact]
    public async Task A_project_that_is_not_a_repository_has_no_workspaces()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "app.yaml"), "schemaVersion: 1\n");
        served["plain"] = new AuthoringProject("plain", plain, null);

        var refused = await AsAda(HttpMethod.Post, "/api/v1/projects/plain/workspaces", """{"name":"draft"}""");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not a git repository", (string)(await Json(refused))["error"]!);
    }

    [Fact]
    public async Task Changes_are_shown_committed_in_the_callers_name_and_listed_in_the_history()
    {
        var id = await Draft();
        File.AppendAllText(Path.Combine(FolderOf(id), "views", "orders.yaml"), "title: Orders\n");

        var shown = await Json(await Send("vic", ServerRole.Viewer, HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/changes"));
        var changes = shown["changes"]!.AsArray();
        Assert.True((bool)shown["uncommitted"]!);
        Assert.Single(changes);
        Assert.Contains("+title: Orders", (string)changes[0]!["patch"]!);
        Assert.True((bool)changes[0]!["sides"]!);
        Assert.Contains("title: Orders", (string)changes[0]!["after"]!);
        Assert.DoesNotContain("title: Orders", (string)changes[0]!["before"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send("vic", ServerRole.Viewer, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"x"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", "{}")).StatusCode);
        var committed = await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"Title the grid"}"""));
        Assert.True((bool)committed["committed"]!);
        Assert.False((bool)(await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"again"}""")))["committed"]!);
        var after = await Json(await Send("vic", ServerRole.Viewer, HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/changes"));
        Assert.False((bool)after["uncommitted"]!);
        Assert.Single(after["changes"]!.AsArray());

        var commits = (await Json(await AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/history")))["commits"]!.AsArray();
        Assert.Equal(("Title the grid", "ada"), ((string)commits.Single()!["message"]!, (string)commits[0]!["author"]!));
        using var repo = new Repository(FolderOf(id));
        Assert.Equal("ada@dbdataworkspace.local", repo.Head.Tip.Author.Email);
    }

    [Fact]
    public async Task Merging_inside_the_server_needs_an_approver_updates_the_checkout_and_ends_the_workspace()
    {
        var id = await Draft();
        File.AppendAllText(Path.Combine(FolderOf(id), "views", "orders.yaml"), "title: Merged\n");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"Merge me"}""");

        Assert.Equal(HttpStatusCode.Forbidden, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/merge")).StatusCode);
        var merged = await Json(await Send("ann", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/merge"));

        Assert.True((bool)merged["merged"]!);
        Assert.Contains("title: Merged", File.ReadAllText(Path.Combine(app1, "views", "orders.yaml")));
        Assert.False(Directory.Exists(FolderOf(id)));
        Assert.Equal([FolderOf(id)], ending);
        using var repo = new Repository(app1);
        Assert.Null(repo.Branches["ws/" + id]);
        Assert.Equal("Merge me", repo.Head.Tip.Message.Trim());
    }

    [Fact]
    public async Task A_conflicting_merge_is_refused_with_the_files_and_the_workspace_stays()
    {
        var id = await Draft();
        File.WriteAllText(Path.Combine(FolderOf(id), "views", "orders.yaml"), "kind: form\n");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"Draft"}""");
        File.WriteAllText(Path.Combine(app1, "views", "orders.yaml"), "kind: detail\n");
        Commit(app1, "Main");

        var refused = await Send("ann", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/merge");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(["views/orders.yaml"], (await Json(refused))["conflicts"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("kind: detail\n", File.ReadAllText(Path.Combine(app1, "views", "orders.yaml")).ReplaceLineEndings("\n"));
        Assert.True(Directory.Exists(FolderOf(id)));
    }

    [Fact]
    public async Task A_project_with_a_remote_is_pushed_to_and_never_merged_inside_the_server()
    {
        var bare = Path.Combine(root, "remote.git");
        Repository.Clone(app1, bare, new CloneOptions { IsBare = true });
        var far = Path.Combine(root, "far-app");
        Repository.Clone(bare, far);
        served["far"] = new AuthoringProject("far", far, bare);
        var id = await Draft("draft", "far");
        File.AppendAllText(Path.Combine(FolderOf(id, "far"), "views", "orders.yaml"), "title: Pushed\n");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/far/workspaces/{id}/commit", """{"message":"Push me"}""");

        Assert.Equal(HttpStatusCode.Conflict, (await Send("ann", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/far/workspaces/{id}/merge")).StatusCode);
        Assert.True((bool)(await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/far/workspaces/{id}/push")))["pushed"]!);

        using var remote = new Repository(bare);
        Assert.Equal("Push me", remote.Branches["ws/" + id].Tip.Message.Trim());
    }

    [Fact]
    public async Task A_project_without_a_remote_has_nothing_to_push_to()
    {
        var id = await Draft();

        Assert.Equal(HttpStatusCode.Conflict, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/push")).StatusCode);
    }

    [Fact]
    public async Task A_token_scoped_to_another_project_is_refused()
    {
        served["other"] = new AuthoringProject("other", MakeAppProject(Path.Combine(root, "other-app")), null);
        var id = await Draft();
        Assert.Equal(HttpStatusCode.OK, (await AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Get, $"/api/v1/projects/other/workspaces/{id}/history")).StatusCode);   // a workspace belongs to its project
    }

    [Fact]
    public async Task A_host_that_keeps_a_history_is_told_what_happened_and_by_whom()
    {
        var id = await Draft();
        File.AppendAllText(Path.Combine(FolderOf(id), "views", "orders.yaml"), "title: Told\n");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"Say it"}""");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"nothing to commit"}""");     // takes nothing: no event
        await Send("ann", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/merge");
        var second = await Draft("again");
        await AsAda(HttpMethod.Delete, $"/api/v1/projects/orders/workspaces/{second}");

        Assert.Equal([("created", "ada", id, "ws/" + id), ("committed", "ada", id, "Say it"), ("merged", "ann", id, null), ("created", "ada", second, "ws/" + second), ("abandoned", "ada", second, "ws/" + second)],
            changes.Select(c => (c.Kind, c.ActorId, c.Workspace.Id, c.Kind == "merged" ? null : c.Detail)));
        Assert.NotNull(changes[2].Detail);      // the commit a merge made
    }
}
