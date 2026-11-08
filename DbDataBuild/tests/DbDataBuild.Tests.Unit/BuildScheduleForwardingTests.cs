using System.Net;
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
/// Plan 28c E10: Build's pause, resume and run-now endpoints and its schedule list for a plan the runner owns (QE5). The runner is a fake that records what it was asked; the real runner's own endpoints are proved in the conformance
/// suite (<c>ProjectPublishOnRunnerTests</c>).
/// </summary>
public sealed class BuildScheduleForwardingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-fwd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly List<string> log = [];
    private readonly FakeRunner runner = new();
    private readonly string serverDir;

    private sealed class FakeRunner : IRunnerTaskControl
    {
        public List<string> Calls { get; } = [];
        public string? RunId { get; set; } = "run-7";
        public bool Fail { get; set; }
        public ClientTaskStatus? Status { get; set; }

        private void Check() { if (Fail) throw new RunnerClientException("The runner refused to pause x (403): This needs the operator role."); }
        public Task PauseAsync(string package, string task, CancellationToken ct) { Check(); Calls.Add($"pause {package} {task}"); return Task.CompletedTask; }
        public Task ResumeAsync(string package, string task, CancellationToken ct) { Check(); Calls.Add($"resume {package} {task}"); return Task.CompletedTask; }
        public Task<string?> RunNowAsync(string package, string task, CancellationToken ct) { Check(); Calls.Add($"run {package} {task}"); return Task.FromResult(RunId); }
        public Task<ClientTaskStatus?> StatusAsync(string package, string task, CancellationToken ct) { Check(); return Task.FromResult(Status); }
    }

    public BuildScheduleForwardingTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental, nightly: incremental }\n",
            "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n    nightly: { schedule: \"0 2 * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        serverDir = Path.Combine(root, "server");
        Directory.CreateDirectory(serverDir);
        // hourly is the runner's, nightly is still this server's
        BuildScheduleSettings.Write(serverDir, "sales", "sqlserver", "hourly", "runner", new ScheduleRunnerLink("runner-x", "prod", null));
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private sealed record Session(WebApplication App, Func<HttpMethod, string, ServerRole, Task<HttpResponseMessage>> Send, BuildScheduler? Scheduler);

    private async Task<Session> StartAsync(bool withScheduler)
    {
        var projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject("sales", project, null, null));
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var scheduler = withScheduler
            ? new BuildScheduler(projects, new NoRunner(), BuildSchedulerSchema.Open(serverDir), TimeProvider.System, log.Add, schedule: () => BuildScheduleSettings.Read(serverDir))
            : null;
        var app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), new NoRunner(), projects, new TuiCommand.CliHost(_ => null).Commands, log.Add, scheduler: scheduler, runnerControl: _ => runner);
        await app.StartAsync();
        var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, ServerRole role)
        {
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
            return await http.SendAsync(request);
        }

        return new Session(app, Send, scheduler);
    }

    private sealed class NoRunner : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, "{}", ""));
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private const string Hourly = "/api/v1/projects/sales/schedule/sqlserver/hourly/";
    private const string Nightly = "/api/v1/projects/sales/schedule/sqlserver/nightly/";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pause_resume_and_run_of_a_runner_owned_plan_go_to_the_runner_with_or_without_a_scheduler_here(bool withScheduler)
    {
        var s = await StartAsync(withScheduler);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(HttpMethod.Post, Hourly + "pause", ServerRole.Viewer)).StatusCode);
            Assert.Empty(runner.Calls);

            var paused = await s.Send(HttpMethod.Post, Hourly + "pause", ServerRole.Operator);
            Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
            Assert.Equal("runner", (string)(await Body(paused))["owner"]!);
            Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Post, Hourly + "resume", ServerRole.Operator)).StatusCode);
            var run = await Body(await s.Send(HttpMethod.Post, Hourly + "run", ServerRole.Operator));
            Assert.Equal("run-7", (string)run["run_id"]!);

            var package = RunnerNaming.PackageOf("sales");
            var task = RunnerNaming.TaskOf("sqlserver", "hourly");
            Assert.Equal([$"pause {package} {task}", $"resume {package} {task}", $"run {package} {task}"], runner.Calls);
            // nothing was done to this server's own scheduler: it has no pause and no queued item for a plan it does not own
            if (s.Scheduler is { } own)
            {
                var hosted = new ProjectsFile(serverDir).Find("sales")!;
                Assert.Null(own.PausedBy(hosted, own.Find(hosted, "sqlserver", "hourly")!));
                Assert.True(own.RunNow(hosted, own.Find(hosted, "sqlserver", "hourly")!));
            }

            // the audit names who asked and where it went
            var audit = File.ReadAllText(Path.Combine(serverDir, "management-audit.log"));
            Assert.Contains("schedule.pause", audit);
            Assert.Contains("sales/sqlserver/hourly", audit);
            Assert.Contains("runner-x", audit);
            Assert.Contains("Ada", audit);
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task A_run_the_runner_already_has_is_a_409_naming_the_runner_and_a_runner_that_refuses_is_a_502_with_its_words()
    {
        var s = await StartAsync(withScheduler: true);
        try
        {
            runner.RunId = null;
            var busy = await s.Send(HttpMethod.Post, Hourly + "run", ServerRole.Operator);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Contains("runner-x", (string)(await Body(busy))["error"]!);

            runner.Fail = true;
            var refused = await s.Send(HttpMethod.Post, Hourly + "pause", ServerRole.Operator);
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            var error = (string)(await Body(refused))["error"]!;
            Assert.Contains("runner-x", error);
            Assert.Contains("operator role", error);
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task A_plan_this_server_owns_is_still_paused_here_and_never_asks_the_runner()
    {
        var s = await StartAsync(withScheduler: true);
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Post, Nightly + "pause", ServerRole.Operator)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Post, Nightly + "run", ServerRole.Operator)).StatusCode);
            Assert.Empty(runner.Calls);
            var hosted = new ProjectsFile(serverDir).Find("sales")!;
            Assert.NotNull(s.Scheduler!.PausedBy(hosted, s.Scheduler.Find(hosted, "sqlserver", "nightly")!));
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }

    [Fact]
    public async Task The_schedule_list_carries_the_owner_and_for_a_runner_owned_plan_the_runners_own_pause_next_time_and_last_run()
    {
        var s = await StartAsync(withScheduler: true);
        try
        {
            var started = new DateTimeOffset(2026, 11, 8, 3, 15, 0, TimeSpan.Zero);
            runner.Status = new ClientTaskStatus("t", "runner", new DateTimeOffset(2026, 11, 8, 4, 15, 0, TimeSpan.Zero),
                new ClientLastRun("run-3", "failed", started, started.AddMinutes(2), 1, 12), 0, 0, true, "bob");

            var jobs = (await Body(await s.Send(HttpMethod.Get, "/api/v1/projects/sales/schedule", ServerRole.Viewer)))["jobs"]!.AsArray();

            var hourly = jobs.Single(j => (string)j!["plan"]! == "hourly")!;
            Assert.Equal("runner", (string)hourly["owner"]!);
            Assert.Equal(1, (int)hourly["last_exit"]!);      // what the console's monitoring row reads: the runner's failed run is this row's failure
            Assert.True((bool)hourly["paused"]!);
            Assert.Equal("bob", (string)hourly["paused_by"]!);
            Assert.Equal("failed", (string)hourly["runner"]!["last_run"]!["outcome"]!);
            Assert.Equal("run-3", (string)hourly["runner"]!["last_run"]!["id"]!);
            Assert.Equal("runner-x", (string)hourly["runner"]!["server"]!);
            Assert.Equal(started, DateTimeOffset.Parse((string)hourly["last_started"]!, null, System.Globalization.DateTimeStyles.AssumeUniversal));
            var nightly = jobs.Single(j => (string)j!["plan"]! == "nightly")!;
            Assert.Equal("old", (string)nightly["owner"]!);
            Assert.Null(nightly["runner"]);

            // a runner that cannot be asked is said, in the row; the list is still an answer
            runner.Fail = true;
            var again = (await Body(await s.Send(HttpMethod.Get, "/api/v1/projects/sales/schedule", ServerRole.Viewer)))["jobs"]!.AsArray().Single(j => (string)j!["plan"]! == "hourly")!;
            Assert.Contains("operator role", (string)again["runner"]!["error"]!);
            Assert.Null(again["runner"]!["last_run"]);
        }
        finally { await s.App.StopAsync(); await s.App.DisposeAsync(); }
    }
}
