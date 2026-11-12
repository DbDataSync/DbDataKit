using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Backlog.Tests;

/// <summary>
/// Plan 27a BK01: the rules of the backlog over the state module, on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them): a transition the rules refuse is refused, a viewer changes nothing,
/// the sprint rules, an attachment through the payload store, and a history of who did what.
/// </summary>
public sealed class BacklogServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");
    private static readonly BacklogPerson Ada = new("ada", "Ada Lovelace", BacklogRole.Lead);
    private static readonly BacklogPerson Bob = new("bob", "Bob", BacklogRole.Member);
    private static readonly BacklogPerson Eve = new("eve", "Eve", BacklogRole.Viewer);

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private (BacklogService Service, BacklogStore Store, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([BacklogModule.Module], time: clock);
        var store = new BacklogStore(database);
        var service = new BacklogService(store, payloads: new PayloadStore(database, chunkSize: 1024));
        service.CreateProject(Ada, "DATA", "Data platform");
        return (service, store, clock);
    }

    private static BacklogException Refused(Action act, BacklogProblem problem)
    {
        var ex = Assert.Throws<BacklogException>(act);
        Assert.Equal(problem, ex.Problem);
        return ex;
    }

    private static BacklogItemRow Story(BacklogService service, string title = "A story", BacklogPerson? by = null, Guid? epic = null) =>
        service.CreateItem(by ?? Bob, new NewItem("DATA", "story", title, Epic: epic));

    [Theory, MemberData(nameof(Engines))]
    public void A_backlog_gets_its_first_board_and_an_item_starts_in_the_initial_state_last_in_the_order(string engine)
    {
        var (service, _, _) = Open(engine);
        Assert.Equal(["main"], service.Boards("DATA").Select(b => b.Id));
        Assert.Contains("\"in-progress\"", service.Boards("DATA")[0].ColumnsDoc);

        var a = Story(service, "First");
        var b = Story(service, "Second");

        Assert.Equal(("DATA-1", "backlog", "medium", "bob"), (a.Key, a.State, a.Priority, a.Reporter));
        Assert.True(b.SortKey > a.SortKey);
        Assert.Equal(b.Id, service.ItemByKey("data-2")!.Id);
        Assert.Null(service.ItemByKey("DATA-9"));
        Assert.Null(service.ItemByKey("nonsense"));
        Refused(() => service.CreateProject(Bob, "OTHER", "Other"), BacklogProblem.Forbidden);
        Refused(() => service.CreateProject(Ada, "data", "Again"), BacklogProblem.Invalid);
        Refused(() => service.CreateProject(Ada, "1BAD", "Bad"), BacklogProblem.Invalid);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_viewer_cannot_change_anything(string engine)
    {
        var (service, _, _) = Open(engine);
        var item = Story(service);
        var sprint = service.CreateSprint(Bob, "DATA", "Sprint 1");

        Refused(() => service.CreateItem(Eve, new NewItem("DATA", "task", "Nope")), BacklogProblem.Forbidden);
        Refused(() => service.UpdateItem(Eve, item.Id, new ItemPatch { Title = Opt.Of<string?>("Nope") }), BacklogProblem.Forbidden);
        Refused(() => service.Transition(Eve, item.Id, "ready"), BacklogProblem.Forbidden);
        Refused(() => service.Rank(Eve, item.Id, null), BacklogProblem.Forbidden);
        Refused(() => service.AddComment(Eve, item.Id, "Hi"), BacklogProblem.Forbidden);
        Refused(() => service.AddLink(Eve, item.Id, "object", "app", "app:sales"), BacklogProblem.Forbidden);
        Refused(() => service.AddAttachment(Eve, item.Id, "a.txt", null, new MemoryStream([1])), BacklogProblem.Forbidden);
        Refused(() => service.SetSprint(Eve, item.Id, sprint.Id), BacklogProblem.Forbidden);
        Refused(() => service.CreateSprint(Eve, "DATA", "S2"), BacklogProblem.Forbidden);
        Refused(() => service.StartSprint(Bob, sprint.Id), BacklogProblem.Forbidden);
        Assert.Equal(item.RowVersion, service.Detail(Eve, item.Id).Item.RowVersion);          // reading is open to a viewer
        Assert.Equal(["created"], service.Detail(Eve, item.Id).History.Select(h => h.Kind));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_transition_the_workflow_does_not_have_is_refused_in_words_that_name_where_the_item_can_go(string engine)
    {
        var (service, _, _) = Open(engine);
        var item = Story(service);

        var ex = Refused(() => service.Transition(Bob, item.Id, "review"), BacklogProblem.Invalid);

        Assert.Contains("no move from 'backlog' to 'review'", ex.Message);
        Assert.Contains("'ready'", ex.Message);
        Refused(() => service.Transition(Bob, item.Id, "elsewhere"), BacklogProblem.Invalid);
        Refused(() => service.Transition(Bob, item.Id, "backlog"), BacklogProblem.Invalid);
        Assert.Equal("backlog", service.Detail(Bob, item.Id).Item.State);
        Assert.Equal(["ready", "cancelled"], service.Detail(Bob, item.Id).Targets);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_state_that_needs_a_field_refuses_an_item_without_it_and_a_move_that_needs_a_lead_refuses_a_member(string engine)
    {
        var (service, _, _) = Open(engine);
        var item = Story(service);
        service.Transition(Bob, item.Id, "ready");

        Assert.Contains("an assignee", Refused(() => service.Transition(Bob, item.Id, "in-progress"), BacklogProblem.Invalid).Message);
        service.UpdateItem(Bob, item.Id, new ItemPatch { Assignee = Opt.Of<string?>("bob") });
        service.Transition(Bob, item.Id, "in-progress");
        Refused(() => service.UpdateItem(Bob, item.Id, new ItemPatch { Assignee = Opt.Of<string?>(null) }), BacklogProblem.Invalid);

        Assert.Contains("a resolution", Refused(() => service.Transition(Bob, item.Id, "done"), BacklogProblem.Invalid).Message);
        var done = service.Transition(Bob, item.Id, "done", "Loaded and checked.");
        Assert.Equal(("done", "Loaded and checked.", true), (done.State, done.Resolution, done.ClosedAt is not null));
        Assert.Equal(Start, done.ClosedAt);

        var ex = Refused(() => service.Transition(Bob, item.Id, "in-progress"), BacklogProblem.Forbidden);
        Assert.Contains("needs the role of a lead", ex.Message);
        Assert.Equal(["in-progress"], service.Detail(Ada, item.Id).Targets.Where(t => t != "cancelled"));

        var reopened = service.Transition(Ada, item.Id, "in-progress");
        Assert.Equal("in-progress", reopened.State);
        Assert.Null(reopened.Resolution);
        Assert.Null(reopened.ClosedAt);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Every_change_is_in_the_history_with_who_made_it_and_what_it_was(string engine)
    {
        var (service, _, clock) = Open(engine);
        var item = Story(service, "Load orders");
        clock.Advance(TimeSpan.FromHours(1));
        service.UpdateItem(Ada, item.Id, new ItemPatch { Priority = Opt.Of<string?>("high"), Estimate = Opt.Of<decimal?>(3), Labels = Opt.Of<IReadOnlyList<string>?>(["etl", "Orders", "etl"]) });
        service.Transition(Bob, item.Id, "ready");
        service.AddComment(Bob, item.Id, "Needs the new connection.");
        service.AddLink(Bob, item.Id, "object", "build-model", "build:orders");
        service.Rank(Ada, Story(service, "Other").Id, item.Id);

        var history = service.Detail(Ada, item.Id).History;

        Assert.Equal(["created", "field", "field", "field", "transition", "comment", "link"], history.Select(h => h.Kind));
        Assert.Equal(("priority", "medium", "high"), (history[1].Field, history[1].OldValue, history[1].NewValue));
        Assert.Equal(("estimate", null, "3"), (history[2].Field, history[2].OldValue, history[2].NewValue));
        Assert.Equal(("ada", "Ada Lovelace", Start + TimeSpan.FromHours(1)), (history[1].Actor, history[1].ActorName, history[1].OccurredAt));
        Assert.Equal(("transition", "backlog", "ready", "bob"), (history[4].Kind, history[4].OldValue, history[4].NewValue, history[4].Actor));
        Assert.Equal(["etl", "orders"], BacklogService.LabelsOf(service.Detail(Ada, item.Id).Item));
        Refused(() => service.UpdateItem(Ada, item.Id, new ItemPatch { Labels = Opt.Of<IReadOnlyList<string>?>(["not valid!"]) }), BacklogProblem.Invalid);
        Refused(() => service.UpdateItem(Ada, item.Id, new ItemPatch { Estimate = Opt.Of<decimal?>(2.3m) }), BacklogProblem.Invalid);
        Refused(() => service.UpdateItem(Ada, item.Id, new ItemPatch { Title = Opt.Of<string?>("  ") }), BacklogProblem.Invalid);
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_edit_on_a_stale_copy_is_a_conflict_that_names_the_item_and_changes_nothing(string engine)
    {
        var (service, _, _) = Open(engine);
        var item = Story(service);
        service.UpdateItem(Ada, item.Id, new ItemPatch { Title = Opt.Of<string?>("By Ada") });

        var ex = Refused(() => service.UpdateItem(Bob, item.Id, new ItemPatch { Title = Opt.Of<string?>("By Bob") }, expectedVersion: item.RowVersion), BacklogProblem.Conflict);

        Assert.Contains("DATA-1", ex.Message);
        Assert.Equal("By Ada", service.Detail(Bob, item.Id).Item.Title);
        var version = service.Detail(Bob, item.Id).Item.RowVersion;
        Assert.Equal(version, service.UpdateItem(Bob, item.Id, new ItemPatch { Title = Opt.Of<string?>("By Ada") }).RowVersion);   // no change, no write
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_epic_holds_items_of_its_own_backlog_and_is_not_finished_while_they_are_open(string engine)
    {
        var (service, _, _) = Open(engine);
        service.CreateProject(Ada, "OPS", "Operations");
        var epic = service.CreateItem(Bob, new NewItem("DATA", "epic", "Orders"));
        var child = Story(service, "Child", epic: epic.Id);
        var foreign = service.CreateItem(Bob, new NewItem("OPS", "story", "Elsewhere"));

        Refused(() => service.CreateItem(Bob, new NewItem("DATA", "epic", "Nested", Epic: epic.Id)), BacklogProblem.Invalid);
        Refused(() => service.CreateItem(Bob, new NewItem("DATA", "story", "Under a story", Epic: child.Id)), BacklogProblem.Invalid);
        Refused(() => service.UpdateItem(Bob, foreign.Id, new ItemPatch { Epic = Opt.Of<Guid?>(epic.Id) }), BacklogProblem.Invalid);
        Refused(() => service.UpdateItem(Bob, epic.Id, new ItemPatch { Epic = Opt.Of<Guid?>(epic.Id) }), BacklogProblem.Invalid);
        Assert.Equal([child.Id], service.Detail(Bob, epic.Id).Children.Select(c => c.Id));

        Assert.Contains("open items", Refused(() => service.Transition(Bob, epic.Id, "cancelled", "Dropped"), BacklogProblem.Invalid).Message);
        Refused(() => service.DeleteItem(Ada, epic.Id), BacklogProblem.Invalid);
        service.Transition(Bob, child.Id, "cancelled", "Not needed");
        Assert.Equal("cancelled", service.Transition(Bob, epic.Id, "cancelled", "Dropped").State);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Putting_an_item_before_another_rewrites_one_row_and_renumbers_only_when_there_is_no_room(string engine)
    {
        var (service, store, _) = Open(engine);
        var a = Story(service, "A");
        var b = Story(service, "B");
        var c = Story(service, "C");

        service.Rank(Bob, c.Id, a.Id);
        Assert.Equal(["C", "A", "B"], service.Items(new BacklogItemQuery(Project: "DATA")).Select(i => i.Title));
        var before = service.Items(new BacklogItemQuery(Project: "DATA")).ToDictionary(i => i.Title, i => i.SortKey);
        Assert.Equal(before["A"], store.Item(a.Id)!.SortKey);             // only C moved

        // halve the room until two items meet, which renumbers the backlog and keeps the order
        for (var i = 0; i < 14; i++) service.Rank(Bob, b.Id, service.Items(new BacklogItemQuery(Project: "DATA")).First(x => x.Title == "A").Id);
        service.Rank(Bob, c.Id, null);
        Assert.Equal(["B", "A", "C"], service.Items(new BacklogItemQuery(Project: "DATA")).Select(i => i.Title));
        Assert.Equal(3, service.Items(new BacklogItemQuery(Project: "DATA")).Select(i => i.SortKey).Distinct().Count());
        Refused(() => service.Rank(Bob, a.Id, Guid.NewGuid()), BacklogProblem.Invalid);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_sprint_is_planned_started_once_at_a_time_and_closed_with_the_unfinished_items_carried_on(string engine)
    {
        var (service, _, clock) = Open(engine);
        var one = service.CreateSprint(Bob, "DATA", "Sprint 1", "Load the orders");
        var two = service.CreateSprint(Bob, "DATA", "Sprint 2");
        var epic = service.CreateItem(Bob, new NewItem("DATA", "epic", "Epic"));
        var finished = service.CreateItem(Bob, new NewItem("DATA", "task", "Finished", Estimate: 3, Assignee: "bob"));
        var open = service.CreateItem(Bob, new NewItem("DATA", "task", "Open", Estimate: 5));
        foreach (var item in new[] { finished, open }) service.SetSprint(Bob, item.Id, one.Id);
        Refused(() => service.SetSprint(Bob, epic.Id, one.Id), BacklogProblem.Invalid);

        service.StartSprint(Ada, one.Id);
        Assert.Contains("still active", Refused(() => service.StartSprint(Ada, two.Id), BacklogProblem.Invalid).Message);
        Refused(() => service.StartSprint(Ada, one.Id), BacklogProblem.Invalid);
        service.Transition(Bob, finished.Id, "ready");
        service.Transition(Bob, finished.Id, "in-progress");
        service.Transition(Bob, finished.Id, "done", "Done");
        clock.Advance(TimeSpan.FromDays(14));

        Refused(() => service.CloseSprint(Bob, one.Id), BacklogProblem.Forbidden);
        Refused(() => service.CloseSprint(Ada, one.Id, carryTo: one.Id), BacklogProblem.Invalid);
        var (closed, summary) = service.CloseSprint(Ada, one.Id, carryTo: two.Id);

        Assert.Equal(("closed", Start + TimeSpan.FromDays(14)), (closed.State, closed.ClosedAt));
        Assert.Equal((1, 1, 3m, 5m, two.Id), (summary.Done, summary.Moved, summary.PointsDone, summary.PointsMoved, summary.MovedTo));
        Assert.Equal(one.Id, service.Detail(Ada, finished.Id).Item.SprintId);      // what the sprint delivered stays in it
        Assert.Equal(two.Id, service.Detail(Ada, open.Id).Item.SprintId);
        Assert.Equal(("sprint", "Sprint 1", "Sprint 2"), service.Detail(Ada, open.Id).History.Where(h => h.Kind == "sprint").Select(h => (h.Kind, h.OldValue, h.NewValue)).Last());
        Refused(() => service.SetSprint(Bob, open.Id, one.Id), BacklogProblem.Invalid);          // a closed sprint takes no more items
        Refused(() => service.CloseSprint(Ada, one.Id), BacklogProblem.Invalid);
        service.StartSprint(Ada, two.Id);                                                          // one at a time again
        Assert.Equal(["Sprint 2", "Sprint 1"], service.Sprints("DATA").Select(s => s.Name));
        service.SetSprint(Bob, open.Id, null);
        Assert.Null(service.Detail(Ada, open.Id).Item.SprintId);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Links_name_an_item_of_the_same_backlog_or_a_known_kind_of_object_workspace_or_pull_request(string engine)
    {
        var (service, store, clock) = Open(engine);
        service.CreateProject(Ada, "OPS", "Operations");
        var a = Story(service, "A");
        var b = Story(service, "B");
        var foreign = service.CreateItem(Bob, new NewItem("OPS", "story", "Elsewhere"));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(service.AddLink(Bob, a.Id, "blocks", null, b.Id.ToString()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(service.AddLink(Bob, a.Id, "blocks", null, b.Id.ToString()));
        Refused(() => service.AddLink(Bob, a.Id, "relates", null, a.Id.ToString()), BacklogProblem.Invalid);
        Refused(() => service.AddLink(Bob, a.Id, "relates", null, foreign.Id.ToString()), BacklogProblem.Invalid);
        Refused(() => service.AddLink(Bob, a.Id, "relates", null, Guid.NewGuid().ToString()), BacklogProblem.NotFound);
        Refused(() => service.AddLink(Bob, a.Id, "object", "spreadsheet", "x"), BacklogProblem.Invalid);
        Refused(() => service.AddLink(Bob, a.Id, "friends", null, "x"), BacklogProblem.Invalid);
        var replication = service.AddLink(Bob, a.Id, "object", "sync-replication", "sync:orders-nightly", "the nightly load")!;
        clock.Advance(TimeSpan.FromSeconds(1));
        service.AddLink(Bob, a.Id, "workspace", null, "build/sales/ws-12");
        clock.Advance(TimeSpan.FromSeconds(1));
        service.AddLink(Bob, a.Id, "pull-request", null, "acme/warehouse#42");

        Assert.Equal(["blocks", "object", "workspace", "pull-request"], service.Detail(Bob, a.Id).Links.Select(l => l.LinkKind));
        Assert.Equal(["workspace", "pull-request"], service.Detail(Bob, a.Id).Links.Skip(2).Select(l => l.TargetKind));
        Assert.Equal([a.Id], store.LinksTo("sync-replication", "sync:orders-nightly").Select(l => l.ItemId));
        service.RemoveLink(Bob, replication.Id);
        Refused(() => service.RemoveLink(Bob, replication.Id), BacklogProblem.NotFound);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Comments_are_edited_by_their_own_author_only(string engine)
    {
        var (service, _, _) = Open(engine);
        var item = Story(service);
        var comment = service.AddComment(Bob, item.Id, "  First thoughts.  ");

        Refused(() => service.EditComment(Ada, comment.Id, "Mine now"), BacklogProblem.Forbidden);
        service.EditComment(Bob, comment.Id, "Second thoughts.");
        Refused(() => service.AddComment(Bob, item.Id, " "), BacklogProblem.Invalid);
        Refused(() => service.EditComment(Bob, Guid.NewGuid(), "x"), BacklogProblem.NotFound);

        var shown = Assert.Single(service.Detail(Ada, item.Id).Comments);
        Assert.Equal(("Second thoughts.", "Bob", true), (shown.Body, shown.AuthorName, shown.EditedAt is not null));
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_attachment_round_trips_through_the_payload_store_is_limited_in_size_and_goes_with_its_item(string engine)
    {
        var (service, store, _) = Open(engine);
        var item = Story(service);
        var bytes = new byte[4500];
        new Random(3).NextBytes(bytes);

        var row = service.AddAttachment(Bob, item.Id, @"C:\temp\spec.pdf", "application/pdf", new MemoryStream(bytes));
        Assert.Equal(("spec.pdf", 4500L), (row.FileName, row.Size));
        var (attachment, content) = service.OpenAttachment(row.Id);
        using (content)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
            Assert.Equal("application/pdf", attachment.MediaType);
        }

        var tooBig = Refused(() => service.AddAttachment(Bob, item.Id, "big.bin", null, new MemoryStream(new byte[3000]), maxBytes: 2048), BacklogProblem.Invalid);
        Assert.Contains("big.bin", tooBig.Message);
        Assert.Single(service.Detail(Bob, item.Id).Attachments);
        Refused(() => service.AddAttachment(Bob, item.Id, " ", null, new MemoryStream([1])), BacklogProblem.Invalid);

        var payload = row.PayloadId;
        service.DeleteItem(Ada, item.Id);
        Assert.Throws<FileNotFoundException>(() => new PayloadStore(store.Database).OpenRead(payload));
        Refused(() => service.OpenAttachment(row.Id), BacklogProblem.NotFound);
    }

    [Fact]
    public void Without_a_payload_store_an_attachment_is_refused_in_words()
    {
        var database = _engines.Provision(StateEngineIds.Sqlite).Open([BacklogModule.Module], time: new StateTestClock(Start));
        var service = new BacklogService(new BacklogStore(database));
        service.CreateProject(Ada, "DATA", "Data platform");
        var item = Story(service);

        Assert.Contains("payload store", Refused(() => service.AddAttachment(Bob, item.Id, "a.txt", null, new MemoryStream([1])), BacklogProblem.Invalid).Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Items_are_imported_from_a_file_epics_first_and_exported_back(string engine)
    {
        var (service, _, _) = Open(engine);
        var file = WorkItemLoader.Load("""
            schema: work-item/1
            project: DATA
            items:
              - { id: s1, type: story, title: Load orders, epic: e1, estimate: 2, labels: [etl] }
              - { id: e1, type: epic, title: "Orders, \"v2\"", priority: high }
              - { id: s2, type: bug, title: Fix totals, assignee: ada, description: "Line one\nLine two" }
            """).File!;

        var made = BacklogImport.Import(service, Bob, file);

        Assert.Equal(["Orders, \"v2\"", "Load orders", "Fix totals"], made.Select(i => i.Title));
        Assert.Equal(made[0].Id, made[1].EpicId);
        Assert.Equal("DATA-1", made[0].Key);

        var exported = BacklogImport.Export(service, "DATA");
        var again = WorkItemLoader.Load(exported);
        Assert.True(again.Ok, string.Join("; ", again.Diagnostics));
        Assert.Equal(["Orders, \"v2\"", "Load orders", "Fix totals"], again.File!.Items.Select(i => i.Title));
        Assert.Equal("Line one\nLine two", again.File.Items[2].Description);
        Assert.Equal("DATA-1", again.File.Items[1].Epic);

        var ex = Refused(() => BacklogImport.Import(service, Eve, file), BacklogProblem.Forbidden);
        Assert.Contains("'e1'", ex.Message);
        var missing = Refused(() => BacklogImport.Import(service, Bob, file with { Project = "NOPE" }), BacklogProblem.NotFound);
        Assert.Contains("NOPE", missing.Message);
    }
}
