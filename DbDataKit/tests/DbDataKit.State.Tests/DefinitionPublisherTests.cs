namespace DbDataKit.State.Tests;

/// <summary>Plan 30's life cycle (R15) on the <c>definitions</c> module, on every engine: publish serve and stage, activate, switch, rollback, retire, bindings, dependencies, status, artifacts.</summary>
public sealed class DefinitionPublisherTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-06T12:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static DefinitionKinds Kinds() => new DefinitionKinds()
        .Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve)
        {
            Validator = c => c.Manifest.Contains("broken", StringComparison.Ordinal) ? "the catalog does not load" : null,
            Slots = [new DefinitionSlot("warehouse")],
        })
        .Register(new DefinitionKind("app", ["serving"], PublishMode.Serve))
        .Register(new DefinitionKind("integration", ["runner"], PublishMode.Stage)
        {
            // a manifest line "slot <name>" declares a slot
            SlotsOf = manifest => [.. manifest.Split('\n').Where(l => l.StartsWith("slot ", StringComparison.Ordinal)).Select(l => new DefinitionSlot(l[5..].Trim()))],
        })
        .Register(new DefinitionKind("agent", ["runner"], PublishMode.Stage, RequiresApproval: true));

    private (DefinitionPublisher Publisher, StateDatabase Database, StateTestClock Clock) Open(string engine, string? target = null)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: clock);
        return (new DefinitionPublisher(database, Kinds(), new PayloadStore(database), target), database, clock);
    }

    private static PublishRequest Serve(string kind, string name, string manifest, string[]? depends = null, int? expected = null, string channel = "prod") =>
        new(kind, name, manifest, "ada", channel, PublishMode.Serve, depends, ExpectedVersion: expected);

    [Fact]
    public void The_definitions_module_still_follows_the_conventions() => Assert.Empty(StateConventions.Violations(DefinitionsModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_serving_kind_goes_through_publish_switch_rollback_and_retire(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");

        var one = publisher.Publish(Serve("semantic-model", "sales", "model one"));
        var two = publisher.Publish(Serve("semantic-model", "sales", "model two", expected: 1));

        Assert.True(one.Changed);
        Assert.Equal((1, 2), (one.Version.Version, two.Version.Version));
        Assert.Equal("model two", store.Current("semantic-model", "sales")!.Content);

        Assert.Equal(1, publisher.Switch("semantic-model", "sales", 1, "ada", expectedVersion: 2).Version);
        Assert.Equal(2, publisher.Rollback("semantic-model", "sales", "ada").Version);
        Assert.Equal(new DefinitionHead("semantic-model", "sales", "prod", 2, 1, null), publisher.Head("semantic-model", "sales"));

        Assert.True(publisher.Retire("semantic-model", "sales", "ada"));
        Assert.Null(store.Current("semantic-model", "sales"));
        Assert.Null(publisher.Head("semantic-model", "sales"));
        Assert.False(publisher.Retire("semantic-model", "sales", "ada"));
        Assert.Equal(2, store.Versions("semantic-model", "sales").Count);
        Assert.Equal(["bind", "publish", "publish", "switch", "rollback", "retire"], store.Changes().Select(e => e.Action));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_runner_kind_is_staged_by_default_and_runs_the_old_version_until_it_is_activated(string engine)
    {
        var (publisher, database, _) = Open(engine, "runner");
        var store = new DefinitionStore(database);

        var one = publisher.Publish(new PublishRequest("integration", "nightly", "package one", "ada"));
        Assert.Equal(PublishMode.Stage, one.Mode);
        Assert.Null(store.Current("integration", "nightly"));
        Assert.Equal(new DefinitionHead("integration", "nightly", "prod", null, null, 1), publisher.Head("integration", "nightly"));
        Assert.Empty(store.CurrentOfKind("integration"));

        Assert.Equal(1, publisher.Activate("integration", "nightly", "ada", expectedVersion: 0).Version);
        publisher.Publish(new PublishRequest("integration", "nightly", "package two", "ada"));
        Assert.Equal(1, store.Current("integration", "nightly")!.Version);
        Assert.Equal(new DefinitionHead("integration", "nightly", "prod", 1, null, 2), publisher.Head("integration", "nightly"));

        publisher.Activate("integration", "nightly", "ada", expectedVersion: 1);
        Assert.Equal(2, store.Current("integration", "nightly")!.Version);
        Assert.Equal(1, publisher.Rollback("integration", "nightly", "ada").Version);
        Assert.True(publisher.Retire("integration", "nightly", "ada"));
        Assert.Equal(["stage", "activate", "stage", "activate", "rollback", "retire"], store.Changes().Select(e => e.Action));
        Assert.Throws<DefinitionNotFoundException>(() => publisher.Activate("integration", "nightly", "ada"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_same_content_again_changes_nothing_and_a_retry_with_a_stale_expected_version_is_not_a_conflict(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");
        var first = publisher.Publish(Serve("semantic-model", "sales", "same", expected: 0));

        var again = publisher.Publish(Serve("semantic-model", "sales", "same", expected: 0));

        Assert.False(again.Changed);
        Assert.Equal(first.Version.Version, again.Version.Version);
        Assert.Single(store.Versions("semantic-model", "sales"));
        Assert.Equal(["bind", "publish"], store.Changes().Select(e => e.Action));

        var staged = publisher.Publish(new PublishRequest("integration", "job", "x", "ada", Mode: PublishMode.Stage));
        var stagedAgain = publisher.Publish(new PublishRequest("integration", "job", "x", "bob", Mode: PublishMode.Stage));
        Assert.False(stagedAgain.Changed);
        Assert.Equal(staged.Version.Version, stagedAgain.Version.Version);
        Assert.Single(store.Versions("integration", "job"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_stale_expected_version_is_refused_and_changes_nothing(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");
        publisher.Publish(Serve("semantic-model", "sales", "one"));
        publisher.Publish(Serve("semantic-model", "sales", "two"));

        var publish = Assert.Throws<DefinitionConflictException>(() => publisher.Publish(Serve("semantic-model", "sales", "three", expected: 1)));
        Assert.Equal((1, 2), (publish.Expected, publish.Actual));
        Assert.Contains("semantic-model 'sales'", publish.Message);
        Assert.Throws<DefinitionConflictException>(() => publisher.Switch("semantic-model", "sales", 1, "ada", expectedVersion: 1));
        Assert.Throws<DefinitionConflictException>(() => publisher.Rollback("semantic-model", "sales", "ada", expectedVersion: 1));
        Assert.Throws<DefinitionConflictException>(() => publisher.Retire("semantic-model", "sales", "ada", expectedVersion: 1));
        var bind = publisher.Bind("prod", "semantic-model", "sales", "extra", "conn:x", "ada");
        Assert.Throws<DefinitionConflictException>(() => publisher.Bind("prod", "semantic-model", "sales", "extra", "conn:y", "ada", expectedRevision: 0));

        Assert.Equal(2, store.Current("semantic-model", "sales")!.Version);
        Assert.Equal(2, store.Versions("semantic-model", "sales").Count);
        Assert.Equal("conn:x", publisher.Bindings("prod", "semantic-model", "sales").Single(b => b.Slot == "extra").BoundTo);
        Assert.Equal(1, bind.Revision);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_promotion_with_an_unbound_slot_is_refused_naming_the_slot_and_the_channel(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        publisher.Bind("test", "semantic-model", "sales", "warehouse", "conn:test-dw", "ada");
        publisher.Publish(Serve("semantic-model", "sales", "model one", channel: "test"));

        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Switch("semantic-model", "sales", 1, "ada", "prod"));
        Assert.Equal(PublishRefusal.UnboundSlot, refused.Reason);
        Assert.Contains("'warehouse'", refused.Message);
        Assert.Contains("'prod'", refused.Message);
        var publish = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("semantic-model", "sales", "model two")));
        Assert.Contains("'warehouse'", publish.Message);
        Assert.Null(store.Current("semantic-model", "sales", "prod"));
        Assert.Single(store.Versions("semantic-model", "sales"));

        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");
        Assert.Equal(1, publisher.Switch("semantic-model", "sales", 1, "ada", "prod").Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_slot_a_manifest_declares_is_checked_when_the_version_is_staged_and_activated(string engine)
    {
        var (publisher, _, _) = Open(engine);
        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("integration", "etl", "slot source\nslot target", "ada")));
        Assert.Contains("'source', 'target'", refused.Message);

        publisher.Bind("prod", "integration", "etl", "source", "conn:a", "ada");
        publisher.Bind("prod", "integration", "etl", "target", "conn:b", "ada");
        publisher.Publish(new PublishRequest("integration", "etl", "slot source\nslot target", "ada"));
        Assert.Equal(1, publisher.Activate("integration", "etl", "ada").Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_dependency_that_is_not_active_in_the_channel_is_refused_naming_it(string engine)
    {
        var (publisher, _, _) = Open(engine);
        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");

        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("app", "crm", "app one", ["semantic-model:sales@1"])));
        Assert.Equal(PublishRefusal.DependencyNotActive, refused.Reason);
        Assert.Contains("semantic-model:sales@1", refused.Message);
        Assert.Contains("'prod'", refused.Message);

        publisher.Publish(Serve("semantic-model", "sales", "model one"));
        publisher.Publish(Serve("semantic-model", "sales", "model two"));
        var wrongVersion = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("app", "crm", "app one", ["semantic-model:sales@1"])));
        Assert.Contains("semantic-model:sales@1 (version 2 is active)", wrongVersion.Message);

        Assert.Equal(1, publisher.Publish(Serve("app", "crm", "app one", ["semantic-model:sales@2", "semantic-model:sales"])).Version.Version);

        // a staged version is checked when it is activated, not when it is staged
        publisher.Publish(new PublishRequest("integration", "etl", "x", "ada", DependsOn: ["app:other@1"]));
        var activation = Assert.Throws<PublishRefusedException>(() => publisher.Activate("integration", "etl", "ada"));
        Assert.Contains("app:other@1", activation.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_unknown_kind_a_failed_validator_and_a_wrong_target_are_refused_naming_the_thing(string engine)
    {
        var (publisher, database, _) = Open(engine, "serving");
        var store = new DefinitionStore(database);
        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");

        var unknown = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("widget", "w", "x")));
        Assert.Equal(PublishRefusal.UnknownKind, unknown.Reason);
        Assert.Contains("'widget'", unknown.Message);
        Assert.Contains("semantic-model", unknown.Message);

        var invalid = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("semantic-model", "sales", "broken catalog")));
        Assert.Equal(PublishRefusal.Invalid, invalid.Reason);
        Assert.Contains("semantic-model 'sales'", invalid.Message);
        Assert.Contains("the catalog does not load", invalid.Message);

        var wrong = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("integration", "etl", "x", "ada")));
        Assert.Equal(PublishRefusal.WrongTarget, wrong.Reason);
        Assert.Contains("'integration'", wrong.Message);
        Assert.Empty(store.Versions("semantic-model", "sales"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Trusted_code_is_staged_and_needs_an_approval_before_it_is_active(string engine)
    {
        var (publisher, _, _) = Open(engine);
        var served = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("agent", "helper", "x", "ada", Mode: PublishMode.Serve)));
        Assert.Equal(PublishRefusal.ApprovalRequired, served.Reason);

        publisher.Publish(new PublishRequest("agent", "helper", "x", "ada"));
        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Activate("agent", "helper", "ada"));
        Assert.Contains("approved", refused.Message);

        publisher.Approve("agent", "helper", 1, "root");
        publisher.Approve("agent", "helper", 1, "root");
        Assert.Equal(1, publisher.Activate("agent", "helper", "ada").Version);
        Assert.True(publisher.IsApproved("agent", "helper", 1));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_staged_version_activates_when_every_live_instance_is_ready_and_not_before(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        string[] live = ["r1", "r2"];
        publisher.Publish(new PublishRequest("integration", "nightly", "v1", "ada"));
        publisher.Status.Report("r1", "integration", "nightly", "prod", 1, InstanceStates.Ready);

        var waiting = publisher.ActivateIfAllReady("integration", "nightly", live, "system");
        Assert.False(waiting.Activated);
        Assert.Equal(["r2"], waiting.Progress.NotReported);
        Assert.Null(publisher.Status.AllReadyFor("integration", "nightly", "prod", live));
        Assert.Null(publisher.Head("integration", "nightly")!.Active);

        publisher.Status.Report("r2", "integration", "nightly", "prod", 1, InstanceStates.Preparing);
        Assert.False(publisher.ActivateIfAllReady("integration", "nightly", live, "system").Activated);

        publisher.Status.Report("r2", "integration", "nightly", "prod", 1, InstanceStates.Ready);
        Assert.Equal(new AllReady("integration", "nightly", "prod", 1), publisher.Status.AllReadyFor("integration", "nightly", "prod", live));
        var done = publisher.ActivateIfAllReady("integration", "nightly", live, "system");

        Assert.True(done.Activated);
        Assert.Equal(1, store.Current("integration", "nightly")!.Version);
        Assert.Null(publisher.Head("integration", "nightly")!.Staged);
        Assert.Contains("automatic", ReadNote(database));
        Assert.Throws<DefinitionNotFoundException>(() => publisher.ActivateIfAllReady("integration", "nightly", live, "system"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void One_failed_instance_blocks_automatic_activation_but_an_administrator_can_activate_anyway_and_it_is_recorded(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        string[] live = ["r1", "r2", "r3"];
        publisher.Publish(new PublishRequest("integration", "nightly", "v1", "ada"));
        publisher.Activate("integration", "nightly", "ada");
        publisher.Publish(new PublishRequest("integration", "nightly", "v2", "ada"));
        publisher.Status.Report("r1", "integration", "nightly", "prod", 2, InstanceStates.Ready);
        publisher.Status.Report("r2", "integration", "nightly", "prod", 2, InstanceStates.Failed, "contract 3.1 is missing");
        publisher.Status.Report("r3", "integration", "nightly", "prod", 2, InstanceStates.Ready);

        var blocked = publisher.ActivateIfAllReady("integration", "nightly", live, "system");

        Assert.False(blocked.Activated);
        Assert.Equal([("r2", "contract 3.1 is missing")], blocked.Progress.Failed);
        Assert.Equal(1, store.Current("integration", "nightly")!.Version);

        var forced = publisher.ActivateAnyway("integration", "nightly", live, "root", reason: "r2 is being retired");

        Assert.True(forced.Activated);
        Assert.Equal(2, store.Current("integration", "nightly")!.Version);
        var note = ReadNote(database);
        Assert.StartsWith("anyway: activated by root", note);
        Assert.Contains("r2 (contract 3.1 is missing)", note);
        Assert.Contains("r2 is being retired", note);
        Assert.Equal("root", store.Changes().Last().ActorId);
        // the instance that failed still says so
        Assert.Equal(InstanceStates.Failed, publisher.Status.List("integration", "nightly", "prod").Single(s => s.Instance == "r2").State);
    }

    [Fact]
    public async Task Looking_up_a_stored_file_never_waits_for_a_writer_on_the_same_sqlite_file()
    {
        var (publisher, database, _) = Open(StateEngineIds.Sqlite);
        var payloads = new PayloadStore(database);
        var files = new PublishArtifacts(payloads, publisher, "ada");
        var file = files.Add("job.csx", "version one"u8);
        publisher.Publish(new PublishRequest("integration", "nightly", "m0", "ada", Artifacts: [file]));

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 1; !stop.IsCancellationRequested; i++)
                publisher.Publish(new PublishRequest("integration", "writer" + (i % 5), "m" + i, "ada", Artifacts: [file]));
        });

        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 200; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.NotNull(publisher.FindArtifact(file.Sha256));
            if (watch.Elapsed > slowest) slowest = watch.Elapsed;
        }

        stop.Cancel();
        await writer;
        Assert.True(slowest < TimeSpan.FromSeconds(2), $"a lookup waited {slowest.TotalSeconds:F1} s for the writer");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_file_unchanged_between_versions_is_stored_once(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var payloads = new PayloadStore(database);
        var first = new PublishArtifacts(payloads, publisher, "ada");
        var files1 = new[] { first.Add("lib/a.dll", "aaaa"u8, "application/octet-stream"), first.Add("lib/copy-of-a.dll", "aaaa"u8), first.Add("job.csx", "version one"u8) };
        publisher.Publish(new PublishRequest("integration", "nightly", "m1", "ada", Artifacts: files1));
        Assert.Equal(2, first.Written);

        var second = new PublishArtifacts(payloads, publisher, "ada");
        var files2 = new[] { second.Add("lib/a.dll", "aaaa"u8, "application/octet-stream"), second.Add("job.csx", "version two"u8) };
        publisher.Publish(new PublishRequest("integration", "nightly", "m1", "ada", Artifacts: files2));

        Assert.Equal(1, second.Written);
        var v1 = publisher.Artifacts("integration", "nightly", 1);
        var v2 = publisher.Artifacts("integration", "nightly", 2);
        Assert.Equal(3, v1.Count);
        Assert.Equal(v1.Single(a => a.Path == "lib/a.dll").PayloadId, v2.Single(a => a.Path == "lib/a.dll").PayloadId);
        Assert.Equal(v1.Single(a => a.Path == "lib/a.dll").PayloadId, v1.Single(a => a.Path == "lib/copy-of-a.dll").PayloadId);
        Assert.NotEqual(v1.Single(a => a.Path == "job.csx").PayloadId, v2.Single(a => a.Path == "job.csx").PayloadId);
        Assert.Equal(3, v1.Select(a => a.PayloadId).Concat(v2.Select(a => a.PayloadId)).Distinct().Count());
        Assert.NotNull(publisher.FindArtifact(files2[0].Sha256));
        Assert.Null(publisher.FindArtifact(new string('0', 64)));
        using var reader = PublishArtifacts.Open(payloads, v2.Single(a => a.Path == "job.csx"));
        Assert.Equal("version two", new StreamReader(reader).ReadToEnd());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_version_that_changes_only_its_file_list_is_a_new_version_and_a_missing_file_is_refused(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var artifacts = new PublishArtifacts(new PayloadStore(database), publisher, "ada");
        var a = artifacts.Add("a.txt", "a"u8);
        var b = artifacts.Add("b.txt", "b"u8);

        var one = publisher.Publish(new PublishRequest("integration", "nightly", "same manifest", "ada", Artifacts: [a]));
        var two = publisher.Publish(new PublishRequest("integration", "nightly", "same manifest", "ada", Artifacts: [a, b]));
        var again = publisher.Publish(new PublishRequest("integration", "nightly", "same manifest", "ada", Artifacts: [b, a]));

        Assert.Equal((1, 2), (one.Version.Version, two.Version.Version));
        Assert.False(again.Changed);
        Assert.Equal(2, again.Version.Version);

        var missing = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("integration", "other", "m", "ada", Artifacts: [new ArtifactRef("x.bin", new string('a', 64), 1, null)])));
        Assert.Equal(PublishRefusal.Artifact, missing.Reason);
        Assert.Contains("x.bin", missing.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Status_is_listed_per_definition_and_channel_gone_instances_are_shown_and_retention_removes_them(string engine)
    {
        var (publisher, database, clock) = Open(engine);
        var status = publisher.Status;
        status.Report("r1", "integration", "nightly", "prod", 1, InstanceStates.Active);
        status.Report("r2", "integration", "nightly", "prod", 1, InstanceStates.Preparing);
        status.Report("r2", "integration", "nightly", "prod", 1, InstanceStates.Failed, "no network");
        status.Report("r1", "integration", "nightly", "test", 2, InstanceStates.Ready);
        status.Report("r3", "integration", "other", "prod", 1, InstanceStates.Active);

        var listed = status.List("integration", "nightly", "prod");
        Assert.Equal(["r1", "r2"], listed.Select(s => s.Instance));
        Assert.Equal([InstanceStates.Active, InstanceStates.Failed], listed.Select(s => s.State));
        Assert.Equal("no network", listed[1].Error);
        Assert.Equal(Start, listed[0].At);
        Assert.Equal(3, status.OfInstance("r1").Count + status.OfInstance("r3").Count);

        Assert.Equal(["r3"], status.Reconcile(["r1", "r2"]));
        Assert.True(status.List("integration", "other", "prod").Single().Gone);
        Assert.Equal(1, status.MarkGone("r2"));
        Assert.False(status.List("integration", "nightly", "prod").Single(s => s.Instance == "r1").Gone);
        Assert.Equal(["r1", "r2"], status.List("integration", "nightly", "prod").Select(s => s.Instance));
        Assert.Equal(InstanceStates.Active, status.List("integration", "nightly", "prod")[0].State);

        clock.Advance(TimeSpan.FromDays(31));
        var result = new RetentionJob(database).Run();

        Assert.Equal(2, result.Removed["definitions.definition_instance_status"]);
        Assert.Equal(["r1"], status.List("integration", "nightly", "prod").Select(s => s.Instance));
        Assert.Empty(status.List("integration", "other", "prod"));

        // an instance that comes back is live again
        status.Report("r1", "integration", "nightly", "prod", 1, InstanceStates.Active);
        Assert.Throws<ArgumentException>(() => status.Report("r1", "integration", "nightly", "prod", 1, "sleeping"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_older_store_and_the_life_cycle_share_their_rows(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);

        // old publish == serve publish with no registry: any kind, no checks
        store.Publish("widget", "w", "old one", "ada", ["thing:missing@9"]);
        Assert.Equal(new DefinitionHead("widget", "w", "prod", 1, null, null), publisher.Head("widget", "w"));
        Assert.Single(store.CurrentOfKind("widget"));

        // the same content through either API is the same version
        publisher.Bind("prod", "semantic-model", "sales", "warehouse", "conn:dw", "ada");
        var viaNew = publisher.Publish(Serve("semantic-model", "sales", "shared"));
        Assert.Equal(viaNew.Version.Version, store.Publish("semantic-model", "sales", "shared", "bob").Version);
        Assert.Single(store.Versions("semantic-model", "sales"));

        // a channel that has only a staged version serves nothing through the old API, and a switch through it works
        publisher.Publish(new PublishRequest("integration", "job", "j1", "ada"));
        Assert.Null(store.Current("integration", "job"));
        Assert.Equal(1, store.Switch("integration", "job", 1, "ada").Version);
        Assert.Null(publisher.Head("integration", "job")!.Staged);
        store.Publish("integration", "job", "j2", "ada");
        Assert.Equal(1, store.Rollback("integration", "job", "ada").Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_follower_does_not_apply_a_stage_and_applies_an_activation(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        var seen = new List<string>();
        var follower = new DefinitionFollower(store, [new Recorder("integration", seen)], (_, _, _, _) => { });
        follower.Start();

        publisher.Publish(new PublishRequest("integration", "job", "j1", "ada"));
        Assert.Equal(0, follower.Poll());
        publisher.Activate("integration", "job", "ada");
        Assert.Equal(1, follower.Poll());
        Assert.Equal(["job@1"], seen);
    }

    private sealed class Recorder(string kind, List<string> seen) : IDefinitionSubscriber
    {
        public string Kind { get; } = kind;

        public void Apply(string name, DefinitionVersion? current) => seen.Add($"{name}@{current?.Version}");
    }

    private static string ReadNote(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(DefinitionsModule.Name, connection, "SELECT note FROM {table:definition_event} WHERE action = 'activate' ORDER BY id DESC;");
        return (string)command.ExecuteScalar()!;
    }
}
