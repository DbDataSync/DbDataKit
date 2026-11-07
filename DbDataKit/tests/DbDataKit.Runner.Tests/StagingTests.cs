using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Runner.Staging;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>
/// Plan 28a R17 and R18 on the engine library, on every engine, with the clock the test moves: staging prepares a staged version and reports it (the dry run of the progress migration with its losses, the contract and label
/// checks), a version activates by itself only when every live instance is ready and the dry run lost nothing, an activation waits for the runs in flight and then migrates in one transaction, two activations at once migrate once,
/// and a rollback puts the progress back.
/// </summary>
public sealed class StagingTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private const string Name = "pkg";
    private const string Prod = "prod";

    private sealed class Rig : IDisposable
    {
        private readonly StateEngines _engines = new();
        private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-staging-").FullName;

        public Rig(string engine, TimeSpan? maxWait = null)
        {
            Database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, ProgressModule.Module, RunnerModule.Module, SchedulingModule.Module, RunsModule.Module], StateScope.Standalone, time: Clock);
            Publisher = new DefinitionPublisher(Database, null);
            Cache = new ArtifactCache(Path.Combine(_directory, "cache"), new EmptySource());
            Activator = new PackageActivator(Database, Publisher, new ActivationOptions { Poll = TimeSpan.FromMilliseconds(10), MaxWait = maxWait ?? TimeSpan.FromMinutes(10) });
            Instances = new RunnerInstances(Database);
        }

        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));

        public StateDatabase Database { get; }

        public DefinitionPublisher Publisher { get; }

        public ArtifactCache Cache { get; }

        public PackageActivator Activator { get; }

        public RunnerInstances Instances { get; }

        public ModuleWorkQueueStore Queue => new(Database, "runner");

        public ProgressStore Store(string task = "orders") => new(Database, new ProgressKey(Name, Prod, task));

        /// <summary>An instance of the group (live: its heartbeat is now) and the staging follower of it.</summary>
        public (Guid Id, StagingFollower Follower) Instance(Func<BuiltAgainst, string?>? contracts = null)
        {
            var id = Instances.Register("dbdatarunner", "host-" + Guid.NewGuid().ToString("N")[..4], "1.0");
            return (id, new StagingFollower(Database, Publisher, Cache, Activator, new StagingOptions { Instance = () => id, Contracts = contracts }));
        }

        public int Publish(PackageManifest manifest, PublishMode mode) =>
            Publisher.Publish(new PublishRequest(PackageActivator.Kind, manifest.Name, PackageManifestWriter.Write(manifest), "ada", Prod, mode)).Version.Version;

        public void Dispose()
        {
            _engines.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* temp */ }
        }
    }

    private static readonly BuiltAgainst Built = new("10.0", null, "dbdatakit.runner.v1");

    private static DeclaredPart Of(ProgressType type, string? format = null) => new(type, Format: format);

    private static PackageManifest Package(ProgressDeclaration progress, string[]? requires = null, BuiltAgainst? built = null, string task = "orders") =>
        new(Name, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "x.dll"), built ?? Built,
            [new PackageTask(task, new TaskEntry("T"), new Schedule(), [], [], [], progress, TaskLimits.None, requires ?? [], [])]);

    private static ProgressDeclaration Declare(params (string Part, ProgressType Type)[] parts) =>
        new([new DeclaredCheckpoint("orders", parts.ToDictionary(p => p.Part, p => Of(p.Type)))], []);

    private static ProgressDeclaration Declare(DeclaredState state, params (string Part, ProgressType Type)[] parts) =>
        new([new DeclaredCheckpoint("orders", parts.ToDictionary(p => p.Part, p => Of(p.Type)))], [state]);

    private static void Seed(ProgressStore store)
    {
        store.Advance("orders", new Dictionary<string, ProgressValue> { ["at"] = ProgressValue.OfInt(42), ["legacy"] = ProgressValue.OfText("keep me") }, 0);
        store.SetState("cursor", ProgressValue.OfInt(7));
    }

    private static InstanceStatus StatusOf(Rig rig, Guid instance) => rig.Publisher.Status.List(PackageActivator.Kind, Name, Prod).Single(s => s.Instance == instance.ToString("D"));

    private static readonly ProgressDeclaration Version1 = Declare(new DeclaredState("cursor", ProgressType.Int), ("at", ProgressType.Int), ("legacy", ProgressType.Text));

    // "legacy" is dropped, the state "cursor" is no longer declared: both are losses
    private static readonly ProgressDeclaration Version2Loses = Declare(("at", ProgressType.Decimal));

    // nothing is lost: "at" is widened and a part is added
    private static readonly ProgressDeclaration Version2Keeps = Declare(new DeclaredState("cursor", ProgressType.Int), ("at", ProgressType.Decimal), ("legacy", ProgressType.Text), ("extra", ProgressType.Int));

    private static async Task<T> Eventually<T>(Func<T?> probe, string what) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found) return found;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Waited 30 seconds for {what}.");
    }

    // ---- R17: staging

    [Theory, MemberData(nameof(Engines))]
    public async Task A_version_that_loses_a_value_stages_with_the_losses_in_the_status_and_does_not_activate_on_its_own_and_confirming_activates_and_migrates(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        Seed(rig.Store());
        rig.Publish(Package(Version1), PublishMode.Serve);
        var two = rig.Publish(Package(Version2Loses), PublishMode.Stage);

        var pass = await follower.PassAsync();

        Assert.Equal([$"{Name}/{Prod}@{two}"], pass.Prepared);
        Assert.Empty(pass.Activated);
        var status = StatusOf(rig, id);
        Assert.Equal(InstanceStates.Ready, status.State);
        Assert.True(status.HasLosses);
        Assert.Contains("orders: orders.legacy", status.Note);                // the loss list is the instance's status text
        Assert.Contains("orders: cursor", status.Note);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Staged);

        // again and again: still waiting for the administrator, nothing moved
        Assert.Empty((await follower.PassAsync()).Activated);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal(2, rig.Store().GetCheckpoint("orders")!.Parts.Count);

        // the administrator's activation without the confirmation changes nothing either
        var live = new[] { id.ToString("D") };
        var refused = await rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live));
        Assert.Equal(ActivationState.LossesNotConfirmed, refused.State);
        Assert.Contains(refused.Losses, l => l.Contains("orders.legacy", StringComparison.Ordinal));

        var done = await rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live, ConfirmLosses: true));

        Assert.True(done.Activated);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        var after = rig.Store().GetCheckpoint("orders")!;
        Assert.Equal(42m, after.Parts["at"].AsDecimal);                         // what the next run reads
        Assert.DoesNotContain("legacy", after.Parts.Keys);
        Assert.Null(rig.Store().GetState("cursor"));
        Assert.Equal(two.ToString(), new TaskProjections(rig.Database).Get(Name, Prod, "orders")!.Version);
        Assert.Contains("confirmed losses", rig.Publisher.EventsOf(PackageActivator.Kind, Name).First(e => e.Action == "activate").Note);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_version_that_loses_nothing_activates_by_itself_when_every_live_instance_is_ready_and_not_before(string engine)
    {
        using var rig = new Rig(engine);
        var (first, one) = rig.Instance();
        var (second, two) = rig.Instance();
        Seed(rig.Store());
        rig.Publish(Package(Version1), PublishMode.Serve);
        var v2 = rig.Publish(Package(Version2Keeps), PublishMode.Stage);

        var a = await one.PassAsync();
        Assert.Empty(a.Activated);                                              // the second instance has not reported
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal(InstanceStates.Ready, StatusOf(rig, first).State);
        Assert.False(StatusOf(rig, first).HasLosses);
        Assert.Equal("migration: orders: orders.at: Int becomes Decimal; orders: orders.extra: new, with no default: starts empty", StatusOf(rig, first).Note);

        var b = await two.PassAsync();

        Assert.Equal([$"{Name}/{Prod}@{v2}"], b.Activated);
        Assert.Equal(v2, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Null(rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Staged);
        Assert.Equal(42m, rig.Store().GetCheckpoint("orders")!.Parts["at"].AsDecimal);
        Assert.Equal("legacy", rig.Store().GetCheckpoint("orders")!.Parts.Keys.Single(k => k == "legacy"));
        Assert.Equal(v2.ToString(), new TaskProjections(rig.Database).Get(Name, Prod, "orders")!.Version);

        // the next look reports both as active
        await one.PassAsync();
        Assert.Equal(InstanceStates.Active, StatusOf(rig, first).State);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_version_whose_required_label_no_live_runner_has_fails_staging_naming_the_label(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        rig.Instances.SetLabel(id, "os:linux", InstanceLabel.Admin);
        rig.Publish(Package(Version1), PublishMode.Serve);
        rig.Publish(Package(Version2Keeps, requires: ["gpu"]), PublishMode.Stage);

        var pass = await follower.PassAsync();

        Assert.Single(pass.Failed);
        var status = StatusOf(rig, id);
        Assert.Equal(InstanceStates.Failed, status.State);
        Assert.Contains("'gpu'", status.Error);
        Assert.Contains("no live runner has", status.Error);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Empty(pass.Activated);

        // a runner that has the label: the same version stages ready (the follower prepares again once the status is not final: here a fresh version)
        rig.Instances.SetLabel(id, "gpu", InstanceLabel.Admin);
        rig.Publish(Package(Version2Keeps, requires: ["gpu"], built: new BuiltAgainst("10.0", "1.0", "dbdatakit.runner.v1")), PublishMode.Stage);
        Assert.Single((await follower.PassAsync()).Prepared);
        Assert.Equal(InstanceStates.Ready, StatusOf(rig, id).State);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_package_built_against_another_contract_fails_staging_with_the_republish_message(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance(built => built.Dotnet == "8.0" ? "the package was built against .NET 8.0 and this runner runs .NET 10.0; build it for .NET 10.0 and publish it again." : null);
        rig.Publish(Package(Version1), PublishMode.Serve);
        var two = rig.Publish(Package(Version2Keeps, built: new BuiltAgainst("8.0", null, "dbdatakit.runner.v1")), PublishMode.Stage);

        await follower.PassAsync();

        var status = StatusOf(rig, id);
        Assert.Equal(InstanceStates.Failed, status.State);
        Assert.Contains("built against .NET 8.0", status.Error);
        Assert.Contains("publish it again", status.Error);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Staged);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_change_the_rules_cannot_make_and_a_stored_value_that_does_not_convert_fail_staging_naming_the_value(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        rig.Store().Advance("orders", new Dictionary<string, ProgressValue> { ["at"] = ProgressValue.OfText("not a number") }, 0);
        rig.Publish(Package(Declare(("at", ProgressType.Text))), PublishMode.Serve);

        rig.Publish(Package(Declare(("at", ProgressType.Time))), PublishMode.Stage);              // text to a time needs a format
        await follower.PassAsync();
        Assert.Contains("orders.at", StatusOf(rig, id).Error);
        Assert.Contains("format", StatusOf(rig, id).Error);

        rig.Publish(Package(Declare(("at", ProgressType.Int))), PublishMode.Stage);               // a text that is not a number
        await follower.PassAsync();
        Assert.Equal(InstanceStates.Failed, StatusOf(rig, id).State);
        Assert.Contains("not a number", StatusOf(rig, id).Error);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal("not a number", rig.Store().GetCheckpoint("orders")!.Parts["at"].AsText);   // nothing was touched by the dry run
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task One_instance_failing_keeps_the_group_on_the_old_version_the_status_names_it_and_activating_anyway_is_recorded(string engine)
    {
        using var rig = new Rig(engine);
        var (good, goodFollower) = rig.Instance();
        var (bad, badFollower) = rig.Instance(_ => "this runner cannot load the package.");
        Seed(rig.Store());
        rig.Publish(Package(Version1), PublishMode.Serve);
        var two = rig.Publish(Package(Version2Keeps), PublishMode.Stage);

        await goodFollower.PassAsync();
        var pass = await badFollower.PassAsync();

        Assert.Empty(pass.Activated);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);                  // the group stays on the old version
        var live = new[] { good.ToString("D"), bad.ToString("D") };
        var progress = rig.Publisher.Status.Progress(PackageActivator.Kind, Name, Prod, live);
        Assert.Equal([good.ToString("D")], progress.Ready);
        Assert.Equal(bad.ToString("D"), progress.Failed.Single().Instance);
        Assert.Contains("cannot load the package", progress.Failed.Single().Error);
        Assert.Contains(bad.ToString("D"), progress.Describe());

        // an administrator's activation without anyway is refused naming the instance and why
        var refused = await rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live));
        Assert.Equal(ActivationState.NotReady, refused.State);
        Assert.Contains("cannot load the package", refused.Message);

        var anyway = await rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Anyway, "ada", live, Reason: "the other runner is being replaced"));

        Assert.True(anyway.Activated);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        var recorded = rig.Publisher.EventsOf(PackageActivator.Kind, Name).First(e => e.Action == "activate");
        Assert.Equal("ada", recorded.ActorId);
        Assert.Contains("anyway: activated by ada", recorded.Note);
        Assert.Contains("cannot load the package", recorded.Note);
        Assert.Contains("the other runner is being replaced", recorded.Note);
        Assert.Equal(InstanceStates.Failed, StatusOf(rig, bad).State);                                      // the failed instance says so
        Assert.Equal(42m, rig.Store().GetCheckpoint("orders")!.Parts["at"].AsDecimal);
    }


    // ---- R18: activation waits for runs in flight

    private static (Guid RunId, long Item) StartRun(Rig rig, string task = "orders")
    {
        var queue = rig.Queue;
        var payload = new WorkPayload { Task = new TaskRef(Name, Prod, task).ToString(), Version = "1", Chain = Guid.NewGuid().ToString("D") }.ToJson();
        queue.Enqueue($"{Name}/{Prod}", RunnerScheduler.Kind, task, payloadJson: payload);
        var claimed = queue.TryClaimNext($"{Name}/{Prod}", "loop-a") ?? throw new InvalidOperationException("nothing to claim");
        Assert.True(queue.TryMarkRunning(claimed.Id, "loop-a"));
        return (claimed.RunId, claimed.Id);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_in_flight_finishes_first_then_the_migration_applies_once_then_the_next_run_is_on_the_new_version(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        Seed(rig.Store());
        var oneManifest = Package(Version1);
        rig.Publish(oneManifest, PublishMode.Serve);
        new TaskProjections(rig.Database).Replace(Name, Prod, ProjectionDocuments.RowsOf(oneManifest, "1"));
        var two = rig.Publish(Package(Version2Keeps), PublishMode.Stage);
        var (_, item) = StartRun(rig);

        var passing = Task.Run(() => follower.PassAsync());
        var holds = new ActivationHolds(rig.Database);
        await Eventually(() => holds.IsHeld(PackageActivator.Kind, Name, Prod) ? holds : null, "the hold on new runs");

        // while the run is in flight: the old version, no migration, the new version only staged and the old projection
        Assert.False(passing.IsCompleted);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal(ProgressType.Int, rig.Store().GetCheckpoint("orders")!.Parts["at"].Type);
        Assert.Equal("1", new TaskProjections(rig.Database).Get(Name, Prod, "orders")!.Version);

        Assert.True(rig.Queue.TryFinish(item, "loop-a", WorkItemStatus.Done));
        var result = await passing;

        Assert.Equal([$"{Name}/{Prod}@{two}"], result.Activated);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Single(rig.Publisher.EventsOf(PackageActivator.Kind, Name), e => e.Action == "activate" && e.Version == two);
        Assert.Equal(ProgressType.Decimal, rig.Store().GetCheckpoint("orders")!.Parts["at"].Type);          // migrated
        Assert.False(holds.IsHeld(PackageActivator.Kind, Name, Prod));

        // the next run is on the new version: a scheduler claims the task now and the run is of version 2
        var runner = new FakeRunner(rig.Database);
        var versions = new InMemoryVersionSource().Add(Name, "2", Package(Version2Keeps));
        var scheduler = new RunnerScheduler(rig.Database, runner, rig.Cache, versions, new RunnerSchedulerOptions { Channels = [Prod], WorkerId = "loop-b", Jitter = () => 0.5 });
        rig.Queue.Enqueue($"{Name}/{Prod}", RunnerScheduler.Kind, "orders", payloadJson: new WorkPayload { Task = new TaskRef(Name, Prod, "orders").ToString(), Version = "1", Chain = Guid.NewGuid().ToString("D") }.ToJson());
        await Tick(scheduler);
        Assert.Equal("2", Assert.Single(runner.Requests).PackageVersion);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_hold_keeps_new_runs_of_the_package_from_starting_until_it_is_released(string engine)
    {
        using var rig = new Rig(engine);
        var manifest = new PackageManifest(Name, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "x.dll"), Built,
            [new PackageTask("orders", new TaskEntry("T"), Schedule.Interval(TimeSpan.FromHours(1)), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], [])]);
        new TaskProjections(rig.Database).Replace(Name, Prod, ProjectionDocuments.RowsOf(manifest, "1"));
        var runner = new FakeRunner(rig.Database);
        var scheduler = new RunnerScheduler(rig.Database, runner, rig.Cache, new InMemoryVersionSource().Add(Name, "1", manifest), new RunnerSchedulerOptions { Channels = [Prod], WorkerId = "loop-a", Jitter = () => 0.5 });
        new ModuleJobStateStore(rig.Database, "runner").SaveHandled($"{Name}/{Prod}", "orders", rig.Clock.GetUtcNow());
        rig.Clock.Advance(TimeSpan.FromHours(2));
        var holds = new ActivationHolds(rig.Database);
        holds.Hold(PackageActivator.Kind, Name, Prod, "runner:x", TimeSpan.FromMinutes(5));

        var held = await Tick(scheduler);

        Assert.Equal(0, held.Claimed);
        Assert.Empty(runner.Requests);                                           // due and queued, but not started
        holds.Release(PackageActivator.Kind, Name, Prod, "runner:x");
        await Tick(scheduler);
        Assert.Single(runner.Requests);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task After_the_longest_wait_the_activation_is_not_applied_names_the_busy_task_releases_the_hold_and_goes_through_when_the_run_is_done(string engine)
    {
        using var rig = new Rig(engine, maxWait: TimeSpan.FromMinutes(1));
        var (id, follower) = rig.Instance();
        Seed(rig.Store());
        rig.Publish(Package(Version1), PublishMode.Serve);
        var two = rig.Publish(Package(Version2Keeps), PublishMode.Stage);
        var (_, item) = StartRun(rig);
        var holds = new ActivationHolds(rig.Database);

        var passing = Task.Run(() => follower.PassAsync());
        await Eventually(() => holds.IsHeld(PackageActivator.Kind, Name, Prod) ? holds : null, "the hold on new runs");
        rig.Clock.Advance(TimeSpan.FromMinutes(2));                              // the longest wait ran out
        var result = await passing;

        Assert.Empty(result.Activated);
        Assert.False(holds.IsHeld(PackageActivator.Kind, Name, Prod));            // released: the package keeps running on the old version
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal(ProgressType.Int, rig.Store().GetCheckpoint("orders")!.Parts["at"].Type);

        // the run is done: the next try goes through
        Assert.True(rig.Queue.TryFinish(item, "loop-a", WorkItemStatus.Done));
        rig.Instances.Heartbeat(id);                                              // the clock moved: the instance beats again
        var third = await follower.PassAsync();
        Assert.Single(third.Activated);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Equal(ProgressType.Decimal, rig.Store().GetCheckpoint("orders")!.Parts["at"].Type);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_activation_reports_the_busy_tasks_by_name_when_the_wait_runs_out(string engine)
    {
        using var rig = new Rig(engine, maxWait: TimeSpan.FromMinutes(1));
        var (id, _) = rig.Instance();
        rig.Publish(Package(Version1), PublishMode.Serve);
        rig.Publish(Package(Version2Keeps), PublishMode.Stage);
        rig.Publisher.Status.Report(id.ToString("D"), PackageActivator.Kind, Name, Prod, 2, InstanceStates.Ready);
        StartRun(rig);

        var waiting = Task.Run(() => rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", [id.ToString("D")])));
        await Eventually(() => new ActivationHolds(rig.Database).IsHeld(PackageActivator.Kind, Name, Prod) ? "held" : null, "the hold on new runs");
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        var outcome = await waiting;

        Assert.Equal(ActivationState.WaitingForRuns, outcome.State);
        Assert.Equal(["orders"], outcome.BusyTasks);
        Assert.Contains("'orders'", outcome.Message);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_runners_activating_at_once_migrate_once(string engine)
    {
        using var rig = new Rig(engine);
        var (a, _) = rig.Instance();
        var (b, _) = rig.Instance();
        Seed(rig.Store());
        rig.Publish(Package(Version1), PublishMode.Serve);
        var two = rig.Publish(Package(Version2Keeps), PublishMode.Stage);
        rig.Publisher.Status.Report(a.ToString("D"), PackageActivator.Kind, Name, Prod, two, InstanceStates.Ready);
        rig.Publisher.Status.Report(b.ToString("D"), PackageActivator.Kind, Name, Prod, two, InstanceStates.Ready);
        var live = new[] { a.ToString("D"), b.ToString("D") };
        using var barrier = new Barrier(2);

        var tasks = new[] { "runner:a", "runner:b" }.Select(who => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Automatic, who, live));
        })).ToArray();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Single(outcomes, o => o.Activated);
        Assert.Single(outcomes, o => o.State is ActivationState.AlreadyDone or ActivationState.NothingStaged);
        Assert.Single(rig.Publisher.EventsOf(PackageActivator.Kind, Name), e => e.Action == "activate");
        // migrated once: every history row of the migration carries one tag
        var tags = rig.Store().History("orders", limit: 1000).Select(c => c.Actor).Where(actor => actor?.StartsWith("migration:", StringComparison.Ordinal) == true).Distinct().ToList();
        Assert.Single(tags);
        Assert.Equal(two, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_rollback_restores_the_progress_a_version_that_dropped_values_had_changed_and_follows_with_the_projection(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        Seed(rig.Store());
        rig.Publish(Package(Version1), PublishMode.Serve);
        new TaskProjections(rig.Database).Replace(Name, Prod, ProjectionDocuments.RowsOf(Package(Version1), "1"));
        var two = rig.Publish(Package(Version2Loses), PublishMode.Stage);
        await follower.PassAsync();
        var live = new[] { id.ToString("D") };
        var activated = await rig.Activator.ActivateAsync(Name, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live, ConfirmLosses: true));
        Assert.True(activated.Activated);
        Assert.DoesNotContain("legacy", rig.Store().GetCheckpoint("orders")!.Parts.Keys);
        Assert.Null(rig.Store().GetState("cursor"));
        Assert.Equal(two.ToString(), new TaskProjections(rig.Database).Get(Name, Prod, "orders")!.Version);

        var back = await rig.Activator.RollbackAsync(Name, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live));

        Assert.True(back.Activated);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Name, Prod)!.Active);
        Assert.Contains("put back", back.Message);
        var restored = rig.Store().GetCheckpoint("orders")!;
        Assert.Equal(ProgressType.Int, restored.Parts["at"].Type);
        Assert.Equal(42L, restored.Parts["at"].AsInt);
        Assert.Equal("keep me", restored.Parts["legacy"].AsText);                // the value the version dropped is back
        Assert.Equal(7L, rig.Store().GetState("cursor")!.AsInt);
        Assert.Equal("1", new TaskProjections(rig.Database).Get(Name, Prod, "orders")!.Version);
    }
}
