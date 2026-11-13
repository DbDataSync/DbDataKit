using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataBuild.Cli.Server;
using DbDataBuild.Core;
using DbDataBuild.Tui.Model;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 28c E21, the part that needs no runner and no engine: the person-driven writes (apply a plan, acknowledge a block, seed) as the manual-only tasks of a package of their own, the command line each task runs, the refusal of inputs
/// that are not what a command should be given, who a run is for, and the Build server's route that starts a task on the runner for the person who asked (a fake runner records what it was told). The real runner and a real engine are
/// in the conformance project (<c>BuildOperationsOnRunnerTests</c>).
/// </summary>
public sealed class BuildOperationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-ops-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly string serverDir;
    private readonly FakeRunner runner = new();

    public BuildOperationsTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        serverDir = Path.Combine(root, "server");
        Directory.CreateDirectory(serverDir);
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private BuiltPackage Built()
    {
        var diagnostics = new List<Diagnostic>();
        var built = OperationsPackage.Build(project, "sales-ops", "0.4.x", diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Select(DiagnosticFormatter.Format)));
        return built!;
    }

    [Fact]
    public void The_operations_package_has_three_manual_only_tasks_every_connection_as_a_slot_and_the_sources_but_no_plan_and_no_compiled_file()
    {
        var built = Built();
        Assert.Equal(["apply_plan", "ack", "seed"], built.Manifest.Tasks.Select(t => t.Name));
        Assert.All(built.Manifest.Tasks, t =>
        {
            Assert.False(t.Schedule.HasSchedule);                                    // nothing starts it but a person
            Assert.Equal([TriggerKind.Manual], t.Triggers.Select(x => x.Kind));
            Assert.Equal(1, t.Limits.Concurrency);
            Assert.Null(t.Limits.Attempts);                                          // a write is never repeated by the runner
            Assert.NotEmpty(t.Connections);
            Assert.All(t.Connections, c => Assert.Equal(SlotEnvironmentFormats.BuildLogin, c.Env));
        });
        Assert.All(built.Manifest.Tasks, t => Assert.DoesNotContain('-', t.Name));   // a refresh task is `connection-plan`: a plan is never taken for an operation
        var paths = built.Files.Select(f => f.Path).ToList();
        Assert.Contains("dbdatabuild.yml", paths);
        Assert.Contains(paths, p => p.StartsWith("models/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.StartsWith("plans/", StringComparison.Ordinal) || p.StartsWith("rendered/", StringComparison.Ordinal) || p.StartsWith(".dbdatabuild", StringComparison.Ordinal));

        // the runner's own reader and validator accept the manifest, and it comes back the same
        var load = PackageManifestReader.Load(built.ManifestText, "package.yaml");
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics));
        Assert.Equal(built.ManifestText, PackageManifestWriter.Write(load.Manifest!));
        Assert.Empty(PackageManifestValidator.Validate(load.Manifest!, "package.yaml"));
        Assert.Equal(["connection", "plan", "plan_text", "allow_risky", "allow_destructive", "allow_consumers", "input_pins"], load.Manifest!.Tasks[0].Inputs.Select(i => i.Name));
    }

    [Fact]
    public void The_refresh_package_is_what_it_was_and_holds_none_of_the_operations()
    {
        var diagnostics = new List<Diagnostic>();
        var refresh = RunnerProject.Build(project, "sales", "0.4.x", diagnostics)!;
        Assert.DoesNotContain(refresh.Manifest.Tasks, t => BuildOperations.All.Contains(t.Name));
        Assert.DoesNotContain(refresh.Files, f => f.Path.StartsWith("models/", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_task_runs_the_command_of_that_name_with_the_inputs_it_was_given_and_a_dry_run_is_the_commands_own()
    {
        Dictionary<string, string> Inputs(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);
        var apply = BuildOperations.Arguments(BuildOperations.ApplyPlan, Inputs(("connection", "wh"), ("plan", "2026-11-12-0a1b2c3d"), ("allow_risky", "true"), ("allow_destructive", "marts.fct, marts.dim"), ("input_pins", "a.b=1\nc=2")), "/p", null, dryRun: false);
        Assert.Equal(["connection", "deploy", "wh", "--apply-plan", "2026-11-12-0a1b2c3d", "--allow-risky", "--allow-destructive", "marts.fct", "--allow-destructive", "marts.dim", "--input=a.b=1", "--input=c=2", "--project", "/p", "--format", "json"], apply);
        Assert.Contains("--dry-run", BuildOperations.Arguments(BuildOperations.ApplyPlan, Inputs(("connection", "wh"), ("plan", "x")), "/p", "/scratch/x.plan.yml", dryRun: true));
        Assert.Contains("/scratch/x.plan.yml", BuildOperations.Arguments(BuildOperations.ApplyPlan, Inputs(("connection", "wh"), ("plan", "x")), "/p", "/scratch/x.plan.yml", dryRun: false));

        Assert.Equal(["connection", "deploy", "wh", "--ack", "drift:marts.fct", "--reason=it was me", "--project", "/p", "--format", "json"],
            BuildOperations.Arguments(BuildOperations.Ack, Inputs(("connection", "wh"), ("kind", "drift"), ("name", "marts.fct"), ("reason", "it was me")), "/p", null, dryRun: false));

        var seed = BuildOperations.Arguments(BuildOperations.Seed, Inputs(("connection", "wh"), ("seed", "7"), ("scale", "100"), ("replace", "true")), "/p", null, dryRun: false);
        Assert.Equal(["connection", "seed", "wh", "--seed", "7", "--scale", "100", "--replace", "--apply", "--project", "/p", "--format", "json"], seed);
        Assert.DoesNotContain("--apply", BuildOperations.Arguments(BuildOperations.Seed, Inputs(("connection", "wh")), "/p", null, dryRun: true));
    }

    [Theory]
    [InlineData("apply_plan", "connection=--help;plan=x")]
    [InlineData("apply_plan", "connection=wh;plan=../../etc/passwd")]
    [InlineData("apply_plan", "connection=wh;plan=x;allow_destructive=--yes")]
    [InlineData("apply_plan", "connection=wh;plan=x;input_pins=--project=/")]
    [InlineData("ack", "connection=wh;kind=nothing;name=a;reason=r")]
    [InlineData("ack", "connection=wh;kind=drift;name=--x;reason=r")]
    [InlineData("ack", "connection=wh;kind=drift;name=a;reason= ")]
    [InlineData("seed", "connection=wh;seed=many")]
    [InlineData("deploy", "connection=wh")]
    public void An_input_that_is_an_option_or_a_path_or_not_the_kind_of_thing_asked_for_is_refused_before_anything_runs(string task, string pairs)
    {
        var inputs = pairs.Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.NotNull(BuildOperations.Refusal(task, inputs));
    }

    [Fact]
    public void A_run_is_for_the_person_the_runner_recorded_and_for_the_run_itself_when_nobody_was()
    {
        var asked = BuildOperations.ActorOf(n => n == "DBDATAKIT_RUN_REQUESTED_BY" ? "Ada via Main console (sent by Build server)" : null, "sales-ops", "prod");
        Assert.Equal("Ada via Main console (sent by Build server)", asked);
        Assert.Equal("runner sales-ops/prod", BuildOperations.ActorOf(_ => null, "sales-ops", "prod"));
        // and it is the name the commands write into the tracking tables
        Assert.Equal(asked, Actor.Of(n => n == Actor.Variable ? asked : null, "login"));
    }

    private sealed class FakeRunner : IRunnerTaskControl
    {
        public List<(string Package, string Task, IReadOnlyDictionary<string, string> Inputs, string Person)> Started { get; } = [];
        public string? RunId { get; set; } = "run-9";

        public Task PauseAsync(string package, string task, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeAsync(string package, string task, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> RunNowAsync(string package, string task, CancellationToken ct) => Task.FromResult(RunId);
        public Task<ClientTaskStatus?> StatusAsync(string package, string task, CancellationToken ct) => Task.FromResult<ClientTaskStatus?>(null);

        public Task<string?> RunForAsync(string package, string task, IReadOnlyDictionary<string, string> inputs, string onBehalfOf, CancellationToken ct)
        {
            Started.Add((package, task, inputs, onBehalfOf));
            return Task.FromResult(RunId);
        }
    }

    private sealed class NoRunner : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => throw new InvalidOperationException("the server runs nothing itself: " + string.Join(' ', argv));
    }

    private async Task<(WebApplication App, Func<string, ServerRole, object?, Task<HttpResponseMessage>> Post)> StartAsync(bool linked)
    {
        if (linked) BuildScheduleSettings.Write(serverDir, "sales", "sqlserver", "hourly", "runner", new ScheduleRunnerLink("runner-x", "prod", null));
        var projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject("sales", project, null, null));
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), new NoRunner(), projects, new TuiCommand.CliHost(_ => null).Commands, _ => { }, runnerControl: _ => runner);
        await app.StartAsync();
        var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Approver | ServerRole.Operator));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "Main console");
        async Task<HttpResponseMessage> Post(string path, ServerRole role, object? body)
        {
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
            return await http.SendAsync(request);
        }

        return (app, Post);
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task The_server_starts_the_task_on_the_runner_for_the_person_who_asked_and_writes_nothing_itself()
    {
        var (app, post) = await StartAsync(linked: true);
        try
        {
            // an operator may refresh but may not apply a plan: the role is what the commands route asks of the same command
            Assert.Equal(HttpStatusCode.Forbidden, (await post("/api/v1/projects/sales/operations/apply-plan", ServerRole.Operator, new { connection = "sqlserver", plan = "2026-11-12-0a1b2c3d" })).StatusCode);
            Assert.Empty(runner.Started);

            var accepted = await post("/api/v1/projects/sales/operations/apply-plan", ServerRole.Approver, new { connection = "sqlserver", plan = "2026-11-12-0a1b2c3d", allow_risky = true });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var body = await Body(accepted);
            Assert.Equal("run-9", (string)body["run_id"]!);
            var started = Assert.Single(runner.Started);
            Assert.Equal((BuildOperations.PackageOf("sales"), "apply_plan"), (started.Package, started.Task));
            Assert.StartsWith("Ada via ", started.Person, StringComparison.Ordinal);       // the person, and the console they came through
            Assert.Equal(started.Person, (string)body["on_behalf_of"]!);
            Assert.Equal(("sqlserver", "2026-11-12-0a1b2c3d", "true"), (started.Inputs["connection"], started.Inputs["plan"], started.Inputs["allow_risky"]));

            // the acknowledgement and the seed go the same way, and an input that is an option is refused before the runner hears of it
            Assert.Equal(HttpStatusCode.Accepted, (await post("/api/v1/projects/sales/operations/ack", ServerRole.Approver, new { connection = "sqlserver", kind = "drift", name = "marts.fct", reason = "it was me" })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await post("/api/v1/projects/sales/operations/seed", ServerRole.Approver, new { connection = "sqlserver" })).StatusCode);
            Assert.Equal(["apply_plan", "ack", "seed"], runner.Started.Select(s => s.Task));
            Assert.Equal(HttpStatusCode.BadRequest, (await post("/api/v1/projects/sales/operations/ack", ServerRole.Approver, new { connection = "--help", kind = "drift", name = "x", reason = "r" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await post("/api/v1/projects/sales/operations/seed", ServerRole.Approver, new { connection = "sqlserver", nonsense = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await post("/api/v1/projects/sales/operations/format-disk", ServerRole.Approver, new { connection = "sqlserver" })).StatusCode);
            Assert.Equal(3, runner.Started.Count);

            // a run that is queued already is a conflict, not a second run
            runner.RunId = null;
            Assert.Equal(HttpStatusCode.Conflict, (await post("/api/v1/projects/sales/operations/seed", ServerRole.Approver, new { connection = "sqlserver" })).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task A_plan_in_a_file_of_the_project_travels_with_the_run()
    {
        Directory.CreateDirectory(Path.Combine(project, "plans", "sqlserver"));
        File.WriteAllText(Path.Combine(project, "plans", "sqlserver", "mine.plan.yml"), "plan text\n");
        var (app, post) = await StartAsync(linked: true);
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await post("/api/v1/projects/sales/operations/apply-plan", ServerRole.Approver, new { connection = "sqlserver", plan = "mine" })).StatusCode);
            Assert.Equal("plan text\n", Assert.Single(runner.Started).Inputs["plan_text"]);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task A_server_with_no_runner_link_refuses_naming_what_is_missing_and_runs_nothing()
    {
        var (app, post) = await StartAsync(linked: false);
        try
        {
            var refused = await post("/api/v1/projects/sales/operations/seed", ServerRole.Approver, new { connection = "sqlserver" });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("schedule.runner", (string)(await Body(refused))["error"]!);
            Assert.Empty(runner.Started);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task The_route_never_touches_a_login_the_server_holds_a_server_with_no_write_secret_at_all_starts_the_task_and_the_source_has_no_way_to_use_one()
    {
        // plan 28c E19 extended: the server's store holds no write login, and nothing is asked of it
        var store = ServerSecrets.Open(serverDir);
        Assert.False(await store.ExistsAsync(ServerSecrets.NameOf("sqlserver", DbDataKit.Connections.LoginRole.Write)));
        var (app, post) = await StartAsync(linked: true);
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await post("/api/v1/projects/sales/operations/seed", ServerRole.Approver, new { connection = "sqlserver" })).StatusCode);
            Assert.Single(runner.Started);
        }
        finally { await app.StopAsync(); }

        // the route has no reference to a secret store, a login or a command runner: a person's write cannot be done by this server
        var source = File.ReadAllText(Path.Combine(PolyglotBindingTests.RepoRoot(), "src", "DbDataBuild.Cli", "Server", "BuildRunnerOperationsApi.cs"));
        foreach (var forbidden in new[] { "ServerSecrets", "LoginRole", "Login.Write", "IProjectCommandRunner", "ChildProcessCommandRunner", "MutationGate", "ReadSession" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }
}
