namespace DbDataKit.State.Tests;

/// <summary>Plan 21 CH02: a release runs its items in order, stops at the first failure naming it, resumes there, and has one approval that every item's permission holder must give.</summary>
public sealed class ReleaseTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private ReleaseStore Open(string engine) => new(_engines.Provision(engine).Open([ReleasesModule.Module]));

    private static ReleaseItem Item(string product, string kind, string name, int version = 1) => new(0, product, "main", kind, name, version, "test", "prod");

    private static readonly ReleaseItem[] Three = [Item("gateway", "semantic-model", "sales"), Item("gateway", "dashboard", "sales-overview"), Item("runner", "integration", "etl")];

    private sealed class Permissions(params string[] lacking) : IReleaseApprovalPermissions
    {
        public bool Holds(string approver, ReleaseItem item) => !lacking.Contains($"{approver}:{item.Name}");
    }

    private sealed class Runner(params string[] failing) : IReleaseItemRunner
    {
        public List<string> Ran { get; } = [];
        public HashSet<string> Failing { get; } = [.. failing];

        public Task<ReleaseItemResult> RunAsync(ReleaseRecord release, ReleaseItem item, CancellationToken cancellationToken)
        {
            Ran.Add(item.Name);
            if (Failing.Contains(item.Name)) return Task.FromResult(ReleaseItemResult.Failure("the dependency is not in prod"));
            return Task.FromResult(ReleaseItemResult.Success);
        }
    }

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(ReleasesModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_three_item_release_runs_in_order_and_stops_at_the_item_that_fails_naming_it_and_resume_continues_there(string engine)
    {
        var store = Open(engine);
        var release = store.Create("november", "ada", Three);
        Assert.Equal([1, 2, 3], release.Items.Select(i => i.Position));
        store.Approve(release.Id, "grace", new Permissions());
        var runner = new Runner("sales-overview");

        var stopped = await new ReleaseRunner(store, runner).StartAsync(release.Id);

        Assert.Equal(["sales", "sales-overview"], runner.Ran);                       // in order, and the third never ran
        Assert.Equal(ReleaseStates.Stopped, stopped.State);
        Assert.Equal(2, stopped.StoppedAt);
        Assert.Contains("item 2 (gateway main: dashboard 'sales-overview' version 1, test to prod) failed: the dependency is not in prod", stopped.StoppedReason);
        Assert.Equal([ReleaseStates.Done, ReleaseStates.Failed, ReleaseStates.Pending], stopped.Items.Select(i => i.State));
        Assert.Equal("the dependency is not in prod", stopped.Items[1].Error);

        // resume: what was done is not done again; the failed item is tried again and the rest follows
        runner.Ran.Clear();
        runner.Failing.Clear();
        var finished = await new ReleaseRunner(store, runner).ResumeAsync(release.Id);
        Assert.Equal(["sales-overview", "etl"], runner.Ran);
        Assert.Equal(ReleaseStates.Succeeded, finished.State);
        Assert.Null(finished.StoppedAt);
        Assert.All(finished.Items, i => Assert.Equal(ReleaseStates.Done, i.State));
        Assert.Null(finished.Items[1].Error);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_approver_who_lacks_the_permission_for_one_item_is_refused_naming_the_item_and_the_release_stays_a_draft(string engine)
    {
        var store = Open(engine);
        var release = store.Create("november", "ada", Three);

        var refused = Assert.Throws<ReleaseRefusedException>(() => store.Approve(release.Id, "grace", new Permissions("grace:etl")));
        Assert.Contains("item 3 (runner main: integration 'etl'", refused.Message);
        Assert.DoesNotContain("item 1", refused.Message);
        Assert.Equal(ReleaseStates.Draft, store.Get(release.Id)!.State);
        Assert.Null(store.Get(release.Id)!.ApprovedBy);

        var approved = store.Approve(release.Id, "heidi", new Permissions("grace:etl"));
        Assert.Equal((ReleaseStates.Approved, "heidi"), (approved.State, approved.ApprovedBy));
        Assert.NotNull(approved.ApprovedAt);
        Assert.Throws<ReleaseRefusedException>(() => store.Approve(release.Id, "heidi", new Permissions()));   // once
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_release_that_is_not_approved_does_not_start_and_the_other_moves_are_refused_in_the_wrong_state(string engine)
    {
        var store = Open(engine);
        var runner = new Runner();
        var release = store.Create("november", "ada", Three);
        var run = new ReleaseRunner(store, runner);

        Assert.Contains("needs its approval", (await Assert.ThrowsAsync<ReleaseRefusedException>(() => run.StartAsync(release.Id))).Message);
        Assert.Contains("only a stopped release is resumed", (await Assert.ThrowsAsync<ReleaseRefusedException>(() => run.ResumeAsync(release.Id))).Message);
        Assert.Empty(runner.Ran);

        store.Approve(release.Id, "grace", new Permissions());
        Assert.Equal(ReleaseStates.Succeeded, (await run.StartAsync(release.Id)).State);
        Assert.Throws<ReleaseRefusedException>(() => store.Abandon(release.Id));                    // a finished release is history
        await Assert.ThrowsAsync<ReleaseRefusedException>(() => run.StartAsync(release.Id));

        var other = store.Create("december", "ada", [Item("gateway", "app", "crm")]);
        Assert.Equal(ReleaseStates.Abandoned, store.Abandon(other.Id).State);
        await Assert.ThrowsAsync<ReleaseRefusedException>(() => run.StartAsync(other.Id));
        Assert.Throws<ReleaseRefusedException>(() => store.Create("empty", "ada", []));
        Assert.Equal(["december", "november"], store.List().Select(r => r.Name));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_cancelled_run_stops_the_release_at_its_item_and_a_stopped_release_can_be_abandoned(string engine)
    {
        var store = Open(engine);
        var release = store.Create("november", "ada", Three);
        store.Approve(release.Id, "grace", new Permissions());
        using var cancel = new CancellationTokenSource();
        var runner = new CancellingRunner(cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReleaseRunner(store, runner).StartAsync(release.Id, cancel.Token));

        var stopped = store.Get(release.Id)!;
        Assert.Equal((ReleaseStates.Stopped, 2), (stopped.State, stopped.StoppedAt));
        Assert.Equal([ReleaseStates.Done, ReleaseStates.Pending, ReleaseStates.Pending], stopped.Items.Select(i => i.State));
        Assert.Equal(ReleaseStates.Abandoned, store.Abandon(release.Id).State);
    }

    private sealed class CancellingRunner(CancellationTokenSource cancel) : IReleaseItemRunner
    {
        public Task<ReleaseItemResult> RunAsync(ReleaseRecord release, ReleaseItem item, CancellationToken cancellationToken)
        {
            if (item.Position == 2) { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return Task.FromResult(ReleaseItemResult.Success);
        }
    }
}
