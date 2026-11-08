using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// The preview of an unsaved document or widget (plan 32 DBR28): <c>POST /api/v1/views/preview</c> resolves what the editor holds as the caller, with the module's limits, refuses what a publish would refuse, audits the call
/// and stores nothing. Through the module's own listener, over the semantic engine on the conformance data, the state database of each engine holding the definitions, files and audit.
/// </summary>
public sealed class ViewsPreviewTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private const string Metric = """
        {"type": "metric", "label": "Revenue", "encodings": {"value": "orders.net_revenue"}, "query": {"model": "sales_catalog"}}
        """;

    private static JsonObject MetricWidget() => JsonNode.Parse(Metric)!.AsObject();

    private static JsonObject Draft(string title = "Draft") => JsonNode.Parse($$$"""
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

    private static Task<Reply> Preview(ViewsHarness h, string? token, JsonNode body) => h.SendAsync(HttpMethod.Post, "/api/v1/views/preview", token, body);

    private static string Text(JsonNode? entry) => entry!["value"]!["text"]!.GetValue<string>();

    private IReadOnlyList<AuditRecord> Audited(ViewsHarness h, string verb) => [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 500)).Reverse()];

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_posted_document_is_resolved_for_each_caller_with_their_own_rows(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);
        var all = await Preview(h, "all", new JsonObject { ["document"] = Draft() });
        var west = await Preview(h, "west", new JsonObject { ["document"] = Draft() });
        Assert.Equal((200, 200), (all.Status, west.Status));
        Assert.All(new[] { all, west }, r => Assert.Equal(("rendered_widgets", "desk"), (r.Object["kind"]!.GetValue<string>(), r.Object["layout"]!.GetValue<string>())));
        Assert.Contains("1,600", Text(all.Object["widgets"]!["revenue"]));
        Assert.Contains("900", Text(west.Object["widgets"]!["revenue"]));
        Assert.Equal(["East", "West"], all.Object["widgets"]!["by_region"]!["rows"]!.AsArray().Select(r => r!["cells"]?[0]?["text"]?.GetValue<string>() ?? r.AsArray()[0]!["text"]!.GetValue<string>()).Order());
        // each request reached the core as the caller who asked, in the channel, with a trace
        Assert.Contains(h.Host.Received, r => r.Context.Caller.Id == "u-all");
        Assert.Contains(h.Host.Received, r => r.Context.Caller.Id == "u-west" && r.Context.Caller.Claims.ContainsKey("region"));
        Assert.All(h.Host.Received, r => { Assert.Equal("prod", r.Context.Channel); Assert.NotNull(r.Context.TraceId); });
    }

    [Fact]
    public async Task One_widget_is_previewed_alone_with_the_parameters_of_its_document_and_a_layout_and_only_are_honoured_for_a_document()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var widget = JsonNode.Parse("""
            {"type": "metric", "label": "Revenue", "encodings": {"value": "orders.net_revenue"},
             "query": {"model": "sales_catalog", "filters": [{"attribute": "customers.region", "operator": "equals", "values": {"param": "region"}}]}}
            """)!;
        var document = JsonNode.Parse("""{"parameters": [{"name": "region", "type": "string", "for": "customers.region", "many": true, "default": []}]}""")!;
        var reply = await Preview(h, "all", new JsonObject { ["widget"] = widget.DeepClone(), ["widget_id"] = "rev", ["document"] = document.DeepClone(), ["parameters"] = new JsonObject { ["region"] = new JsonArray("East") } });
        Assert.Equal(200, reply.Status);
        Assert.Equal(["rev"], reply.Object["widgets"]!.AsObject().Select(w => w.Key));
        Assert.Contains("700", Text(reply.Object["widgets"]!["rev"]));

        // without a document the widget has no parameter to name: the loader says so, and nothing is asked of the core
        h.Host.Received.Clear();
        var undeclared = await Preview(h, "all", new JsonObject { ["widget"] = widget.DeepClone() });
        Assert.Equal((400, "invalid"), (undeclared.Status, undeclared.Object["code"]!.GetValue<string>()));
        Assert.Contains("region", undeclared.Object["message"]!.GetValue<string>());
        Assert.Empty(h.Host.Received);

        // a document: `only` asks for one widget, a named layout is the one in play
        h.Host.Received.Clear();
        var doc = Draft();
        doc["layouts"]!.AsArray().Add(JsonNode.Parse("""{"name": "kpi", "type": "reflow", "grid": {"columns": 12, "rows": 2}, "tiles": [{"widget": "revenue", "x": 0, "y": 0, "w": 12, "h": 2}]}"""));
        var only = await Preview(h, "all", new JsonObject { ["document"] = doc, ["layout"] = "kpi", ["only"] = new JsonArray("revenue") });
        Assert.Equal("kpi", only.Object["layout"]!.GetValue<string>());
        Assert.Equal(["revenue"], only.Object["widgets"]!.AsObject().Select(w => w.Key));
        Assert.Single(h.Host.Received);
    }

    [Fact]
    public async Task An_empty_only_checks_the_draft_as_a_publish_would_and_asks_the_core_for_nothing()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var ok = await Preview(h, "all", new JsonObject { ["document"] = Draft(), ["only"] = new JsonArray() });
        Assert.Equal(200, ok.Status);
        Assert.Empty(ok.Object["widgets"]!.AsObject());
        var bad = Draft();
        bad["widgets"]!["revenue"]!["encodings"]!["value"] = "orders.no_such_measure";
        Assert.Equal(400, (await Preview(h, "all", new JsonObject { ["document"] = bad, ["only"] = new JsonArray() })).Status);
        Assert.Empty(h.Host.Received);
    }

    [Fact]
    public async Task A_report_is_previewed_from_its_text_and_a_report_widget_on_a_flow_layout()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var reply = await Preview(h, "all", new JsonObject { ["kind"] = "report", ["text"] = ViewsHarness.Report() });
        Assert.Equal((200, "print"), (reply.Status, reply.Object["layout"]!.GetValue<string>()));
        Assert.Contains("1,600", Text(reply.Object["widgets"]!["revenue"]));
        var one = await Preview(h, "west", new JsonObject { ["kind"] = "report", ["widget"] = MetricWidget() });
        Assert.Equal(200, one.Status);
        Assert.Contains("900", Text(one.Object["widgets"]!["preview"]));
        // a dashboard's text sent as a report is the loader's refusal, placed in the text
        var wrong = await Preview(h, "all", new JsonObject { ["kind"] = "report", ["text"] = ViewsHarness.Dashboard() });
        Assert.Equal((400, "invalid"), (wrong.Status, wrong.Object["code"]!.GetValue<string>()));
        Assert.NotEmpty(wrong.Object["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task A_widget_the_caller_cannot_read_is_an_error_entry_and_the_others_render()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        h.Host.Before = (context, request) => context.Caller.Id == "u-west" && request.Text.Contains("fact_sales", StringComparison.Ordinal) && !request.Text.Contains("GROUP BY", StringComparison.Ordinal)
            ? throw new ServingDeniedException("Source 'warehouse' is not available to wendy.") : Task.CompletedTask;
        var west = await Preview(h, "west", new JsonObject { ["document"] = Draft() });
        Assert.Equal(200, west.Status);
        Assert.Equal(("error", "denied"), (west.Object["widgets"]!["revenue"]!["type"]!.GetValue<string>(), west.Object["widgets"]!["revenue"]!["code"]!.GetValue<string>()));
        Assert.NotEqual("error", west.Object["widgets"]!["by_region"]!["type"]!.GetValue<string>());
        var detail = JsonNode.Parse(Audited(h, "view.preview").Single().Entry.Detail!)!;
        Assert.Equal((1, "document"), (detail["errors"]!.GetValue<int>(), detail["shape"]!.GetValue<string>()));
        Assert.Equal(["denied"], detail["error_codes"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Nothing_is_written_the_registry_the_files_the_cache_and_the_list_stay_as_they_were(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine, cacheTtl: TimeSpan.FromSeconds(30));
        var heads = () => string.Join(";", ViewDefinitions.Kinds.Concat(["semantic-model"]).SelectMany(k => h.Store.CurrentOfKind(k).Select(v => $"{k}/{v.Name}@{v.Version}:{v.Hash}")).Order());
        var events = h.Store.LatestEventId();
        var before = heads();
        var served = h.Published.Dashboards.All.Select(v => v.Name + v.Version).ToList();

        for (var i = 0; i < 3; i++) Assert.Equal(200, (await Preview(h, "west", new JsonObject { ["document"] = Draft($"Draft {i}") })).Status);
        Assert.Equal(200, (await Preview(h, "all", new JsonObject { ["widget"] = MetricWidget() })).Status);

        Assert.Equal(before, heads());
        Assert.Equal(events, h.Store.LatestEventId());
        Assert.Equal(served, h.Published.Dashboards.All.Select(v => v.Name + v.Version));
        Assert.Empty(h.Store.Versions("dashboard", ViewsApi.DraftName));
        Assert.Equal(0, h.Module.Api!.Cache!.Count);                     // not the result cache either
        Assert.Equal(["sales"], (await h.GetAsync("/api/v1/dashboards", "all")).Object["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal(404, (await h.GetAsync("/api/v1/dashboards/draft", "all")).Status);
    }

    [Fact]
    public async Task What_a_publish_would_refuse_is_refused_in_the_same_words_with_the_loaders_positions()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);

        // an unknown measure: the catalog check of a publish
        var unknown = Draft();
        unknown["widgets"]!["revenue"]!["encodings"]!["value"] = "orders.no_such_measure";
        var refused = await Preview(h, "all", new JsonObject { ["document"] = unknown });
        Assert.Equal((400, "invalid"), (refused.Status, refused.Object["code"]!.GetValue<string>()));
        Assert.Contains("no_such_measure", refused.Object["message"]!.GetValue<string>());
        var publish = Assert.Throws<PublishRefusedException>(() => h.Publisher.Publish(new PublishRequest("dashboard", "draft", unknown.ToJsonString(), "ada", "prod", PublishMode.Serve, [$"semantic-model:{ViewsHarness.Model}@1"])));
        Assert.Contains("no_such_measure", publish.Message);
        Assert.Contains("orders.no_such_measure is not a catalog member", refused.Object["message"]!.GetValue<string>());
        Assert.Contains("orders.no_such_measure is not a catalog member", publish.Message);

        // a model nothing serves: the publish's dependency refusal
        var elsewhere = Draft();
        elsewhere["widgets"]!["revenue"]!["query"]!["model"] = "not_published";
        var noModel = await Preview(h, "all", new JsonObject { ["document"] = elsewhere });
        Assert.Equal(400, noModel.Status);
        Assert.Contains("not_published", noModel.Object["message"]!.GetValue<string>());

        // the loader's own findings come with their place in the text
        var bad = await Preview(h, "all", new JsonObject { ["text"] = "schemaVersion: 1\nkind: dashboard\ntitle: T\nwidgets: {}\nlayouts:\n  - {name: desk, type: reflow, default: true, grid: {columns: 12}, tiles: []}\n" });
        Assert.Equal(400, bad.Status);
        var diagnostics = bad.Object["diagnostics"]!.AsArray();
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => { Assert.StartsWith("DBD-", d!["code"]!.GetValue<string>()); Assert.NotNull(d["message"]); });
        Assert.Empty(h.Host.Received);

        // none of them was audited as a render, and each as a refused preview
        Assert.Equal(["refused", "refused", "refused"], Audited(h, "view.preview").Select(r => r.Entry.Outcome));
        Assert.Empty(Audited(h, "view.render"));
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_it_is_read_and_a_request_that_is_not_one_is_refused_in_words()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var big = await Preview(h, "all", new JsonObject { ["text"] = new string('x', 300_000) });
        Assert.Equal((413, "too_large"), (big.Status, big.Object["code"]!.GetValue<string>()));
        Assert.Contains("262144", big.Object["message"]!.GetValue<string>());
        // a render's limit is the smaller one, and a draft of 100 kilobytes is a preview's to read
        var ok = Draft();
        ok["description"] = new string('d', 100_000);
        Assert.NotEqual(413, (await Preview(h, "all", new JsonObject { ["document"] = ok })).Status);

        foreach (var (body, words) in new (JsonNode, string)[]
        {
            (new JsonObject(), "`document`"),
            (new JsonObject { ["document"] = Draft(), ["text"] = "x" }, "not both"),
            (new JsonObject { ["document"] = "text" }, "as an object"),
            (new JsonObject { ["widget"] = MetricWidget(), ["layout"] = "desk" }, "`widget` preview"),
            (new JsonObject { ["document"] = Draft(), ["widget_id"] = "x" }, "goes with a `widget`"),
            (new JsonObject { ["widget"] = MetricWidget(), ["widget_id"] = "no spaces" }, "not a name"),
            (new JsonObject { ["document"] = Draft(), ["widgets"] = new JsonArray("revenue") }, "Unknown key `widgets`"),
            (new JsonObject { ["document"] = Draft(), ["kind"] = "view" }, "`kind`"),
            (new JsonObject { ["document"] = Draft(), ["only"] = "revenue" }, "`only`"),
            (new JsonObject { ["document"] = Draft(), ["layout"] = "wall" }, "`desk`"),
        })
        {
            var reply = await Preview(h, "all", body);
            Assert.Equal(400, reply.Status);
            Assert.Contains(words, reply.Object["message"]!.GetValue<string>());
        }
        Assert.Equal(401, (await Preview(h, null, new JsonObject { ["document"] = Draft() })).Status);
        Assert.Equal(401, (await Preview(h, "nobody", new JsonObject { ["document"] = Draft() })).Status);
    }

    [Fact]
    public async Task An_apps_token_is_held_to_its_manifests_models_and_the_call_is_audited_without_the_text_or_a_value()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        Assert.Equal(200, (await Preview(h, "app-sales", new JsonObject { ["document"] = Draft() })).Status);
        Assert.All(h.Host.Received, r => Assert.Equal("portal", r.Context.Caller.Claims["app"]));
        h.Host.Received.Clear();
        foreach (var token in new[] { "app-other", "app-none" })
        {
            var denied = await Preview(h, token, new JsonObject { ["document"] = Draft() });
            Assert.Equal((403, "denied"), (denied.Status, denied.Object["code"]!.GetValue<string>()));
            Assert.Contains("sales_catalog", denied.Object["message"]!.GetValue<string>());
        }
        Assert.Empty(h.Host.Received);

        await Preview(h, "all", new JsonObject { ["widget"] = MetricWidget(), ["parameters"] = new JsonObject { ["secret"] = "do-not-record-this-value" } });
        var rows = Audited(h, "view.preview");
        Assert.Equal([("u-west", "app", "portal", "ok"), ("u-west", "app", "budget", "refused"), ("u-all", "app", "empty", "refused"), ("u-all", "api", null, "refused")],
            rows.Select(r => (r.Entry.ActorId, r.Entry.ViaKind, r.Entry.ViaId, r.Entry.Outcome)));
        Assert.All(rows, r => { Assert.Equal(("dashboard", "draft"), (r.Entry.TargetKind, r.Entry.TargetId)); Assert.DoesNotContain("do-not-record", r.Entry.Detail); Assert.DoesNotContain("orders.net_revenue", r.Entry.Detail); });
    }

    [Fact]
    public async Task An_answer_whose_audit_entry_cannot_be_written_is_not_given()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, audit: new ThrowingAudit());
        var reply = await Preview(h, "all", new JsonObject { ["document"] = Draft() });
        Assert.Equal((500, "audit_unavailable"), (reply.Status, reply.Object["code"]!.GetValue<string>()));
        Assert.DoesNotContain("1,600", reply.Text);
    }
}
