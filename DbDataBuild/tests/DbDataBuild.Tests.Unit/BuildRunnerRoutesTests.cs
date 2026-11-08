using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Runner.Client;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 33, UE05: the runners a Build server is paired with (<c>GET /api/v1/runners</c>) and publishing to one through the command route (the author role).
/// </summary>
public sealed class BuildRunnerRoutesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-owner-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly string serverDir;
    private readonly string clientDir;
    private readonly List<string> log = [];

    public BuildRunnerRoutesTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        serverDir = Path.Combine(root, "server");
        clientDir = Path.Combine(root, "client");
        Directory.CreateDirectory(serverDir);
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private sealed record Session(WebApplication App, Func<HttpMethod, string, ServerRole, string?, Task<HttpResponseMessage>> Send);

    private async Task<Session> StartAsync()
    {
        var projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject("sales", project, null, null));
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), new NoRunner(), projects, new TuiCommand.CliHost(_ => null).Commands, log.Add, runnerClientDirectory: clientDir);
        await app.StartAsync();
        var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator | ServerRole.Author));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, ServerRole role, string? body)
        {
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
            return await http.SendAsync(request);
        }

        return new Session(app, Send);
    }

    private sealed class NoRunner : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, "{}", ""));
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task The_runner_list_names_the_pairings_and_what_each_allows_and_none_is_an_empty_list()
    {
        var s = await StartAsync();
        try
        {
            Assert.Empty((await Body(await s.Send(HttpMethod.Get, "/api/v1/runners", ServerRole.Viewer, null)))["runners"]!.AsArray());

            new RunnerClientStore(clientDir).Add(new PairedRunner("runner-x", "https://runner.example:7443", "inst-1", "abc", ["viewer", "operator", "publisher"], DateTimeOffset.UtcNow));
            var runners = (await Body(await s.Send(HttpMethod.Get, "/api/v1/runners", ServerRole.Viewer, null)))["runners"]!.AsArray();
            var one = Assert.Single(runners)!;
            Assert.Equal("runner-x", (string)one["name"]!);
            Assert.Equal("https://runner.example:7443", (string)one["url"]!);
            Assert.Contains("publisher", one["roles"]!.AsArray().Select(r => (string)r!));
            Assert.DoesNotContain("abc", one.ToJsonString());    // the pinned fingerprint is the pairing's own, not shown
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task Publishing_to_a_runner_is_offered_through_the_command_route_to_the_author_role_and_not_to_a_viewer()
    {
        var s = await StartAsync();
        try
        {
            var offered = async (ServerRole role) => { var r = await s.Send(HttpMethod.Get, "/api/v1/projects/sales/commands", role, null); Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}"); return (await Body(r))["commands"]!.AsArray().Single(c => (string)c!["tool"]! == "project_publish")!; };
            var viewer = await offered(ServerRole.Viewer);
            Assert.Equal("author", (string)viewer["role"]!);
            Assert.False((bool)viewer["allowed"]!);
            Assert.True((bool)(await offered(ServerRole.Author))["allowed"]!);
            var refused = await s.Send(HttpMethod.Post, "/api/v1/projects/sales/commands/project_publish", ServerRole.Viewer, """{"server": "runner-x", "channel": "prod"}""");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task A_broken_pairing_file_is_a_409_naming_it()
    {
        Directory.CreateDirectory(clientDir);
        File.WriteAllText(Path.Combine(clientDir, "publish.json"), "{ not a list");
        var s = await StartAsync();
        try
        {
            var answer = await s.Send(HttpMethod.Get, "/api/v1/runners", ServerRole.Viewer, null);
            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
            Assert.Contains("publish.json", (string)(await Body(answer))["error"]!);
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }
}
