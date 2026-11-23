namespace DbDataKit.State.Tests;

/// <summary>The record of each deploy of a build the control service made (<see cref="DeployStore"/>), found by Workspace server, project, workspace and build.</summary>
public sealed class DeployStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private DeployStore Open(string engine) => new(_engines.Provision(engine).Open([DeploysModule.Module]));

    private static DeployRecord Record(string workspace, string build, DateTimeOffset at, string outcome = "ok", string? error = null) =>
        new(DeployStore.NewId(), "ws-1", "orders", workspace, build, "package", "sync", "runner-1", "jobs", "runner", "package", "orders-set", "dev", outcome == "ok" ? 2 : null, "stage", "verified", "abcd1234abcd1234",
            """{"schema":"build-record/1"}""", "ada", at, outcome, error, outcome == "ok" ? """{"version":2}""" : null);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(DeploysModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_deploy_is_kept_as_it_was_and_the_list_narrows_by_project_workspace_and_build_newest_first(string engine)
    {
        var store = Open(engine);
        var at = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
        var first = store.Add(Record("main", "b1", at));
        store.Add(Record("main", "b2", at.AddMinutes(1), "refused", "The runner refused the package."));
        store.Add(Record("other", "b3", at.AddMinutes(2)));
        store.Add(Record("main", "b1", at.AddMinutes(3)) with { WorkspaceServer = "ws-2" });

        Assert.Equal(first, store.Get(first.Id));
        Assert.Equal(["b3", "b2", "b1"], store.List("ws-1").Select(d => d.Build));
        Assert.Equal(["b2", "b1"], store.List("ws-1", "orders", "main").Select(d => d.Build));
        var refused = Assert.Single(store.List("ws-1", "orders", "main", "b2"));
        Assert.Equal(("refused", "The runner refused the package.", null, null), (refused.Outcome, refused.Error, refused.Version, refused.Answer));
        Assert.Empty(store.List("ws-1", "billing"));
    }
}
