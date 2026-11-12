using DbDataKit.Web.Trust;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The runner embedded in the console (plan 28a R20): a real console host with the runner on, over SQLite, PostgreSQL and SQL Server. The tasks of a package are run by a recording stand-in for the executor (the real executor and the task host
/// are the runner's own tests, <c>DbDataRunner.Tests</c>); everything else (the scheduler, the posture, the publishing, the API, the state scope, the system tasks, the stop) is the real thing.
/// </summary>
public sealed class EmbeddedRunnerTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<IAsyncDisposable> _hosts = [];
    private readonly List<IDisposable> _databases = [];

    public static TheoryData<string> Engines => ["sqlite", "postgres", "mssql"];

    public void Dispose()
    {
        foreach (var host in _hosts) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var database in _databases) database.Dispose();
        foreach (var directory in _directories) try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // the artifact store's files are read-only on Windows
    }

    /// <summary>Stands in for the executor: records what it ran and when, and runs for <see cref="Duration"/> (or until it is cancelled).</summary>
    private sealed class Recorder(Func<StateDatabase> state) : ITaskRunner
    {
        public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(300);
        public bool UntilCancelled { get; set; }
        public List<(string Task, DateTimeOffset At)> Started { get; } = [];
        private int _running;
        public int MaxConcurrent;

        public async Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default)
        {
            var runs = new RunStore(state(), RunnerNames.EngineService);
            var id = request.RunId ?? Guid.NewGuid();
            runs.Start(request.Task.ToString(), id, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);
            lock (Started) Started.Add((request.Task.ToString(), DateTimeOffset.UtcNow));
            var now = Interlocked.Increment(ref _running);
            int seen;
            do { seen = MaxConcurrent; } while (now > seen && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen);
            TaskEnd end;
            try
            {
                if (UntilCancelled) await Task.Delay(Timeout.Infinite, ct); else await Task.Delay(Duration, ct);
                end = new TaskEnd(TaskEndState.Succeeded, Processed: 1);
            }
            catch (OperationCanceledException) { end = new TaskEnd(TaskEndState.Stopped); }
            finally { Interlocked.Decrement(ref _running); }
            var finish = end.ToRunFinish();
            runs.Finish(id, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
            return new TaskRunResult(id, end, true, null, false, TimeSpan.Zero, 0, 0, 0, 0, DbDataKit.Runner.Limits.LimitEnforcement.None, DbDataKit.Runner.Limits.LimitEnforcement.None, "none", 0, false, []);
        }
    }

    private sealed class Rig(WebApplication app, HttpClient http, Recorder recorder, string directory)
    {
        public WebApplication App => app;
        public Recorder Recorder => recorder;
        public string Directory => directory;
        public StateDatabase State => app.Services.GetRequiredService<StateDatabase>();
        public ConsoleRunner Runner => app.Services.GetRequiredService<ConsoleRunner>();
        public string Admin { get; } = Session(app, UserRole.Admin);
        public string Viewer { get; } = Session(app, UserRole.Viewer);

        private static string Session(WebApplication app, UserRole role) =>
            app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role).Id).Id;

        public async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null, byte[]? raw = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
            if (body is not null) request.Content = JsonContent.Create(body);
            if (raw is not null) { request.Content = new ByteArrayContent(raw); request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream"); }
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
            return (response.StatusCode, json);
        }

        public async Task<bool> Eventually(Func<bool> condition, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(100);
            }

            return condition();
        }
    }

    private async Task<Rig> Start(string engine, ConsoleRunnerSettings? runner = null, RetentionSettings? retention = null, bool enabled = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ddk-console-runner-" + Guid.NewGuid().ToString("N")[..8]);
        _directories.Add(directory);
        StateSettings? state = null;
        if (engine != "sqlite")
        {
            var scratch = ScratchStateDatabase.For(engine);
            _databases.Add(scratch);
            state = new StateSettings(engine, scratch.Connection);
        }

        if (retention is not null) state = (state ?? new StateSettings()) with { Retention = retention };
        StateDatabase? opened = null;
        var recorder = new Recorder(() => opened!);
        runner ??= new ConsoleRunnerSettings();
        runner = runner with
        {
            Enabled = enabled,
            Tick = TimeSpan.FromMilliseconds(200),
            CancellationGrace = TimeSpan.FromSeconds(5),
            TaskHostPath = typeof(RunnerHost).Assembly.Location,
            WrapRunner = _ => recorder,
        };
        var settings = new ConsoleSettings(directory, new Uri("https://localhost:0"), State: state) { Runner = runner };
        var app = ConsoleHost.Build(settings, ConsoleHost.Certificate(directory), _ => { }, webRoot: directory);
        opened = app.Services.GetRequiredService<StateDatabase>();
        await app.StartAsync();
        _hosts.Add(new Stop(app));
        var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return new Rig(app, http, recorder, directory);
    }

    private sealed class Stop(WebApplication app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await app.StopAsync(); } catch (ObjectDisposedException) { }
            await app.DisposeAsync();
        }
    }

    private static string Manifest(string name, params (string Task, TimeSpan? Every)[] tasks)
    {
        PackageTask Task((string Task, TimeSpan? Every) t) => new(t.Task, new TaskEntry(), t.Every is { } every ? Schedule.Interval(every) : new Schedule(), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], []);
        return PackageManifestWriter.Write(new PackageManifest(name, null, new PackageLaunch(LaunchKind.Executable, Command: "sample"), new BuiltAgainst(RunnerContracts.Dotnet, null, RunnerContracts.Protocol), [.. tasks.Select(Task)]));
    }

    private static readonly RunnerActor Tester = new("test", "tester", "command");

    private static PublishOutcome Publish(Rig rig, string name, string channel, params (string Task, TimeSpan? Every)[] tasks) =>
        rig.Runner.Publishing.Publish(new PublishInput(name, Manifest(name, tasks), [], channel, PublishMode.Serve), Tester);

    // ---- a console that does not enable it is unchanged

    [Fact]
    public async Task A_console_that_does_not_enable_the_runner_has_no_runner_tables_modules_or_server_entry()
    {
        var rig = await Start("sqlite", enabled: false);

        Assert.False(rig.State.Layout!.Has(RunnerModule.Name));
        Assert.False(rig.State.Layout.Has(DefinitionsModule.Name));
        Assert.False(rig.State.Layout.Has(SchedulingModule.Name));
        Assert.Null(rig.App.Services.GetService<ConsoleRunner>());
        var (status, list) = await rig.Call(HttpMethod.Get, "/api/instances", rig.Admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(list!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Get, "/api/embedded-runner/status", rig.Admin)).Status);
        Assert.False(Directory.Exists(Path.Combine(rig.Directory, "runner")), "no runner directory, no child process, no task host");
    }

    [Fact]
    public void The_runner_is_off_unless_the_settings_file_turns_it_on_and_a_wrong_key_is_refused_naming_the_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ddk-console-runner-" + Guid.NewGuid().ToString("N")[..8]);
        _directories.Add(directory);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.yaml");
        Assert.False(ConsoleRunnerSettings.Load(directory).Enabled);

        File.WriteAllText(file, "state:\n  engine: sqlite\nrunner:\n  enabled: true\n  channels: [dev, qa]\n  max_concurrent: 1\n");
        var loaded = ConsoleRunnerSettings.Load(directory);
        Assert.True(loaded.Enabled);
        Assert.Equal(["dev", "qa"], loaded.Channels);
        Assert.Equal(1, loaded.MaxConcurrent);
        Assert.Equal(Path.Combine(directory, "runner"), loaded.DirectoryFor(directory));
        Assert.Equal(["dev", "preview"], new ConsoleRunnerSettings().Channels);
        Assert.Equal(2, new ConsoleRunnerSettings().MaxConcurrent);

        File.WriteAllText(file, "runner:\n  enabled: true\n  maxconcurrent: 3\n");
        Assert.Contains(file, Assert.Throws<InvalidOperationException>(() => ConsoleRunnerSettings.Load(directory)).Message);
        File.WriteAllText(file, "runner:\n  enabled: true\n  channels: [dev, prod]\n");
        Assert.Contains("accept-production", Assert.Throws<InvalidOperationException>(() => ConsoleRunnerSettings.Load(directory)).Message);
    }

    // ---- production is an administrator's recorded choice

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_production_package_is_refused_by_default_and_after_the_recorded_choice_it_publishes_runs_and_the_warning_is_reported(string engine)
    {
        var rig = await Start(engine);
        Assert.Equal(new StateScope(Group: "console", Service: "dbdatakit"), ConsoleState.ScopeFor(rig.Runner.Settings));

        var refused = Assert.Throws<RunnerPublishException>(() => Publish(rig, "billing", "prod", ("nightly", TimeSpan.FromSeconds(1))));
        Assert.Equal(403, refused.Status);
        Assert.Contains("accept_production_on_console_runner", refused.Message);
        Assert.Contains("production schedules", ProductionEffect(refused.Message) ?? refused.Message);
        // activating into it is refused too
        var (activate, body) = await rig.Call(HttpMethod.Post, "/api/embedded-runner/definitions/integration/billing/activate", rig.Admin, new { channel = "prod" });
        Assert.Equal(HttpStatusCode.Forbidden, activate);
        Assert.Contains("accept_production_on_console_runner", (string)body!["error"]!);
        Assert.False((bool)(await rig.Call(HttpMethod.Get, "/api/embedded-runner/posture", rig.Viewer)).Body!["production_accepted"]!);

        // a viewer cannot choose; an administrator must confirm
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Post, "/api/embedded-runner/accept-production", rig.Viewer, new { confirmed = true })).Status);
        var unconfirmed = await rig.Call(HttpMethod.Post, "/api/embedded-runner/accept-production", rig.Admin, new { confirmed = false });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.Status);
        Assert.Contains("not confirmed", (string)unconfirmed.Body!["error"]!);
        Assert.Throws<RunnerPublishException>(() => Publish(rig, "billing", "prod", ("nightly", TimeSpan.FromSeconds(1))));

        var chosen = await rig.Call(HttpMethod.Post, "/api/embedded-runner/accept-production", rig.Admin, new { confirmed = true, note = "small install" });
        Assert.Equal(HttpStatusCode.OK, chosen.Status);
        Assert.True((bool)chosen.Body!["production_accepted"]!);
        Assert.Contains("stop whenever the console stops", (string)chosen.Body["production_warning"]!);
        var audit = new AuditLog(rig.State, "dbdatakit").Read(new AuditQuery(Verb: PostureGuard.ChooseVerb));
        Assert.Single(audit);

        Publish(rig, "billing", "prod", ("nightly", TimeSpan.FromSeconds(1)));
        Assert.True(await rig.Eventually(() => rig.Recorder.Started.Any(s => s.Task == "billing/prod/nightly")), "the production task ran");
        var status = (await rig.Call(HttpMethod.Get, "/api/embedded-runner/status", rig.Viewer)).Body!;
        Assert.Contains("prod", status["channels"]!.AsArray().Select(c => (string)c!));
        Assert.Equal("console", (string)status["group"]!);
        Assert.NotNull(status["console_runner"]!["production_warning"]);
        Assert.Equal("false", (string)status["capabilities"]!["account_separate"]!);
    }

    private static string? ProductionEffect(string message) => message.Contains("their schedules stop whenever the console stops") ? "production schedules" : null;

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_development_package_runs_and_the_cap_holds(string engine)
    {
        var rig = await Start(engine, new ConsoleRunnerSettings { MaxConcurrent = 1 });
        rig.Recorder.Duration = TimeSpan.FromMilliseconds(700);

        Publish(rig, "etl", "dev", ("one", TimeSpan.FromSeconds(1)), ("two", TimeSpan.FromSeconds(1)));

        Assert.True(await rig.Eventually(() => rig.Recorder.Started.Select(s => s.Task).Distinct().Count() == 2), "both due tasks ran, one after the other");
        Assert.Equal(1, rig.Recorder.MaxConcurrent);
        Assert.Equal(1, (await rig.Call(HttpMethod.Get, "/api/embedded-runner/status", rig.Viewer)).Body!["max_concurrent_runs"]!.GetValue<int>());
        var runs = new RunStore(rig.State, RunnerNames.EngineService).Read(new RunQuery(RunnerNames.EngineService, Package: "etl", Channel: "dev"));
        Assert.NotEmpty(runs);
    }

    [Fact]
    public async Task Without_the_cap_two_due_tasks_run_together()
    {
        var rig = await Start("sqlite", new ConsoleRunnerSettings { MaxConcurrent = 2 });
        rig.Recorder.Duration = TimeSpan.FromMilliseconds(1500);

        Publish(rig, "etl", "dev", ("one", TimeSpan.FromSeconds(1)), ("two", TimeSpan.FromSeconds(1)));

        Assert.True(await rig.Eventually(() => rig.Recorder.MaxConcurrent == 2));
    }

    [Fact]
    public async Task A_channel_that_is_neither_listed_nor_production_is_refused_naming_the_setting()
    {
        var rig = await Start("sqlite");

        var refused = Assert.Throws<RunnerPublishException>(() => Publish(rig, "etl", "qa", ("one", null)));

        Assert.Contains("runner.channels", refused.Message);
        Publish(rig, "etl", "preview", ("one", null));
    }

    // ---- the console's own tasks

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_state_retention_system_task_runs_on_its_schedule_in_the_console_and_removes_expired_rows(string engine)
    {
        var rig = await Start(engine, new ConsoleRunnerSettings { RetentionEvery = TimeSpan.FromSeconds(1) },
            retention: new RetentionSettings { Modules = new Dictionary<string, double> { ["audit"] = 0.00001 } });
        var audit = new AuditLog(rig.State, "fixture");
        audit.Record(new AuditEvent("person", "fixture", "fixture.expired", TargetKind: "fixture", TargetId: "old"));
        Assert.Single(audit.Read(new AuditQuery(Verb: "fixture.expired")));

        Assert.True(await rig.Eventually(() => audit.Read(new AuditQuery(Verb: "fixture.expired")).Count == 0), "the expired row was removed by the system task");

        // the task removes the rows before its run record is closed: wait for the record, do not read it in the gap (Windows showed the run still Running)
        var store = new RunStore(rig.State, RunnerNames.EngineService);
        Assert.True(await rig.Eventually(() => store.Read(new RunQuery(RunnerNames.EngineService, Package: ConsoleSystemTasks.Package, Channel: "system"))
            .Any(r => r.Run.Outcome == RunOutcome.Succeeded && r.Run.JobId == "console-system/system/state-retention")), "the system task's run was recorded as succeeded");
        Assert.Empty(rig.Recorder.Started);   // it ran in the console's process, not through the executor
    }

    [Fact]
    public async Task The_state_backup_system_task_writes_a_verified_snapshot_to_the_folder_on_its_schedule_and_keeps_the_newest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ddk-backups-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rig = await Start("sqlite", new ConsoleRunnerSettings { BackupDirectory = folder, BackupEvery = TimeSpan.FromSeconds(1), BackupKeep = 2 });

            Assert.True(await rig.Eventually(() => DbDataKit.State.Engines.Snapshots.StateBackupJob.List(folder).Count == 2 && !Directory.GetDirectories(folder, "*.partial").Any()), "two backups were written and the older ones pruned");
            var newest = DbDataKit.State.Engines.Snapshots.StateBackupJob.List(folder)[0];
            Assert.True((await new DbDataKit.State.Engines.Snapshots.StateSnapshotService().VerifyAsync(newest, null, default)).Equal, "the newest backup verifies against its own manifest");
            var store = new RunStore(rig.State, RunnerNames.EngineService);
            Assert.True(await rig.Eventually(() => store.Read(new RunQuery(RunnerNames.EngineService, Package: ConsoleSystemTasks.Package, Channel: "system")).Any(r => r.Run.Outcome == RunOutcome.Succeeded && r.Run.JobId == "console-system/system/state-backup")));
            Assert.Empty(rig.Recorder.Started);   // in the console's process
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { /* released later */ }
        }
    }

    [Fact]
    public async Task Without_a_backup_folder_there_is_no_backup_task()
    {
        var rig = await Start("sqlite");
        Assert.True(await rig.Eventually(() => rig.Runner.Host?.Control is not null));
        var (status, _) = await rig.Call(HttpMethod.Post, "/api/embedded-runner/tasks/console-system/system/state-backup/run", rig.Admin);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task The_evaluation_task_is_listed_and_says_it_is_a_placeholder_when_run()
    {
        var rig = await Start("sqlite");
        Assert.True(await rig.Eventually(() => rig.Runner.Host?.Control is not null));

        var (status, _) = await rig.Call(HttpMethod.Post, "/api/embedded-runner/tasks/console-system/system/evaluation/run", rig.Admin);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.True(await rig.Eventually(() => new RunStore(rig.State, RunnerNames.EngineService).Read(new RunQuery(RunnerNames.EngineService, Package: ConsoleSystemTasks.Package)).Any(r => r.Run.Outcome == RunOutcome.Failed && r.Run.Failure!.Message.Contains("plan 27"))));
    }

    // ---- the servers list and the screens' API

    [Fact]
    public async Task The_embedded_runner_is_in_the_servers_list_cannot_be_forgotten_and_the_runner_screens_api_answers_with_the_roles()
    {
        var rig = await Start("sqlite");
        Publish(rig, "etl", "dev", ("one", null));

        var (_, list) = await rig.Call(HttpMethod.Get, "/api/instances", rig.Admin);
        var entry = Assert.Single(list!.AsArray());
        Assert.Equal("console-runner", (string)entry!["id"]!);
        Assert.Equal("dbdatarunner", (string)entry["product"]!);
        Assert.True((bool)entry["embedded"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Call(HttpMethod.Delete, "/api/instances/console-runner", rig.Admin)).Status);

        foreach (var prefix in new[] { "/api/embedded-runner", "/relay/console-runner/api/v1" })
        {
            var read = await rig.Call(HttpMethod.Get, $"{prefix}/status", rig.Viewer);
            Assert.Equal(HttpStatusCode.OK, read.Status);
            Assert.Contains(read.Body!["tasks"]!.AsArray(), t => (string)t!["task"]! == "one");
            Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Get, $"{prefix}/runs", rig.Viewer)).Status);
            Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Get, $"{prefix}/instances", rig.Viewer)).Status);
            Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Get, $"{prefix}/definitions/integration", rig.Viewer)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Post, $"{prefix}/tasks/etl/dev/one/run", rig.Viewer)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Post, $"{prefix}/tasks/etl/dev/one/pause", rig.Viewer)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Call(HttpMethod.Get, $"{prefix}/status", null)).Status);
        }

        var run = await rig.Call(HttpMethod.Post, "/relay/console-runner/api/v1/tasks/etl/dev/one/run", rig.Admin);
        Assert.Equal(HttpStatusCode.Accepted, run.Status);
        Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Post, "/relay/console-runner/api/v1/tasks/etl/dev/one/pause", rig.Admin)).Status);
        var recorded = new AuditLog(rig.State, "dbdatakit").Read(new AuditQuery(VerbPrefix: "runner.task."));
        Assert.Contains(recorded, r => r.Entry.Verb == RunnerControl.RunVerb && r.Entry.Outcome == AuditEvent.Ok && r.Entry.ViaKind == "console");
        Assert.Contains(recorded, r => r.Entry.Verb == RunnerControl.RunVerb && r.Entry.Outcome == AuditEvent.Refused);

        // what is not a management call is not relayed: 403 from the console, whoever is signed in
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Post, "/relay/console-runner/api/v1/webhooks/etl/dev/shop", rig.Admin, new { })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Delete, "/relay/console-runner/api/v1/bindings/dev/integration/etl", rig.Admin)).Status);
    }

    [Fact]
    public async Task An_administrator_binds_slots_and_sets_require_signatures_on_the_embedded_runner_a_viewer_is_refused_and_both_are_audited()
    {
        var rig = await Start("sqlite");
        Publish(rig, "etl", "dev", ("one", null));
        const string bind = "/relay/console-runner/api/v1/bindings/dev/integration/etl";

        var refused = await rig.Call(HttpMethod.Put, bind, rig.Viewer, new { slots = new { source = "erp" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        var bound = await rig.Call(HttpMethod.Put, bind, rig.Admin, new { slots = new { source = "erp" } });
        Assert.Equal(HttpStatusCode.OK, bound.Status);
        var (_, detail) = await rig.Call(HttpMethod.Get, "/relay/console-runner/api/v1/definitions/integration/etl", rig.Viewer);
        Assert.Contains(detail!["bindings"]!.AsArray(), b => (string)b!["slot"]! == "source" && (string)b["bound_to"]! == "erp" && (string)b["channel"]! == "dev");

        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Put, "/relay/console-runner/api/v1/settings/require_signatures", rig.Viewer, new { value = true })).Status);
        Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Put, "/relay/console-runner/api/v1/settings/require_signatures", rig.Admin, new { value = true })).Status);
        var (_, settings) = await rig.Call(HttpMethod.Get, "/relay/console-runner/api/v1/settings", rig.Viewer);
        Assert.True((bool)settings!["require_signatures"]!);

        var audit = new AuditLog(rig.State, "dbdatakit").Read(new AuditQuery(VerbPrefix: "runner."));
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.BindVerb && r.Entry.Outcome == AuditEvent.Ok && r.Entry.ViaKind == "console");
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.BindVerb && r.Entry.Outcome == AuditEvent.Refused);
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.SettingVerb && r.Entry.Outcome == AuditEvent.Ok);
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.SettingVerb && r.Entry.Outcome == AuditEvent.Refused);
        Assert.All(audit.Where(r => r.Entry.Verb == RunnerPublishing.BindVerb), r => Assert.DoesNotContain("erp", r.Entry.Detail));
    }

    [Fact]
    public async Task A_package_is_published_from_files_through_the_relay_on_the_embedded_runner_and_a_wrong_hash_or_a_viewer_is_refused()
    {
        var rig = await Start("sqlite");
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var path = "/relay/console-runner/api/v1/artifacts/" + sha;
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Call(HttpMethod.Head, path, rig.Admin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Put, path, rig.Viewer, raw: bytes)).Status);
        var wrong = await rig.Call(HttpMethod.Put, "/relay/console-runner/api/v1/artifacts/" + new string('a', 64), rig.Admin, raw: bytes);   // the bytes are not the hash named
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains(sha, (string)wrong.Body!["error"]!);

        var stored = await rig.Call(HttpMethod.Put, path, rig.Admin, raw: bytes);
        Assert.Equal(HttpStatusCode.OK, stored.Status);
        Assert.False((bool)stored.Body!["already_stored"]!);
        Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Head, path, rig.Admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Head, path, rig.Viewer)).Status);   // a viewer may ask whether the runner has a file

        var body = new { manifest = Manifest("etl2", [("one", null)]), channel = "dev", mode = "stage", artifacts = new[] { new { path = "notes.txt", sha256 = sha, size = bytes.Length } } };
        var definition = "/relay/console-runner/api/v1/definitions/integration/etl2";
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Put, definition, rig.Viewer, body)).Status);
        var published = await rig.Call(HttpMethod.Put, definition, rig.Admin, body);
        Assert.Equal(HttpStatusCode.OK, published.Status);
        Assert.Equal("stage", (string)published.Body!["mode"]!);
        Assert.Equal(1, (int)published.Body["version"]!);
        var (_, detail) = await rig.Call(HttpMethod.Get, "/relay/console-runner/api/v1/definitions/integration/etl2", rig.Viewer);
        // the runner's own staging follower looks every few moments and activates a staged version nobody objects to (no run in flight, no losses): on a slow machine (Windows CI) the GET comes after that,
        // so the channel holds version 1 either as staged or, when the follower was first, as active
        var channel = detail!["channels"]![0]!;
        Assert.True(channel["staged"] is { } staged ? (int)staged == 1 : (int?)channel["active"] == 1, $"version 1 is neither staged nor active: {channel}");

        var audit = new AuditLog(rig.State, "dbdatakit").Read(new AuditQuery(VerbPrefix: "runner."));
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.UploadVerb && r.Entry.Outcome == AuditEvent.Ok && r.Entry.ViaKind == "console");
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.UploadVerb && r.Entry.Outcome == AuditEvent.Refused);
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.PublishVerb && r.Entry.Outcome == AuditEvent.Ok && r.Entry.TargetId == "etl2/dev");
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.PublishVerb && r.Entry.Outcome == AuditEvent.Refused);
    }

    [Fact]
    public async Task An_administrator_labels_the_embedded_runners_instance_through_the_relay_a_viewer_is_refused_and_the_audit_is_the_consoles()
    {
        var rig = await Start("sqlite");
        Assert.True(await rig.Eventually(() => rig.Runner.Host?.InstanceId is not null));
        var path = $"/relay/console-runner/api/v1/instances/{rig.Runner.Host!.InstanceId}/labels";

        var read = await rig.Call(HttpMethod.Get, path, rig.Viewer);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Contains(read.Body!["labels"]!.AsArray(), l => (string)l!["source"]! == "reported");
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Put, path, rig.Viewer, new { labels = new[] { "gpu" } })).Status);
        var set = await rig.Call(HttpMethod.Put, path, rig.Admin, new { labels = new[] { "gpu" } });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal(["gpu"], set.Body!["added"]!.AsArray().Select(l => (string)l!));
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Call(HttpMethod.Put, path, rig.Admin, new { labels = new[] { "not a label" } })).Status);

        var audit = new AuditLog(rig.State, "dbdatakit").Read(new AuditQuery(VerbPrefix: "runner.label."));
        Assert.Contains(audit, r => r.Entry.Verb == "runner.label.add" && r.Entry.Outcome == AuditEvent.Ok && r.Entry.ViaKind == "console");
        Assert.Contains(audit, r => r.Entry.Verb == "runner.label.set" && r.Entry.Outcome == AuditEvent.Refused);
    }

    /// <summary>A path the route's pattern matches, on the embedded runner's own address.</summary>
    private static string EmbeddedPath(ServerProducts.RunnerRoute route, Guid instance) =>
        "/relay/console-runner/" + (route.Pattern.StartsWith("api/v1/bindings/{name}/", StringComparison.Ordinal) ? "api/v1/bindings/dev/" + route.Pattern["api/v1/bindings/{name}/".Length..] : route.Pattern).Replace("{name}", "etl").Replace("{sha}", new string('a', 64)).Replace("{id}", instance.ToString());

    /// <summary>Every route of the table but the upgrade of a server by itself (plan 24a UC09): the embedded runner is a part of the console and is upgraded with it, so only a standalone runner server mounts <c>api/v1/upgrade</c> (<c>DbDataRunner.Server</c>, through <c>UpgradeSetup</c>). No test yet pins the roles of that route to the table's (the runner test harness does not mount it): a follow-up.</summary>
    public static TheoryData<string> RouteRows => [.. ServerProducts.RunnerRoutes.Where(r => !r.Pattern.StartsWith("api/v1/state", StringComparison.Ordinal) && r.Pattern is not ("api/v1/upgrade" or "api/v1/certificate" or "api/v1/secrets/overview" or "api/v1/bindings/{name}/integration/{name}/test")).Select(r => $"{r.Method} {r.Pattern}")];

    [Theory]
    [MemberData(nameof(RouteRows))]
    public async Task Every_route_of_the_relays_table_is_the_embedded_runners_with_the_role_the_table_names_a_viewer_is_refused_by_the_runner_above_reading_and_an_administrator_is_not(string row)
    {
        var route = ServerProducts.RunnerRoutes.Single(r => $"{r.Method} {r.Pattern}" == row);
        var rig = await Start("sqlite");
        Assert.True(await rig.Eventually(() => rig.Runner.Host?.InstanceId is not null));
        var path = EmbeddedPath(route, rig.Runner.Host!.InstanceId!.Value);
        object? body = route.Method switch
        {
            "PUT" when route.Pattern.EndsWith("definitions/integration/{name}", StringComparison.Ordinal) => new { manifest = Manifest("etl", [("one", null)]), channel = "dev", mode = "stage", artifacts = Array.Empty<object>() },
            "PUT" when route.Pattern.Contains("/bindings/", StringComparison.Ordinal) => new { slots = new { source = "erp" } },
            "PUT" when route.Pattern.EndsWith("require_signatures", StringComparison.Ordinal) => new { value = false },
            "PUT" when route.Pattern.EndsWith("/labels", StringComparison.Ordinal) => new { labels = new[] { "gpu" } },
            "PUT" when route.Pattern.EndsWith("/owner", StringComparison.Ordinal) => new { owner = "runner", reason = "table test" },
            "POST" => new { channel = "dev" },
            _ => null,
        };

        var viewer = await rig.Call(new HttpMethod(route.Method), path, rig.Viewer, body);
        if (route.Role == ServerRole.Viewer) Assert.NotEqual(HttpStatusCode.Forbidden, viewer.Status);
        else Assert.Equal(HttpStatusCode.Forbidden, viewer.Status);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await rig.Call(new HttpMethod(route.Method), path, rig.Admin, body)).Status);
    }

    // ---- promotion between groups (plan 30 PB06): the embedded runner answered in this process, the other group through the relay

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_package_is_promoted_from_the_embedded_runner_to_another_runner_and_back_as_the_signed_in_person(string engine)
    {
        var rig = await Start(engine);
        var other = new FakeRunnerGroup();
        await using var otherServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-other", map: other.Map);
        await rig.App.Services.GetRequiredService<ConsoleInstances>().PairAsync(otherServer.Url, otherServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Other runner", default);

        // a version with a file is served on dev of the embedded runner
        var bytes = System.Text.Encoding.UTF8.GetBytes("promoted bytes");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.Equal(HttpStatusCode.OK, (await rig.Call(HttpMethod.Put, "/relay/console-runner/api/v1/artifacts/" + sha, rig.Admin, raw: bytes)).Status);
        var manifest = Manifest("etl3", [("one", null)]);
        var published = await rig.Call(HttpMethod.Put, "/relay/console-runner/api/v1/definitions/integration/etl3", rig.Admin,
            new { manifest, channel = "dev", mode = "serve", artifacts = new[] { new { path = "notes.txt", sha256 = sha, size = bytes.Length } } });
        Assert.Equal(HttpStatusCode.OK, published.Status);

        // out: read from the embedded runner in this process, written to the other group over the relay
        var out_ = await rig.Call(HttpMethod.Post, "/api/promotions", rig.Admin, new { from = "console-runner", to = "runner-other", name = "etl3", fromChannel = "dev", toChannel = "prod" });
        Assert.Equal(HttpStatusCode.OK, out_.Status);
        Assert.Equal(1, (int)out_.Body!["sourceVersion"]!);
        Assert.Equal(1, (int)out_.Body["sent"]!);
        Assert.Equal(bytes, other.Files[sha]);
        Assert.Equal(manifest, other.Published["prod"].Manifest);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Call(HttpMethod.Post, "/api/promotions", rig.Viewer, new { from = "console-runner", to = "runner-other", name = "etl3", fromChannel = "dev", toChannel = "prod" })).Status);

        // in: a version of the other group is written to the embedded runner (here, a channel it serves)
        other.Versions[7] = (Manifest("etl4", [("one", null)]), [("data.bin", bytes)], null);
        other.Active["test"] = 7;
        var inward = await rig.Call(HttpMethod.Post, "/api/promotions", rig.Admin, new { from = "runner-other", to = "console-runner", name = "etl4", fromChannel = "test", toChannel = "dev", mode = "serve" });
        Assert.Equal(HttpStatusCode.OK, inward.Status);
        Assert.Equal(7, (int)inward.Body!["sourceVersion"]!);
        Assert.Equal(0, (int)inward.Body["sent"]!);   // the embedded runner already holds the file by its hash
        var (_, detail) = await rig.Call(HttpMethod.Get, "/relay/console-runner/api/v1/definitions/integration/etl4", rig.Viewer);
        Assert.Equal(1, (int)detail!["channels"]![0]!["active"]!);

        // the embedded runner records the person, through the console, as the actor of both
        var audit = new AuditLog(rig.State, "dbdatakit").Read(new AuditQuery(VerbPrefix: "runner."));
        Assert.Contains(audit, r => r.Entry.Verb == RunnerPublishing.PublishVerb && r.Entry.Outcome == AuditEvent.Ok && r.Entry.TargetId == "etl4/dev" && r.Entry.ViaKind == "console");

        // a channel the embedded runner does not serve is refused in its own words
        var refused = await rig.Call(HttpMethod.Post, "/api/promotions", rig.Admin, new { from = "runner-other", to = "console-runner", name = "etl4", fromChannel = "test", toChannel = "qa" });
        Assert.True(refused.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden, $"{refused.Status}");
        Assert.Contains("console's runner refused", (string)refused.Body!["error"]!);
    }

    // ---- the stop

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Stopping_the_console_stops_the_runner_and_leaves_no_run_running_and_no_instance_live(string engine)
    {
        var rig = await Start(engine);
        rig.Recorder.UntilCancelled = true;
        Publish(rig, "etl", "dev", ("one", TimeSpan.FromSeconds(1)));
        Assert.True(await rig.Eventually(() => rig.Recorder.Started.Count > 0), "a run is going");
        var runs = new RunStore(rig.State, RunnerNames.EngineService);
        Assert.Contains(runs.Read(new RunQuery(RunnerNames.EngineService, Package: "etl")), r => r.Run.Outcome == RunOutcome.Running);

        await rig.App.StopAsync();

        Assert.DoesNotContain(runs.Read(new RunQuery(RunnerNames.EngineService)), r => r.Run.Outcome == RunOutcome.Running);
        Assert.Empty(new RunnerInstances(rig.State).LiveInstances(TimeSpan.FromMinutes(5)));
    }
}
