using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 22a GP31: the <c>workspace</c> module on every state engine: its conventions, layout and retention, the projects, the drafts and their history written in one transaction, the builds, the publisher's key reference (only a
/// reference), and a format change that is refused naming the database.
/// </summary>
public sealed class WorkspaceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-09T12:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private (WorkspaceStore Store, StateDatabase Database, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([WorkspaceModule.Module], StateScope.Standalone, time: clock);
        return (new WorkspaceStore(database), database, clock);
    }

    private static WorkspaceRow Draft(string project = "orders", string owner = "ada", string name = "new-orders", DateTimeOffset? at = null) =>
        new(project, $"{owner}-{name}", owner, "Ada Lovelace", name, $"ws/{owner}-{name}", "0123abc", at ?? Start);

    private static long Count(StateDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command(WorkspaceModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    // ---- convention, layout, retention declared in the module ----

    [Fact]
    public void The_module_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(WorkspaceModule.Module));
    }

    [Fact]
    public void The_module_declares_a_year_of_events_ninety_days_of_builds_and_no_rule_for_the_rest()
    {
        var rules = WorkspaceModule.Module.Retention.ToDictionary(r => r.Table);
        Assert.Equal(["build", "workspace_event"], rules.Keys.Order());
        Assert.Equal(("occurred_at", 365d), (rules["workspace_event"].TimeColumn, rules["workspace_event"].DefaultDays));
        Assert.Equal(("requested_at", 90d), (rules["build"].TimeColumn, rules["build"].DefaultDays));
        Assert.Equal("workspace", WorkspaceModule.Module.Name);
        Assert.Equal(1, WorkspaceModule.Module.Version);
        Assert.Equal(StateScopeKind.Service, WorkspaceModule.Module.Scope);
        Assert.Equal(["build", "project", "publisher_key", "workspace", "workspace_event"], StateTableDefinitions.Read(WorkspaceModule.Module).Select(t => t.Name).Order());
        Assert.All(StateTableDefinitions.Read(WorkspaceModule.Module), t => Assert.NotEmpty(t.PrimaryKey));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_layout_has_every_table_in_the_namespace_of_the_module(string engine)
    {
        var (_, database, _) = Open(engine);
        Assert.Equal("workspace", database.Layout!.Namespace(WorkspaceModule.Name).Module.Name);
        Assert.Equal(1, Assert.Single(database.RegisteredModules(), m => m.Module == "workspace").Version);
        foreach (var table in new[] { "project", "workspace", "workspace_event", "build", "publisher_key" }) Assert.Equal(0, Count(database, table));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Two_services_on_one_database_each_keep_their_own_copy(string engine)
    {
        var location = _engines.Provision(engine);
        var one = new WorkspaceStore(location.Open([WorkspaceModule.Module], new StateScope(Group: "main", Service: "workspace-a"), time: new StateTestClock(Start)));
        var two = new WorkspaceStore(location.Open([WorkspaceModule.Module], new StateScope(Group: "main", Service: "workspace-b"), time: new StateTestClock(Start)));

        Assert.True(one.AddProject("orders", "app", null, null, "ada"));
        one.RecordCreated(Draft());

        Assert.Single(one.Projects());
        Assert.Empty(two.Projects());
        Assert.Empty(two.Workspaces());
    }

    // ---- projects ----

    [Theory, MemberData(nameof(Engines))]
    public void A_project_is_added_once_and_read_back(string engine)
    {
        var (store, _, _) = Open(engine);

        Assert.True(store.AddProject("orders", "app", "Orders app", "/srv/orders", "ada"));
        Assert.False(store.AddProject("orders", "app", "Another", null, "bob"));
        Assert.True(store.AddProject("billing", "app", null, null, "ada"));

        Assert.Equal(["billing", "orders"], store.Projects().Select(p => p.Id));
        var orders = store.FindProject("orders")!;
        Assert.Equal(("app", "Orders app", "/srv/orders", "ada", Start), (orders.Kind, orders.Title, orders.Path, orders.CreatedBy, orders.CreatedAt));
        Assert.Null(store.FindProject("billing")!.Path);
        Assert.Null(store.FindProject("nope"));
        Assert.Throws<ArgumentException>(() => store.AddProject("x", "agent", null, null, "ada"));
    }

    // ---- drafts and their history ----

    [Theory, MemberData(nameof(Engines))]
    public void A_draft_is_recorded_with_its_creation_and_listed_by_project(string engine)
    {
        var (store, _, _) = Open(engine);

        Assert.True(store.RecordCreated(Draft()));
        Assert.False(store.RecordCreated(Draft()));    // recorded already: nothing doubles
        store.RecordCreated(Draft(owner: "bob", name: "extra"));
        store.RecordCreated(Draft(project: "billing"));

        Assert.Equal(["ada-new-orders", "bob-extra"], store.Workspaces("orders").Select(w => w.Id));
        Assert.Equal(3, store.Workspaces().Count);
        var ada = store.Workspaces("orders")[0];
        Assert.Equal(("ada", "Ada Lovelace", "new-orders", "ws/ada-new-orders", "0123abc", Start), (ada.Owner, ada.OwnerName, ada.Name, ada.Branch, ada.BaseCommit, ada.CreatedAt));
        var events = store.Events("orders", "ada-new-orders");
        var created = Assert.Single(events);
        Assert.Equal(("created", "ada", "ws/ada-new-orders"), (created.Kind, created.Actor, created.Detail));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Committing_pushing_and_ending_a_draft_are_events_and_ending_removes_the_row(string engine)
    {
        var (store, database, clock) = Open(engine);
        store.RecordCreated(Draft());
        clock.Advance(TimeSpan.FromMinutes(5));
        store.RecordEvent("orders", "ada-new-orders", "committed", "ada", "Ada Lovelace", "Title the grid");
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(store.RecordEnded("orders", "ada-new-orders", "merged", "ann", "Ann", "fast-forward"));

        Assert.Empty(store.Workspaces("orders"));
        Assert.Equal(["merged", "committed", "created"], store.Events("orders", "ada-new-orders").Select(e => e.Kind));
        Assert.Equal("Title the grid", store.Events("orders", "ada-new-orders")[1].Detail);
        Assert.Equal(3, Count(database, "workspace_event"));
        // ending a draft that is not there changes nothing, and writes no event
        Assert.False(store.RecordEnded("orders", "ada-new-orders", "abandoned", "ada", "Ada"));
        Assert.Equal(3, Count(database, "workspace_event"));
        Assert.Throws<ArgumentException>(() => store.RecordEvent("orders", "x", "merged", "a", "A", null));
        Assert.Throws<ArgumentException>(() => store.RecordEnded("orders", "x", "committed", "a", "A"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_name_can_be_used_again_after_the_draft_is_abandoned(string engine)
    {
        var (store, _, clock) = Open(engine);
        store.RecordCreated(Draft());
        clock.Advance(TimeSpan.FromSeconds(1));
        store.RecordEnded("orders", "ada-new-orders", "abandoned", "ada", "Ada");
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(store.RecordCreated(Draft(at: Start.AddSeconds(2))));

        Assert.Single(store.Workspaces("orders"));
        Assert.Equal(["created", "abandoned", "created"], store.Events("orders").Select(e => e.Kind).Reverse());   // newest first
    }

    // ---- builds ----

    [Theory, MemberData(nameof(Engines))]
    public void A_build_is_queued_runs_and_ends(string engine)
    {
        var (store, _, clock) = Open(engine);
        var queued = store.RequestBuild("orders", "ada-new-orders", "ada", "0123abc");
        Assert.Equal(("queued", null, null), (queued.Status, queued.StartedAt, queued.FinishedAt));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(store.SetBuildStatus(queued.Id, "running"));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(store.SetBuildStatus(queued.Id, "failed", "the bundler did not start"));
        Assert.False(store.SetBuildStatus(Guid.NewGuid(), "running"));

        var build = Assert.Single(store.Builds("orders", "ada-new-orders"));
        Assert.Equal(("failed", "0123abc", "ada", "the bundler did not start"), (build.Status, build.CommitSha, build.RequestedBy, build.Message));
        Assert.Equal((Start, Start.AddSeconds(10), Start.AddSeconds(30)), (build.RequestedAt, build.StartedAt, build.FinishedAt));
        Assert.Empty(store.Builds("orders", "someone-else"));
        Assert.Throws<ArgumentException>(() => store.SetBuildStatus(queued.Id, "queued"));
        Assert.Throws<ArgumentException>(() => store.SetBuildStatus(queued.Id, "lost"));
    }

    // ---- the publisher's key reference ----

    [Theory, MemberData(nameof(Engines))]
    public void The_publishers_key_is_a_reference_that_is_set_replaced_and_removed(string engine)
    {
        var (store, _, clock) = Open(engine);
        var first = store.SetPublisherKey("main-group", "secret:dbdata:workspace:publisher:main-group", "ada");
        clock.Advance(TimeSpan.FromHours(1));
        var again = store.SetPublisherKey("main-group", "secret:dbdata:workspace:publisher:main-group-2", "bob");

        Assert.Equal(("secret:dbdata:workspace:publisher:main-group-2", "ada", Start, Start.AddHours(1)), (again.SecretRef, again.CreatedBy, again.CreatedAt, again.UpdatedAt));
        Assert.Equal(Start, first.UpdatedAt);
        Assert.Single(store.PublisherKeys());

        Assert.True(store.RemovePublisherKey("main-group"));
        Assert.False(store.RemovePublisherKey("main-group"));
        Assert.Empty(store.PublisherKeys());
    }

    [Theory, MemberData(nameof(Engines))]
    public void Anything_that_is_not_a_reference_is_refused_and_never_reaches_a_row(string engine)
    {
        var (store, database, _) = Open(engine);
        const string key = "AAAAC3NzaC1lZDI1NTE5AAAAIGkZ0q";

        foreach (var bad in new[] { key, "", "secret:", "secret: with space", "secret:" + new string('x', 200) })
            Assert.Throws<ArgumentException>(() => store.SetPublisherKey("t", bad, "ada"));

        Assert.Equal(0, Count(database, "publisher_key"));
    }

    // ---- retention ----

    [Theory, MemberData(nameof(Engines))]
    public void Events_older_than_a_year_and_builds_older_than_ninety_days_go_and_the_drafts_and_keys_stay(string engine)
    {
        var (store, database, clock) = Open(engine);
        store.RecordCreated(Draft(at: Start.AddDays(-400)));   // its creation event is as old as that
        store.RecordEvent("orders", "ada-new-orders", "committed", "ada", "Ada", "recent");
        store.RequestBuild("orders", "ada-new-orders", "ada");
        store.SetPublisherKey("t", "secret:dbdata:workspace:publisher:t", "ada");
        Assert.Equal(2, Count(database, "workspace_event"));
        clock.Advance(TimeSpan.FromDays(100));    // the build is 100 days old now

        var removed = new RetentionJob(database).Run();

        Assert.Equal(1, removed.Removed["workspace.build"]);
        Assert.Equal(1, removed.Removed["workspace.workspace_event"]);    // the creation event is dated by the draft's creation (400 days ago); the commit event was written 100 days ago and stays
        Assert.Equal(["committed"], store.Events("orders", "ada-new-orders").Select(e => e.Kind));
        Assert.Empty(store.Builds("orders"));
        Assert.Single(store.Workspaces("orders"));
        Assert.Single(store.PublisherKeys());
        clock.Advance(TimeSpan.FromDays(300));
        Assert.Equal(1, new RetentionJob(database).Run().Removed["workspace.workspace_event"]);
        Assert.Single(store.Workspaces("orders"));
        Assert.Single(store.PublisherKeys());
    }

    // ---- a format change ----

    [Theory, MemberData(nameof(Engines))]
    public void A_database_written_by_a_newer_workspace_module_is_refused_naming_the_module_the_database_and_both_versions(string engine)
    {
        var location = _engines.Provision(engine);
        var newer = new StateModule("workspace", StateScopeKind.Service, [.. WorkspaceModule.Module.Migrations, "ALTER TABLE {table:build} {{addcolumn}} unit {{key}} NULL;"], retention: WorkspaceModule.Module.Retention);
        location.Open([newer], StateScope.Standalone, appliedBy: "newer-workspace 2.0");

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([WorkspaceModule.Module], StateScope.Standalone));

        Assert.Contains("'workspace", ex.Message);
        Assert.Contains("version 2", ex.Message);
        Assert.Contains("version 1", ex.Message);
        Assert.Contains("newer-workspace 2.0", ex.Message);
        Assert.Contains("state database", ex.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_edited_first_migration_is_refused_the_way_every_shipped_migration_is(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open([WorkspaceModule.Module], StateScope.Standalone);
        var edited = new StateModule("workspace", StateScopeKind.Service, [WorkspaceModule.Module.Migrations[0].Replace("occurred_at  {{time}} NOT NULL", "occurred_at  {{time}} NULL", StringComparison.Ordinal)]);
        Assert.NotEqual(WorkspaceModule.Module.Migrations[0], edited.Migrations[0]);

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([edited], StateScope.Standalone));

        Assert.Contains("'workspace", ex.Message);
        Assert.Contains("edited", ex.Message);
    }
}
