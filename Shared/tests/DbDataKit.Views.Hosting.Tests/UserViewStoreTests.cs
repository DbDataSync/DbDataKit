using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// The store of people's own dashboards and reports (plan 32 DBR12) on every state engine (SQLite always; SQL Server and PostgreSQL when <c>testenv</c> is up, as the State suites): the module's conventions and retention,
/// save with an expected version, history and restore, shares by person, role and everyone, delete, two groups, retention of replaced versions, and the promoted file.
/// </summary>
public sealed class UserViewStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private (UserViewStore Store, StateDatabase Database, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var database = engines.Provision(engine).Open([UserViewsModule.Module], new StateScope(Group: "main"), time: clock);
        return (new UserViewStore(database), database, clock);
    }

    private static readonly UserViewViewer Alice = new("alice", "Alice", ["analyst"]);
    private static readonly UserViewViewer Wendy = new("wendy", "Wendy", ["analyst"]);
    private static readonly UserViewViewer Bob = new("bob", "Bob", ["clerk"]);

    private static string Text(string title) => $"schemaVersion: 1\nkind: dashboard\ntitle: {title}\nwidgets: {{}}\nlayouts: []\n";

    private static long Count(StateDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command(UserViewsModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    [Fact]
    public void The_module_follows_the_conventions_and_declares_retention_for_the_history_only()
    {
        Assert.Empty(StateConventions.Violations(UserViewsModule.Module));
        var rule = Assert.Single(UserViewsModule.Module.Retention);
        Assert.Equal(("user_view_version", "saved_at", 365d), (rule.Table, rule.TimeColumn, rule.DefaultDays));
        Assert.Equal(StateScopeKind.Group, UserViewsModule.Module.Scope);
        Assert.Equal(("user_views", 1), (UserViewsModule.Module.Name, UserViewsModule.Module.Version));
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_save_is_a_version_and_a_stale_or_second_create_is_a_conflict_that_says_where_the_document_is(string engine)
    {
        var (store, _, clock) = Open(engine);
        var one = store.Save("Alice", "dashboard", "sales", "Sales", Text("one"), "alice");
        Assert.Equal(("alice", 1, "Sales"), (one.Owner, one.Version, one.Title));

        clock.Advance(TimeSpan.FromMinutes(5));
        var two = store.Save("alice", "dashboard", "sales", "Sales two", Text("two"), "alice", expectedVersion: 1);
        Assert.Equal((2, Start.AddMinutes(5)), (two.Version, two.UpdatedAt));

        var stale = Assert.Throws<UserViewConflictException>(() => store.Save("alice", "dashboard", "sales", "x", Text("stale"), "alice", expectedVersion: 1));
        Assert.Equal(2, stale.CurrentVersion);
        var again = Assert.Throws<UserViewConflictException>(() => store.Save("alice", "dashboard", "sales", "x", Text("again"), "alice"));
        Assert.Equal(2, again.CurrentVersion);
        var gone = Assert.Throws<UserViewConflictException>(() => store.Save("alice", "dashboard", "nothing", "x", Text("x"), "alice", expectedVersion: 3));
        Assert.Null(gone.CurrentVersion);
        Assert.Equal(Text("two"), store.Get("alice", "dashboard", "sales")!.Text);

        // a report and a dashboard of one name are two documents; another owner's document of that name is a third
        Assert.Equal(1, store.Save("alice", "report", "sales", "R", Text("r"), "alice").Version);
        Assert.Equal(1, store.Save("wendy", "dashboard", "sales", "W", Text("w"), "wendy").Version);
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void History_keeps_every_version_and_a_restore_is_a_new_version(string engine)
    {
        var (store, database, clock) = Open(engine);
        store.Save("alice", "dashboard", "sales", "t1", Text("one"), "alice");
        clock.Advance(TimeSpan.FromHours(1));
        store.Save("alice", "dashboard", "sales", "t2", Text("two"), "alice", 1);
        clock.Advance(TimeSpan.FromHours(1));
        store.Save("alice", "dashboard", "sales", "t3", Text("three"), "alice", 2);

        var history = store.History("alice", "dashboard", "sales");
        Assert.Equal([3, 2, 1], history.Select(v => v.Version));
        Assert.Equal([true, false, false], history.Select(v => v.Current));
        Assert.Equal([Text("three"), Text("two"), Text("one")], history.Select(v => v.Text));
        Assert.Equal([Start.AddHours(2), Start.AddHours(1), Start], history.Select(v => v.SavedAt));
        Assert.Equal(2, Count(database, "user_view_version"));   // the current version is the row of the document, not a copy

        var restored = store.Restore("alice", "dashboard", "sales", 1, "alice", expectedVersion: 3, text => text.Contains("one", StringComparison.Ordinal) ? "t1" : "?");
        Assert.Equal((4, Text("one"), "t1"), (restored.Version, restored.Text, restored.Title));
        Assert.Equal([4, 3, 2, 1], store.History("alice", "dashboard", "sales").Select(v => v.Version));

        Assert.Equal(4, Assert.Throws<UserViewConflictException>(() => store.Restore("alice", "dashboard", "sales", 2, "alice", 3, _ => "t")).CurrentVersion);
        Assert.Contains("no version 9", Assert.Throws<UserViewConflictException>(() => store.Restore("alice", "dashboard", "sales", 9, "alice", 4, _ => "t")).Message);
        Assert.Empty(store.History("wendy", "dashboard", "sales"));
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_document_is_opened_by_its_owner_and_by_those_it_is_shared_with_and_nobody_else(string engine)
    {
        var (store, _, _) = Open(engine);
        store.Save("alice", "dashboard", "sales", "Sales", Text("one"), "alice");
        Assert.NotNull(store.Find(Alice, "alice", "dashboard", "sales"));
        Assert.Null(store.Find(Wendy, "alice", "dashboard", "sales"));
        Assert.Null(store.Find(Bob, "alice", "dashboard", "sales"));
        Assert.Empty(store.List(Wendy));

        store.Share("alice", "dashboard", "sales", "group:Analyst", "alice");
        store.Share("alice", "dashboard", "sales", "group:analyst", "alice");   // twice is no change
        Assert.Equal(["group:analyst"], store.Shares("alice", "dashboard", "sales"));
        Assert.NotNull(store.Find(Wendy, "alice", "dashboard", "sales"));
        Assert.Null(store.Find(Bob, "alice", "dashboard", "sales"));
        var seen = Assert.Single(store.List(Wendy));
        Assert.Equal(("alice", "sales", false, 0), (seen.Owner, seen.Name, seen.Own, seen.SharedWith.Count));   // the owner's share list is the owner's
        Assert.Equal(["group:analyst"], Assert.Single(store.List(Alice)).SharedWith);
        Assert.Empty(store.List(Alice, shared: true, own: false));

        store.Unshare("alice", "dashboard", "sales", "group:analyst");
        store.Share("alice", "dashboard", "sales", "user:BOB", "alice");
        Assert.Null(store.Find(Wendy, "alice", "dashboard", "sales"));
        Assert.NotNull(store.Find(Bob, "alice", "dashboard", "sales"));

        store.Share("alice", "dashboard", "sales", "*", "alice");
        Assert.NotNull(store.Find(Wendy, "alice", "dashboard", "sales"));
        Assert.Throws<ArgumentException>(() => store.Share("alice", "dashboard", "sales", "everyone", "alice"));
        Assert.Contains("to share", Assert.Throws<UserViewConflictException>(() => store.Share("alice", "dashboard", "nothing", "*", "alice")).Message);
    }

    [Fact]
    public void A_person_is_shared_with_by_id_or_by_their_name_as_a_handle_and_the_owner_is_the_id_never_the_name()
    {
        var (store, _, _) = Open(StateEngineIds.Sqlite);
        var ada = new UserViewViewer("U-9F", "Ada Lovelace", []);
        var other = new UserViewViewer("u-77", "Ada Lovelace", []);   // two people may have one display name; they are two people
        Assert.Equal("ada-lovelace", UserViewViewer.Handle("  Ada   Lovelace "));
        Assert.Equal(["*", "user:u-9f", "user:ada-lovelace"], ada.Grantees);

        var made = store.Save("u-9f", "dashboard", "mine", "t", Text("one"), "Ada Lovelace", ownerName: "Ada Lovelace");
        Assert.Equal(("u-9f", "Ada Lovelace"), (made.Owner, made.OwnerName));
        Assert.NotNull(store.Find(ada, "U-9F", "dashboard", "mine"));
        Assert.Null(store.Find(other, "u-9f", "dashboard", "mine"));   // the same display name is not the owner
        store.Share("u-9f", "dashboard", "mine", "user:u-77", "Ada Lovelace");
        Assert.NotNull(store.Find(other, "u-9f", "dashboard", "mine"));
        Assert.Empty(store.List(ada, own: false));
        Assert.Equal("Ada Lovelace", Assert.Single(store.List(other, own: false)).OwnerName);
        store.Unshare("u-9f", "dashboard", "mine", "user:u-77");
        store.Share("u-9f", "dashboard", "mine", "user:ada-lovelace", "Ada Lovelace");   // by handle: both people of that name are named
        Assert.NotNull(store.Find(other, "u-9f", "dashboard", "mine"));

        // the name shown is the one of the last save
        Assert.Equal("Ada King", store.Save("u-9f", "dashboard", "mine", "t", Text("two"), "Ada King", 1, "Ada King").OwnerName);
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Delete_takes_the_history_and_the_shares_with_it_and_a_new_document_of_the_name_starts_again(string engine)
    {
        var (store, database, _) = Open(engine);
        store.Save("alice", "dashboard", "sales", "t", Text("one"), "alice");
        store.Save("alice", "dashboard", "sales", "t", Text("two"), "alice", 1);
        store.Share("alice", "dashboard", "sales", "*", "alice");
        store.Save("wendy", "dashboard", "sales", "t", Text("w"), "wendy");

        Assert.True(store.Delete("alice", "dashboard", "sales"));
        Assert.False(store.Delete("alice", "dashboard", "sales"));
        Assert.Equal((1, 0, 0), (Count(database, "user_view"), Count(database, "user_view_version"), Count(database, "user_view_share")));
        Assert.Null(store.Find(Wendy, "alice", "dashboard", "sales"));
        Assert.Equal(1, store.Save("alice", "dashboard", "sales", "t", Text("again"), "alice").Version);
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Replaced_versions_older_than_a_year_go_and_the_current_version_and_the_shares_stay(string engine)
    {
        var (store, database, clock) = Open(engine);
        store.Save("alice", "dashboard", "sales", "t", Text("one"), "alice");
        clock.Advance(TimeSpan.FromDays(300));
        store.Save("alice", "dashboard", "sales", "t", Text("two"), "alice", 1);   // version 1 (saved at Start) goes to the history now
        store.Share("alice", "dashboard", "sales", "*", "alice");
        Assert.Equal(1, Count(database, "user_view_version"));

        clock.Advance(TimeSpan.FromDays(100));   // version 1 is 400 days old; the current version is 100 days old and is not in the history table anyway
        Assert.Equal(1, new RetentionJob(database).Run().Removed["user_views.user_view_version"]);
        Assert.Equal([2], store.History("alice", "dashboard", "sales").Select(v => v.Version));

        clock.Advance(TimeSpan.FromDays(1000));   // the document itself has no age: an old document stays
        new RetentionJob(database).Run();
        Assert.Equal(2, store.Get("alice", "dashboard", "sales")!.Version);
        Assert.Single(store.Shares("alice", "dashboard", "sales"));
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_groups_on_one_database_each_keep_their_own_documents(string engine)
    {
        var location = engines.Provision(engine);
        var one = new UserViewStore(location.Open([UserViewsModule.Module], new StateScope(Group: "serving-a"), time: new StateTestClock(Start)));
        var two = new UserViewStore(location.Open([UserViewsModule.Module], new StateScope(Group: "serving-b"), time: new StateTestClock(Start)));
        one.Save("alice", "dashboard", "sales", "t", Text("a"), "alice");
        Assert.Null(two.Get("alice", "dashboard", "sales"));
        Assert.Equal(1, two.Save("alice", "dashboard", "sales", "t", Text("b"), "alice").Version);
        Assert.Equal(Text("a"), one.Get("alice", "dashboard", "sales")!.Text);
    }

    [Fact]
    public void Names_kinds_and_grantees_are_checked()
    {
        var (store, _, _) = Open(StateEngineIds.Sqlite);
        Assert.Throws<ArgumentException>(() => store.Save("alice", "dashboard", "Sales", "t", "x", "alice"));
        Assert.Throws<ArgumentException>(() => store.Save("alice", "dashboard", "1sales", "t", "x", "alice"));
        Assert.Throws<ArgumentException>(() => store.Save("alice", "dashboard", new string('a', 65), "t", "x", "alice"));
        Assert.Throws<ArgumentException>(() => store.Save("alice", "widget", "sales", "t", "x", "alice"));
        Assert.True(UserViewStore.IsName("sales-by_region2"));
        Assert.True(UserViewStore.IsGrantee("group:analyst") && UserViewStore.IsGrantee("user:alice") && UserViewStore.IsGrantee("*"));
        Assert.False(UserViewStore.IsGrantee("alice"));
    }

    [Fact]
    public void A_promoted_document_is_a_file_the_views_loader_reads_and_one_that_does_not_load_is_refused()
    {
        var (store, _, _) = Open(StateEngineIds.Sqlite);
        store.Save("alice", "dashboard", "sales", "Sales", ViewsHarness.Dashboard("Promoted"), "alice");
        store.Save("alice", "report", "summary", "Summary", ViewsHarness.Report(), "alice");
        store.Save("alice", "dashboard", "broken", "Broken", "schemaVersion: 1\nkind: report\ntitle: wrong kind\n", "alice");

        var file = store.Promote("alice", "dashboard", "sales");
        Assert.Equal("dashboards/sales.yaml", file.Path);
        var loaded = ViewsLoader.Load(file.Text, file.Path, ViewKind.Dashboard);
        Assert.NotNull(loaded.Document);
        Assert.Equal("Promoted", loaded.Document!.Title);
        Assert.Equal("reports/summary.yaml", store.Promote("alice", "report", "summary").Path);
        Assert.Contains("does not load", Assert.Throws<InvalidOperationException>(() => store.Promote("alice", "dashboard", "broken")).Message);
        Assert.NotNull(store.Get("alice", "dashboard", "sales"));   // promoting leaves the document where it was
        Assert.Throws<UserViewConflictException>(() => store.Promote("wendy", "dashboard", "sales"));
    }
}
