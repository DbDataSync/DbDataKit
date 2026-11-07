using DbDataKit.Runner.Api;
using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Limits;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28a R10 end to end: a real <c>dbdatarunner</c> server over a state on SQLite, PostgreSQL and SQL Server (the last two when the shared test engines are up), a package installed into it, its tasks run on their schedule in real task
/// host processes, and a paired console reading and acting through the management API.
/// </summary>
public sealed class RunnerServerTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Text(JsonNode? node, string key) => (string)node![key]!;

    [Theory, MemberData(nameof(Engines))]
    public async Task A_package_runs_on_its_schedule_and_a_paired_client_reads_the_runs(string engine)
    {
        await using var h = new RunnerHarness(engine);
        var installed = h.Install(SamplePackage.Task("ok", TimeSpan.FromSeconds(1)));
        Assert.Equal(("sample", "dev", 1, true), (installed.Package, installed.Channel, installed.Version, installed.Changed));
        await h.StartAsync();

        var runs = await h.WaitAsync(async () => await h.RunsAsync("?package=sample&channel=dev&outcome=succeeded") is { Count: >= 2 } r ? r : null, "two successful runs on the schedule");

        foreach (var run in runs)
        {
            Assert.Equal(("sample", "dev", "ok", "1", "succeeded"), (Text(run, "package"), Text(run, "channel"), Text(run, "task"), Text(run, "package_version"), Text(run, "outcome")));
            Assert.Equal(1, (int)run!["attempt"]!);
            Assert.NotNull(run["finished"]);
        }

        Assert.Contains(h.Runs.Logs(Guid.Parse(Text(runs[0], "id"))), l => l.Message == "hello from ok");
        Assert.Empty(await h.RunsAsync("?package=other"));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/v1/runs?task=ok")).Status);   // a task needs its package and channel
        Assert.NotEmpty(await h.RunsAsync("?package=sample&channel=dev&task=ok&limit=1"));

        var (_, status) = await h.GetAsync("/api/v1/status");
        var task = Assert.Single(status!["tasks"]!.AsArray());
        Assert.Equal(("sample", "dev", "ok", "1"), (Text(task, "package"), Text(task, "channel"), Text(task, "task"), Text(task, "version")));
        Assert.Equal("succeeded", Text(task!["last_run"], "outcome"));
        Assert.NotNull(task["next_due"]);
        Assert.Equal(engine == StateEngineIds.Sqlite, File.Exists(Path.Combine(h.Directory, RunnerState.FileName)));   // a server engine keeps nothing in the directory
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_status_call_reports_the_engine_the_channels_the_capabilities_and_the_instance(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();

        var (code, status) = await h.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(("dbdatarunner", RunnerServer.Version, engine, h.InstanceId), (Text(status, "product"), Text(status, "version"), Text(status, "state_engine"), Text(status, "instance_id")));
        Assert.Equal(["dev"], status!["channels"]!.AsArray().Select(c => (string)c!));
        var detected = LimitCapabilities.Detect();
        var reported = status["capabilities"]!.AsObject();
        Assert.Equal(detected[LimitCapabilities.Os], Text(reported, LimitCapabilities.Os));
        Assert.Equal(detected[LimitCapabilities.MemoryLimit], Text(reported, LimitCapabilities.MemoryLimit));
        Assert.Equal(detected[LimitCapabilities.CpuLimit], Text(reported, LimitCapabilities.CpuLimit));
        Assert.True(reported.ContainsKey(LimitCapabilities.Network));

        // the instance is registered with the same capabilities, and its heartbeat is renewed
        var instance = Guid.Parse(Text(status, "runner_instance"));
        var instances = new RunnerInstances(h.State);
        Assert.Equal(RunnerInstance.Running, instances.Get(instance)!.State);
        Assert.Equal(detected[LimitCapabilities.Os], instances.Capabilities(instance).Single(c => c.Name == LimitCapabilities.Os).Value);
        var first = instances.Get(instance)!.HeartbeatAt;
        await h.WaitAsync(() => Task.FromResult(instances.Get(instance)!.HeartbeatAt > first ? "renewed" : null), "a renewed heartbeat", 20);
        Assert.Single(instances.ListLive(h.Settings.InstanceLease));
    }

    [Fact]
    public async Task Every_call_needs_a_token_but_health_does_not()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Get, "/api/v1/status", ServerRole.Viewer, token: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Post, "/api/v1/tasks/sample/dev/ok/run", ServerRole.Operator, token: false)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Get, "/health/live", ServerRole.Viewer, token: false)).Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Run_now_pause_and_resume_work_through_the_api_and_the_audit_records_them(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(SamplePackage.Task("allocates"));   // no schedule: it runs when a person says so
        await h.StartAsync();

        var (run, started) = await h.PostAsync("/api/v1/tasks/sample/dev/allocates/run", body: new { inputs = new { megabytes = "25" } }, subject: "ada");
        Assert.Equal(HttpStatusCode.Accepted, run);
        var runId = Text(started, "run_id");
        var finished = await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&task=allocates&channel=dev")).FirstOrDefault(r => Text(r, "id") == runId && r!["finished"] is not null), "the run that was asked for");
        Assert.True(Text(finished, "outcome") == "succeeded", string.Join(" | ", h.Runs.Logs(Guid.Parse(runId)).Select(l => l.Message)) + " // " + finished!["failure"]);
        Assert.Equal("allocates", Text(finished, "task"));
        Assert.Contains(h.Runs.Logs(Guid.Parse(runId)), l => l.Message == "allocated 25 MB without being stopped");   // the input reached the process

        var (pause, paused) = await h.PostAsync("/api/v1/tasks/sample/dev/allocates/pause", subject: "bob");
        Assert.Equal((HttpStatusCode.OK, true), (pause, (bool)paused!["paused"]!));
        var task = (await h.GetAsync("/api/v1/status")).Body!["tasks"]![0]!;
        Assert.Equal((true, "Bob"), ((bool)task["paused"]!, Text(task, "paused_by")));

        var (resume, resumed) = await h.PostAsync("/api/v1/tasks/sample/dev/allocates/resume", ServerRole.Admin, subject: "cy");   // an administrator is an operator too
        Assert.Equal((HttpStatusCode.OK, false), (resume, (bool)resumed!["paused"]!));
        Assert.False((bool)(await h.GetAsync("/api/v1/status")).Body!["tasks"]![0]!["paused"]!);

        var entries = h.Audit.Read(new AuditQuery(VerbPrefix: "runner.task.")).OrderBy(e => e.Id).ToList();
        Assert.Equal([RunnerControl.RunVerb, RunnerControl.PauseVerb, RunnerControl.ResumeVerb], entries.Select(e => e.Entry.Verb));
        Assert.Equal(["ada", "bob", "cy"], entries.Select(e => e.Entry.ActorId));
        Assert.All(entries, e => Assert.Equal(("person", "console", "sample/dev/allocates", AuditEvent.Ok, RunnerState.ServiceId), (e.Entry.ActorKind, e.Entry.ViaKind, e.Entry.TargetId, e.Entry.Outcome, e.ServiceId)));
        Assert.Contains(runId, entries[0].Entry.Detail);
        Assert.Contains("megabytes", entries[0].Entry.Detail);   // the names of the inputs, never their values
        Assert.DoesNotContain("\"25\"", entries[0].Entry.Detail);
    }

    [Fact]
    public async Task A_paused_task_is_not_started_on_its_schedule_until_it_is_resumed()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok", TimeSpan.FromSeconds(1)));
        await h.StartAsync();
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample")).Count >= 1 ? "first" : null, "the first run");

        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync("/api/v1/tasks/sample/dev/ok/pause")).Status);
        await h.WaitAsync(async () => (await h.GetAsync("/api/v1/status")).Body!["tasks"]![0]!["running"]!.GetValue<int>() == 0 ? "idle" : null, "the run in flight to end");
        var count = (await h.RunsAsync("?package=sample")).Count;
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(count, (await h.RunsAsync("?package=sample")).Count);

        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync("/api/v1/tasks/sample/dev/ok/resume")).Status);
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample")).Count > count ? "more" : null, "runs again after the resume");
    }

    [Fact]
    public async Task A_viewer_can_read_but_not_run_pause_or_resume_and_the_refusals_are_audited()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/v1/status")).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.GetAsync("/api/v1/runs")).Status);
        foreach (var act in new[] { "run", "pause", "resume" })
        {
            var (status, body) = await h.PostAsync($"/api/v1/tasks/sample/dev/ok/{act}", ServerRole.Viewer, subject: "vera");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Contains("operator", Text(body, "error"));
        }

        Assert.False((bool)(await h.GetAsync("/api/v1/status")).Body!["tasks"]![0]!["paused"]!);   // nothing changed
        Assert.Empty(await h.RunsAsync());
        Assert.All(h.Audit.Read(new AuditQuery(VerbPrefix: "runner.task.")), e => Assert.Equal((AuditEvent.Refused, "vera"), (e.Entry.Outcome, e.Entry.ActorId)));
        Assert.Equal(3, h.Audit.Read(new AuditQuery(VerbPrefix: "runner.task.")).Count);
    }

    [Fact]
    public async Task A_task_that_is_not_served_is_not_found_a_bad_name_is_refused_and_a_task_running_is_not_started_twice()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("waits"));
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync("/api/v1/tasks/sample/dev/nothing/run")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync("/api/v1/tasks/sample/prod/waits/pause")).Status);   // a channel this runner does not serve
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/tasks/Sample/dev/waits/run")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/tasks/sample/dev/waits/run", body: new { inputs = 3 })).Status);

        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/tasks/sample/dev/waits/run")).Status);
        var second = await h.WaitAsync(async () => await h.PostAsync("/api/v1/tasks/sample/dev/waits/run") is { Status: HttpStatusCode.Conflict } c ? c.Body : null, "a refusal of a second run");
        Assert.False((bool)second!["queued"]!);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_graceful_shutdown_leaves_the_instance_gone_and_no_run_running(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(SamplePackage.Task("waits"));
        await h.StartAsync();
        var instance = Guid.Parse(Text((await h.GetAsync("/api/v1/status")).Body, "runner_instance"));
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/tasks/sample/dev/waits/run")).Status);
        var runId = Guid.Parse(Text(await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&outcome=running")).FirstOrDefault(), "the run to start"), "id"));
        await h.WaitAsync(() => Task.FromResult(h.Runs.Logs(runId).Any(l => l.Message == "ready") ? "ready" : null), "the task to be ready");

        await h.StopAsync();

        Assert.Equal(RunnerInstance.Gone, new RunnerInstances(h.State).Get(instance)!.State);
        Assert.Empty(new RunnerInstances(h.State).ListLive(TimeSpan.FromMinutes(5)));
        Assert.Empty(h.Runs.Read(new RunQuery(RunnerState.EngineService, Outcome: RunOutcome.Running)));
        Assert.Equal(RunOutcome.Stopped, h.Runs.Get(runId)!.Run.Outcome);
        Assert.Empty(System.IO.Directory.EnumerateDirectories(h.Settings.ScratchDirectory));
    }

    [Fact]
    public async Task A_restart_marks_the_runs_a_dead_process_left_as_interrupted_and_registers_a_new_instance()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        // the state of a process that died in the middle of a run: a run still marked running and an instance still marked live
        var stale = new RunStore(h.State, RunnerState.EngineService).Start("sample/dev/ok", package: "sample", packageVersion: "1", channel: "dev", attempt: 1);
        var old = new RunnerInstances(h.State).Register(RunnerState.ServiceId, "gone-host", "0.0.1");
        await Task.Delay(h.Settings.InstanceLease + TimeSpan.FromMilliseconds(500));   // a live row would refuse the start: SQLite is one runner's; this one's lease ran out

        await h.StartAsync();

        Assert.Equal(RunOutcome.Interrupted, h.Runs.Get(stale)!.Run.Outcome);
        var live = new RunnerInstances(h.State).ListLive(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(live, i => i.Id == old);                                  // the dead process's row is cleared at the start
        Assert.Null(new RunnerInstances(h.State).Get(old));
        Assert.Contains(live, i => i.Host == Environment.MachineName);
    }

    [Fact]
    public void The_task_host_is_found_beside_the_server_in_its_build_output()
    {
        var folder = Path.Combine(ServerBuild.Directory, RunnerTaskHost.Folder);

        var found = RunnerTaskHost.Find(ServerBuild.Directory);

        Assert.Equal(Path.Combine(folder, "dbdatakit-taskhost.dll"), found);
        foreach (var file in new[] { "dbdatakit-taskhost.dll", "dbdatakit-taskhost.deps.json", "dbdatakit-taskhost.runtimeconfig.json", "DbDataKit.Runner.Kit.dll", "DbDataKit.Runner.Protocol.dll" })
            Assert.True(File.Exists(Path.Combine(folder, file)), $"{file} is not in {folder}");
        // plain .NET, not the ASP.NET shared framework the server itself runs on
        Assert.DoesNotContain("AspNetCore", File.ReadAllText(Path.Combine(folder, "dbdatakit-taskhost.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(ServerBuild.Directory, "dbdatarunner.dll")));
        Assert.Equal(found, RunnerTaskHost.Require(null, ServerBuild.Directory));
    }

    [Fact]
    public void A_server_without_its_task_host_is_refused_naming_where_it_looked()
    {
        var empty = Directory.CreateTempSubdirectory("dbdr-empty-").FullName;
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(() => RunnerTaskHost.Require(null, empty));

            Assert.Contains(Path.Combine(empty, "taskhost"), refused.Message);
            Assert.Contains("dbdatakit-taskhost", refused.Message);
            Assert.Throws<InvalidOperationException>(() => RunnerTaskHost.Require(Path.Combine(empty, "nowhere.dll"), empty));
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    [Fact]
    public async Task The_server_listens_on_https_only()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);

        var refused = Assert.Throws<ArgumentException>(() => RunnerServer.Build(h.Settings with { Listen = new Uri("http://127.0.0.1:0") }, RunnerServer.Certificate(h.Directory), _ => { }, h.State));

        Assert.Contains("HTTPS", refused.Message);
    }
}
