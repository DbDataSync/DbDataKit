using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>The Build server running refresh plans on their declared schedules (plan 13, M08), on a clock the test moves and a runner that records what it was asked to run.</summary>
public sealed class BuildSchedulerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-sched-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> log = [];
    private readonly Clock clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly Recorder runner = new();
    private readonly string project;
    private readonly ProjectsFile projects;
    private readonly BuildScheduler scheduler;

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void To(DateTimeOffset when) => _now = when;
    }

    private sealed class Recorder : IProjectCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public int Exit { get; set; }
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken)
        {
            Calls.Add(argv);
            return Task.FromResult(new CommandResult(Exit, "{}", ""));
        }
    }

    public BuildSchedulerTests()
    {
        // a project whose "hourly" plan runs at quarter past, and whose "quiet" plan has no schedule
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n",
            "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n    quiet: {}\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        Directory.CreateDirectory(root);
        projects = new ProjectsFile(root);
        projects.Add(new HostedProject("sales", project, null, null));
        scheduler = new BuildScheduler(projects, runner, BuildSchedulerSchema.Open(root), clock, log.Add);
    }

    public void Dispose()
    {
        foreach (var d in new[] { root, project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 25, hour, minute, 0, TimeSpan.Zero);

    private async Task<bool> RunNext()
    {
        if (scheduler.Claim("test") is not var (item, hosted)) return false;
        await scheduler.RunAsync(item, hosted, "test", CancellationToken.None);
        return true;
    }

    [Fact]
    public void Only_a_plan_that_declares_a_schedule_is_a_job()
    {
        var jobs = RefreshJobs.Discover(project);

        var job = Assert.Single(jobs);
        Assert.Equal(("sqlserver", "hourly", "15 * * * *", "UTC"), (job.Connection, job.Plan, job.Schedule.Cron, job.Schedule.TimeZoneId));
    }

    [Fact]
    public async Task A_job_seen_for_the_first_time_waits_for_its_next_time_and_then_runs_once()
    {
        Assert.Empty(scheduler.Tick());                       // 12:00, first sight: noted, not run
        clock.To(At(12, 10));
        Assert.Empty(scheduler.Tick());                       // before the quarter past

        clock.To(At(12, 16));
        var queued = scheduler.Tick();
        Assert.Equal("hourly", Assert.Single(queued).Job.Plan);

        Assert.True(await RunNext());
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["connection", "refresh", "sqlserver", "hourly", "--project", project], call);

        Assert.False(await RunNext());                        // nothing else queued
        clock.To(At(12, 40));
        Assert.Empty(scheduler.Tick());                       // handled; the next is 13:15
        Assert.False(await RunNext());
    }

    [Fact]
    public async Task How_the_last_run_ended_is_kept_and_a_failure_is_logged()
    {
        scheduler.Tick();
        clock.To(At(12, 16));
        scheduler.Tick();
        runner.Exit = 1;

        await RunNext();

        var state = Assert.Single(scheduler.Jobs(projects.Find("sales")!)).State!;
        Assert.Equal(1, state.LastExit);
        Assert.Equal(At(12, 16), state.LastStarted);
        Assert.Contains(log, l => l.Contains("exit code 1") && l.Contains("sqlserver/hourly"));
    }

    [Fact]
    public void A_plan_whose_run_is_still_going_is_not_queued_a_second_time()
    {
        scheduler.Tick();
        clock.To(At(12, 16));
        scheduler.Tick();
        var patient = new BuildScheduler(projects, runner, BuildSchedulerSchema.Open(root), clock, log.Add, lease: TimeSpan.FromHours(3));
        Assert.NotNull(patient.Claim("test"));                // claimed, not finished, and its worker is alive (the lease holds)

        clock.To(At(13, 16));
        patient.Tick();                                        // due again while the first is still in flight

        Assert.Null(patient.Claim("test"));
    }

    [Fact]
    public void A_run_whose_worker_died_is_given_back_when_its_lease_runs_out()
    {
        scheduler.Tick();
        clock.To(At(12, 16));
        scheduler.Tick();
        Assert.NotNull(scheduler.Claim("worker-that-died"));
        Assert.Null(scheduler.Claim("another"));

        clock.To(At(12, 40));                                  // past the ten minutes, no heartbeat
        scheduler.Tick();

        Assert.NotNull(scheduler.Claim("another"));
    }

    [Fact]
    public async Task Times_that_passed_while_the_server_was_down_run_once()
    {
        scheduler.Tick();
        clock.To(At(18, 0));                                   // six quarter-hours missed

        Assert.Single(scheduler.Tick());
        await RunNext();
        Assert.Single(runner.Calls);
        Assert.False(await RunNext());
    }

    [Fact]
    public async Task What_the_scheduler_remembers_survives_a_restart()
    {
        scheduler.Tick();                                      // 12:00 noted
        clock.To(At(12, 16));
        var restarted = new BuildScheduler(projects, runner, BuildSchedulerSchema.Open(root), clock, log.Add);

        Assert.Single(restarted.Tick());                       // the new instance knew 12:00, so 12:15 is due
    }

    [Fact]
    public void A_project_whose_folder_is_gone_is_left_alone()
    {
        projects.Add(new HostedProject("gone", null, "https://example.invalid/x.git", null));      // registered, never cloned

        scheduler.Tick();
        clock.To(At(12, 16));
        Assert.Single(scheduler.Tick());                       // only sales
    }

    [Fact]
    public async Task The_schedule_is_listed_whether_or_not_the_server_runs_it()
    {
        // a server without the scheduler still tells a console what is scheduled, and says it is not running it
        var serverDir = Path.Combine(root, "server");
        Directory.CreateDirectory(serverDir);
        var serverProjects = new ProjectsFile(serverDir);
        serverProjects.Add(new HostedProject("sales", project, null, null));
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), runner, serverProjects, new TuiCommand.CliHost(_ => null).Commands, log.Add);
        await app.StartAsync();
        try
        {
            var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
            var url = new Uri(app.Urls.First());
            var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRole.Viewer);
            var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/sales/schedule");
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer)));

            var reply = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!;
            Assert.False((bool)body["enabled"]!);
            var job = body["jobs"]![0]!;
            Assert.Equal(("sqlserver", "hourly", "15 * * * *"), ((string)job["connection"]!, (string)job["plan"]!, (string)job["schedule"]!));
            Assert.NotNull(job["next_due"]);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_paused_plan_passes_its_times_by_and_resuming_does_not_run_what_was_missed()
    {
        var hosted = projects.Find("sales")!;
        var job = scheduler.Find(hosted, "sqlserver", "hourly")!;
        scheduler.Tick();                                      // 12:00 noted
        scheduler.Pause(hosted, job, "ada");
        Assert.Equal("ada", scheduler.PausedBy(hosted, job)!.Value.By);

        clock.To(At(12, 16));
        Assert.Empty(scheduler.Tick());                        // due, and passed over
        scheduler.Resume(hosted, job);
        Assert.Null(scheduler.PausedBy(hosted, job));
        Assert.Empty(scheduler.Tick());                        // resuming does not start the plan for 12:15

        clock.To(At(13, 16));
        Assert.Single(scheduler.Tick());                       // the next time runs
        Assert.True(await RunNext());
    }

    [Fact]
    public async Task Run_now_queues_the_plan_apart_from_its_schedule_even_when_paused_and_not_twice()
    {
        var hosted = projects.Find("sales")!;
        var job = scheduler.Find(hosted, "sqlserver", "hourly")!;
        scheduler.Pause(hosted, job, "ada");

        Assert.True(scheduler.RunNow(hosted, job));
        Assert.False(scheduler.RunNow(hosted, job));           // one is queued already

        Assert.True(await RunNext());
        Assert.Equal(["connection", "refresh", "sqlserver", "hourly", "--project", project], Assert.Single(runner.Calls));
        Assert.True(scheduler.RunNow(hosted, job));            // finished, so another may be asked for
        Assert.Null(scheduler.Find(hosted, "sqlserver", "quiet"));    // only a plan with a schedule is a job
    }

    [Fact]
    public async Task The_server_pauses_resumes_and_runs_a_plan_for_an_operator_only_and_only_when_it_runs_schedules()
    {
        var serverDir = Path.Combine(root, "server2");
        Directory.CreateDirectory(serverDir);
        var serverProjects = new ProjectsFile(serverDir);
        serverProjects.Add(new HostedProject("sales", project, null, null));
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var ownScheduler = new BuildScheduler(serverProjects, runner, BuildSchedulerSchema.Open(serverDir), clock, log.Add);
        var withScheduler = BuildServer.Build(settings, BuildServer.Certificate(serverDir), runner, serverProjects, new TuiCommand.CliHost(_ => null).Commands, log.Add, scheduler: ownScheduler);
        await withScheduler.StartAsync();
        try
        {
            var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
            var url = new Uri(withScheduler.Urls.First());
            var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator));
            var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
            async Task<HttpResponseMessage> Send(HttpMethod method, string path, ServerRole role)
            {
                using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
                using var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", role)));
                return await http.SendAsync(request);
            }
            const string plan = "/api/v1/projects/sales/schedule/sqlserver/hourly/";

            Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, plan + "pause", ServerRole.Viewer)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/v1/projects/sales/schedule/sqlserver/quiet/pause", ServerRole.Operator)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, plan + "pause", ServerRole.Operator)).StatusCode);

            var listed = JsonNode.Parse(await (await Send(HttpMethod.Get, "/api/v1/projects/sales/schedule", ServerRole.Viewer)).Content.ReadAsStringAsync())!["jobs"]![0]!;
            Assert.True((bool)listed["paused"]!);
            Assert.Equal("Ada", (string)listed["paused_by"]!);

            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, plan + "run", ServerRole.Operator)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, plan + "run", ServerRole.Operator)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, plan + "resume", ServerRole.Operator)).StatusCode);
        }
        finally
        {
            await withScheduler.StopAsync();
            await withScheduler.DisposeAsync();
        }

        // a server that does not run schedules says so, and changes nothing
        var plainDir = Path.Combine(root, "server3");
        Directory.CreateDirectory(plainDir);
        var plainProjects = new ProjectsFile(plainDir);
        plainProjects.Add(new HostedProject("sales", project, null, null));
        var plainSettings = new BuildServerSettings(plainDir, new Uri("https://127.0.0.1:0"));
        var plain = BuildServer.Build(plainSettings, BuildServer.Certificate(plainDir), runner, plainProjects, new TuiCommand.CliHost(_ => null).Commands, log.Add);
        await plain.StartAsync();
        try
        {
            var console = new ConsoleIdentity("console-2", ConsoleSigningKey.Generate());
            var url = new Uri(plain.Urls.First());
            var code = new PairingFiles(plainSettings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator));
            var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects/sales/schedule/sqlserver/hourly/pause");
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Operator)));
            Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(request)).StatusCode);
        }
        finally
        {
            await plain.StopAsync();
            await plain.DisposeAsync();
        }
    }
}
