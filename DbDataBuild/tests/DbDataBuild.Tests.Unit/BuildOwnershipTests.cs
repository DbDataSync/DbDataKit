using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Runner.Client;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Tests.Unit;

/// <summary>A clock the tests move, shared by the Build scheduler, its state and (in the tests that have one) the runner's.</summary>
internal sealed class OwnershipClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void To(DateTimeOffset when) => _now = when;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>What a Build server's directory, a project with scheduled plans, a scheduler over it (reading the owners from <c>server.yaml</c> at every tick) and a run recorder come to.</summary>
internal sealed class OwnershipFixture : IDisposable
{
    public const string Connection = "sqlserver";

    private sealed class Recorder : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, "{}", ""));
    }

    private sealed class Observed(List<(string Project, string Plan, DateTimeOffset At, int Missed, bool Paused)> into) : IObservedDecisions
    {
        public void Report(string project, ScheduledJob job, DateTimeOffset at, int missed, bool paused) => into.Add((project, job.Plan, at, missed, paused));
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ddb-own-" + Guid.NewGuid().ToString("N")[..8]);
    public string Project { get; }
    public OwnershipClock Clock { get; } = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    public ProjectsFile Projects { get; }
    public StateDatabase State { get; }
    public BuildScheduler Scheduler { get; }
    public List<string> Log { get; } = [];
    public List<(string Project, string Plan, DateTimeOffset At, int Missed, bool Paused)> Reported { get; } = [];
    public List<AuditEntry> Audit { get; } = [];

    /// <param name="plans">The plans of the project as <c>name: cron | zone</c>, each running the one load.</param>
    public OwnershipFixture(params (string Name, string Cron, string Zone)[] plans)
    {
        if (plans.Length == 0) plans = [("hourly", "15 * * * *", "UTC")];
        var refresh = string.Join(", ", plans.Select(p => $"{p.Name}: incremental"));
        var config = "refresh:\n  plans:\n" + string.Concat(plans.Select(p => $"    {p.Name}: {{ schedule: \"{p.Cron}\", time_zone: {p.Zone} }}\n")) + "    quiet: {}\n";
        Project = RefreshPlansTests.Project($"refresh: {{ {refresh} }}\n", config);
        Assert.Equal(0, RefreshPlansTests.Compile(Project).Exit);
        Directory.CreateDirectory(Root);
        Projects = new ProjectsFile(Root);
        Projects.Add(new HostedProject("sales", Project, null, null));
        State = BuildSchedulerSchema.Open(Root, Clock);
        Scheduler = new BuildScheduler(Projects, new Recorder(), State, Clock, Log.Add, schedule: () => BuildScheduleSettings.Read(Root), observed: new Observed(Reported));
    }

    public static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    public string Subject(string plan) => $"{Connection}/{plan}";

    public ModuleJobStateStore Jobs => new(State, BuildSchedulerSchema.Service);

    public ModuleJobPauseStore Pauses => new(State, BuildSchedulerSchema.Service);

    public ModuleWorkQueueStore Queue => new(State, BuildSchedulerSchema.Service);

    public string OwnerInFile(string plan) => BuildScheduleSettings.Read(Root).OwnerOf("sales", Connection, plan);

    public ScheduleRunnerLink Link { get; } = new("prod-runner", "prod", null);

    public IAuditLog AuditLog { get; private set; } = null!;

    /// <summary>Ticks and runs everything queued to its end, as a server with a worker does; returns what the tick queued.</summary>
    public async Task<IReadOnlyList<(string Project, ScheduledJob Job)>> TickAndRun()
    {
        var queued = Scheduler.Tick();
        while (Scheduler.Claim("test") is var (item, hosted)) await Scheduler.RunAsync(item, hosted, "test", CancellationToken.None);
        return queued;
    }

    public BuildOwnership Ownership(IRunnerSchedule runner, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        AuditLog = new ListAudit(Audit);
        return new BuildOwnership(Root, Projects, State, Clock, runner, AuditLog, "ada", delay ?? ((_, _) => Task.CompletedTask));
    }

    private sealed class ListAudit(List<AuditEntry> into) : IAuditLog
    {
        public void Append(AuditEntry entry) => into.Add(entry);
        public IReadOnlyList<AuditEntry> Read(int limit = 100) => into;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();      // the state file is deleted below, and Windows will not delete a file a pooled connection holds
        foreach (var d in new[] { Root, Project }) try { Directory.Delete(d, true); } catch (IOException) { }
    }
}

