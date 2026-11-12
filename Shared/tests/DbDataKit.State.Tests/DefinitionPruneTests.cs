namespace DbDataKit.State.Tests;

/// <summary>Plan 30 PB05: pruning the versions of a definition, on every engine.</summary>
public sealed class DefinitionPruneTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private (DefinitionPublisher Publisher, PayloadStore Payloads, DefinitionStore Store) Open(string engine)
    {
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind("dashboard", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind("integration", ["runner"], PublishMode.Serve));
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"));
        var payloads = new PayloadStore(database);
        return (new DefinitionPublisher(database, kinds, payloads), payloads, new DefinitionStore(database));
    }

    private static PublishRequest Serve(string kind, string name, string manifest, string[]? depends = null, IReadOnlyList<ArtifactRef>? files = null, string channel = "prod") =>
        new(kind, name, manifest, "ada", channel, PublishMode.Serve, depends, Artifacts: files);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_dry_run_lists_what_would_go_and_removes_nothing_and_the_active_previous_and_staged_stay(string engine)
    {
        var (publisher, _, store) = Open(engine);
        for (var i = 1; i <= 6; i++) publisher.Publish(Serve("semantic-model", "sales", "m" + i));
        publisher.Switch("semantic-model", "sales", 4, "ada", channel: "test");   // the test channel serves version 4

        var plan = publisher.Prune("semantic-model", "sales", "root", keep: 1, dryRun: true);
        Assert.False(plan.Done);
        Assert.Equal(6, store.Versions("semantic-model", "sales").Count);
        Assert.DoesNotContain(6, plan.Remove);
        Assert.DoesNotContain(5, plan.Remove);
        Assert.DoesNotContain(4, plan.Remove);
        Assert.Contains(plan.Retained, r => r.Version == 4 && r.Reason.Contains("test", StringComparison.Ordinal));
        Assert.All(plan.Retained, r => Assert.NotEmpty(r.Reason));

        var done = publisher.Prune("semantic-model", "sales", "root", keep: 1, dryRun: false);
        Assert.True(done.Done);
        Assert.Equal(plan.Remove, done.Remove);
        Assert.Equal([.. Enumerable.Range(1, 6).Except(done.Remove)], store.Versions("semantic-model", "sales").Select(v => v.Version));
        Assert.Contains(store.Versions("semantic-model", "sales"), v => v.Version == 6);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_version_another_version_pins_stays(string engine)
    {
        var (publisher, _, store) = Open(engine);
        for (var i = 1; i <= 4; i++) publisher.Publish(Serve("semantic-model", "sales", "m" + i));
        publisher.Publish(Serve("dashboard", "kpi", "d1", ["semantic-model:sales@4"]));

        // version 4 is active and pinned; ask to remove 1..4 by name
        var plan = publisher.Prune("semantic-model", "sales", "root", versions: [1, 2, 3, 4], dryRun: false);
        Assert.Equal([1, 2], plan.Remove);
        Assert.Contains(plan.Retained, r => r.Version == 4);   // active and pinned
        Assert.Contains(plan.Retained, r => r.Version == 3);   // the previous one

        // the dashboard moves on; the pin goes with its version only when that is pruned
        publisher.Publish(Serve("semantic-model", "sales", "m5"));
        publisher.Publish(Serve("semantic-model", "sales", "m6"));
        publisher.Publish(Serve("dashboard", "kpi", "d2", ["semantic-model:sales@6"]));
        publisher.Publish(Serve("dashboard", "kpi", "d3", ["semantic-model:sales@6"]));
        var more = publisher.Prune("semantic-model", "sales", "root", versions: [4], dryRun: true);
        Assert.Contains(more.Retained, r => r.Version == 4 && r.Reason.Contains("dashboard 'kpi' version 1", StringComparison.Ordinal));
        publisher.Prune("dashboard", "kpi", "root", versions: [1], dryRun: false);
        Assert.Equal([4], publisher.Prune("semantic-model", "sales", "root", versions: [4], dryRun: true).Remove);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_file_two_versions_share_goes_only_when_both_are_gone(string engine)
    {
        var (publisher, payloads, _) = Open(engine);
        var files = new PublishArtifacts(payloads, publisher, "ada");
        var shared = files.Add("lib/a.dll", "aaaa"u8);
        var own1 = files.Add("job.csx", "one"u8);
        var own2 = files.Add("job.csx", "two"u8);
        publisher.Publish(Serve("integration", "nightly", "m1", files: [shared, own1]));
        publisher.Publish(Serve("integration", "nightly", "m2", files: [shared, own2]));
        publisher.Publish(Serve("integration", "nightly", "m3", files: [shared]));
        var sharedPayload = publisher.Artifacts("integration", "nightly", 1).Single(a => a.Path == "lib/a.dll").PayloadId;
        var ownPayload = publisher.Artifacts("integration", "nightly", 1).Single(a => a.Path == "job.csx").PayloadId;

        var first = publisher.Prune("integration", "nightly", "root", versions: [1], dryRun: false);
        Assert.Equal([own1.Sha256], first.Artifacts);
        Assert.Null(payloads.Get(ownPayload));
        Assert.NotNull(payloads.Get(sharedPayload));

        publisher.Publish(Serve("integration", "nightly", "m4", files: [shared]));   // 2 is no longer the previous version
        var second = publisher.Prune("integration", "nightly", "root", versions: [2], dryRun: false);
        Assert.Equal([own2.Sha256], second.Artifacts);
        Assert.NotNull(payloads.Get(sharedPayload));
        Assert.Single(publisher.Artifacts("integration", "nightly", 3));
        Assert.Single(publisher.Artifacts("integration", "nightly", 4));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Both_or_neither_way_of_choosing_and_unknown_versions_are_refused(string engine)
    {
        var (publisher, _, store) = Open(engine);
        publisher.Publish(Serve("semantic-model", "sales", "m1"));
        Assert.Throws<ArgumentException>(() => publisher.Prune("semantic-model", "sales", "root"));
        Assert.Throws<ArgumentException>(() => publisher.Prune("semantic-model", "sales", "root", keep: 1, versions: [1]));
        Assert.Throws<DefinitionNotFoundException>(() => publisher.Prune("semantic-model", "sales", "root", versions: [9]));
        Assert.Throws<DefinitionNotFoundException>(() => publisher.Prune("semantic-model", "nope", "root", keep: 1));
    }
}
