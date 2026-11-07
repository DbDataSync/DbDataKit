using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Staging;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28a R17 and R18 with real servers and real task processes: a package is staged, every runner prepares it and reports, it activates by itself when all are ready (and the dry run lost nothing), an administrator
/// confirms losses or activates anyway, an activation waits for the runs in flight and migrates the progress once, and a rollback puts the progress back.
/// </summary>
public sealed class StagingServerTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    public static TheoryData<string> ServerEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    private const string Dev = "dev";

    private static DeclaredPart Part(ProgressType type, bool reset = false) => new(type, Reset: reset);

    // version 1: a checkpoint the task moves (seen), another it does not (extra) and a state value (legacy)
    private static ProgressDeclaration V1() => new(
        [new DeclaredCheckpoint("seen", new Dictionary<string, DeclaredPart> { ["value"] = Part(ProgressType.Int) }), new DeclaredCheckpoint("extra", new Dictionary<string, DeclaredPart> { ["n"] = Part(ProgressType.Int) })],
        [new DeclaredState("legacy", ProgressType.Int)]);

    // version 2 loses a value: "seen" is reset on purpose; it also widens two values (no loss)
    private static ProgressDeclaration V2Reset() => new(
        [new DeclaredCheckpoint("seen", new Dictionary<string, DeclaredPart> { ["value"] = Part(ProgressType.Int, reset: true) }, Reset: true), new DeclaredCheckpoint("extra", new Dictionary<string, DeclaredPart> { ["n"] = Part(ProgressType.Decimal) })],
        [new DeclaredState("legacy", ProgressType.Text)]);

    // version 2 that loses nothing: values are widened
    private static ProgressDeclaration V2Widen() => new(
        [new DeclaredCheckpoint("seen", new Dictionary<string, DeclaredPart> { ["value"] = Part(ProgressType.Int) }), new DeclaredCheckpoint("extra", new Dictionary<string, DeclaredPart> { ["n"] = Part(ProgressType.Decimal) })],
        [new DeclaredState("legacy", ProgressType.Text)]);

    private static PackageTask Tally(ProgressDeclaration progress) => SamplePackage.Task("tally") with { Progress = progress };

    private static string ManifestText(params PackageTask[] tasks) => PackageManifestWriter.Write(SamplePackage.Manifest(tasks));

    private static ProgressStore Store(RunnerHarness h) => new(h.State, new ProgressKey("sample", Dev, "tally"));

    private static void Seed(RunnerHarness h)
    {
        Store(h).Advance("extra", new Dictionary<string, ProgressValue> { ["n"] = ProgressValue.OfInt(10) }, 0);
        Store(h).SetState("legacy", ProgressValue.OfInt(5));
    }

    private static IReadOnlyList<PublishFile> FilesOf(RunnerHarness h, int version = 1) =>
        [.. new RunnerPublishing(h.State).Publisher.Artifacts(RunnerPublishing.Kind, "sample", version).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType))];

    /// <summary>Publishes the next version of the sample the way the API does (the same service layer), with the mode the call names or the kind's default.</summary>
    private static PublishOutcome Publish(RunnerHarness h, string manifest, PublishMode? mode = null) =>
        new RunnerPublishing(h.State).Publish(new PublishInput("sample", manifest, FilesOf(h), Dev, mode), RunnerActor.Local());

    private static async Task<int> TalliesAsync(RunnerHarness h, string outcome = "succeeded") => (await h.RunsAsync($"?package=sample&channel={Dev}&task=tally&outcome={outcome}")).Count;

    private static async Task RunTallyAsync(RunnerHarness h)
    {
        var before = await TalliesAsync(h);
        // the run before may have written its end a moment before its queue item is finished: ask again until the server takes it
        var status = HttpStatusCode.Conflict;
        for (var attempt = 0; attempt < 100 && status == HttpStatusCode.Conflict; attempt++)
        {
            status = (await h.PostAsync($"/api/v1/tasks/sample/{Dev}/tally/run")).Status;
            if (status == HttpStatusCode.Conflict) await Task.Delay(100);
        }

        Assert.Equal(HttpStatusCode.Accepted, status);
        await h.WaitAsync(async () => await TalliesAsync(h) > before ? "done" : null, "a run of tally to succeed");
    }

    private static async Task<JsonNode> DescribeWhenAsync(RunnerHarness h, Func<JsonNode, bool> condition, string what, int seconds = 60)
    {
        try { return await h.WaitAsync(async () => (await h.GetAsync("/api/v1/definitions/integration/sample")).Body is { } body && condition(body) ? body : null, what, seconds); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + "\nIt said: " + (await h.GetAsync("/api/v1/definitions/integration/sample")).Body?.ToJsonString());
        }
    }

    private static JsonNode? Channel(JsonNode describe) => describe["channels"]!.AsArray().FirstOrDefault(c => (string)c!["channel"]! == Dev);

    private static int? Active(JsonNode describe) => Channel(describe)?["active"]?.GetValue<int>();

    private static IReadOnlyList<JsonNode?> Instances(JsonNode describe, int version) => [.. describe["instances"]!.AsArray().Where(i => i!["version"]!.GetValue<int>() == version)];

    private static IReadOnlyList<AuditRecord> Audited(RunnerHarness h, string verb, string outcome) =>
        [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 200)).Where(r => r.Entry.Outcome == outcome)];

    private static string ProjectionVersion(RunnerHarness h) => string.Join(",", new TaskProjections(h.State).ListByChannel(Dev, "sample").Select(t => t.Version).Distinct());

    private static JsonNode? Detail(AuditRecord record) => JsonNode.Parse(record.Entry.Detail!);

    private static string[] LossesOf(JsonNode describe) => [.. describe["staging"]!.AsArray().SelectMany(s => s!["losses"]!.AsArray()).Select(l => (string)l!)];

    // ---- R17

    [Theory, MemberData(nameof(Engines))]
    public async Task A_package_that_loses_a_value_stages_with_the_loss_in_the_status_waits_for_confirmation_and_the_next_run_sees_the_migrated_progress(string engine)
    {
        await using var h = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30));
        h.Install(Tally(V1()));
        await h.StartAsync();
        for (var i = 0; i < 3; i++) await RunTallyAsync(h);
        Assert.Equal(3L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);
        Seed(h);

        // the default mode of an integration is stage
        var staged = Publish(h, ManifestText(Tally(V2Reset())));
        Assert.Equal(PublishMode.Stage, staged.Mode);

        // every instance prepares it: ready, and the status carries the loss list
        var described = await DescribeWhenAsync(h, d => Instances(d, staged.Version).Any(i => (string)i!["state"]! == "ready"), "the runner to report the staged version ready");
        var instance = Instances(described, staged.Version).Single()!;
        Assert.True((bool)instance["has_losses"]!);
        Assert.Contains("LOSSES", (string)instance["note"]!);
        Assert.Contains("tally: seen", (string)instance["note"]!);
        var losses = LossesOf(described);
        Assert.NotEmpty(losses);
        Assert.True((bool)described["staging"]!.AsArray().Single()!["needs_confirmation"]!);

        // and it does not activate on its own
        await Task.Delay(1500);
        Assert.Equal(1, Active((await h.GetAsync("/api/v1/definitions/integration/sample")).Body!));
        Assert.Equal("1", ProjectionVersion(h));

        // the API refuses an activation that does not confirm, naming the loss; a confirmation of another list is refused too
        var (refused, body) = await h.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("loses progress", (string)body!["error"]!);
        Assert.Contains("seen", (string)body["error"]!);
        var (wrongList, wrongBody) = await h.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev, confirm_losses = true, losses = new[] { "tally: something else" } });
        Assert.Equal(HttpStatusCode.Conflict, wrongList);
        Assert.Contains("not the ones", (string)wrongBody!["error"]!);
        Assert.Equal(1, Active((await h.GetAsync("/api/v1/definitions/integration/sample")).Body!));

        // confirmed with the list echoed: it activates and migrates
        var (activated, activation) = await h.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev, confirm_losses = true, losses }, subject: "grace");
        Assert.Equal(HttpStatusCode.OK, activated);
        Assert.Equal("activated", (string)activation!["state"]!);
        Assert.Equal(staged.Version, Active((await h.GetAsync("/api/v1/definitions/integration/sample")).Body!));
        Assert.Equal(staged.Version.ToString(), ProjectionVersion(h));
        Assert.Null(Store(h).GetCheckpoint("seen"));                                     // reset on purpose
        Assert.Equal(10m, Store(h).GetCheckpoint("extra")!.Parts["n"].AsDecimal);        // widened
        Assert.Equal("5", Store(h).GetState("legacy")!.AsText);

        // the choice is recorded with who and what
        var recorded = Assert.Single(Audited(h, RunnerPublishing.ActivateVerb, AuditEvent.Ok));
        Assert.Equal("grace", recorded.Entry.ActorId);
        Assert.Contains("seen", (string)Detail(recorded)!["confirmed_losses"]![0]!);
        Assert.Single(Audited(h, RunnerPublishing.ActivateVerb, AuditEvent.Refused), r => r.Entry.Detail!.Contains("loses progress", StringComparison.Ordinal));

        // the next run sees the migrated progress: it starts from the default and the version it runs is 2
        await RunTallyAsync(h);
        Assert.Equal(1L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);
        Assert.Equal(staged.Version.ToString(), (string)(await h.RunsAsync($"?package=sample&channel={Dev}&task=tally&outcome=succeeded"))[0]!["package_version"]!);
    }

    [Fact]
    public async Task A_staged_publish_alone_is_not_scheduled_and_the_default_mode_of_an_integration_is_stage()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "ci-1");
        var package = FakePackage.Make("fresh", "1", SamplePackage.Task("ok", TimeSpan.FromSeconds(1)));
        await package.UploadAllAsync(publisher);

        var (status, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/fresh", new
        {
            manifest = package.Manifest(), channel = Dev, artifacts = package.Files.Select(f => new { path = f.Key, sha256 = FakePackage.Sha(f.Value), size = f.Value.Length }).ToArray(),
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("stage", (string)body!["mode"]!);                                  // no mode named: the kind's default
        await Task.Delay(2500);
        Assert.Empty(await h.RunsAsync("?package=fresh"));                             // due every second, never scheduled
        Assert.Empty(new TaskProjections(h.State).ListByChannel(Dev, "fresh"));
        var (_, described) = await h.GetAsync("/api/v1/definitions/integration/fresh");
        Assert.Equal(1, (int)Channel(described!)!["staged"]!);
        Assert.Null(Channel(described!)!["active"]);

        // the development path serves at once
        var installed = h.Install(SamplePackage.Task("ok"));
        Assert.Equal(1, installed.Version);
        Assert.Equal("1", ProjectionVersion(h));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_contract_mismatch_and_a_missing_label_fail_staging_and_the_status_says_why(string engine)
    {
        await using var h = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30));
        h.Install(Tally(V1()));
        await h.StartAsync();
        var publisher = new RunnerPublishing(h.State).Publisher;
        var files = FilesOf(h);

        // a package built for another .NET: the kind's validator refuses it at a publish; one that got in (a runner upgraded since) fails staging
        var old = PackageManifestWriter.Write(new PackageManifest("sample", null, new PackageLaunch(LaunchKind.Assembly, Assembly: "DbDataKit.Runner.TestPackage.dll"), new BuiltAgainst("8.0", null, "dbdatakit.runner.v1"), [Tally(V1())]));
        var bypass = new DefinitionPublisher(h.State, null).Publish(new PublishRequest(RunnerPublishing.Kind, "sample", old, "ci", Dev, PublishMode.Stage, Artifacts: [.. files.Select(f => new ArtifactRef(f.Path, f.Sha256, f.Size, f.MediaType))]));
        var contract = await DescribeWhenAsync(h, d => Instances(d, bypass.Version.Version).Any(i => (string)i!["state"]! == "failed"), "staging to fail on the contract");
        var error = (string)Instances(contract, bypass.Version.Version).Single()!["error"]!;
        Assert.Contains("built against .NET 8.0", error);
        Assert.Contains("publish it again", error);
        Assert.Equal(1, Active(contract));

        // a version whose task requires a label no live runner has fails staging naming the label
        var labelled = Publish(h, ManifestText(Tally(V2Widen()) with { Requires = ["gpu"] }));
        var label = await DescribeWhenAsync(h, d => Instances(d, labelled.Version).Any(i => (string)i!["state"]! == "failed"), "staging to fail on the label");
        var message = (string)Instances(label, labelled.Version).Single()!["error"]!;
        Assert.Contains("'gpu'", message);
        Assert.Contains("no live runner has", message);
        Assert.Equal(1, Active(label));
        Assert.Equal("1", ProjectionVersion(h));
        Assert.NotNull(publisher.Head(RunnerPublishing.Kind, "sample", Dev)!.Staged);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_runner_on_the_old_format_refuses_a_manifest_with_launch_runtime_at_staging_naming_the_key_and_a_current_runner_stages_it_up_to_the_missing_runtime(string engine)
    {
        await using var old = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30), stagingContracts: m => RunnerContracts.CheckKeys(m, []));
        old.Install(Tally(V1()));
        await old.StartAsync();
        var definition = SamplePackage.Manifest(Tally(V2Widen()) with { Entry = new TaskEntry() }) with { Launch = new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference("engine", "2.x")) };

        var staged = Publish(old, PackageManifestWriter.Write(definition));

        var described = await DescribeWhenAsync(old, d => Instances(d, staged.Version).Any(i => (string)i!["state"]! == "failed"), "staging to fail on the key");
        var error = (string)Instances(described, staged.Version).Single()!["error"]!;
        Assert.Contains("`launch.runtime`", error);
        Assert.Contains("upgrade the runner", error);
        Assert.Equal(1, Active(described));
    }

    [Theory, MemberData(nameof(ServerEngines))]
    public async Task With_two_runners_one_failing_to_prepare_the_group_stays_on_the_old_version_the_status_names_the_instance_and_activating_anyway_is_audited(string engine)
    {
        await using var one = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30));
        await using var two = new RunnerHarness(engine, sharedState: one.Settings.State, staging: true, lease: TimeSpan.FromSeconds(30),
            stagingContracts: _ => "this runner is older: it serves kit contract 0 and cannot load a package built for the current kit; upgrade it or publish the package again.");
        one.Install(Tally(V1()));
        await one.StartAsync();
        await two.StartAsync();
        Seed(one);

        var staged = Publish(one, ManifestText(Tally(V2Widen())));
        var twoId = two.Engine.InstanceId!.Value.ToString("D");
        var described = await DescribeWhenAsync(one, d => Instances(d, staged.Version).Count == 2 && Instances(d, staged.Version).Any(i => (string)i!["state"]! == "failed") && Instances(d, staged.Version).Any(i => (string)i!["state"]! == "ready"),
            "one runner ready and the other failed");

        var failed = Instances(described, staged.Version).Single(i => (string)i!["state"]! == "failed")!;
        Assert.Equal(twoId, (string)failed["instance"]!);                                // the status names the instance
        Assert.Contains("kit contract 0", (string)failed["error"]!);                      // and why
        await Task.Delay(1500);
        Assert.Equal(1, Active((await one.GetAsync("/api/v1/definitions/integration/sample")).Body!));    // the group stays on the old version

        // an administrator's activation does not go through silently
        var (refused, body) = await one.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains(twoId, (string)body!["error"]!);
        Assert.Contains("anyway", (string)body["error"]!);

        var (done, _) = await one.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev, anyway = true, reason = "the older runner is being replaced" }, subject: "grace");
        Assert.Equal(HttpStatusCode.OK, done);
        var after = (await one.GetAsync("/api/v1/definitions/integration/sample")).Body!;
        Assert.Equal(staged.Version, Active(after));
        var recorded = Assert.Single(Audited(one, RunnerPublishing.ActivateVerb, AuditEvent.Ok));
        Assert.Equal("grace", recorded.Entry.ActorId);
        Assert.True((bool)Detail(recorded)!["anyway"]!);
        var activate = after["events"]!.AsArray().First(e => (string)e!["action"]! == "activate")!;
        Assert.Contains("anyway: activated by grace", (string)activate["note"]!);
        Assert.Contains(twoId, (string)activate["note"]!);
        Assert.Equal("failed", (string)Instances(after, staged.Version).Single(i => (string)i!["instance"]! == twoId)!["state"]!);   // the instance that failed says so
        Assert.Equal(10m, Store(one).GetCheckpoint("extra")!.Parts["n"].AsDecimal);
    }

    // ---- R18

    [Theory, MemberData(nameof(Engines))]
    public async Task A_run_in_flight_finishes_on_the_old_version_then_the_migration_applies_once_then_the_next_run_is_on_the_new_version(string engine)
    {
        await using var h = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30));
        h.Install(Tally(V1()), SamplePackage.Task("lingers"));
        await h.StartAsync();
        await RunTallyAsync(h);
        await RunTallyAsync(h);
        Seed(h);
        Assert.Equal(2L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);

        // a run is in flight when the new version is staged and confirmed
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync($"/api/v1/tasks/sample/{Dev}/lingers/run")).Status);
        await h.WaitAsync(async () => (await h.RunsAsync($"?package=sample&channel=dev&task=lingers&outcome=running")).Count == 1 ? "running" : null, "lingers to run");
        var staged = Publish(h, ManifestText(Tally(V2Reset()), SamplePackage.Task("lingers")));
        var described = await DescribeWhenAsync(h, d => Instances(d, staged.Version).Any(i => (string)i!["state"]! == "ready"), "the version to be ready");
        var activation = Task.Run(() => h.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev, confirm_losses = true, losses = LossesOf(described) }));

        // the activation waits: new runs of the package are held, the old version stays, nothing is migrated
        var holds = new ActivationHolds(h.State);
        await h.WaitAsync(() => System.Threading.Tasks.Task.FromResult(holds.IsHeld(RunnerPublishing.Kind, "sample", Dev) ? "held" : null), "the hold on new runs");
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync($"/api/v1/tasks/sample/{Dev}/tally/run")).Status);   // asked for during the wait
        await Task.Delay(1500);
        Assert.False(activation.IsCompleted);
        Assert.Equal(2, await TalliesAsync(h));                                                     // the new run has not started
        Assert.Equal(1, Active((await h.GetAsync("/api/v1/definitions/integration/sample")).Body!));
        Assert.Equal(2L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);

        // the run in flight ends on the version it started with; then the migration applies, once, and the held run starts on the new version
        var (status, _) = await activation;
        Assert.Equal(HttpStatusCode.OK, status);
        var lingers = (await h.RunsAsync("?package=sample&channel=dev&task=lingers")).Single()!;
        Assert.Equal("succeeded", (string)lingers["outcome"]!);
        Assert.Equal("1", (string)lingers["package_version"]!);
        await h.WaitAsync(async () => await TalliesAsync(h) == 3 ? "ran" : null, "the held run to start once the version moved");
        var third = (await h.RunsAsync($"?package=sample&channel={Dev}&task=tally&outcome=succeeded")).First(r => (string)r!["package_version"]! == staged.Version.ToString());
        Assert.NotNull(third);
        Assert.Equal(1L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);                     // it saw the migrated (reset) progress
        Assert.Single((await h.GetAsync("/api/v1/definitions/integration/sample")).Body!["events"]!.AsArray(), e => (string)e!["action"]! == "activate");
        var tags = Store(h).History("extra", limit: 100).Select(c => c.Actor).Where(a => a is not null && a.StartsWith("migration:", StringComparison.Ordinal)).Distinct().ToList();
        Assert.Single(tags);                                                                         // applied once
    }

    [Theory, MemberData(nameof(ServerEngines))]
    public async Task Two_runners_activating_at_once_migrate_once(string engine)
    {
        await using var one = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30));
        await using var two = new RunnerHarness(engine, sharedState: one.Settings.State, staging: true, lease: TimeSpan.FromSeconds(30));
        one.Install(Tally(V1()));
        await one.StartAsync();
        await two.StartAsync();
        Seed(one);

        var staged = Publish(one, ManifestText(Tally(V2Widen())));
        await DescribeWhenAsync(one, d => Active(d) == staged.Version, "the version to activate once both runners are ready");
        await DescribeWhenAsync(one, d => Instances(d, staged.Version).Count == 2 && Instances(d, staged.Version).All(i => (string)i!["state"]! == "active"), "both runners to report it active");

        var described = (await one.GetAsync("/api/v1/definitions/integration/sample")).Body!;
        Assert.Single(described["events"]!.AsArray(), e => (string)e!["action"]! == "activate");
        Assert.Equal(10m, Store(one).GetCheckpoint("extra")!.Parts["n"].AsDecimal);
        var tags = Store(one).History("extra", limit: 100).Select(c => c.Actor).Where(a => a is not null && a.StartsWith("migration:", StringComparison.Ordinal)).Distinct().ToList();
        Assert.Single(tags);                                                                         // one migration, whichever runner did it
        Assert.Equal(staged.Version.ToString(), ProjectionVersion(one));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_rollback_restores_the_progress_a_version_that_dropped_values_had_changed(string engine)
    {
        await using var h = new RunnerHarness(engine, staging: true, lease: TimeSpan.FromSeconds(30));
        h.Install(Tally(V1()));
        await h.StartAsync();
        for (var i = 0; i < 3; i++) await RunTallyAsync(h);
        Seed(h);
        var staged = Publish(h, ManifestText(Tally(V2Reset())));
        var described = await DescribeWhenAsync(h, d => Instances(d, staged.Version).Any(i => (string)i!["state"]! == "ready"), "the version to be ready");
        Assert.Equal(HttpStatusCode.OK, (await h.PostAsync("/api/v1/definitions/integration/sample/activate", ServerRole.Admin, new { channel = Dev, confirm_losses = true, losses = LossesOf(described) })).Status);
        await RunTallyAsync(h);
        Assert.Equal(1L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);

        var (status, body) = await h.PostAsync("/api/v1/definitions/integration/sample/rollback", ServerRole.Admin, new { channel = Dev });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, (int)body!["active"]!);
        Assert.Equal("1", ProjectionVersion(h));
        Assert.Equal(3L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);                     // the value before the reset is back
        Assert.Equal(10L, Store(h).GetCheckpoint("extra")!.Parts["n"].AsInt);                       // and in its own type
        Assert.Equal(5L, Store(h).GetState("legacy")!.AsInt);
        await RunTallyAsync(h);
        Assert.Equal(4L, Store(h).GetCheckpoint("seen")!.Parts["value"].AsInt);                     // the next run goes on from there
        Assert.Single(Audited(h, RunnerPublishing.RollbackVerb, AuditEvent.Ok));
    }
}
