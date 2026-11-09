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

    private const string Linux = RuntimePlatforms.LinuxX64;
    private const string Windows = RuntimePlatforms.WindowsX64;

    /// <summary>The registry name of the runtime built for a platform, as a publish stores it.</summary>
    private static string Stored(string platform = Linux) => RuntimePlatforms.PackageName(Engine, platform);

    private static PackageManifest Runtime(string version, string platform = Linux) =>
        new(Engine, null, new PackageLaunch(LaunchKind.Executable, Command: "bin/engine", Args: ["--serve"]), Built, [], IsRuntime: true, Version: version, Platform: platform);

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

        public (Guid Id, StagingFollower Follower) Instance(Func<PackageManifest, string?>? contracts = null, string platform = Linux)
        {
            var id = Instances.Register("dbdatarunner", "host-" + Guid.NewGuid().ToString("N")[..4], "1.0");
            return (id, new StagingFollower(Database, Publisher, Cache, Activator, new StagingOptions { Instance = () => id, Contracts = contracts, Platform = platform }));
        }

        public int Publish(PackageManifest manifest, PublishMode mode, params (string Path, string Content)[] files)
        {
            var artifacts = new PublishArtifacts(Payloads, Publisher, "ada");
            var refs = files.Select(f => artifacts.Add(f.Path, System.Text.Encoding.UTF8.GetBytes(f.Content))).ToList();
            return Publisher.Publish(new PublishRequest(PackageActivator.Kind, manifest.PackageName, PackageManifestWriter.Write(manifest), "ada", Prod, mode, Artifacts: refs)).Version.Version;
        }

        public InstanceStatus StatusOf(Guid instance, string package) => Publisher.Status.List(PackageActivator.Kind, package, Prod).Single(s => s.Instance == instance.ToString("D"));

        public void Dispose()
        {
            _engines.Dispose();
            ArtifactTestSupport.DeleteTree(_directory);      // the cache's files are read-only; Windows will not delete a read-only file
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
        Assert.Contains("has no active version for linux-x64: it has no active version of it for any platform", status.Error);
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
        Assert.Equal(one, rig.Publisher.Head(PackageActivator.Kind, Stored(), Prod)!.Active);

        Assert.True(rig.Queue.TryFinish(claimed.Id, "loop-a", WorkItemStatus.Done));
        var result = await passing;

        Assert.Equal([$"{Stored()}/{Prod}@{three}"], result.Activated);
        Assert.Equal(three, rig.Publisher.Head(PackageActivator.Kind, Stored(), Prod)!.Active);
        Assert.False(holds.IsHeld(PackageActivator.Kind, Definition, Prod));
        Assert.False(holds.IsHeld(PackageActivator.Kind, Stored(), Prod));

        var live = rig.Instances.LiveInstances(TimeSpan.FromMinutes(5)).Select(l => l.Instance.Id.ToString("D")).ToList();
        var back = await rig.Activator.RollbackAsync(Stored(), Prod, new ActivationRequest(ActivationMode.Administrator, "ada", live));
        Assert.True(back.Activated);
        Assert.Equal(one, rig.Publisher.Head(PackageActivator.Kind, Stored(), Prod)!.Active);
        Assert.Equal(three, rig.Publisher.Head(PackageActivator.Kind, Stored(), Prod)!.Previous);
    }

    // ---- the scheduler

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_starts_the_runtimes_command_with_the_definition_as_package_and_the_next_run_after_an_upgrade_is_on_the_new_runtime(string engine)
    {
        using var h = new SchedulerHarness(engine);
        var definition = Defined("2.x", SchedulerHarness.Package);
        h.Versions.Add(Stored(), "1", Runtime("2.1.0")).ActivateRuntime(Stored(), "1", Channel);
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
        h.Versions.Add(Stored(), "2", Runtime("2.2.0")).ActivateRuntime(Stored(), "2", Channel);
        Enqueue();
        await Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);

        // runtime 3.0: the definition's 2.x no longer holds, the run does not start
        h.Versions.Add(Stored(), "3", Runtime("3.0.0")).ActivateRuntime(Stored(), "3", Channel);
        Enqueue();
        await Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);
    }

    // ---- platforms

    [Theory, MemberData(nameof(Engines))]
    public async Task A_runtime_published_for_both_platforms_side_by_side_resolves_to_the_runners_own_platform_on_either_runner(string engine)
    {
        using var rig = new Rig(engine);
        // the same runtime name and the same engine version, once per platform: no clash, each platform has its own version numbers and its own active version
        var linux = rig.Publish(Runtime("2.1.0", Linux), PublishMode.Serve, ("bin/engine", "linux engine"));
        var windows = rig.Publish(Runtime("2.1.0", Windows), PublishMode.Serve, ("bin/engine.exe", "windows engine"));
        Assert.Equal(1, linux);
        Assert.Equal(1, windows);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Stored(Linux), Prod)!.Active);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Stored(Windows), Prod)!.Active);
        rig.Publish(Runtime("2.2.0", Linux), PublishMode.Serve, ("bin/engine", "linux engine 2.2"));    // a new Linux build moves only the Linux head
        Assert.Equal(2, rig.Publisher.Head(PackageActivator.Kind, Stored(Linux), Prod)!.Active);
        Assert.Equal(1, rig.Publisher.Head(PackageActivator.Kind, Stored(Windows), Prod)!.Active);

        var store = new DefinitionStore(rig.Database);
        var reference = new RuntimeReference(Engine, "2.x");
        var onLinux = RuntimeResolution.Resolve(store, rig.Publisher, reference, Prod, Definition, Linux);
        var onWindows = RuntimeResolution.Resolve(store, rig.Publisher, reference, Prod, Definition, Windows);

        Assert.Equal((Stored(Linux), "2", "2.2.0", "linux-x64"), (onLinux.Package, onLinux.Version, onLinux.RuntimeVersion, onLinux.Content.Manifest.Platform));
        Assert.Equal((Stored(Windows), "1", "2.1.0", "win-x64"), (onWindows.Package, onWindows.Version, onWindows.RuntimeVersion, onWindows.Content.Manifest.Platform));
        Assert.Equal(["bin/engine.exe"], onWindows.Content.Files.Select(f => f.Path));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_definition_stages_ready_on_a_runner_whose_platform_has_the_runtime_and_fails_naming_runtime_platform_and_the_platforms_available_on_the_other(string engine)
    {
        using var rig = new Rig(engine);
        var (_, linuxRunner) = rig.Instance(platform: Linux);
        rig.Publish(Runtime("2.1.0", Linux), PublishMode.Serve, ("bin/engine", "linux engine"));
        rig.Publish(Defined("2.x"), PublishMode.Stage, ("data/a.yml", "a"));

        var onLinux = await linuxRunner.PassAsync();
        Assert.Equal([$"{Definition}/{Prod}@1"], onLinux.Prepared);

        // a runner of the other platform: the Linux runtime is all the channel has
        var (windowsId, windowsRunner) = rig.Instance(platform: Windows);
        rig.Publish(Defined("2.x", "second-set"), PublishMode.Stage);
        var onWindows = await windowsRunner.PassAsync();

        Assert.Contains("second-set/prod@1", onWindows.Failed);
        var status = rig.StatusOf(windowsId, "second-set");
        Assert.Equal(InstanceStates.Failed, status.State);
        Assert.Equal(
            "Version 1 of 'second-set' cannot be staged: 'second-set@1' needs the runtime 'engine' in the range '2.x' for the platform win-x64, and channel 'prod' has no active version for win-x64: it has it for linux-x64 (version 2.1.0) only; publish and activate the runtime built for win-x64 there first.",
            status.Error);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_runner_never_materializes_the_runtime_of_another_platform(string engine)
    {
        using var rig = new Rig(engine);
        var (id, windowsRunner) = rig.Instance(platform: Windows);
        rig.Publish(Runtime("2.1.0", Linux), PublishMode.Stage, ("bin/engine", "linux engine"));

        var pass = await windowsRunner.PassAsync();

        Assert.Equal([$"{Stored(Linux)}/{Prod}@1"], pass.Prepared);       // ready, so the group can activate it for the runners that use it
        var status = rig.StatusOf(id, Stored(Linux));
        Assert.Equal(InstanceStates.Ready, status.State);
        Assert.Contains("this runner is win-x64 and does not fetch it", status.Note);
        Assert.Empty(rig.Cache.CachedHashes());                           // nothing of the Linux runtime was stored here
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_runtime_published_without_a_platform_is_refused_and_one_already_active_is_named_in_the_refusal(string engine)
    {
        using var rig = new Rig(engine);
        var (id, follower) = rig.Instance();
        var legacy = Runtime("2.1.0") with { Platform = null };
        Assert.Contains("says the platform its files are built for", Assert.Throws<PackageManifestException>(() => PackageManifestReader.Load(PackageManifestWriter.Write(legacy), "legacy").ThrowIfInvalid()).Message);
        var refused = RunnerPackages.Check(new DefinitionCandidate(PackageActivator.Kind, Engine, PackageManifestWriter.Write(legacy), [], []));
        Assert.Contains("platform", refused);

        // one that an older runner stored (written to the registry directly, as a runner of the earlier build did) is named when a definition needs it
        var artifacts = new PublishArtifacts(rig.Payloads, rig.Publisher, "ada");
        rig.Publisher.Publish(new PublishRequest(PackageActivator.Kind, Engine, PackageManifestWriter.Write(legacy), "ada", Prod, PublishMode.Serve, Artifacts: [artifacts.Add("bin/engine", "x"u8.ToArray())]));
        rig.Publish(Defined("2.x"), PublishMode.Stage);

        await follower.PassAsync();

        var status = rig.StatusOf(id, Definition);
        Assert.Equal(InstanceStates.Failed, status.State);
        Assert.Contains("a runtime package with no `platform:`", status.Error);
        Assert.Contains("publish it again once per platform", status.Error);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_resolves_the_runtime_of_the_schedulers_platform_and_fails_without_starting_when_the_channel_has_only_the_other(string engine)
    {
        using var h = new SchedulerHarness(engine);
        h.Versions.Add(Stored(Linux), "1", Runtime("2.1.0", Linux)).ActivateRuntime(Stored(Linux), "1", Channel);
        h.Versions.Add(Stored(Windows), "1", Runtime("2.1.0", Windows) with { Launch = new PackageLaunch(LaunchKind.Executable, Command: "bin/engine.exe") }).ActivateRuntime(Stored(Windows), "1", Channel);
        h.Activate(Defined("2.x", SchedulerHarness.Package), "1");
        var runner = new FakeRunner(h.Database);

        void Enqueue() => h.Queue.Enqueue($"{SchedulerHarness.Package}/{Channel}", RunnerScheduler.Kind, "refresh",
            payloadJson: new WorkPayload { Task = new TaskRef(SchedulerHarness.Package, Channel, "refresh").ToString(), Version = "1", Chain = Guid.NewGuid().ToString("D") }.ToJson());

        Enqueue();
        await Tick(h.NewScheduler(runner, worker: "loop-a", platform: Windows));
        Assert.Equal("bin/engine.exe", Assert.Single(runner.Requests).Launch.Command);

        Enqueue();
        await Tick(h.NewScheduler(runner, worker: "loop-b", platform: Linux));
        Assert.Equal("bin/engine", runner.Requests.Last().Launch.Command);
        Assert.Equal(2, runner.Requests.Count);

        // a platform the channel has no runtime for: the run does not start, and the message says so
        Enqueue();
        await Tick(h.NewScheduler(runner, worker: "loop-c", platform: "osx-arm64"));
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

    [Fact]
    public async Task A_runtime_that_a_host_program_starts_has_a_view_of_copies_and_a_self_contained_one_of_links()
    {
        // plan 28c E12e: `dotnet {runtime}/bin/Engine.dll` resolves a link to the object it points at and looks for its runtime configuration beside that (exit code 131); the engine of Sync is such a runtime
        var root = Directory.CreateTempSubdirectory("dbdatakit-sharedview-copies-").FullName;
        try
        {
            var source = new MemorySource();
            var file = source.Add("bin/Engine.dll", "engine");
            var cache = new ArtifactCache(Path.Combine(root, "cache"), source);
            var views = new SharedViewCache(cache);

            using var copied = await views.AcquireAsync("hosted@1", [file], copies: true);
            using var linked = await views.AcquireAsync("self-contained@1", [file], copies: false);

            var copy = new FileInfo(Path.Combine(copied.Directory, "bin", "Engine.dll"));
            var link = new FileInfo(Path.Combine(linked.Directory, "bin", "Engine.dll"));
            Assert.Null(copy.LinkTarget);
            Assert.Equal("engine", await File.ReadAllTextAsync(copy.FullName));
            if (!OperatingSystem.IsWindows()) Assert.NotNull(link.LinkTarget);   // links are what Linux makes of a view otherwise
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
        Assert.Equal(3, rig.Publisher.Head(PackageActivator.Kind, Stored(), Prod)!.Active);
        Assert.Equal(2, rig.Publisher.Head(PackageActivator.Kind, Stored(), Prod)!.Previous);

        foreach (var version in new[] { v1, v2, v3 })
        {
            var files = rig.Publisher.Artifacts(PackageActivator.Kind, Stored(), version).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId)).ToList();
            using var view = await rig.Cache.MaterializeAsync($"{Engine}@{version}", files);
        }

        Assert.Equal(4, rig.Cache.CachedHashes().Count);   // the native once, three engines

        var eviction = await rig.Cache.EvictAsync(ArtifactKeep.Hashes(rig.Publisher));

        Assert.Equal(1, eviction.Removed);                  // engine 1: neither active nor previous
        Assert.Equal(3, rig.Cache.CachedHashes().Count);
    }
}
