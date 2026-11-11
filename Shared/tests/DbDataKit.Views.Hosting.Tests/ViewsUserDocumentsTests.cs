using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// People's own dashboards and reports on the real module (plan 32 DBR12), through its own listener over the semantic engine on the conformance data and the state database of each engine: **two people** (and a third and
/// a fourth), a share to a role, to a person and to everyone, another person sees only what is shared with them **and only the rows their own access gives**, a stale edit is a conflict, history and restore, promote to a file the loader reads,
/// the audit, and what an app's token and a module with no store are told.
/// </summary>
public sealed class ViewsUserDocumentsTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private const string Mine = "/api/v1/my/dashboards/";

    private static JsonObject Doc(string title = "My sales") => JsonNode.Parse($$$"""
        {
          "schemaVersion": 1, "kind": "dashboard", "title": "{{{title}}}",
          "widgets": {
            "revenue": {"type": "metric", "label": "Revenue", "encodings": {"value": "orders.net_revenue"}, "query": {"model": "sales_catalog"}},
            "by_region": {"type": "table", "encodings": {"columns": [{"field": "customers.region"}, {"field": "orders.net_revenue"}]}, "query": {"model": "sales_catalog", "limit": 100}}
          },
          "layouts": [{"name": "desk", "type": "reflow", "default": true, "grid": {"columns": 12, "rows": 4},
                       "tiles": [{"widget": "revenue", "x": 0, "y": 0, "w": 4, "h": 2}, {"widget": "by_region", "x": 4, "y": 0, "w": 8, "h": 4}]}]
        }
        """)!.AsObject();

    private static JsonObject Save(JsonObject document, int? expected = null)
    {
        var body = new JsonObject { ["text"] = document.ToJsonString() };
        if (expected is { } e) body["expected_version"] = e;
        return body;
    }

    private static string Text(JsonNode? entry) => entry!["value"]!["text"]!.GetValue<string>();

    private static string Code(Reply r) => r.Object["code"]!.GetValue<string>();

    private static List<string> Names(Reply r) => [.. r.Object["items"]!.AsArray().Select(i => $"{i!["owner"]}/{i["name"]}")];

    private IReadOnlyList<AuditRecord> Audited(ViewsHarness h, string? verb = null) => [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 500)).Reverse()];

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_person_saves_opens_lists_and_nobody_else_sees_it_until_it_is_shared(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);
        var saved = await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc()));
        Assert.Equal(200, saved.Status);
        Assert.Equal(("u-all", "sales", "1"), (saved.Object["owner"]!.GetValue<string>(), saved.Object["name"]!.GetValue<string>(), saved.Object["version"]!.GetValue<string>()));

        var opened = await h.GetAsync(Mine + "sales", "all");
        Assert.Equal(200, opened.Status);
        Assert.Equal(("My sales", "1", true), (opened.Object["title"]!.GetValue<string>(), opened.Object["version"]!.GetValue<string>(), opened.Object["own"]!.GetValue<bool>()));
        Assert.Equal(["desk"], opened.Object["layouts"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()));
        Assert.NotNull(opened.Object["document"]);
        Assert.Empty(opened.Object["shared_with"]!.AsArray());
        Assert.Equal(["u-all/sales"], Names(await h.GetAsync("/api/v1/my/views", "all")));

        // another person: the list is empty, the document is a 404 that is the answer for a name that does not exist too, the published list is untouched
        Assert.Empty((await h.GetAsync("/api/v1/my/views?scope=all", "west")).Object["items"]!.AsArray());
        var other = await h.GetAsync(Mine + "sales?owner=u-all", "west");
        var missing = await h.GetAsync(Mine + "nothing?owner=u-all", "west");
        Assert.Equal((404, 404), (other.Status, missing.Status));
        Assert.Equal(Code(other), Code(missing));
        Assert.Equal(["sales"], (await h.GetAsync("/api/v1/dashboards", "west")).Object["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal(404, (await h.RenderAsync("west", name: "sales", kind: "my/dashboards")).Status);   // the name is not the published one's
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, Mine + "sales/render?owner=u-all", "west", new JsonObject())).Status);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_document_shared_to_a_role_is_opened_by_that_role_and_shows_each_viewer_only_the_rows_their_access_gives(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);
        await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc()));

        // alice sees everything; before the share wendy is refused
        var all = await h.SendAsync(HttpMethod.Post, Mine + "sales/render", "all", new JsonObject());
        Assert.Equal(200, all.Status);
        Assert.Contains("1,600", Text(all.Object["widgets"]!["revenue"]));
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, Mine + "sales/render?owner=u-all", "west", new JsonObject())).Status);

        var share = await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("group:analyst") });
        Assert.Equal(200, share.Status);
        Assert.Equal(["group:analyst"], share.Object["grantees"]!.AsArray().Select(g => g!.GetValue<string>()));

        // wendy (analyst, West only) and erin (analyst, East only) open it and each is shown their own rows; bob (clerk) is not in the group
        h.Host.Received.Clear();
        var west = await h.SendAsync(HttpMethod.Post, Mine + "sales/render?owner=u-all", "west", new JsonObject());
        var east = await h.SendAsync(HttpMethod.Post, Mine + "sales/render?owner=u-all", "east", new JsonObject());
        Assert.Equal((200, 200), (west.Status, east.Status));
        Assert.Contains("900", Text(west.Object["widgets"]!["revenue"]));
        Assert.Contains("700", Text(east.Object["widgets"]!["revenue"]));
        Assert.Equal(["West"], west.Object["widgets"]!["by_region"]!["rows"]!.AsArray().Select(r => r!["cells"]?[0]?["text"]?.GetValue<string>() ?? r.AsArray()[0]!["text"]!.GetValue<string>()));
        // every request reached the core as the person who asked (never as the owner), in the channel
        Assert.Contains(h.Host.Received, r => r.Context.Caller.Id == "u-west" && r.Context.Caller.Claims["region"] == "West");
        Assert.Contains(h.Host.Received, r => r.Context.Caller.Id == "u-east" && r.Context.Caller.Claims["region"] == "East");
        Assert.DoesNotContain(h.Host.Received, r => r.Context.Caller.Id == "u-all");
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, Mine + "sales/render?owner=u-all", "bob", new JsonObject())).Status);
        Assert.Equal(404, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status);

        // what wendy sees in her lists: the document, as not hers, and not who else it is shared with
        var shared = await h.GetAsync("/api/v1/my/views?scope=shared", "west");
        Assert.Equal(["u-all/sales"], Names(shared));
        Assert.False(shared.Object["items"]![0]!["own"]!.GetValue<bool>());
        Assert.Empty(shared.Object["items"]![0]!["shared_with"]!.AsArray());
        Assert.Empty((await h.GetAsync("/api/v1/my/views?scope=mine", "west")).Object["items"]!.AsArray());
        var openedByWendy = await h.GetAsync(Mine + "sales?owner=u-all", "west");
        Assert.False(openedByWendy.Object["own"]!.GetValue<bool>());
        Assert.Null(openedByWendy.Object["shared_with"]);

        // to a person, then to everyone, then to nobody
        await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("user:bob") });
        Assert.Equal((404, 200), ((await h.GetAsync(Mine + "sales?owner=u-all", "west")).Status, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status));
        await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("*") });
        Assert.Equal((200, 200), ((await h.GetAsync(Mine + "sales?owner=u-all", "west")).Status, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status));
        await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray() });
        Assert.Equal((404, 404), ((await h.GetAsync(Mine + "sales?owner=u-all", "west")).Status, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status));
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("everyone") })).Status);
    }

    [Fact]
    public async Task Only_the_owner_changes_shares_restores_promotes_or_deletes_and_a_save_never_reaches_another_persons_document()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("Alice's")));
        await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("*") });

        // wendy's calls on that name are calls on HER documents, of which there are none
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Delete, Mine + "sales", "west")).Status);
        Assert.Equal(404, (await h.GetAsync(Mine + "sales/history", "west")).Status);
        Assert.Equal(404, (await h.GetAsync(Mine + "sales/promote", "west")).Status);
        Assert.Equal(404, (await h.GetAsync(Mine + "sales/shares", "west")).Status);
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "west", new JsonObject { ["grantees"] = new JsonArray("*") })).Status);
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, Mine + "sales/restore", "west", new JsonObject { ["version"] = 1, ["expected_version"] = 1 })).Status);
        var update = await h.SendAsync(HttpMethod.Put, Mine + "sales", "west", Save(Doc("Wendy's"), expected: 1));
        Assert.Equal((404, "not_found"), (update.Status, Code(update)));

        // a save under that name is wendy's own new document: alice's is as it was
        var created = await h.SendAsync(HttpMethod.Put, Mine + "sales", "west", Save(Doc("Wendy's")));
        Assert.Equal((200, "1"), (created.Status, created.Object["version"]!.GetValue<string>()));
        Assert.Equal("Alice's", (await h.GetAsync(Mine + "sales?owner=u-all", "west")).Object["title"]!.GetValue<string>());
        Assert.Equal("Wendy's", (await h.GetAsync(Mine + "sales", "west")).Object["title"]!.GetValue<string>());
        Assert.Equal(["u-west/sales", "u-all/sales"], Names(await h.GetAsync("/api/v1/my/views", "west")));   // her own first, then what others shared
        Assert.Equal("Alice's", (await h.GetAsync(Mine + "sales", "all")).Object["title"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_stale_edit_is_a_conflict_history_lists_the_versions_and_a_restore_is_a_new_version(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);
        Assert.Equal("1", (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("one")))).Object["version"]!.GetValue<string>());
        var twice = await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("again")));
        Assert.Equal((409, "version_conflict", "1"), (twice.Status, Code(twice), twice.Object["current_version"]!.GetValue<string>()));
        Assert.Equal("2", (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("two"), expected: 1))).Object["version"]!.GetValue<string>());

        var stale = await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("stale"), expected: 1));
        Assert.Equal((409, "version_conflict", "2"), (stale.Status, Code(stale), stale.Object["current_version"]!.GetValue<string>()));
        Assert.Equal("two", (await h.GetAsync(Mine + "sales", "all")).Object["title"]!.GetValue<string>());
        // the version arrives as the number or as the text the open gave
        Assert.Equal("3", (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", new JsonObject { ["text"] = Doc("three").ToJsonString(), ["expected_version"] = "2" })).Object["version"]!.GetValue<string>());

        var history = await h.GetAsync(Mine + "sales/history", "all");
        Assert.Equal(["3", "2", "1"], history.Object["items"]!.AsArray().Select(i => i!["version"]!.GetValue<string>()));
        Assert.Equal([true, false, false], history.Object["items"]!.AsArray().Select(i => i!["current"]!.GetValue<bool>()));
        Assert.Contains("\"title\":\"one\"", (await h.GetAsync(Mine + "sales/history?version=1", "all")).Object["text"]!.GetValue<string>());
        Assert.Equal(404, (await h.GetAsync(Mine + "sales/history?version=9", "all")).Status);

        var late = await h.SendAsync(HttpMethod.Post, Mine + "sales/restore", "all", new JsonObject { ["version"] = 1, ["expected_version"] = 2 });
        Assert.Equal((409, "3"), (late.Status, late.Object["current_version"]!.GetValue<string>()));
        var restored = await h.SendAsync(HttpMethod.Post, Mine + "sales/restore", "all", new JsonObject { ["version"] = 1, ["expected_version"] = 3 });
        Assert.Equal((200, "4", "1"), (restored.Status, restored.Object["version"]!.GetValue<string>(), restored.Object["restored"]!.GetValue<string>()));
        var now = await h.GetAsync(Mine + "sales", "all");
        Assert.Equal(("one", "4"), (now.Object["title"]!.GetValue<string>(), now.Object["version"]!.GetValue<string>()));
        Assert.Equal(["4", "3", "2", "1"], (await h.GetAsync(Mine + "sales/history", "all")).Object["items"]!.AsArray().Select(i => i!["version"]!.GetValue<string>()));
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Post, Mine + "sales/restore", "all", new JsonObject { ["version"] = 1 })).Status);
    }

    [Fact]
    public async Task A_document_the_checks_refuse_is_not_saved_and_the_words_are_a_publishes()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var bad = Doc();
        bad["widgets"]!["revenue"]!["encodings"]!["value"] = "orders.no_such_measure";
        var refused = await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(bad));
        Assert.Equal((400, "invalid"), (refused.Status, Code(refused)));
        Assert.Contains("orders.no_such_measure is not a catalog member.", refused.Object["message"]!.GetValue<string>());
        Assert.Empty((await h.GetAsync("/api/v1/my/views", "all")).Object["items"]!.AsArray());

        var notYaml = await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", new JsonObject { ["text"] = "schemaVersion: 2\nkind: dashboard\n" });
        Assert.Equal(400, notYaml.Status);
        Assert.NotEmpty(notYaml.Object["diagnostics"]!.AsArray());
        var wrongKind = await h.SendAsync(HttpMethod.Put, "/api/v1/my/reports/sales", "all", Save(Doc()));
        Assert.Equal(400, wrongKind.Status);   // a dashboard's text is not a report

        Assert.Equal(400, (await h.SendAsync(HttpMethod.Put, Mine + "Bad Name", "all", Save(Doc()))).Status);
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", new JsonObject { ["text"] = Doc().ToJsonString(), ["owner"] = "wendy" })).Status);   // an owner is never named
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", new JsonObject { ["document"] = Doc() })).Status);
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", null)).Status);
        Assert.Equal(400, (await h.GetAsync("/api/v1/my/views?scope=everything", "all")).Status);
        Assert.Empty((await h.GetAsync("/api/v1/my/views", "all")).Object["items"]!.AsArray());

        // a body over the limit is refused unread
        var huge = new JsonObject { ["text"] = new string('x', 300 * 1024) };
        Assert.Equal(413, (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", huge)).Status);
    }

    [Fact]
    public async Task A_report_is_a_document_of_its_own_and_promote_gives_a_file_the_loader_reads()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var report = await h.SendAsync(HttpMethod.Put, "/api/v1/my/reports/summary", "all", new JsonObject { ["text"] = ViewsHarness.Report() });
        Assert.Equal(200, report.Status);
        await h.SendAsync(HttpMethod.Put, Mine + "summary", "all", new JsonObject { ["text"] = ViewsHarness.Dashboard("Board") });
        Assert.Equal(["u-all/summary", "u-all/summary"], Names(await h.GetAsync("/api/v1/my/views?kind=report", "all")).Concat(Names(await h.GetAsync("/api/v1/my/views?kind=dashboard", "all"))));
        var rendered = await h.SendAsync(HttpMethod.Post, "/api/v1/my/reports/summary/render", "all", new JsonObject());
        Assert.Equal(200, rendered.Status);
        Assert.Contains("1,600", Text(rendered.Object["widgets"]!["revenue"]));

        var promoted = await h.GetAsync(Mine + "summary/promote", "all");
        Assert.Equal((200, "dashboards/summary.yaml"), (promoted.Status, promoted.Object["path"]!.GetValue<string>()));
        var loaded = ViewsLoader.Load(promoted.Object["text"]!.GetValue<string>(), "dashboards/summary.yaml", ViewKind.Dashboard);
        Assert.Equal("Board", loaded.Document!.Title);
        Assert.Equal("reports/summary.yaml", (await h.GetAsync("/api/v1/my/reports/summary/promote", "all")).Object["path"]!.GetValue<string>());
        Assert.Equal(404, (await h.GetAsync(Mine + "summary/promote", "west")).Status);
        Assert.Equal("Board", (await h.GetAsync(Mine + "summary", "all")).Object["title"]!.GetValue<string>());   // the document stays
    }

    [Fact]
    public async Task Delete_removes_the_document_its_history_and_its_shares()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("one")));
        await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("two"), expected: 1));
        await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("*") });
        Assert.Equal(200, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status);

        Assert.Equal(200, (await h.SendAsync(HttpMethod.Delete, Mine + "sales", "all")).Status);
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Delete, Mine + "sales", "all")).Status);
        Assert.Equal(404, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status);
        Assert.Equal(404, (await h.GetAsync(Mine + "sales/history", "all")).Status);
        Assert.Equal("1", (await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("fresh")))).Object["version"]!.GetValue<string>());
        Assert.Empty((await h.GetAsync(Mine + "sales/shares", "all")).Object["grantees"]!.AsArray());   // the old share did not come back
        Assert.Equal(404, (await h.GetAsync(Mine + "sales?owner=u-all", "bob")).Status);
    }

    [Fact]
    public async Task A_document_whose_model_went_away_is_still_opened_so_its_owner_can_mend_it_and_is_refused_in_words_when_drawn()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        // stored behind the module's back: a text that passed when it was saved and does not now (the model it names is no longer served)
        h.UserViews.Save("u-all", "dashboard", "old", "Old", Doc("Old").ToJsonString().Replace("sales_catalog", "gone_model", StringComparison.Ordinal), "alice");
        var opened = await h.GetAsync(Mine + "old", "all");
        Assert.Equal(200, opened.Status);
        Assert.Null(opened.Object["document"]);
        Assert.Contains("gone_model", opened.Object["problem"]!.GetValue<string>());
        Assert.Contains("Old", opened.Object["text"]!.GetValue<string>());
        var drawn = await h.SendAsync(HttpMethod.Post, Mine + "old/render", "all", new JsonObject());
        Assert.Equal((400, "invalid"), (drawn.Status, Code(drawn)));
        Assert.Contains("cannot be drawn now", drawn.Object["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_call_is_audited_as_the_caller_with_the_owner_and_name_and_never_the_text()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("A very particular title")));
        await h.SendAsync(HttpMethod.Put, Mine + "sales/shares", "all", new JsonObject { ["grantees"] = new JsonArray("group:analyst") });
        await h.SendAsync(HttpMethod.Post, Mine + "sales/render?owner=u-all", "west", new JsonObject());
        await h.GetAsync(Mine + "sales?owner=u-all", "bob");   // refused
        await h.GetAsync(Mine + "sales/history", "all");
        await h.GetAsync(Mine + "sales/promote", "all");
        await h.SendAsync(HttpMethod.Put, Mine + "sales", "all", Save(Doc("stale"), expected: 7));   // conflict, refused

        var save = Audited(h, "view.my.save");
        Assert.Equal(["ok", "refused"], save.Select(r => r.Entry.Outcome));
        Assert.Equal(("u-all", "u-all/sales@1", "dashboard"), (save[0].Entry.ActorId, save[0].Entry.TargetId, save[0].Entry.TargetKind));
        Assert.Contains("u-all/sales", Audited(h, "view.my.share").Single().Entry.TargetId);
        Assert.Contains("group:analyst", Audited(h, "view.my.share").Single().Entry.Detail);
        var render = Audited(h, "view.my.render").Single();
        Assert.Equal(("u-west", "u-all/sales@1", "ok"), (render.Entry.ActorId, render.Entry.TargetId, render.Entry.Outcome));
        var refused = Audited(h, "view.my.open").Single();
        Assert.Equal(("u-bob", "refused"), (refused.Entry.ActorId, refused.Entry.Outcome));
        Assert.Single(Audited(h, "view.my.history"));
        Assert.Single(Audited(h, "view.my.promote"));
        foreach (var record in Audited(h)) Assert.DoesNotContain("A very particular title", record.Entry.Detail ?? "");
    }

    [Fact]
    public async Task A_missing_token_is_401_an_apps_token_has_no_personal_documents_and_a_module_with_no_store_says_so()
    {
        await using (var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite))
        {
            Assert.Equal(401, (await h.GetAsync("/api/v1/my/views", null)).Status);
            Assert.Equal(401, (await h.GetAsync("/api/v1/my/views", "nobody")).Status);
            await h.SendAsync(HttpMethod.Put, Mine + "sales", "west", Save(Doc()));
            var app = await h.GetAsync(Mine + "sales", "app-sales");
            Assert.Equal((403, "denied"), (app.Status, Code(app)));
            Assert.Equal(403, (await h.SendAsync(HttpMethod.Put, Mine + "sales2", "app-sales", Save(Doc()))).Status);
            Assert.Equal(403, (await h.GetAsync("/api/v1/my/views", "app-sales")).Status);
            Assert.Equal(["u-west/sales"], Names(await h.GetAsync("/api/v1/my/views", "west")));   // the app is wendy's, and still did not touch her documents
        }
        await using (var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, personalDocuments: false))
        {
            var none = await h.GetAsync("/api/v1/my/views", "all");
            Assert.Equal((404, "not_found"), (none.Status, Code(none)));
            Assert.Contains("user_views", none.Object["message"]!.GetValue<string>());
        }
    }
}
