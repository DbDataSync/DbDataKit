namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a BK01: the <c>backlog</c> module on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them): its conventions, layout and retention, numbers that are never given
/// twice, a stale write refused, the history written with each change, an attachment through the payload store, and an upgrade that keeps rows.
/// </summary>
public sealed class BacklogStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");
    private static readonly BacklogActor Ada = new("ada", "Ada Lovelace");
    private static readonly BacklogActor Bob = new("bob", "Bob");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private (BacklogStore Store, StateDatabase Database, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([BacklogModule.Module], time: clock);
        var store = new BacklogStore(database);
        Assert.True(store.CreateProject("DATA", "Data platform", null, "ada"));
        return (store, database, clock);
    }

    private static BacklogItemRow Draft(string title = "A title", string state = "backlog", long sort = 1024) =>
        new(Guid.CreateVersion7(), "DATA", 0, "story", title, null, state, "medium", null, null, "ada", null, null, sort, "[]", null, default, default, null, 1);

    private static long Count(StateDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command(BacklogModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    [Fact]
    public void The_module_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(BacklogModule.Module));
        Assert.Empty(StateConventions.Violations(BacklogModule.GroupModule));
    }

    [Fact]
    public void Nothing_has_an_age_limit_and_an_attachments_payload_belongs_to_its_row()
    {
        Assert.Empty(BacklogModule.Module.Retention);
        Assert.Equal(["attachment", "board", "comment", "history", "item", "link", "project", "sprint"], StateTableDefinitions.Read(BacklogModule.Module).Select(t => t.Name).Order());
        Assert.All(StateTableDefinitions.Read(BacklogModule.Module), t => Assert.NotEmpty(t.PrimaryKey));
        Assert.Equal("attachment", Assert.Single(BacklogModule.Module.PayloadOwners).Table);
        Assert.Equal(StateScopeKind.Install, BacklogModule.Module.Scope);
        Assert.Equal(StateScopeKind.Group, BacklogModule.GroupModule.Scope);
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_layout_has_every_table_in_the_namespace_of_the_module(string engine)
    {
        var (_, database, _) = Open(engine);
        Assert.Equal("backlog", database.Layout!.Namespace(BacklogModule.Name).Module.Name);
        Assert.Equal(1, Assert.Single(database.RegisteredModules(), m => m.Module == "backlog").Version);
        foreach (var table in new[] { "board", "item", "sprint", "comment", "link", "attachment", "history" }) Assert.Equal(0, Count(database, table));
        Assert.Equal(1, Count(database, "project"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Items_are_numbered_in_their_project_and_each_change_writes_its_history_in_the_same_transaction(string engine)
    {
        var (store, _, clock) = Open(engine);
        var first = store.InsertItem(Draft("First"), Ada)!;
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = store.InsertItem(Draft("Second", sort: 2048), Bob)!;
        Assert.Equal(("DATA-1", "DATA-2"), (first.Key, second.Key));
        Assert.Equal(first.Id, store.Item("DATA", 1)!.Id);
        Assert.Null(store.InsertItem(Draft() with { ProjectId = "NOPE" }, Ada));
        Assert.Equal(["DATA-1", "DATA-2"], store.Items(new BacklogItemQuery(Project: "DATA")).Select(i => i.Key));

        Assert.True(store.UpdateItem(first with { Title = "First, renamed", Estimate = 2.5m }, first.RowVersion, Bob, [new BacklogChange("field", "title", "First", "First, renamed"), new BacklogChange("field", "estimate", null, "2.5")]));
        var changed = store.Item(first.Id)!;
        Assert.Equal(("First, renamed", 2.5m, 2L), (changed.Title, changed.Estimate, changed.RowVersion));
        var history = store.History(first.Id);
        Assert.Equal(["created", "field", "field"], history.Select(h => h.Kind));
        Assert.Equal([1L, 2L, 3L], history.Select(h => h.Seq));
        Assert.Equal(("bob", "Bob", "title", "First", "First, renamed"), (history[1].Actor, history[1].ActorName, history[1].Field, history[1].OldValue, history[1].NewValue));
        Assert.Equal(Start, history[0].OccurredAt);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_write_on_a_stale_copy_is_refused_and_writes_no_history(string engine)
    {
        var (store, database, _) = Open(engine);
        var item = store.InsertItem(Draft(), Ada)!;
        Assert.True(store.UpdateItem(item with { Title = "By Ada" }, item.RowVersion, Ada, [new BacklogChange("field", "title", "A title", "By Ada")]));
        var rows = Count(database, "history");

        Assert.False(store.UpdateItem(item with { Title = "By Bob" }, item.RowVersion, Bob, [new BacklogChange("field", "title", "A title", "By Bob")]));

        Assert.Equal("By Ada", store.Item(item.Id)!.Title);
        Assert.Equal(rows, Count(database, "history"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Items_are_found_by_state_sprint_epic_assignee_and_words(string engine)
    {
        var (store, _, _) = Open(engine);
        var epic = store.InsertItem(Draft("Orders epic") with { ItemType = "epic" }, Ada)!;
        var sprint = new BacklogSprintRow(Guid.CreateVersion7(), "DATA", "Sprint 1", null, "planned", null, null, null, null, 1);
        store.InsertSprint(sprint);
        var a = store.InsertItem(Draft("Load 50% of the orders", sort: 2048) with { EpicId = epic.Id, SprintId = sprint.Id, Assignee = "ada", State = "ready" }, Ada)!;
        var b = store.InsertItem(Draft("Fix the report", sort: 3072) with { ItemType = "bug", Assignee = "bob" }, Ada)!;

        Assert.Equal([a.Id], store.Items(new BacklogItemQuery(Sprint: sprint.Id)).Select(i => i.Id));
        Assert.Equal([epic.Id, b.Id], store.Items(new BacklogItemQuery(NoSprint: true)).Select(i => i.Id));
        Assert.Equal([a.Id], store.Items(new BacklogItemQuery(Epic: epic.Id)).Select(i => i.Id));
        Assert.Equal([b.Id], store.Items(new BacklogItemQuery(Assignee: "bob")).Select(i => i.Id));
        Assert.Equal([a.Id], store.Items(new BacklogItemQuery(States: ["ready", "review"])).Select(i => i.Id));
        Assert.Equal([b.Id], store.Items(new BacklogItemQuery(ItemType: "bug")).Select(i => i.Id));
        Assert.Equal([a.Id], store.Items(new BacklogItemQuery(Text: "50% of")).Select(i => i.Id));
        Assert.Empty(store.Items(new BacklogItemQuery(Text: "50_of")));
        Assert.Equal(2, store.Items(new BacklogItemQuery(Limit: 2)).Count);
        Assert.Equal(3072, store.MaxSortKey("DATA"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Comments_links_and_sprints_are_kept_with_their_people_and_each_leaves_a_history_row(string engine)
    {
        var (store, _, _) = Open(engine);
        var item = store.InsertItem(Draft(), Ada)!;
        var other = store.InsertItem(Draft("Other", sort: 2048), Ada)!;

        var comment = store.AddComment(item.Id, "Looks good.", Bob);
        Assert.Equal("Bob", Assert.Single(store.Comments(item.Id)).AuthorName);
        Assert.False(store.EditComment(comment.Id, "Hijacked", "ada"), "only the author edits a comment");
        Assert.True(store.EditComment(comment.Id, "Looks fine.", "bob"));
        Assert.Equal(("Looks fine.", true), (store.Comment(comment.Id)!.Body, store.Comment(comment.Id)!.EditedAt is not null));

        var blocks = store.AddLink(item.Id, "blocks", "item", other.Id.ToString("D"), null, Ada)!;
        Assert.Null(store.AddLink(item.Id, "blocks", "item", other.Id.ToString("D"), null, Ada));
        var model = store.AddLink(item.Id, "object", "build-model", "build:orders", "the model", Ada)!;
        Assert.Equal(["ok", "ok"], store.Links(item.Id).Select(l => l.State));
        Assert.Equal([item.Id], store.LinksTo("build-model", "build:orders").Select(l => l.ItemId));
        Assert.Equal(1, store.MarkTarget("build-model", "build:orders", "dropped"));
        Assert.Equal(0, store.MarkTarget("build-model", "build:orders", "dropped"));
        Assert.Equal("dropped", Assert.Single(store.LinksTo("build-model", "build:orders")).State);
        Assert.Equal(2, store.Links(item.Id).Count);       // flagged, never deleted
        Assert.Throws<ArgumentException>(() => store.MarkTarget("build-model", "x", "gone"));
        Assert.True(store.RemoveLink(blocks.Id, Bob));
        Assert.False(store.RemoveLink(blocks.Id, Bob));

        Assert.Equal(["created", "comment", "link", "link", "link"], store.History(item.Id).Select(h => h.Kind));
        Assert.Equal(("bob", null, "item:" + other.Id.ToString("D")), (store.History(item.Id)[4].Actor, store.History(item.Id)[4].NewValue, store.History(item.Id)[4].OldValue));
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_attachment_round_trips_through_the_payload_store_and_goes_with_its_row(string engine)
    {
        var (store, database, clock) = Open(engine);
        var payloads = new PayloadStore(database, chunkSize: 1024);
        var item = store.InsertItem(Draft(), Ada)!;
        var bytes = new byte[5000];
        new Random(7).NextBytes(bytes);

        var id = Guid.CreateVersion7();
        Guid payloadId;
        using (var writer = payloads.OpenWrite(new PayloadOptions("application/pdf", BacklogModule.Name, id.ToString("D"), "ada", Label: "spec.pdf")))
        {
            writer.Write(bytes);
            writer.Complete();
            payloadId = writer.Id;
        }

        var row = store.AddAttachment(id, item.Id, "spec.pdf", "application/pdf", bytes.Length, payloadId, Ada);
        Assert.Equal(("spec.pdf", 5000L), (Assert.Single(store.Attachments(item.Id)).FileName, row.Size));
        using (var reader = payloads.OpenRead(store.Attachment(id)!.PayloadId))
        {
            using var copy = new MemoryStream();
            reader.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        // the payload stays while its row does, and goes (after the grace period) once the row is gone
        clock.Advance(TimeSpan.FromHours(2));
        Assert.DoesNotContain("payloads.orphans", new RetentionJob(database).Run().Removed.Keys);
        Assert.NotNull(payloads.Get(payloadId));
        Assert.NotNull(store.RemoveAttachment(id, Bob));
        Assert.Equal(1, new RetentionJob(database).Run().Removed["payloads.orphans"]);
        Assert.Null(payloads.Get(payloadId));
        Assert.Equal(["created", "attachment", "attachment"], store.History(item.Id).Select(h => h.Kind));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Deleting_an_item_removes_what_belongs_to_it_and_the_links_that_pointed_at_it(string engine)
    {
        var (store, database, _) = Open(engine);
        var epic = store.InsertItem(Draft("Epic") with { ItemType = "epic" }, Ada)!;
        var child = store.InsertItem(Draft("Child", sort: 2048) with { EpicId = epic.Id }, Ada)!;
        var other = store.InsertItem(Draft("Other", sort: 3072), Ada)!;
        store.AddComment(epic.Id, "Hi", Ada);
        store.AddLink(other.Id, "relates", "item", epic.Id.ToString("D"), null, Ada);
        var payload = Guid.CreateVersion7();
        store.AddAttachment(Guid.CreateVersion7(), epic.Id, "a.txt", "text/plain", 1, payload, Ada);

        Assert.Equal([payload], store.DeleteItem(epic.Id));

        Assert.Null(store.Item(epic.Id));
        Assert.Null(store.DeleteItem(epic.Id));
        Assert.Null(store.Item(child.Id)!.EpicId);
        Assert.Empty(store.Links(other.Id));
        Assert.Equal(0, Count(database, "comment") + Count(database, "attachment"));
        Assert.Empty(store.History(epic.Id));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_database_written_by_a_newer_backlog_module_is_refused_naming_the_module_and_both_versions(string engine)
    {
        var location = _engines.Provision(engine);
        var newer = new StateModule("backlog", StateScopeKind.Install, [.. BacklogModule.Module.Migrations, "ALTER TABLE {table:item} {{addcolumn}} story_points {{int}} NULL;"], dependsOn: BacklogModule.Module.DependsOn, payloadOwners: BacklogModule.Module.PayloadOwners);
        location.Open([newer], appliedBy: "newer-backlog 3.0");

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([BacklogModule.Module]));

        Assert.Contains("'backlog", ex.Message);
        Assert.Contains("version 2", ex.Message);
        Assert.Contains("version 1", ex.Message);
        Assert.Contains("newer-backlog 3.0", ex.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_upgrade_from_module_version_1_to_2_keeps_the_rows(string engine)
    {
        var location = _engines.Provision(engine);
        Guid id;
        {
            var store = new BacklogStore(location.Open([BacklogModule.Module], time: new StateTestClock(Start)));
            store.CreateProject("DATA", "Data platform", null, "ada");
            id = store.InsertItem(Draft("Survives"), Ada)!.Id;
            store.AddComment(id, "A comment", Ada);
        }

        var second = new StateModule("backlog", StateScopeKind.Install, [.. BacklogModule.Module.Migrations, "ALTER TABLE {table:item} {{addcolumn}} story_points {{int}} NULL;"], dependsOn: BacklogModule.Module.DependsOn, payloadOwners: BacklogModule.Module.PayloadOwners);
        Assert.Empty(StateConventions.Violations(second));
        var two = location.Open([second], time: new StateTestClock(Start), appliedBy: "backlog 2");

        Assert.Equal(2, Assert.Single(two.RegisteredModules(), m => m.Module == "backlog").Version);
        var again = new BacklogStore(two);
        Assert.Equal("Survives", again.Item(id)!.Title);
        Assert.Equal(["created", "comment"], again.History(id).Select(h => h.Kind));
        Assert.Single(again.Comments(id));
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_edited_migration_that_already_shipped_is_refused_naming_the_module(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open([BacklogModule.Module]);
        var edited = new StateModule("backlog", StateScopeKind.Install, [BacklogModule.Module.Migrations[0].Replace("title        {{text}} NOT NULL", "title        {{key}} NOT NULL")], dependsOn: BacklogModule.Module.DependsOn, payloadOwners: BacklogModule.Module.PayloadOwners);
        Assert.NotEqual(BacklogModule.Module.Migrations[0], edited.Migrations[0]);

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([edited]));

        Assert.Contains("'backlog'", ex.Message);
        Assert.Contains("edited", ex.Message);
    }
}
