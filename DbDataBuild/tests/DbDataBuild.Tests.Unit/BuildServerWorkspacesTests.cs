using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>Workspaces (plan 13, W02): a git worktree per person and name, listed, abandoned by its owner, commands run in it only for the owner, and the project's own checkout, which the scheduler reads, never moves.</summary>
public sealed class BuildServerWorkspacesTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-workspaces-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly Recorder runner = new();
    private ProjectsFile projects = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private string project = null!;

    private string ServerDir => Path.Combine(root, "server");

    private sealed class Recorder : IProjectCommandRunner
    {
        public List<string> Roots { get; } = [];
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken)
        {
            Roots.Add(projectRoot);
            return Task.FromResult(new CommandResult(0, """{"ok":true}""", ""));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ServerDir);
        project = Path.Combine(root, "sales");
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        Repository.Init(project);
        using (var repo = new Repository(project))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            repo.Commit("first", who, who);
        }
        projects = new ProjectsFile(ServerDir);
        projects.Add(new HostedProject("sales", project, null, null));
        var settings = new BuildServerSettings(ServerDir, new Uri("https://127.0.0.1:0"));
        app = BuildServer.Build(settings, BuildServer.Certificate(ServerDir), runner, projects, new TuiCommand.CliHost(_ => null).Commands, _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRole.Author);
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
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
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, who, who, role)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private Task<HttpResponseMessage> AsAda(HttpMethod method, string path, string? body = null) => Send("ada", ServerRole.Author, method, path, body);

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_workspace_is_made_listed_and_abandoned_by_its_owner()
    {
        var made = await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"new-orders"}""");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var id = (string)(await Json(made))["id"]!;
        Assert.Equal("ada-new-orders", id);

        var folder = Path.Combine(ServerDir, "workspaces", "sales", id);
        Assert.True(File.Exists(Path.Combine(folder, "dbdatabuild.yml")));
        Assert.Equal("ws/ada-new-orders", (string)(await Json(made))["branch"]!);

        var listed = (await Json(await AsAda(HttpMethod.Get, "/api/v1/projects/sales/workspaces")))["workspaces"]!.AsArray();
        Assert.Equal([id], listed.Select(w => (string)w!["id"]!));
        Assert.True((bool)listed[0]!["mine"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await AsAda(HttpMethod.Delete, $"/api/v1/projects/sales/workspaces/{id}")).StatusCode);
        Assert.False(Directory.Exists(folder));
        Assert.Empty((await Json(await AsAda(HttpMethod.Get, "/api/v1/projects/sales/workspaces")))["workspaces"]!.AsArray());
        using var repo = new Repository(project);
        Assert.Null(repo.Branches["ws/ada-new-orders"]);
    }

    [Fact]
    public async Task A_name_that_is_not_valid_or_is_taken_is_refused_and_so_is_a_viewer()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"Has Spaces"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"draft"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"draft"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("vic", ServerRole.Viewer, HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"mine"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("vic", ServerRole.Viewer, HttpMethod.Get, "/api/v1/projects/sales/workspaces")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_runs_commands_in_or_abandons_a_workspace()
    {
        var id = (string)(await Json(await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"draft"}""")))["id"]!;

        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Post, $"/api/v1/projects/sales/commands/project_show_graph?workspace={id}", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Delete, $"/api/v1/projects/sales/workspaces/{id}")).StatusCode);
        Assert.Empty(runner.Roots);

        Assert.Equal(HttpStatusCode.OK, (await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/commands/project_show_graph?workspace={id}", "{}")).StatusCode);
        Assert.Equal(Path.Combine(ServerDir, "workspaces", "sales", id), runner.Roots[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Post, "/api/v1/projects/sales/commands/project_show_graph?workspace=nope", "{}")).StatusCode);
    }

    [Fact]
    public async Task A_draft_does_not_change_what_the_projects_own_checkout_holds()
    {
        var id = (string)(await Json(await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"draft"}""")))["id"]!;
        var folder = Path.Combine(ServerDir, "workspaces", "sales", id);
        var model = Directory.EnumerateFiles(Path.Combine(folder, "models"), "*.sql", SearchOption.AllDirectories).First();
        var relative = Path.GetRelativePath(folder, model);
        var original = File.ReadAllText(Path.Combine(project, relative));

        File.AppendAllText(model, "\n-- a draft\n");
        using (var draft = new Repository(folder))
        {
            Commands.Stage(draft, "*");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            draft.Commit("a draft", who, who);
        }

        Assert.Equal(original, File.ReadAllText(Path.Combine(project, relative)));
        using var main = new Repository(project);
        Assert.Equal("first", main.Head.Tip.Message.Trim());
        Assert.Equal("a draft", main.Branches["ws/ada-draft"].Tip.Message.Trim());
        Assert.Equal(project, projects.RootOf(projects.Find("sales")!));
    }

    [Fact]
    public async Task A_project_that_is_not_a_repository_has_no_workspaces()
    {
        var plain = Path.Combine(root, "plain");
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", plain, "--format", "json"], new StringWriter(), new StringWriter()));
        projects.Add(new HostedProject("plain", plain, null, null));
        var refused = await AsAda(HttpMethod.Post, "/api/v1/projects/plain/workspaces", """{"name":"draft"}""");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not a git repository", (string)(await Json(refused))["error"]!);
    }
}
