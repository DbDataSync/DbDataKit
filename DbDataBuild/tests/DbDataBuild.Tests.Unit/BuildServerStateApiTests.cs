using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.State.Cli;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 29b C05: the Build server's management API tells a console what its state is and puts the server into maintenance (requests refused, the scheduler stops claiming), audited.</summary>
public sealed class BuildServerStateApiTests : IAsyncLifetime
{
    private readonly string serverDir = Path.Combine(Path.GetTempPath(), "ddb-c05-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project = Path.Combine(Path.GetTempPath(), "ddb-c05-project-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly MaintenanceGate gate;
    private readonly Blockable runner = new();
    private WebApplication? app;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private ScratchStateDatabase? scratch;

    public BuildServerStateApiTests() => gate = new MaintenanceGate();

    /// <summary>A runner whose commands wait until the test lets them go: the stand-in for a slow request.</summary>
    private sealed class Blockable : IProjectCommandRunner
    {
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; }
        public void Release() => _go.TrySetResult();
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => RunAsync(argv, projectRoot, null, cancellationToken);
        public async Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, string? actor, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Block) await _go.Task;
            return new CommandResult(0, """{"command":"fake","ok":true}""", "");
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        runner.Release();
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        scratch?.Dispose();
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { project, serverDir }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    /// <summary>Starts a server whose state is on <paramref name="engine"/>; false when that engine is not set up here.</summary>
    private async Task<bool> StartAsync(string engine)
    {
        Directory.CreateDirectory(serverDir);
        StateSettings settings;
        Func<string, string?>? environment = null;
        if (engine == "sqlite") settings = new StateSettings();
        else
        {
            scratch = ScratchStateDatabase.For(engine);
            if (scratch is null) return false;
            settings = new StateSettings(scratch.Engine, scratch.Connection);
            environment = scratch.Environment;
        }

        BuildSchedulerSchema.Open(serverDir, settings: settings);   // the server's state, made by a start with the scheduler on
        var target = new StateTarget(settings, BuildState.SqlitePath(serverDir), () => BuildState.Secrets(serverDir, environment), BuildState.SettingsFile(serverDir), StateSources.Defaults);
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        var projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject(Path.GetFileName(project), project, null, null));
        var serverSettings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        app = BuildServer.Build(serverSettings, BuildServer.Certificate(serverDir), runner, projects, new TuiCommand.CliHost(_ => null).Commands, _ => { }, maintenance: gate, state: target);
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(serverSettings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Admin));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return true;
    }

    // a token is good for one call
    private string Token(ServerRole roles, string user = "ada") => console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, user, user, roles));

    private async Task<(HttpResponseMessage Response, string Text)> Send(HttpMethod method, string path, ServerRole roles, string? json = null, string user = "ada")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, Token(roles, user));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private IReadOnlyList<AuditEntry> Audit() => new FileAuditLog(Path.Combine(serverDir, "management-audit.log")).Read();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_viewer_reads_the_state_with_a_masked_connection_and_the_module_versions(string engine)
    {
        if (!await StartAsync(engine)) return;   // xunit v2 has no runtime skip: the engine is not set up here

        var (response, text) = await Send(HttpMethod.Get, "/api/v1/state", ServerRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = JsonNode.Parse(text)!;
        Assert.Equal("dbdatabuild", (string?)state["product"]);
        Assert.Equal(engine == "sqlite" ? StateEngineIds.Sqlite : engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, (string?)state["engine"]);
        Assert.True((bool)state["connected"]!);
        Assert.Equal(0, (int)state["pending_migrations"]!);
        Assert.Contains(state["modules"]!.AsArray(), m => (string?)m!["module"] == "scheduling" && (int)m["binary_version"]! == (int)m["registered_version"]!);
        Assert.False((bool)state["maintenance"]!["enabled"]!);
        if (scratch is not null)
        {
            Assert.Contains("env:" + scratch.PasswordVariable, (string?)state["connection"]);
            Assert.DoesNotContain(scratch.Environment(scratch.PasswordVariable)!, text);
        }

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/state/maintenance", ServerRole.Viewer)).Response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_enable_maintenance_and_the_refusal_is_audited()
    {
        await StartAsync("sqlite");

        var (response, _) = await Send(HttpMethod.Post, "/api/v1/state/maintenance", ServerRole.Viewer, """{"enabled": true, "reason": "no"}""", "vera");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(gate.Enabled);
        var entry = Assert.Single(Audit());
        Assert.Equal(("state.maintenance", "refused", "vera"), (entry.Command, entry.Outcome, entry.UserId));
    }

    [Fact]
    public async Task Maintenance_refuses_requests_drains_the_one_in_flight_and_reports_idle_then_leaving_restores_service()
    {
        await StartAsync("sqlite");
        var id = Path.GetFileName(project);
        runner.Block = true;
        var slow = Send(HttpMethod.Post, $"/api/v1/projects/{id}/commands/project_show_graph", ServerRole.Viewer, "{}");   // a command that is running when maintenance begins
        await runner.Started.Task;

        var (enabled, enabledText) = await Send(HttpMethod.Post, "/api/v1/state/maintenance", ServerRole.Admin, """{"enabled": true, "reason": "moving the state"}""");
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var status = JsonNode.Parse(enabledText)!;
        Assert.True((bool)status["enabled"]!);
        Assert.False((bool)status["idle"]!);
        Assert.Equal(1, (int)status["inflight"]!);

        var (refused, refusedText) = await Send(HttpMethod.Get, "/api/v1/projects", ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal("moving the state", (string?)JsonNode.Parse(refusedText)!["reason"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/state", ServerRole.Viewer)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/projects", ServerRole.Admin)).Response.StatusCode);
        Assert.False((bool)JsonNode.Parse((await Send(HttpMethod.Get, "/api/v1/state/maintenance", ServerRole.Viewer)).Text)!["idle"]!);

        runner.Release();
        Assert.Equal(HttpStatusCode.OK, (await slow).Response.StatusCode);   // what was in flight finished normally
        Assert.True((bool)JsonNode.Parse((await Send(HttpMethod.Get, "/api/v1/state/maintenance", ServerRole.Viewer)).Text)!["idle"]!);

        var (left, _) = await Send(HttpMethod.Post, "/api/v1/state/maintenance", ServerRole.Admin, """{"enabled": false}""");
        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/projects", ServerRole.Viewer)).Response.StatusCode);

        var audit = Audit();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, e => { Assert.Equal("state.maintenance", e.Command); Assert.Equal("ok", e.Outcome); Assert.Equal("ada", e.UserId); });
        Assert.Contains("moving the state", audit[0].Target);
    }
}

