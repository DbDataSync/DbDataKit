using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>The DbDataWorkspace server (plan 22a, GP31): a real HTTPS server in a directory of its own, paired the way a console pairs, reporting who it is and that it is well; its command line is the suite's grammar.</summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceServerTests : IAsyncLifetime
{
    private WorkspaceHarness server = null!;

    public async ValueTask InitializeAsync() => server = await WorkspaceHarness.StartAsync();

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    [Fact]
    public async Task A_console_pairs_with_the_server_and_is_told_who_it_is()
    {
        var (response, info) = await server.Send("ada", ServerRole.Viewer, HttpMethod.Get, "/api/v1/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("dbdataworkspace", (string)info!["product"]!);
        Assert.Equal(WorkspaceServer.Version, (string)info["version"]!);
        Assert.Equal(server.Paired.InstanceId, (string)info["instance_id"]!);
        Assert.StartsWith("workspace-", server.Paired.InstanceId);
    }

    [Fact]
    public void The_instance_id_is_the_same_after_a_restart_because_it_is_kept_in_the_state_database()
    {
        var first = WorkspaceServer.InstanceId(server.Directory);
        Assert.Equal(first, WorkspaceServer.InstanceId(server.Directory));
        Assert.Equal(server.Paired.InstanceId, first);
        Assert.True(File.Exists(Path.Combine(server.Directory, WorkspaceState.FileName)));
    }

    [Fact]
    public async Task Health_and_version_answer_without_a_token_and_everything_else_needs_one()
    {
        async Task<HttpStatusCode> Get(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
            return (await server.Http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Get("/health/live", null));
        using (var version = await server.Http.GetAsync("/version")) Assert.Equal("dbdataworkspace", (string)JsonNode.Parse(await version.Content.ReadAsStringAsync())!["product"]!);
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/api/v1/projects", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/api/v1/projects", "garbage"));
        Assert.NotEmpty(server.Log);
        // a token for another server is no use here
        var elsewhere = server.Console.Issuer().Issue(new ConsoleTokenRequest("someone-else", "ada", "Ada", ServerRole.Viewer));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/api/v1/projects", elsewhere));
    }

    [Fact]
    public async Task A_token_with_more_roles_than_the_pairing_allowed_is_refused()
    {
        await using var narrow = await WorkspaceHarness.StartAsync(ceiling: ServerRole.Viewer);

        var (response, _) = await narrow.Send("ada", ServerRole.Admin, HttpMethod.Get, "/api/v1/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void A_server_listens_on_https_only()
    {
        var plain = new WorkspaceSettings(server.Directory, new Uri("http://127.0.0.1:0"));
        var refused = Assert.Throws<ArgumentException>(() => WorkspaceServer.Build(plain, WorkspaceServer.Certificate(server.Directory), _ => { }));
        Assert.Contains("HTTPS", refused.Message);
    }

    private string Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = WorkspaceCli.RunAsync([.. args, "--repo", server.Directory], new CliContext(output, error, TextReader.Null)).GetAwaiter().GetResult();
        Assert.True(exit == 0, error.ToString());
        return output.ToString();
    }

    [Fact]
    public async Task The_command_line_pairs_a_client_and_lists_it_and_unpairs_it()
    {
        var made = Run("server", "pair", "--max-role", "viewer");
        var code = System.Text.RegularExpressions.Regex.Match(made, @"Pairing code: (\S+)").Groups[1].Value;
        Assert.NotEmpty(code);

        var other = new ConsoleIdentity("console-2", ConsoleSigningKey.Generate());
        await PairingClient.PairAsync(server.Http.BaseAddress!, code, other, ClientKind.Console, "from the command line");
        Assert.Contains("console-2", Run("server", "clients"));
        Run("server", "unpair", other.Id);
        Assert.DoesNotContain("console-2", Run("server", "clients"));
    }

    [Fact]
    public async Task The_command_line_has_the_shared_nouns_and_the_workspace_ones()
    {
        var help = new StringWriter();
        await WorkspaceCli.Router().RunAsync(["help"], new CliContext(help, new StringWriter(), TextReader.Null));
        foreach (var noun in new[] { "server", "project", "workspace", "state", "service", "health" }) Assert.Contains(noun, help.ToString());

        // a health check of a server that is not there is exit 1, and its JSON document is the shared one
        var document = new StringWriter();
        var exit = await WorkspaceCli.Router().RunAsync(["health", "check", "--url", "https://127.0.0.1:1/health/live", "--format", "json"], new CliContext(document, new StringWriter(), TextReader.Null));
        Assert.Equal(1, exit);
        Assert.Equal("dbdataworkspace.output/1", (string)JsonNode.Parse(document.ToString())!["schema"]!);
    }

    [Fact]
    public void The_command_line_makes_adds_and_lists_projects_and_lists_workspaces()
    {
        Assert.Contains("hosts no project", Run("project", "list"));
        Assert.Contains("Made the app project orders", Run("project", "create", "orders", "--title", "Orders"));
        Assert.True(File.Exists(Path.Combine(server.Directory, "projects", "orders", "README.md")));
        Assert.True(Directory.Exists(Path.Combine(server.Directory, "projects", "orders", ".git")));

        var existing = Path.Combine(server.Directory, "elsewhere");
        LibGit2Sharp.Repository.Init(existing);
        Assert.Contains($"Hosting {existing} as the app project billing", Run("project", "add", "billing", "--path", existing));
        var listed = Run("project", "list");
        Assert.Contains("orders", listed);
        Assert.Contains("billing", listed);

        Assert.Contains("There is no workspace", Run("workspace", "list"));
    }

    [Fact]
    public void The_command_line_refuses_a_bad_name_a_taken_name_and_a_folder_that_is_no_repository()
    {
        Run("project", "create", "orders");
        string Fails(params string[] args)
        {
            var (output, error) = (new StringWriter(), new StringWriter());
            var exit = WorkspaceCli.RunAsync([.. args, "--repo", server.Directory], new CliContext(output, error, TextReader.Null)).GetAwaiter().GetResult();
            Assert.NotEqual(0, exit);
            return error.ToString();
        }

        Assert.Contains("not a project name", Fails("project", "create", "Has Spaces"));
        Assert.Contains("already hosts", Fails("project", "create", "orders"));
        Assert.Contains("not the folder of a git repository", Fails("project", "add", "plain", "--path", server.Directory));
        Assert.Contains("not a kind", Fails("project", "create", "x", "--kind", "agent"));
    }
}
