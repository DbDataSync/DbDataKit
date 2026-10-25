using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>A Build server hosting several projects (plan 13, R02, D19): folders and remote repositories, added and removed while it runs, each command run in its own project.</summary>
public sealed class BuildServerProjectsTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-projects-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly Recorder runner = new();
    private ProjectsFile projects = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;

    private string ServerDir => Path.Combine(root, "server");

    private sealed class Recorder : IProjectCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken)
        {
            Calls.Add([.. argv, "in:" + projectRoot]);
            return Task.FromResult(new CommandResult(0, """{"ok":true}""", ""));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ServerDir);
        projects = new ProjectsFile(ServerDir);
        var settings = new BuildServerSettings(ServerDir, new Uri("https://127.0.0.1:0"));
        app = BuildServer.Build(settings, BuildServer.Certificate(ServerDir), runner, projects, new TuiCommand.CliHost(_ => null).Commands, _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRole.Viewer);
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

    private string NewProject(string name)
    {
        var dir = Path.Combine(root, name);
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        return dir;
    }

    /// <summary>A repository holding a starter project, as a remote the server can clone.</summary>
    private string RemoteWithProject(string name)
    {
        var dir = NewProject(name);
        Repository.Init(dir);
        using var repo = new Repository(dir);
        Commands.Stage(repo, "*");
        var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
        repo.Commit("first", who, who);
        return dir;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Two_projects_are_served_and_each_command_runs_in_its_own()
    {
        var (a, b) = (NewProject("alpha"), NewProject("beta"));
        projects.Add(new HostedProject("alpha", a, null, null));
        projects.Add(new HostedProject("beta", b, null, null));

        var listed = (await Json(await Send(HttpMethod.Get, "/api/v1/projects")))["projects"]!.AsArray();
        Assert.Equal(["alpha", "beta"], listed.Select(p => (string)p!["id"]!).Order());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/projects/alpha/commands/project_show_graph", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/projects/beta/commands/project_show_graph", "{}")).StatusCode);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(a, runner.Calls[0][runner.Calls[0].ToList().IndexOf("--project") + 1]);
        Assert.Equal("in:" + a, runner.Calls[0][^1]);
        Assert.Equal(b, runner.Calls[1][runner.Calls[1].ToList().IndexOf("--project") + 1]);
        Assert.Equal("in:" + b, runner.Calls[1][^1]);
    }

    [Fact]
    public async Task A_project_added_or_removed_while_the_server_runs_is_served_or_gone_at_once()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/v1/projects/late/commands")).StatusCode);

        projects.Add(new HostedProject("late", NewProject("late"), null, null));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/projects/late/commands")).StatusCode);

        Assert.True(projects.Remove("late"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/v1/projects/late/commands/project_show_graph", "{}")).StatusCode);
    }

    [Fact]
    public async Task A_remote_project_is_cloned_into_the_servers_directory_and_pulled_when_the_remote_moves()
    {
        var remote = RemoteWithProject("upstream");
        var project = new HostedProject("sales", null, remote, null);
        projects.Add(project);

        var cloned = projects.Pull(project)!;
        Assert.True(cloned.Cloned);
        Assert.Equal(Path.Combine(ServerDir, "projects", "sales"), projects.RootOf(project));
        Assert.True(File.Exists(Path.Combine(projects.RootOf(project), "dbdatabuild.yml")));

        var listed = (await Json(await Send(HttpMethod.Get, "/api/v1/projects")))["projects"]![0]!;
        Assert.Equal("git", (string)listed["source"]!);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/v1/projects/sales/commands/project_show_graph", "{}")).StatusCode);
        Assert.Equal("in:" + projects.RootOf(project), runner.Calls[0][^1]);

        File.WriteAllText(Path.Combine(remote, "NOTES.md"), "new on the remote");
        using (var repo = new Repository(remote))
        {
            Commands.Stage(repo, "NOTES.md");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            repo.Commit("second", who, who);
        }

        var pulled = projects.Pull(project)!;
        Assert.False(pulled.Cloned);
        Assert.Equal("new on the remote", File.ReadAllText(Path.Combine(projects.RootOf(project), "NOTES.md")));
    }

    [Fact]
    public async Task A_remote_project_that_was_never_cloned_says_how_to_get_it()
    {
        projects.Add(new HostedProject("sales", null, "https://example.invalid/sales.git", null));

        var response = await Send(HttpMethod.Post, "/api/v1/projects/sales/commands/project_show_graph", "{}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("server project pull sales", await response.Content.ReadAsStringAsync());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Entries_that_are_not_valid_are_skipped_and_named()
    {
        projects.Add(new HostedProject("good", NewProject("good"), null, null));
        File.AppendAllText(projects.Path, "  - {id: Bad Id, path: /x}\n  - {id: both, path: /x, git: http://y}\n  - {id: neither}\n  - {id: good, path: /again}\n");

        Assert.Equal(["good"], projects.All().Select(p => p.Id));
        Assert.Equal(4, projects.Problems.Count);
        Assert.Contains(projects.Problems, p => p.Contains("both") && p.Contains("only one"));
        Assert.Contains(projects.Problems, p => p.Contains("listed twice"));
    }

    [Fact]
    public void What_is_not_a_project_is_refused_with_the_reason()
    {
        Assert.Contains("not a project id", Assert.Throws<ArgumentException>(() => projects.Add(new HostedProject("Has Space", root, null, null))).Message);
        Assert.Contains("not both and not neither", Assert.Throws<ArgumentException>(() => projects.Add(new HostedProject("x", root, "http://y", null))).Message);
        Assert.Contains("no dbdatabuild.yml", Assert.Throws<ArgumentException>(() => projects.Add(new HostedProject("x", root, null, null))).Message);
    }

    private async Task<(int Exit, string Out, string Err)> Verb(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync([.. args, "--repo", ServerDir], o, e, TextReader.Null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task The_project_verbs_add_list_pull_and_remove()
    {
        var local = NewProject("local");
        var remote = RemoteWithProject("upstream");

        Assert.Equal(0, (await Verb("server", "project", "add", "local", "--path", local)).Exit);
        var added = await Verb("server", "project", "add", "sales", "--git", remote);
        Assert.Equal(0, added.Exit);
        Assert.Contains("Cloned 'sales'", added.Out);

        var list = await Verb("server", "project", "list");
        Assert.Contains("local", list.Out);
        Assert.Contains("sales", list.Out);
        Assert.Contains("git " + remote, list.Out);

        Assert.Contains("is at", (await Verb("server", "project", "pull", "sales")).Out);
        Assert.Contains("nothing to pull", (await Verb("server", "project", "pull", "local")).Out);

        Assert.Equal(0, (await Verb("server", "project", "remove", "local")).Exit);
        Assert.Equal(1, (await Verb("server", "project", "remove", "local")).Exit);
        Assert.DoesNotContain("local", (await Verb("server", "project", "list")).Out.Replace("sales", ""));
    }

    [Fact]
    public async Task A_project_that_cannot_be_added_leaves_nothing_behind()
    {
        Assert.Equal(2, (await Verb("server", "project", "add", "x")).Exit);
        Assert.Equal(2, (await Verb("server", "project", "add", "x", "--path", root, "--git", "http://y")).Exit);
        Assert.Equal(2, (await Verb("server", "project", "add", "x", "--path", Path.Combine(root, "nowhere"))).Exit);

        var bad = await Verb("server", "project", "add", "gone", "--git", Path.Combine(root, "no-such-repo"));
        Assert.Equal(1, bad.Exit);
        Assert.Empty(projects.All());
    }
}
