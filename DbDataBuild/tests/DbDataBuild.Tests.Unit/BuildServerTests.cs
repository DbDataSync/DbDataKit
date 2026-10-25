using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>The Build server (plan 13, R01): a real HTTPS server over a template project, paired the way a console pairs, answering the read-only commands with the documents the command line gives.</summary>
public sealed class BuildServerTests : IAsyncLifetime
{
    private readonly string project = Path.Combine(Path.GetTempPath(), "ddb-server-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string serverDir = Path.Combine(Path.GetTempPath(), "ddb-server-dir-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly List<string> log = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private string instanceId = null!;
    private ProjectsFile projects = null!;

    private sealed class Fake : IProjectCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken)
        {
            Calls.Add(argv);
            return Task.FromResult(new CommandResult(0, """{"command":"fake","ok":true}""", ""));
        }
    }

    public async Task InitializeAsync()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        await Start(new ChildProcessCommandRunner());
    }

    private async Task Start(IProjectCommandRunner runner, Func<string, IReadOnlyList<CommandInfo>, DbDataBuild.Cli.Mcp.ToolSurface>? surface = null, ServerRole ceiling = ServerRole.Viewer)
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); http.Dispose(); }
        Directory.CreateDirectory(serverDir);
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var certificate = BuildServer.Certificate(serverDir);
        projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject(Path.GetFileName(project), project, null, null));
        app = BuildServer.Build(settings, certificate, runner, projects, new TuiCommand.CliHost(_ => null).Commands, log.Add, surface);
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        instanceId = BuildServer.InstanceId(settings.ServerYaml);

        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ceiling));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var d in new[] { project, serverDir }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private string Token(ServerRole roles = ServerRole.Viewer) => console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", roles));

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static string CommandLine(params string[] args)
    {
        var o = new StringWriter();
        CliApp.Run([.. args, "--format", "json"], o, new StringWriter());
        return o.ToString();
    }

    [Fact]
    public async Task A_paired_console_gets_the_same_document_the_command_line_gives()
    {
        var graph = await Send(HttpMethod.Post, $"/api/v1/projects/{Path.GetFileName(project)}/commands/project_show_graph", "{}", Token());
        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        var reply = JsonNode.Parse(await graph.Content.ReadAsStringAsync())!;
        Assert.Equal(0, (int)reply["exit"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CommandLine("project", "show", "graph", "--project", project)), reply["document"]));

        var status = await Send(HttpMethod.Post, $"/api/v1/projects/{Path.GetFileName(project)}/commands/connection_status", "{}", Token());
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusReply = JsonNode.Parse(await status.Content.ReadAsStringAsync())!;
        Assert.Equal("connection status", (string)statusReply["document"]!["command"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CommandLine("connection", "status", "--project", project)), statusReply["document"]));
    }

    [Fact]
    public async Task Without_a_good_token_nothing_is_answered()
    {
        var id = Path.GetFileName(project);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/v1/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/project_show_graph", "{}", "garbage")).StatusCode);
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task The_server_says_what_it_offers_and_what_the_caller_may_run()
    {
        var id = Path.GetFileName(project);
        var projects = JsonNode.Parse(await (await Send(HttpMethod.Get, "/api/v1/projects", token: Token())).Content.ReadAsStringAsync())!;
        Assert.Equal(id, (string)projects["projects"]![0]!["id"]!);

        var commands = JsonNode.Parse(await (await Send(HttpMethod.Get, $"/api/v1/projects/{id}/commands", token: Token())).Content.ReadAsStringAsync())!["commands"]!.AsArray();
        var names = commands.Select(c => (string)c!["name"]!).ToList();
        Assert.Contains("connection status", names);
        Assert.Contains("project show graph", names);
        Assert.DoesNotContain("connection refresh", names);
        Assert.DoesNotContain("project seed", names);
        Assert.All(commands, c => Assert.Equal("viewer", (string)c!["role"]!));

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/v1/projects/other/commands", token: Token())).StatusCode);
    }

    [Fact]
    public async Task What_is_not_offered_or_not_valid_is_refused_before_anything_runs()
    {
        var fake = new Fake();
        await Start(fake);
        var id = Path.GetFileName(project);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/connection_refresh", "{}", Token())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/projects/other/commands/project_show_graph", "{}", Token())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/project_show_graph", """{"bogus":1}""", Token())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/project_show_graph", "[1]", Token())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/connection_compare", """{"show_values":true}""", Token())).StatusCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_command_needing_more_than_the_caller_has_is_refused_with_the_role_it_needs()
    {
        // A server that offers a command which writes target data (refresh): the effect class sets the role it needs, and the console's ceiling limits the token.
        var fake = new Fake();
        await Start(fake, (root, all) => new DbDataBuild.Cli.Mcp.ToolSurface(root, all.Where(c => c.Name == "connection refresh")), ServerRole.Operator);
        var id = Path.GetFileName(project);

        var viewer = await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/connection_refresh", "{}", Token(ServerRole.Viewer));
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Contains("operator", await viewer.Content.ReadAsStringAsync());
        Assert.Empty(fake.Calls);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/connection_refresh", "{}", Token(ServerRole.Operator))).StatusCode);
        Assert.Single(fake.Calls);
        Assert.Equal(["connection", "refresh"], fake.Calls[0].Take(2));
        Assert.Contains("--project", fake.Calls[0]);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_stopped_and_says_so()
    {
        var result = await new ChildProcessCommandRunner(TimeSpan.FromMilliseconds(1)).RunAsync(["help", "matrix"], project, CancellationToken.None);

        Assert.Equal(ChildProcessCommandRunner.TimedOut, result.Exit);
        Assert.Contains("longer than the server allows", result.Err);
    }

    [Fact]
    public async Task The_server_verbs_pair_list_and_unpair_and_refuse_a_wrong_start()
    {
        var dir = Path.Combine(serverDir, "cli");
        var o = new StringWriter();
        Assert.Equal(0, await ServerCli.RunAsync(["server", "pair", "--max-role", "viewer", "--repo", dir], o, new StringWriter(), TextReader.Null));
        Assert.Contains("Pairing code:", o.ToString());

        var err = new StringWriter();
        Assert.Equal(2, await ServerCli.RunAsync(["server", "run", "--repo", dir, "--listen", "http://127.0.0.1:1"], new StringWriter(), err, TextReader.Null));
        Assert.Equal(2, await ServerCli.RunAsync(["server", "run", "--project", project, "--repo", dir], new StringWriter(), err, TextReader.Null));
        Assert.Contains("server project add", err.ToString());

        var clients = new StringWriter();
        Assert.Equal(0, await ServerCli.RunAsync(["server", "clients", "--repo", dir], clients, new StringWriter(), TextReader.Null));
        Assert.Contains("No client is paired", clients.ToString());
    }

    [Fact]
    public async Task Health_and_version_answer_without_a_token_and_name_the_product()
    {
        var health = await Send(HttpMethod.Get, "/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var version = JsonNode.Parse(await (await Send(HttpMethod.Get, "/version")).Content.ReadAsStringAsync())!;
        Assert.Equal("dbdatabuild", (string)version["product"]!);
        Assert.Equal(DbDataBuild.Core.ProductInfo.Version, (string)version["version"]!);
    }
}
