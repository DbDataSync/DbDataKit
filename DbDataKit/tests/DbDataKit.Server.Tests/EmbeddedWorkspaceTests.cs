using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The Workspace hosted in the console (plan 33 SC05): a real console with <c>workspace.enabled</c>, no Workspace server paired. A person signed in to the console makes a project, a draft, edits and commits in it through the same calls the
/// Workspaces screens make for a paired server (at <c>/relay/console-workspace/...</c>), with the roles the console gives them; only the calls of the screens' table exist; and what is done is in the console's audit with the module named.
/// The builds and the publish are the engine's own tests (<c>DbDataWorkspace.Tests</c>: the same engine, the standalone host).
/// </summary>
public sealed class EmbeddedWorkspaceTests : IAsyncLifetime
{
    private const string Root = "/relay/" + ConsoleWorkspaceSettings.ServerId + "/api/v1";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-ws-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = null!;
    private string viewer = null!;

    public async ValueTask InitializeAsync()
    {
        var settings = new ConsoleSettings(directory, new Uri("https://localhost:0")) { Workspace = new ConsoleWorkspaceSettings { Enabled = true, Directory = Path.Combine(directory, "ws") } };
        app = ConsoleHost.Build(settings, ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = Session(UserRole.Admin);
        viewer = Session(UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Session(UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string session, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
        return (response.StatusCode, json);
    }

    [Fact]
    public async Task A_person_makes_a_project_a_draft_edits_and_commits_with_no_workspace_server_and_it_is_audited_in_the_console()
    {
        var made = await Call(HttpMethod.Post, Root + "/projects", admin, """{"id":"orders","title":"Orders"}""");
        Assert.Equal(HttpStatusCode.Created, made.Status);
        var draft = await Call(HttpMethod.Post, Root + "/projects/orders/workspaces", admin, """{"name":"draft"}""");
        Assert.Equal(HttpStatusCode.Created, draft.Status);
        var id = (string)draft.Body!["id"]!;
        var files = $"{Root}/projects/orders/workspaces/{id}";

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, files + "/files/notes.md", admin, """{"content":"hello\n"}""")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, files + "/files/notes.md", viewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, files + "/commit", admin, """{"message":"Notes"}""")).Status);
        var history = await Call(HttpMethod.Get, files + "/history", viewer);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        Assert.Contains("Notes", history.Body!.ToJsonString());

        // the folder is the Workspace's own, outside the console's own folder of state and keys
        var workspaceFolder = app.Services.GetRequiredService<ConsoleWorkspace>().Directory;
        Assert.True(Directory.Exists(Path.Combine(workspaceFolder, "projects", "orders")));
        Assert.NotEqual(Path.GetFullPath(directory), Path.GetFullPath(workspaceFolder));
        Assert.False(workspaceFolder.StartsWith(Path.Combine(directory, "secrets"), StringComparison.Ordinal));

        // the console's audit has it, with the module named, and the person
        var audit = new WorkspaceAuditSink(app.Services.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId).Read();
        var entry = Assert.Single(audit, a => a.Command == "project.create");
        Assert.Equal("orders", entry.Target);
        Assert.Equal("ok", entry.Outcome);
        var record = new AuditLog(app.Services.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: "workspace.")).First();
        Assert.StartsWith("workspace.", record.Entry.Verb);
    }

    [Fact]
    public async Task A_viewer_reads_and_is_refused_what_needs_a_role_and_a_call_outside_the_screens_table_does_not_exist()
    {
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, Root + "/projects", admin, """{"id":"orders"}""")).Status);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Root + "/projects", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, Root + "/projects", viewer, """{"id":"other"}""")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, Root + "/projects/orders/workspaces", viewer, """{"name":"mine"}""")).Status);
        // a call the table does not name is refused whoever signed in, and the standalone server's own calls (pairing, state) are not here
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, Root + "/state", admin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, Root + "/projects/orders/workspaces/x/rebase", admin)).Status);
        // nobody signed in
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(Root + "/projects")).StatusCode);
        // the same address under the console's own prefix
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, ConsoleWorkspaceApi.Prefix + "/api/v1/projects", viewer)).Status);
    }

    [Fact]
    public async Task The_workspace_is_listed_as_part_of_the_console_and_cannot_be_forgotten()
    {
        var list = await Call(HttpMethod.Get, "/api/instances", admin);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var entry = Assert.Single(list.Body!.AsArray(), i => (string)i!["id"]! == ConsoleWorkspaceSettings.ServerId)!;
        Assert.Equal("dbdataworkspace", (string)entry["product"]!);
        Assert.True((bool)entry["embedded"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Delete, "/api/instances/" + ConsoleWorkspaceSettings.ServerId, admin)).Status);
    }
}