/// <summary>The scheduler stops claiming while the server is in maintenance, and the plans it started are part of what the server drains.</summary>
public sealed class BuildSchedulerMaintenanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-c05-sched-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly ProjectsFile projects;
    private readonly MaintenanceGate gate;
    private readonly BuildScheduler scheduler;
    private readonly Clock clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void To(DateTimeOffset when) => _now = when;
    }

    private sealed class Held : IProjectCommandRunner
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken)
        {
            await Release.Task;
            return new CommandResult(0, "{}", "");
        }
    }

    private readonly Held runner = new();

    public BuildSchedulerMaintenanceTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        Directory.CreateDirectory(root);
        projects = new ProjectsFile(root);
        projects.Add(new HostedProject("sales", project, null, null));
        gate = new MaintenanceGate(work: () => scheduler!.Running);
        scheduler = new BuildScheduler(projects, runner, BuildSchedulerSchema.Open(root, clock), clock, _ => { }, paused: () => gate.Enabled);
    }

    public void Dispose()
    {
        runner.Release.TrySetResult();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_queued_run_is_not_claimed_in_maintenance_and_the_one_running_keeps_the_server_busy_until_it_ends()
    {
        scheduler.Tick();
        clock.To(new DateTimeOffset(2026, 10, 25, 12, 16, 0, TimeSpan.Zero));
        Assert.Single(scheduler.Tick());   // due, queued

        gate.Enter("moving the state");
        Assert.Null(scheduler.Claim("w"));   // queued work waits
        Assert.True(gate.Status().Idle);

        gate.Leave();
        var claimed = scheduler.Claim("w");
        Assert.NotNull(claimed);
        Assert.Equal(1, scheduler.Running);
        var running = scheduler.RunAsync(claimed!.Value.Item, claimed.Value.Project, "w", CancellationToken.None);

        gate.Enter("moving the state");
        Assert.False(gate.Status().Idle);   // a plan is still running: draining
        Assert.Equal(1, gate.Status().InFlight);

        runner.Release.SetResult();
        await running;
        Assert.Equal(0, scheduler.Running);
        Assert.True(gate.Status().Idle);
    }
}
