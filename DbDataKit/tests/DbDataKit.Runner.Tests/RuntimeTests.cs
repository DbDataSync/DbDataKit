using DbDataKit.Runner.Scheduler;
using DbDataKit.Runner.Staging;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>
/// Plan 28c E04 on the engine library: a definition package names its runtime package by a version range (<c>launch.runtime</c>); staging materializes both and refuses a range the channel's active runtime is outside of;
/// an activation of a runtime holds the definitions that run on it and waits for their runs in flight; the scheduler starts the runtime's command with the definition's directory; a runtime's view is shared; the cache keeps what
/// the active and previous runtime need.
/// </summary>
public sealed class RuntimeTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private const string Engine = "engine";
    private const string Definition = "orders-set";
    private const string Prod = "prod";

    private static readonly BuiltAgainst Built = new(null, null, "dbdatakit.runner.v1");

    private static PackageManifest Runtime(string version) =>
        new(Engine, null, new PackageLaunch(LaunchKind.Executable, Command: "bin/engine", Args: ["--serve"]), Built, [], IsRuntime: true, Version: version);

    private static PackageManifest Defined(string range, string name = Definition) =>
        new(name, null, new PackageLaunch(LaunchKind.Executable, Args: ["--definition"], Runtime: new RuntimeReference(Engine, range)), Built,
            [new PackageTask("refresh", new TaskEntry(Args: ["go"]))]);

    private sealed class Rig : IDisposable
    {
        private readonly StateEngines _engines = new();
        private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-runtime-").FullName;

        public Rig(string engine, TimeSpan? maxWait = null)
        {
            Database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, ProgressModule.Module, RunnerModule.Module, SchedulingModule.Module, RunsModule.Module], StateScope.Standalone, time: Clock);
            Payloads = new PayloadStore(Database);
            Publisher = new DefinitionPublisher(Database, null);
            Cache = new ArtifactCache(Path.Combine(_directory, "cache"), new PayloadArtifactSource(Payloads));
            Activator = new PackageActivator(Database, Publisher, new ActivationOptions { Poll = TimeSpan.FromMilliseconds(10), MaxWait = maxWait ?? TimeSpan.FromMinutes(10) });
            Instances = new RunnerInstances(Database);
        }

        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));

        public StateDatabase Database { get; }

        public PayloadStore Payloads { get; }

        public DefinitionPublisher Publisher { get; }

        public ArtifactCache Cache { get; }

        public PackageActivator Activator { get; }

        public RunnerInstances Instances { get; }

        public ModuleWorkQueueStore Queue => new(Database, "runner");

        public (Guid Id, StagingFollower Follower) Instance(Func<PackageManifest, string?>? contracts = null)
        {
            var id = Instances.Register("dbdatarunner", "host-" + Guid.NewGuid().ToString("N")[..4], "1.0");
            return (id, new StagingFollower(Database, Publisher, Cache, Activator, new StagingOptions { Instance = () => id, Contracts = contracts }));
        }

        public int Publish(PackageManifest manifest, PublishMode mode, params (string Path, string Content)[] files)
        {
            var artifacts = new PublishArtifacts(Payloads, Publisher, "ada");
            var refs = files.Select(f => artifacts.Add(f.Path, System.Text.Encoding.UTF8.GetBytes(f.Content))).ToList();
            return Publisher.Publish(new PublishRequest(PackageActivator.Kind, manifest.Name, PackageManifestWriter.Write(manifest), "ada", Prod, mode, Artifacts: refs)).Version.Version;
        }

        public InstanceStatus StatusOf(Guid instance, string package) => Publisher.Status.List(PackageActivator.Kind, package, Prod).Single(s => s.Instance == instance.ToString("D"));

        public void Dispose()
        {
            _engines.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* temp */ }
        }
    }

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

    // ---- staging

    [Theory, MemberData(nameof(Engines))]
    public async Task A_definition_in_range_of_the_active_runtime_stages_ready_and_one_outside_it_fails_naming_runtime_range_and_channel(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        rig.Publish(Runtime("2.1.0"), PublishMode.Serve, ("bin/engine", "engine 2.1"));

        rig.Publish(Defined("2.x"), PublishMode.Stage, ("data/a.yml", "a"));
        await follower.PassAsync();
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Definition, Prod)!.Active);   // prepared, and activated by the group of one

        rig.Publish(Defined("3.x", "needs-three"), PublishMode.Stage);
        var pass = await follower.PassAsync();

        Assert.Contains("needs-three/prod@1", pass.Failed);
        var status = rig.StatusOf(id, "needs-three");
        Assert.Equal(InstanceStates.Failed, status.State);
        Assert.Contains("'engine'", status.Error);
        Assert.Contains("'3.x'", status.Error);
        Assert.Contains("2.1.0", status.Error);
        Assert.Contains("'prod'", status.Error);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_definition_whose_runtime_is_not_active_in_the_channel_fails_staging_saying_so(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        // no runtime published at all
        rig.Publish(Defined("2.x"), PublishMode.Stage);

        await follower.PassAsync();

        var status = rig.StatusOf(id, Definition);
        Assert.Equal(InstanceStates.Failed, status.State);
        Assert.Contains("no active version of 'engine'", status.Error);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_runner_that_does_not_know_launch_runtime_refuses_the_manifest_naming_the_key(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance(m => m.NewerKeys.FirstOrDefault() is { } key ? $"the manifest uses `{key}`, which this runner's package/1 does not have." : null);
        rig.Publish(Runtime("2.1.0"), PublishMode.Serve);
        rig.Publish(Defined("2.x"), PublishMode.Stage);

        await follower.PassAsync();

        Assert.Contains("`launch.runtime`", rig.StatusOf(id, Definition).Error);
    }

    // ---- activation

    [Theory, MemberData(nameof(Engines))]
    public async Task Activating_a_runtime_holds_its_definitions_waits_for_their_runs_in_flight_and_rollback_restores_the_old_one(string engine)
    {
        using var rig = new Rig(engine);
        var (_, follower) = rig.Instance();
        var one = rig.Publish(Runtime("2.1.0"), PublishMode.Serve, ("bin/engine", "engine 2.1"));
        rig.Publish(Defined("*"), PublishMode.Serve);

        // a run of the definition is in flight on runtime 2.1
        var payload = new WorkPayload { Task = new TaskRef(Definition, Prod, "refresh").ToString(), Version = "1", Chain = Guid.NewGuid().ToString("D") }.ToJson();
        rig.Queue.Enqueue($"{Definition}/{Prod}", RunnerScheduler.Kind, "refresh", payloadJson: payload);
        var claimed = rig.Queue.TryClaimNext($"{Definition}/{Prod}", "loop-a") ?? throw new InvalidOperationException("nothing to claim");
        Assert.True(rig.Queue.TryMarkRunning(claimed.Id, "loop-a"));

        var three = rig.Publish(Runtime("3.0.0"), PublishMode.Stage, ("bin/engine", "engine 3.0"));
        var passing = Task.Run(() => follower.PassAsync());
        var holds = new ActivationHolds(rig.Database);
        await Eventually(() => holds.IsHeld(PackageActivator.Kind, Definition, Prod) ? holds : null, "the hold on the definition's new runs");

        // while the run is in flight: still runtime 2.1 active, the definition held, the activation waiting
        Assert.False(passing.IsCompleted);
        Assert.Equal(one, rig.Publisher.Head(PackageActivator.Kind, Engine, Prod)!.Active);

        Assert.True(rig.Queue.TryFinish(claimed.Id, "loop-a", WorkItemStatus.Done));
        var result = await passing;

        Assert.Equal([$"{Engine}/{Prod}@{three}"], result.Activated);
        Assert.Equal(three, rig.Publisher.Head(PackageActivator.Kind, Engine, Prod)!.Active);
        Assert.False(holds.IsHeld(PackageActivator.Kind, Definition, Prod));
        Assert.False(holds.IsHeld(PackageActivator.Kind, Engine, Prod));

        var live = rig.Instances.LiveInstances(TimeSpan.FromMinutes(5)).Select(l => l.Instance.Id.ToString("D")).ToList();
        var back = await rig.Activator.RollbackAsync(Engine, Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live));
        Assert.True(back.Activated);
        Assert.Equal(one, rig.Publisher.Head(PackageActivator.Kind, Engine, Prod)!.Active);
        Assert.Equal(three, rig.Publisher.Head(PackageActivator.Kind, Engine, Prod)!.Previous);
    }

    // ---- the scheduler

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_starts_the_runtimes_command_with_the_definition_as_package_and_the_next_run_after_an_upgrade_is_on_the_new_runtime(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var definition = Defined("2.x", SchedulerHarness.Package);
        h.Versions.Add(Engine, "1", Runtime("2.1.0")).ActivateRuntime(Engine, "1", Channel);
        h.Activate(definition, "1");
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, worker: "loop-a");

        void Enqueue() => h.Queue.Enqueue($"{SchedulerHarness.Package}/{Channel}", RunnerScheduler.Kind, "refresh",
            payloadJson: new WorkPayload { Task = new TaskRef(SchedulerHarness.Package, Channel, "refresh").ToString(), Version = "1", Chain = Guid.NewGuid().ToString("D") }.ToJson());

        Enqueue();
        await Tick(scheduler);
        var first = Assert.Single(runner.Requests);
        Assert.Equal("bin/engine", first.Launch.Command);
        Assert.Equal(["--serve", "--definition"], first.Launch.ArgList);
        Assert.NotNull(first.RuntimeDirectory);
        Assert.NotEqual(first.PackageDirectory, first.RuntimeDirectory);

        // runtime 2.2 is activated: the next run is on it, and 2.x still holds
        h.Versions.Add(Engine, "2", Runtime("2.2.0")).ActivateRuntime(Engine, "2", Channel);
        Enqueue();
        await Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);

        // runtime 3.0: the definition's 2.x no longer holds, the run does not start
        h.Versions.Add(Engine, "3", Runtime("3.0.0")).ActivateRuntime(Engine, "3", Channel);
        Enqueue();
        await Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);
    }

    // ---- views and the cache

    [Fact]
    public async Task A_runtimes_view_is_shared_by_the_runs_that_use_it_and_removed_with_the_last_one()
    {
        var root = Directory.CreateTempSubdirectory("dbdatakit-sharedview-").FullName;
        try
        {
            var source = new MemorySource();
            var engineFile = source.Add("bin/engine", "engine");
            var cache = new ArtifactCache(Path.Combine(root, "cache"), source);
            var views = new SharedViewCache(cache);

            var a = await views.AcquireAsync("engine@1", [engineFile]);
            var b = await views.AcquireAsync("engine@1", [engineFile]);
            Assert.Equal(a.Directory, b.Directory);
            Assert.Equal(1, views.Built);
            Assert.Equal("engine", await File.ReadAllTextAsync(Path.Combine(a.Directory, "bin", "engine")));

            a.Dispose();
            Assert.True(File.Exists(Path.Combine(b.Directory, "bin", "engine")));   // one run still uses it
            b.Dispose();
            Assert.Equal(0, views.Count);
            Assert.False(Directory.Exists(b.Directory));
        }
        finally { ArtifactTestSupport.DeleteTree(root); }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Two_runtimes_sharing_a_file_store_it_once_and_eviction_keeps_the_files_of_the_active_and_previous_runtime(string engine)
    {
        using var rig = new Rig(engine);
        var v1 = rig.Publish(Runtime("1.0.0"), PublishMode.Serve, ("lib/native.so", "shared native"), ("bin/engine", "engine 1"));
        var v2 = rig.Publish(Runtime("2.0.0"), PublishMode.Serve, ("lib/native.so", "shared native"), ("bin/engine", "engine 2"));
        var v3 = rig.Publish(Runtime("3.0.0"), PublishMode.Serve, ("lib/native.so", "shared native"), ("bin/engine", "engine 3"));
        Assert.Equal(3, rig.Publisher.Head(PackageActivator.Kind, Engine, Prod)!.Active);
        Assert.Equal(2, rig.Publisher.Head(PackageActivator.Kind, Engine, Prod)!.Previous);

        foreach (var version in new[] { v1, v2, v3 })
        {
            var files = rig.Publisher.Artifacts(PackageActivator.Kind, Engine, version).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId)).ToList();
            using var view = await rig.Cache.MaterializeAsync($"{Engine}@{version}", files);
        }

        Assert.Equal(4, rig.Cache.CachedHashes().Count);   // the native once, three engines

        var eviction = await rig.Cache.EvictAsync(ArtifactKeep.Hashes(rig.Publisher));

        Assert.Equal(1, eviction.Removed);                  // engine 1: neither active nor previous
        Assert.Equal(3, rig.Cache.CachedHashes().Count);
    }
}