/// <summary>The runner as the Build server's ownership code sees it, in memory: what it answers and what it was told, with hooks to act between two calls.</summary>
internal sealed class FakeRunnerSchedule : IRunnerSchedule
{
    public string? Version { get; set; } = "1";
    public string Owner { get; set; } = TaskOwners.Runner;
    public string? OwnerSetBy { get; set; }
    public DateTimeOffset? Handled { get; set; }
    public string? PausedBy { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public int Running { get; set; }
    public Exception? FailSetOwner { get; set; }
    public Func<int, Task>? OnScheduleOf { get; set; }
    public List<(string Owner, ClientSeed? Seed, string? Reason)> Told { get; } = [];
    public List<ClientDecision> Decisions { get; } = [];
    private int _asked;

    public async Task<ClientTaskSchedule> ScheduleOfAsync(string package, string task, CancellationToken ct)
    {
        if (OnScheduleOf is { } hook) await hook(++_asked);
        return new ClientTaskSchedule($"{package}/prod/{task}", Owner, OwnerSetBy, null, null, Handled, PausedBy is not null, PausedBy, PausedAt, Running, 0, Version);
    }

    public Task SetOwnerAsync(string package, string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct)
    {
        Told.Add((owner, seed, reason));
        if (FailSetOwner is { } failure) throw failure;
        Owner = owner;
        OwnerSetBy = "ada";
        if (seed is not null && owner == TaskOwners.Runner) (Handled, PausedBy, PausedAt) = (seed.HandledUpTo, seed.PausedBy, seed.PausedAt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClientDecision>> DecisionsAsync(string package, string task, DateTimeOffset? since, CancellationToken ct) => Task.FromResult<IReadOnlyList<ClientDecision>>([.. Decisions]);
}

/// <summary>Plan 28c E09, Build's half: the scheduler reading its owners, and the verbs that move one plan to a runner and back, against a runner that is faked so each step and its order can be seen.</summary>
public sealed class BuildOwnershipTests
{
    private static ScheduledJob? Job(OwnershipFixture f, string plan) => f.Scheduler.Find(f.Projects.Find("sales")!, OwnershipFixture.Connection, plan);

    // ---- the scheduler

    [Fact]
    public async Task A_plan_of_the_runner_is_left_alone_neither_queued_nor_moved_and_comes_back_from_the_position_it_had()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();                                                       // 12:00 noted
        BuildScheduleSettings.Write(f.Root, "sales", "sqlserver", "hourly", TaskOwners.Runner, f.Link);

        f.Clock.To(OwnershipFixture.At(25, 18));                                  // six quarter-pasts later
        Assert.Empty(f.Scheduler.Tick());
        Assert.Equal(OwnershipFixture.At(25, 12), f.Jobs.Get("sales", "sqlserver/hourly")!.HandledUpTo);

        BuildScheduleSettings.Write(f.Root, "sales", "sqlserver", "hourly", TaskOwners.Old);
        Assert.Single(await f.TickAndRun());                                      // back: the span passed meanwhile is one run, from the untouched position
    }

    [Fact]
    public async Task An_observed_plan_is_scheduled_as_before_and_each_decision_is_reported_with_its_pause()
    {
        using var f = new OwnershipFixture();
        BuildScheduleSettings.Write(f.Root, "sales", "sqlserver", "hourly", TaskOwners.Observe, f.Link);
        f.Scheduler.Tick();                                                       // 12:00 noted

        f.Clock.To(OwnershipFixture.At(25, 12, 16));
        Assert.Single(await f.TickAndRun());
        f.Scheduler.Pause(f.Projects.Find("sales")!, Job(f, "hourly")!, "bob");
        f.Clock.To(OwnershipFixture.At(25, 13, 16));
        Assert.Empty(f.Scheduler.Tick());                                         // paused: passed over

        Assert.Equal([("hourly", OwnershipFixture.At(25, 12, 16), false), ("hourly", OwnershipFixture.At(25, 13, 16), true)], f.Reported.Select(r => (r.Plan, r.At, r.Paused)));
    }

    [Fact]
    public void An_owner_written_between_the_first_look_and_the_queueing_is_honoured_and_the_position_is_not_moved()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        f.Clock.To(OwnershipFixture.At(25, 12, 16));
        var looks = 0;
        var late = new BuildScheduler(f.Projects, new NoRunner(), f.State, f.Clock, f.Log.Add, schedule: () => ++looks == 1 ? BuildScheduleSettings.Default : new BuildScheduleSettings(f.Link, new Dictionary<string, string> { ["sales/sqlserver/hourly"] = TaskOwners.Runner }));

        Assert.Empty(late.Tick());                                                // the cutover was written after the tick read the owners

        Assert.False(f.Queue.HasInFlight("sales", BuildScheduler.Kind, "sqlserver/hourly"));
        Assert.Equal(OwnershipFixture.At(25, 12), f.Jobs.Get("sales", "sqlserver/hourly")!.HandledUpTo);   // as the cutover will find it
    }

    private sealed class NoRunner : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, "{}", ""));
    }

    // ---- observe

    [Fact]
    public async Task Observe_needs_the_package_to_be_active_on_the_runner_and_changes_nothing_when_it_is_not()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var runner = new FakeRunnerSchedule { Version = null };

        var refused = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(runner).ObserveAsync("sales", "sqlserver", "hourly", f.Link, null));

        Assert.Contains("no active version of the package 'sales' with the task 'sqlserver-hourly' in channel 'prod'", refused.Message);
        Assert.Empty(runner.Told);
        Assert.False(File.Exists(Path.Combine(f.Root, "server.yaml")) && File.ReadAllText(Path.Combine(f.Root, "server.yaml")).Contains("schedule:"));
    }

    [Fact]
    public async Task Observe_hands_the_runner_a_copy_of_the_position_and_the_pause_and_records_the_owner_here()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        f.Scheduler.Pause(f.Projects.Find("sales")!, Job(f, "hourly")!, "bob");
        var runner = new FakeRunnerSchedule();

        var change = await f.Ownership(runner).ObserveAsync("sales", "sqlserver", "hourly", f.Link, "to compare");

        var (owner, seed, reason) = Assert.Single(runner.Told);
        Assert.Equal((TaskOwners.Observe, "to compare", OwnershipFixture.At(25, 12), "bob"), (owner, reason, seed!.HandledUpTo, seed.PausedBy));
        Assert.Equal(TaskOwners.Observe, f.OwnerInFile("hourly"));
        Assert.Equal((TaskOwners.Old, TaskOwners.Observe), (change.From, change.To));
        Assert.Equal("schedule.observe", Assert.Single(f.Audit).Command);
    }

    // ---- cutover

    [Fact]
    public async Task A_cutover_during_a_run_in_flight_waits_for_it_neither_runs_nor_skips_a_time_and_leaves_the_plan_scheduled_once()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        f.Clock.To(OwnershipFixture.At(25, 12, 16));
        f.Scheduler.Tick();                                                       // the 12:15 run is queued ...
        var (item, hosted) = f.Scheduler.Claim("worker")!.Value;                  // ... and claimed: in flight, not finished
        var runner = new FakeRunnerSchedule();
        var waits = 0;
        var ownership = f.Ownership(runner, async (_, ct) =>
        {
            if (waits++ > 0) return;
            Assert.Equal(TaskOwners.Runner, f.OwnerInFile("hourly"));             // this scheduler has let go while the run is waited for
            f.Clock.To(OwnershipFixture.At(25, 13, 20));                          // 13:15 comes and goes during the wait ...
            Assert.Empty(f.Scheduler.Tick());                                     // ... and is neither queued here ...
            Assert.Empty(runner.Told);                                            // ... nor handed over yet
            await f.Scheduler.RunAsync(item, hosted, "worker", ct);               // the run in flight ends
        });

        var change = await ownership.CutOverAsync("sales", "sqlserver", "hourly", f.Link, "go", TimeSpan.FromMinutes(30));

        var (owner, seed, _) = Assert.Single(runner.Told);
        Assert.Equal(TaskOwners.Runner, owner);
        Assert.Equal(OwnershipFixture.At(25, 12, 16), seed!.HandledUpTo);        // the position as the run in flight left it: the runner finds 13:15 due and runs it once, not skipped
        Assert.True(change.Waited);
        Assert.Equal(TaskOwners.Runner, f.OwnerInFile("hourly"));
        f.Clock.To(OwnershipFixture.At(25, 15, 0));
        Assert.Empty(await f.TickAndRun());                                       // never scheduled here again, so never by both
        Assert.Equal("schedule.cutover", Assert.Single(f.Audit).Command);
        Assert.Equal("ok", f.Audit[0].Outcome);
    }

    [Fact]
    public async Task A_cutover_whose_run_does_not_end_in_time_puts_the_owner_back_and_tells_the_runner_nothing()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        f.Clock.To(OwnershipFixture.At(25, 12, 16));
        f.Scheduler.Tick();
        Assert.NotNull(f.Scheduler.Claim("worker"));                              // in flight, and stays so
        var runner = new FakeRunnerSchedule();
        var ownership = f.Ownership(runner, (_, _) => { f.Clock.Advance(TimeSpan.FromMinutes(20)); return Task.CompletedTask; });

        var refused = await Assert.ThrowsAsync<OwnershipException>(() => ownership.CutOverAsync("sales", "sqlserver", "hourly", f.Link, null, TimeSpan.FromMinutes(30)));

        Assert.Contains("still queued or going", refused.Message);
        Assert.Empty(runner.Told);
        Assert.Equal(TaskOwners.Old, f.OwnerInFile("hourly"));                    // this scheduler looks at the plan again, from its own position
        Assert.Equal("failed", Assert.Single(f.Audit).Outcome);
    }

    [Fact]
    public async Task A_cutover_hands_over_the_pause_and_a_plan_never_seen_starts_from_now()
    {
        using var f = new OwnershipFixture();
        var hosted = f.Projects.Find("sales")!;
        f.Scheduler.Pause(hosted, Job(f, "hourly")!, "bob");                      // paused before the scheduler ever ticked: no position
        var runner = new FakeRunnerSchedule();

        await f.Ownership(runner).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null);

        var seed = Assert.Single(runner.Told).Seed!;
        Assert.Equal((f.Clock.GetUtcNow(), "bob"), (seed.HandledUpTo, seed.PausedBy));   // a first sight is noted as now, as this scheduler would: the runner does not run it at once
    }

    [Fact]
    public async Task A_runner_that_refuses_the_hand_over_leaves_the_plan_here_and_one_that_cannot_be_asked_leaves_it_nowhere_twice()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var refusing = new FakeRunnerSchedule { FailSetOwner = new RunnerClientException("The runner refused (403): this needs the operator role.") };
        var refused = await Assert.ThrowsAsync<RunnerClientException>(() => f.Ownership(refusing).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("operator role", refused.Message);
        Assert.Equal(TaskOwners.Old, f.OwnerInFile("hourly"));                    // the runner says it is not the owner, so the plan goes back

        var silent = new FakeRunnerSchedule { FailSetOwner = new RunnerClientException("no answer") };
        silent.OnScheduleOf = n => n >= 2 ? throw new RunnerClientException("unreachable") : Task.CompletedTask;       // the first look (before) answers, the one after the failure cannot
        var unknown = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(silent).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("stays 'runner'", unknown.Message);
        Assert.Equal(TaskOwners.Runner, f.OwnerInFile("hourly"));                 // the runner has it or nobody does: never both

        var acted = new FakeRunnerSchedule { FailSetOwner = new RunnerClientException("timed out after the runner had acted") };
        acted.OnScheduleOf = n => { if (n >= 2) (acted.Owner, acted.OwnerSetBy) = (TaskOwners.Runner, "ada"); return Task.CompletedTask; };
        BuildScheduleSettings.Write(f.Root, "sales", "sqlserver", "hourly", TaskOwners.Old);
        (acted.Owner, acted.OwnerSetBy) = (TaskOwners.Old, "ada");
        var change = await f.Ownership(acted).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null);              // the answer was lost but the runner has the plan: that is a cutover
        Assert.Equal(TaskOwners.Runner, change.To);
        Assert.Equal(TaskOwners.Runner, f.OwnerInFile("hourly"));
    }

    [Fact]
    public async Task A_cutover_is_refused_for_what_is_not_a_scheduled_plan_what_the_runner_does_not_serve_and_what_is_moved_already()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();

        var quiet = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(new FakeRunnerSchedule()).CutOverAsync("sales", "sqlserver", "quiet", f.Link, null));
        Assert.Contains("no refresh plan 'sqlserver/quiet' that declares a schedule", quiet.Message);
        var unknown = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(new FakeRunnerSchedule()).CutOverAsync("elsewhere", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("hosts no project 'elsewhere'", unknown.Message);
        var inactive = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(new FakeRunnerSchedule { Version = null }).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("no active version", inactive.Message);
        var already = new FakeRunnerSchedule { OwnerSetBy = "bob" };              // the runner's word counts: it says it has the plan while this file says old
        var disagrees = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(already).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("set by bob", disagrees.Message);
        Assert.Empty(already.Told);

        await f.Ownership(new FakeRunnerSchedule()).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null);
        var again = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(new FakeRunnerSchedule()).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("scheduled by the runner already", again.Message);
    }

    // ---- rollback

    [Fact]
    public async Task A_rollback_lets_the_runner_go_waits_for_its_run_copies_its_position_and_pause_back_and_then_schedules_here_once()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var runner = new FakeRunnerSchedule();
        await f.Ownership(runner).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null);
        // the runner has run the plan since: its position is 14:00, it has a pause, and a run is still going when the rollback starts
        (runner.Handled, runner.PausedBy, runner.PausedAt, runner.Running) = (OwnershipFixture.At(25, 14), "cy", OwnershipFixture.At(25, 13, 30), 1);
        runner.Told.Clear();
        var owners = new List<string>();
        runner.OnScheduleOf = n => { owners.Add(runner.Owner); if (n >= 2) runner.Running = 0; return Task.CompletedTask; };

        var change = await f.Ownership(runner).RollBackAsync("sales", "sqlserver", "hourly", f.Link, "back");

        Assert.Equal([TaskOwners.Old], runner.Told.Select(t => t.Owner));        // the runner is told once, before anything is copied
        Assert.All(owners, o => Assert.Equal(TaskOwners.Old, o));                // and it was no longer the owner while its run was waited for
        Assert.True(change.Waited);
        Assert.Equal(OwnershipFixture.At(25, 14), f.Jobs.Get("sales", "sqlserver/hourly")!.HandledUpTo);
        Assert.Equal("cy", f.Pauses.Get("sales", "sqlserver/hourly")!.Value.By);
        Assert.Equal(TaskOwners.Old, f.OwnerInFile("hourly"));

        f.Pauses.Clear("sales", "sqlserver/hourly");
        f.Clock.To(OwnershipFixture.At(25, 14, 16));
        Assert.Single(await f.TickAndRun());                                      // scheduled here again, once, from the runner's position
        Assert.Equal(["schedule.cutover", "schedule.rollback"], f.Audit.Select(a => a.Command));
    }

    [Fact]
    public async Task A_rollback_whose_run_does_not_end_puts_the_runner_back_in_charge_and_changes_nothing_here()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var runner = new FakeRunnerSchedule();
        await f.Ownership(runner).CutOverAsync("sales", "sqlserver", "hourly", f.Link, null);
        (runner.Handled, runner.Running) = (OwnershipFixture.At(25, 14), 1);
        runner.Told.Clear();
        var ownership = f.Ownership(runner, (_, _) => { f.Clock.Advance(TimeSpan.FromMinutes(20)); return Task.CompletedTask; });

        var refused = await Assert.ThrowsAsync<OwnershipException>(() => ownership.RollBackAsync("sales", "sqlserver", "hourly", f.Link, null, TimeSpan.FromMinutes(30)));

        Assert.Contains("still queued or going on the runner", refused.Message);
        Assert.Equal([TaskOwners.Old, TaskOwners.Runner], runner.Told.Select(t => t.Owner));
        Assert.Equal(TaskOwners.Runner, f.OwnerInFile("hourly"));
        Assert.Equal(OwnershipFixture.At(25, 12), f.Jobs.Get("sales", "sqlserver/hourly")!.HandledUpTo);
    }

    [Fact]
    public async Task Rolling_back_an_observed_plan_needs_no_position_and_a_plan_that_is_this_servers_is_refused()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var runner = new FakeRunnerSchedule();
        var refused = await Assert.ThrowsAsync<OwnershipException>(() => f.Ownership(runner).RollBackAsync("sales", "sqlserver", "hourly", f.Link, null));
        Assert.Contains("scheduled by this server already", refused.Message);

        await f.Ownership(runner).ObserveAsync("sales", "sqlserver", "hourly", f.Link, null);
        var change = await f.Ownership(runner).RollBackAsync("sales", "sqlserver", "hourly", f.Link, null);

        Assert.Equal((TaskOwners.Observe, TaskOwners.Old), (change.From, change.To));
        Assert.Equal(TaskOwners.Old, f.OwnerInFile("hourly"));
        Assert.Equal(TaskOwners.Old, runner.Owner);
    }

    // ---- compare and owners

    [Fact]
    public void Decisions_are_paired_within_the_tolerance_and_by_pause_and_what_is_left_is_a_difference()
    {
        var t = OwnershipFixture.At(25, 12, 15);
        ClientDecision Runner(int s, bool paused = false) => new("runner", t.AddSeconds(s), 0, paused);
        ClientDecision Old(int s, bool paused = false) => new("old", t.AddSeconds(s), 0, paused);

        var comparison = BuildOwnership.Compare([Runner(0), Old(7), Runner(3600), Old(3640, true), Runner(7200), Old(9000)], TimeSpan.FromSeconds(30));

        Assert.Equal(1, comparison.Matched);
        Assert.Equal(["only the runner would have queued a run", "only this scheduler passed this time over", "only the runner would have queued a run", "only this scheduler queued a run"], comparison.Differences.Select(d => d.Kind));
        Assert.False(comparison.Agrees);
        Assert.True(BuildOwnership.Compare([Runner(0), Old(20)], TimeSpan.FromSeconds(30)).Agrees);
    }

    [Fact]
    public async Task The_owners_list_puts_the_runners_word_beside_the_file_and_flags_a_plan_the_two_disagree_about()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var agreeing = new FakeRunnerSchedule { Owner = TaskOwners.Old, OwnerSetBy = "ada" };
        BuildScheduleSettings.Write(f.Root, "sales", "sqlserver", "hourly", TaskOwners.Old, f.Link);

        var row = Assert.Single(await f.Ownership(agreeing).OwnersAsync());
        Assert.Equal((TaskOwners.Old, TaskOwners.Old, false), (row.Setting, row.Runner, row.Mismatch));

        var runnerSchedules = new FakeRunnerSchedule { Owner = TaskOwners.Runner };          // a published plan the runner would schedule while this file says old: the dangerous one
        row = Assert.Single(await f.Ownership(runnerSchedules).OwnersAsync("sales"));
        Assert.True(row.Mismatch);
        Assert.Null(Assert.Single(await f.Ownership(new FakeRunnerSchedule { Version = null }).OwnersAsync()).Runner);
    }
}
