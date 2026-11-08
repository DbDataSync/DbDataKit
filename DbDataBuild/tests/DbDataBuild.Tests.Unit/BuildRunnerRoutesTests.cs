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
/// Plan 33, UE05 and UE06: the runners a Build server is paired with (<c>GET /api/v1/runners</c>) and the move of a plan's schedule as one call (<c>PUT .../schedule/{connection}/{plan}/owner</c>: observe, cutover, rollback), with the roles,
/// the refusals in the words of the code that makes them and the audit line. The runner is a fake that records what it was told.
/// </summary>
public sealed class BuildRunnerRoutesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-owner-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly string serverDir;
    private readonly string clientDir;
    private readonly List<string> log = [];
    private readonly FakeSchedule runner = new();

    private sealed class FakeSchedule : IRunnerSchedule
    {
        public List<string> Calls { get; } = [];
        public string? ActiveVersion { get; set; } = "3";
        public bool Fail { get; set; }

        public Task<ClientTaskSchedule> ScheduleOfAsync(string package, string task, CancellationToken ct) =>
            Task.FromResult(new ClientTaskSchedule(task, "old", null, null, null, null, false, null, null, 0, 0, ActiveVersion));

        public Task SetOwnerAsync(string package, string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct)
        {
            if (Fail) throw new RunnerClientException("The runner refused to set the owner of x (403): This needs the operator role.");
            Calls.Add($"{owner} {reason} {(seed is null ? "no seed" : "seed")}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClientDecision>> DecisionsAsync(string package, string task, DateTimeOffset? since, CancellationToken ct) => Task.FromResult<IReadOnlyList<ClientDecision>>([]);
    }

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
        var app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), new NoRunner(), projects, new TuiCommand.CliHost(_ => null).Commands, log.Add, runnerSchedule: _ => runner, runnerClientDirectory: clientDir);
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

    private const string Owner = "/api/v1/projects/sales/schedule/sqlserver/hourly/owner";

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

    [Fact]
    public async Task Observe_then_rollback_move_the_owner_on_both_sides_and_are_audited_and_a_viewer_is_refused()
    {
        var s = await StartAsync();
        try
        {
            var body = """{"owner": "observe", "reason": "trial", "runner": "runner-x", "channel": "prod"}""";
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(HttpMethod.Put, Owner, ServerRole.Viewer, body)).StatusCode);
            Assert.Empty(runner.Calls);
            Assert.Equal("old", BuildScheduleSettings.Read(serverDir).OwnerOf("sales", "sqlserver", "hourly"));

            var observed = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, body);
            Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
            var answer = await Body(observed);
            Assert.Equal("observe", (string)answer["owner"]!);
            Assert.Equal("runner-x", (string)answer["runner"]!);
            Assert.Equal("observe", BuildScheduleSettings.Read(serverDir).OwnerOf("sales", "sqlserver", "hourly"));
            Assert.Equal("runner-x", BuildScheduleSettings.Read(serverDir).Runner!.Server);

            // the runner this server wrote down is used when the body names none
            var back = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, """{"owner": "old", "reason": "enough"}""");
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
            Assert.Equal("old", BuildScheduleSettings.Read(serverDir).OwnerOf("sales", "sqlserver", "hourly"));
            // a plan this scheduler never ran has no position to copy (a first sight), so no seed goes with the first move
            Assert.Equal(["observe trial no seed", "old enough no seed"], runner.Calls);

            var audit = File.ReadAllText(Path.Combine(serverDir, "management-audit.log"));
            Assert.Contains("schedule.observe", audit);
            Assert.Contains("schedule.rollback", audit);
            Assert.Contains("sales/sqlserver/hourly", audit);
            Assert.Contains("Ada", audit);
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task A_cutover_hands_the_plan_over_and_a_second_one_is_refused_in_the_words_of_the_move()
    {
        var s = await StartAsync();
        try
        {
            var cut = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, """{"owner": "runner", "reason": "go", "runner": "runner-x", "channel": "prod", "wait_seconds": 10}""");
            Assert.Equal(HttpStatusCode.OK, cut.StatusCode);
            var answer = await Body(cut);
            Assert.Equal("runner", (string)answer["owner"]!);
            Assert.NotNull(answer["handled_up_to"]);
            Assert.Equal("runner", BuildScheduleSettings.Read(serverDir).OwnerOf("sales", "sqlserver", "hourly"));

            var again = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, """{"owner": "runner"}""");
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("scheduled by the runner already", (string)(await Body(again))["error"]!);
            Assert.Contains("schedule.cutover", File.ReadAllText(Path.Combine(serverDir, "management-audit.log")));
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task Bad_input_a_package_the_runner_does_not_serve_and_a_runner_that_refuses_change_nothing_here()
    {
        var s = await StartAsync();
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, """{"owner": "sideways"}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, "not json")).StatusCode);
            var nowhere = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, """{"owner": "observe"}""");
            Assert.Equal(HttpStatusCode.BadRequest, nowhere.StatusCode);
            Assert.Contains("Say where the runner is", (string)(await Body(nowhere))["error"]!);
            Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Put, "/api/v1/projects/nope/schedule/a/b/owner", ServerRole.Operator, """{"owner": "observe"}""")).StatusCode);

            var link = """, "runner": "runner-x", "channel": "prod"}""";
            runner.ActiveVersion = null;
            var notServed = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, "{\"owner\": \"observe\"" + link);
            Assert.Equal(HttpStatusCode.Conflict, notServed.StatusCode);
            Assert.Contains("no active version", (string)(await Body(notServed))["error"]!);

            runner.ActiveVersion = "3";
            runner.Fail = true;
            var refused = await s.Send(HttpMethod.Put, Owner, ServerRole.Operator, "{\"owner\": \"observe\"" + link);
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.Contains("operator role", (string)(await Body(refused))["error"]!);
            Assert.Equal("old", BuildScheduleSettings.Read(serverDir).OwnerOf("sales", "sqlserver", "hourly"));
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }
}
