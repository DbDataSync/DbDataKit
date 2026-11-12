using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// The serving module <c>views</c> through its own listener (plan 32 DBR11): published dashboards and reports over REST, rendered as the caller by the semantic engine over the conformance data, with the state database of each engine
/// holding the definitions, the files and the audit. The module is the real one (Kestrel, the follower, the resolver, the cache); what the host supplies is a table of tokens and a query service with a row rule.
/// </summary>
public sealed class ViewsModuleTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private static readonly JsonObject NoParameters = new();

    private static JsonObject Region(params string[] regions) => new() { ["parameters"] = new JsonObject { ["region"] = new JsonArray(regions.Select(r => (JsonNode)r).ToArray()) } };

    private static string MetricText(Reply render) => render.Object["widgets"]!["revenue"]!["value"]!["text"]!.GetValue<string>();

    private static List<string> TableRegions(Reply render) =>
        [.. render.Object["widgets"]!["by_region"]!["rows"]!.AsArray().Select(r => r!["cells"]?[0]?["text"]?.GetValue<string>() ?? r!.AsArray()[0]!["text"]!.GetValue<string>())];

    private static List<string> ControlValues(Reply render) =>
        [.. render.Object["widgets"]!["regions"]!["values"]!.AsArray().Select(v => v!["value"]!.GetValue<string>())];

    private IReadOnlyList<AuditRecord> Audited(ViewsHarness h, string? verb = null) => [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 500)).Reverse()];

    // ---- open and list

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_published_dashboard_and_report_are_listed_and_opened_with_document_layouts_and_as_of(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);

        var list = await h.GetAsync("/api/v1/dashboards", "all");
        Assert.Equal(200, list.Status);
        Assert.Equal(["sales"], list.Object["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal("Sales", list.Object["items"]![0]!["title"]!.GetValue<string>());
        Assert.Equal(["summary"], (await h.GetAsync("/api/v1/reports", "all")).Object["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));

        var opened = await h.GetAsync("/api/v1/dashboards/sales", "all");
        Assert.Equal(200, opened.Status);
        Assert.Equal("dashboard/sales@1", opened.Headers.GetValues("X-DbData-Definition").Single());
        var body = opened.Object;
        Assert.Equal(("dashboard", "sales", "Sales", "1"), (body["kind"]!.GetValue<string>(), body["name"]!.GetValue<string>(), body["title"]!.GetValue<string>(), body["version"]!.GetValue<string>()));
        Assert.Equal("2026-11-08T12:00:00Z", body["as_of"]!.GetValue<string>());
        Assert.Contains("kind: dashboard", body["text"]!.GetValue<string>());
        Assert.Equal("Sales", body["document"]!["title"]!.GetValue<string>());
        Assert.Equal("metric", body["document"]!["widgets"]!["revenue"]!["type"]!.GetValue<string>());
        Assert.Equal([("desk", "reflow", true), ("kpi", "reflow", false)], body["layouts"]!.AsArray().Select(l => (l!["name"]!.GetValue<string>(), l["type"]!.GetValue<string>(), l["default"]!.GetValue<bool>())));

        var report = await h.GetAsync("/api/v1/reports/summary", "all");
        Assert.Equal("print", report.Object["layouts"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("flow", report.Object["layouts"]![0]!["type"]!.GetValue<string>());

        // a kind is its own collection: the dashboard is not a report
        var wrong = await h.GetAsync("/api/v1/reports/sales", "all");
        Assert.Equal((404, "not_found"), (wrong.Status, wrong.Object["code"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_call_without_a_token_or_with_one_nobody_holds_is_refused_and_says_nothing_of_why()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        foreach (var token in new string?[] { null, "nobody" })
        {
            var reply = await h.GetAsync("/api/v1/dashboards/sales", token);
            Assert.Equal((401, "unauthenticated"), (reply.Status, reply.Object["code"]!.GetValue<string>()));
            Assert.Equal("Bearer", reply.Headers.WwwAuthenticate.Single().Scheme);
        }
        Assert.Equal(401, (await h.RenderAsync("nobody")).Status);
        Assert.Empty(Audited(h));
        Assert.Empty(h.Host.Received);
    }

    // ---- render as the caller

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Two_callers_whose_rows_differ_each_get_their_own_rendering(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);

        var all = await h.RenderAsync("all");
        var west = await h.RenderAsync("west");
        Assert.Equal((200, 200), (all.Status, west.Status));
        Assert.All(new[] { all, west }, r => Assert.Equal("desk", r.Object["layout"]!.GetValue<string>()));
        Assert.All(new[] { all, west }, r => Assert.True(!r.Text.Contains("\"error\""), r.Text + string.Join("\n", h.Host.Failures)));
        Assert.Equal("rendered_widgets", all.Object["kind"]!.GetValue<string>());

        // the same widgets, the same request, another caller's rows
        Assert.NotEqual(MetricText(all), MetricText(west));
        Assert.Equal(["East", "West"], TableRegions(all).Order());
        Assert.Equal(["West"], TableRegions(west));
        Assert.Contains("North", ControlValues(all));
        Assert.Equal(["West"], ControlValues(west));

        // the engine's own rows: the West's net revenue is a part of the whole
        Assert.Contains("1,600", MetricText(all));
        Assert.Contains("900", MetricText(west));

        // every request reached the serving core as the caller who asked, in the channel, with a trace
        foreach (var (context, _) in h.Host.Received)
        {
            Assert.Equal("prod", context.Channel);
            Assert.NotNull(context.TraceId);
            Assert.Equal(context.Caller.Id == "u-west", context.Caller.Claims.ContainsKey("region"));
        }
        Assert.Contains(h.Host.Received, r => r.Context.Caller.Id == "u-all");
        Assert.Contains(h.Host.Received, r => r.Context.Caller.Id == "u-west");
    }

    [Fact]
    public async Task A_parameter_a_layout_and_a_narrowed_widget_list_are_honoured()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);

        var west = await h.RenderAsync("all", Region("West"));
        Assert.Contains("900", MetricText(west));
        Assert.Equal(new[] { "East", "West" }, TableRegions(west).Order());      // the table has no filter on the parameter

        // `only`: a parameter changed, the widgets that use it resolve again
        h.Host.Received.Clear();
        var only = await h.RenderAsync("all", new JsonObject { ["only"] = new JsonArray("revenue"), ["parameters"] = Region("East")["parameters"]!.DeepClone() });
        Assert.Equal(["revenue"], only.Object["widgets"]!.AsObject().Select(w => w.Key));
        Assert.Contains("700", MetricText(only));
        Assert.Single(h.Host.Received, r => r.Request.Text.Contains("SUM(", StringComparison.Ordinal));      // the table and the control were not asked for
        Assert.DoesNotContain(h.Host.Received, r => r.Request.Text.Contains("GROUP BY", StringComparison.Ordinal));

        // a viewer's sort of a table (DBR45): the table's own order is replaced for that run; the unsorted result is as it was
        JsonObject Sort(string dir) => new() { ["only"] = new JsonArray("by_region"), ["order"] = new JsonObject { ["by_region"] = new JsonObject { ["by"] = "orders.net_revenue", ["dir"] = dir } } };
        Assert.Equal(["West", "East"], TableRegions(await h.RenderAsync("all", Sort("desc"))));
        Assert.Equal(["East", "West"], TableRegions(await h.RenderAsync("all", Sort("asc"))));
        var absent = await h.RenderAsync("all", new JsonObject { ["only"] = new JsonArray("by_region"), ["order"] = new JsonObject { ["by_region"] = new JsonObject { ["by"] = "orders.units" } } });
        Assert.Contains("does not show", absent.Object["widgets"]!["by_region"]!["message"]!.GetValue<string>());

        // a named layout, and a layout chosen for the viewport
        var named = await h.RenderAsync("all", new JsonObject { ["layout"] = "kpi" });
        Assert.Equal("kpi", named.Object["layout"]!.GetValue<string>());
        Assert.Equal(["regions", "revenue"], named.Object["widgets"]!.AsObject().Select(w => w.Key).Order());
        var phone = await h.RenderAsync("all", new JsonObject { ["viewport"] = new JsonObject { ["width"] = 390, ["height"] = 800 } });
        Assert.Equal("kpi", phone.Object["layout"]!.GetValue<string>());
        var desk = await h.RenderAsync("all", new JsonObject { ["viewport"] = new JsonObject { ["width"] = 1440, ["height"] = 900 } });
        Assert.Equal("desk", desk.Object["layout"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_that_is_not_one_is_refused_in_words()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var unknownLayout = await h.RenderAsync("all", new JsonObject { ["layout"] = "wall" });
        Assert.Equal((400, "DBD-VIEW-057"), (unknownLayout.Status, unknownLayout.Object["code"]!.GetValue<string>()));
        Assert.Contains("`desk`", unknownLayout.Object["message"]!.GetValue<string>());

        var badValue = await h.RenderAsync("all", new JsonObject { ["parameters"] = new JsonObject { ["nope"] = "x" } });
        Assert.Equal(400, badValue.Status);
        Assert.Contains("nope", badValue.Text);

        var unknownKey = await h.RenderAsync("all", new JsonObject { ["widgets"] = new JsonArray("revenue") });
        Assert.Equal(400, unknownKey.Status);
        Assert.Contains("Unknown key `widgets`", unknownKey.Object["message"]!.GetValue<string>());

        Assert.Equal(400, (await h.RenderAsync("all", new JsonObject { ["only"] = "revenue" })).Status);
        Assert.Equal(400, (await h.RenderAsync("all", new JsonObject { ["order"] = "by_region" })).Status);
        var sideways = await h.RenderAsync("all", new JsonObject { ["order"] = new JsonObject { ["by_region"] = new JsonObject { ["by"] = "orders.net_revenue", ["dir"] = "sideways" } } });
        Assert.Equal(400, sideways.Status);
        Assert.Contains("asc", sideways.Object["message"]!.GetValue<string>());
        // `expand` (DBR55): an object of widget id to rows of member values
        Assert.Equal(400, (await h.RenderAsync("all", new JsonObject { ["expand"] = "by_region" })).Status);
        var notRows = await h.RenderAsync("all", new JsonObject { ["expand"] = new JsonObject { ["by_region"] = "West" } });
        Assert.Equal(400, notRows.Status);
        Assert.Contains("`expand`", notRows.Object["message"]!.GetValue<string>());
        Assert.Equal(400, (await h.RenderAsync("all", new JsonObject { ["expand"] = new JsonObject { ["by_region"] = new JsonArray(new JsonArray(new JsonObject())) } })).Status);
        Assert.Equal(404, (await h.RenderAsync("all", name: "missing")).Status);

        var big = await h.RenderAsync("all", new JsonObject { ["parameters"] = new JsonObject { ["region"] = new string('x', 70_000) } });
        Assert.Equal((413, "too_large"), (big.Status, big.Object["code"]!.GetValue<string>()));
        Assert.Empty(h.Host.Received);
    }

    [Fact]
    public async Task A_widget_the_core_refuses_is_an_error_entry_and_the_others_render()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        h.Host.Before = (context, request) => context.Caller.Id == "u-west" && request.Text.Contains("fact_sales", StringComparison.Ordinal) && !request.Text.Contains("GROUP BY", StringComparison.Ordinal)
            ? throw new ServingDeniedException("Source 'warehouse' is not available to wendy.") : Task.CompletedTask;

        var west = await h.RenderAsync("west");
        Assert.Equal(200, west.Status);
        var entries = west.Object["widgets"]!.AsObject().ToDictionary(w => w.Key, w => w.Value!["type"]!.GetValue<string>());
        Assert.Equal("error", entries["revenue"]);
        Assert.Equal("denied", west.Object["widgets"]!["revenue"]!["code"]!.GetValue<string>());
        Assert.NotEqual("error", entries["by_region"]);
        Assert.Equal("control", entries["regions"]);
        var audit = Audited(h, "view.render").Single().Entry;
        Assert.Equal("ok", audit.Outcome);
        var detail = JsonNode.Parse(audit.Detail!)!;
        Assert.Equal(1, detail["errors"]!.GetValue<int>());
        Assert.Equal(["denied"], detail["error_codes"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    // ---- the result cache

    [Fact]
    public async Task The_cache_answers_the_same_viewer_again_and_never_another_viewer_or_an_app()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, cacheTtl: TimeSpan.FromSeconds(30), useModuleClock: true);
        var cache = h.Module.Api!.Cache!;

        var first = await h.RenderAsync("west");
        var asked = h.Host.Received.Count;
        Assert.True(asked > 0);
        Assert.Equal(0, cache.Hits);

        var second = await h.RenderAsync("west");
        Assert.Equal(asked, h.Host.Received.Count);                 // every widget came from the cache
        Assert.True(cache.Hits >= 3);
        Assert.Equal(MetricText(first), MetricText(second));

        // another caller, with other claims: nothing of the first one's is theirs
        var all = await h.RenderAsync("all");
        Assert.True(h.Host.Received.Count > asked);
        Assert.NotEqual(MetricText(first), MetricText(all));

        // the same person inside an app is another viewer (the context says which app), and the app's rows are asked for
        var before = h.Host.Received.Count;
        Assert.Equal(200, (await h.RenderAsync("app-sales")).Status);
        Assert.True(h.Host.Received.Count > before);

        // after the time to live the rows are asked for again
        h.ModuleClock.Advance(TimeSpan.FromSeconds(31));
        before = h.Host.Received.Count;
        await h.RenderAsync("west");
        Assert.True(h.Host.Received.Count > before);

        // a new version of the model's dependants is a new scope: publishing a new dashboard version does not answer from the old rows
        var scoped = h.Published.Dashboards.Find("sales")!.CacheScope;
        Assert.Contains("semantic-model:sales_catalog@1", scoped);
    }

    // ---- an app's narrowed token

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task An_apps_token_is_honoured_it_renders_what_the_manifest_names_and_nothing_else(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);

        var ok = await h.RenderAsync("app-sales");
        Assert.Equal(200, ok.Status);
        Assert.Equal(["West"], TableRegions(ok));                     // the person's rows still apply inside the app
        Assert.All(h.Host.Received, r => Assert.Equal("portal", r.Context.Caller.Claims["app"]));
        Assert.Equal(200, (await h.GetAsync("/api/v1/dashboards/sales", "app-sales")).Status);
        Assert.Single((await h.GetAsync("/api/v1/dashboards", "app-sales")).Object["items"]!.AsArray());

        h.Host.Received.Clear();
        foreach (var token in new[] { "app-other", "app-none" })
        {
            var denied = await h.RenderAsync(token);
            Assert.Equal((403, "denied"), (denied.Status, denied.Object["code"]!.GetValue<string>()));
            Assert.Contains("sales_catalog", denied.Object["message"]!.GetValue<string>());
            Assert.Equal(403, (await h.GetAsync("/api/v1/dashboards/sales", token)).Status);
            Assert.Empty((await h.GetAsync("/api/v1/dashboards", token)).Object["items"]!.AsArray());
        }
        Assert.Empty(h.Host.Received);                               // nothing was asked of the core for a refused app

        // the audit says it was an app, and which
        var rows = Audited(h, "view.render");
        Assert.Equal([("u-west", "app", "portal", "ok"), ("u-west", "app", "budget", "refused"), ("u-all", "app", "empty", "refused")],
            rows.Select(r => (r.Entry.ActorId, r.Entry.ViaKind, r.Entry.ViaId, r.Entry.Outcome)));
    }

    // ---- audit

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Every_call_is_audited_with_who_what_version_and_outcome_and_never_a_value(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);
        await h.GetAsync("/api/v1/dashboards", "all");
        await h.GetAsync("/api/v1/dashboards/sales", "all");
        await h.RenderAsync("west", Region("West"));
        await h.GetAsync("/api/v1/dashboards/missing", "west");
        var export = await h.GetAsync("/api/v1/dashboards/sales/export?format=pdf&layout=desk", "all");
        Assert.Equal((501, "export_unavailable"), (export.Status, export.Object["code"]!.GetValue<string>()));
        Assert.Contains("views.export", export.Object["message"]!.GetValue<string>());

        var rows = Audited(h).Select(r => r.Entry).ToList();
        Assert.Equal(["view.list", "view.open", "view.render", "view.open", "view.export"], rows.Select(r => r.Verb));
        Assert.Equal(["ok", "ok", "ok", "refused", "refused"], rows.Select(r => r.Outcome));
        Assert.Equal(["u-all", "u-all", "u-west", "u-west", "u-all"], rows.Select(r => r.ActorId));
        Assert.All(rows, r => Assert.Equal(("person", "api", "dashboard"), (r.ActorKind, r.ViaKind, r.TargetKind)));
        Assert.Equal([null, "sales@1", "sales@1", "missing", "sales@1"], rows.Select(r => r.TargetId));
        // the detail is JSON (a server engine keeps it as it likes, so it is read, not compared as text)
        var render = JsonNode.Parse(rows[2].Detail!)!;
        Assert.Equal("desk", render["layout"]!.GetValue<string>());
        Assert.Equal(["region"], render["parameters"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.NotNull(render["trace_id"]);
        Assert.DoesNotContain("West", rows[2].Detail);               // a parameter's name is recorded, never its value
        Assert.Equal("pdf", JsonNode.Parse(rows[4].Detail!)!["format"]!.GetValue<string>());
        Assert.All(Audited(h), r => Assert.Equal("views", r.ServiceId));
    }

    [Fact]
    public async Task An_answer_that_cannot_be_audited_is_not_given()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, audit: new ThrowingAudit());
        foreach (var reply in new[] { await h.GetAsync("/api/v1/dashboards/sales", "all"), await h.RenderAsync("all"), await h.GetAsync("/api/v1/dashboards", "all") })
        {
            Assert.Equal((500, "audit_unavailable"), (reply.Status, reply.Object["code"]!.GetValue<string>()));
            Assert.DoesNotContain("widgets", reply.Text);
            Assert.DoesNotContain("Revenue", reply.Text);
        }
    }

    // ---- following the group

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_new_version_is_served_after_the_follower_looks_and_a_rollback_goes_back(string engine)
    {
        await using var h = await ViewsHarness.StartAsync(engines, engine);
        Assert.Equal("1", (await h.GetAsync("/api/v1/dashboards/sales", "all")).Object["version"]!.GetValue<string>());

        h.Publisher.Publish(new PublishRequest("dashboard", "sales", ViewsHarness.Dashboard("Sales v2"), "ada", "prod", PublishMode.Serve, [$"semantic-model:{ViewsHarness.Model}@1"]));
        // not yet: the instance serves what it has until it looks, and its health says the group is ahead
        Assert.Equal("1", (await h.GetAsync("/api/v1/dashboards/sales", "all")).Object["version"]!.GetValue<string>());
        var behind = await h.GetAsync("/health", "all");
        Assert.Equal(200, behind.Status);
        Assert.Equal("degraded", behind.Object["state"]!.GetValue<string>());
        Assert.Equal(("dashboard", "sales", 2, 1), (behind.Object["not_served"]![0]!["kind"]!.GetValue<string>(), behind.Object["not_served"]![0]!["name"]!.GetValue<string>(), behind.Object["not_served"]![0]!["version"]!.GetValue<int>(), behind.Object["not_served"]![0]!["served"]!.GetValue<int>()));

        Assert.Equal(1, h.Module.Refresh());
        var v2 = await h.GetAsync("/api/v1/dashboards/sales", "all");
        Assert.Equal(("2", "Sales v2"), (v2.Object["version"]!.GetValue<string>(), v2.Object["title"]!.GetValue<string>()));
        Assert.Equal("ok", (await h.GetAsync("/health", null)).Object["state"]!.GetValue<string>());

        h.Publisher.Rollback("dashboard", "sales", "ada");
        h.Module.Refresh();
        Assert.Equal("1", (await h.GetAsync("/api/v1/dashboards/sales", "all")).Object["version"]!.GetValue<string>());
        Assert.Equal(["sales@1", "sales@1", "sales@2", "sales@1"], Audited(h, "view.open").Select(r => r.Entry.TargetId));
    }

    // ---- drain and health

    [Fact]
    public async Task The_module_drains_it_refuses_new_requests_finishes_the_one_in_flight_and_reports_it()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        Assert.Equal(ServingHealthState.Ok, h.Module.Health().State);

        var held = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        h.Host.Before = async (_, _) => { reached.TrySetResult(); await held.Task; };
        var inFlight = h.RenderAsync("all");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, h.Module.Api!.Health(h.Module.Health())["in_flight"]!.GetValue<int>());

        var draining = h.Module.DrainAsync();
        await Task.Delay(100);
        Assert.False(draining.IsCompleted);                          // it waits for the request in flight

        var refused = await h.GetAsync("/api/v1/dashboards/sales", "all");
        Assert.Equal((503, "draining"), (refused.Status, refused.Object["code"]!.GetValue<string>()));
        Assert.Equal("5", refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        Assert.Equal(503, (await h.RenderAsync("all")).Status);
        var health = await h.GetAsync("/health", null);
        Assert.Equal((503, "degraded", true), (health.Status, health.Object["state"]!.GetValue<string>(), health.Object["draining"]!.GetValue<bool>()));
        Assert.Contains("Draining", h.Module.Health().Detail);
        Assert.Equal(200, (await h.GetAsync("/health/live", null)).Status);

        held.SetResult();
        var finished = await inFlight;
        Assert.Equal(200, finished.Status);                          // what was running finished and was answered
        await draining.WaitAsync(TimeSpan.FromSeconds(20));

        h.Module.Resume();
        h.Host.Before = null;
        Assert.Equal(200, (await h.GetAsync("/api/v1/dashboards/sales", "all")).Status);

        await h.Module.StopAsync();
        Assert.Equal(ServingHealthState.Down, h.Module.Health().State);
    }

    [Fact]
    public async Task The_health_reports_what_is_served_the_cache_and_that_export_is_unavailable()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, cacheTtl: TimeSpan.FromSeconds(30));
        await h.RenderAsync("all");
        await h.RenderAsync("all");
        var health = await h.GetAsync("/health", null);
        var o = health.Object;
        Assert.Equal(200, health.Status);
        Assert.Equal(("views", "ok", false, 0), (o["module"]!.GetValue<string>(), o["state"]!.GetValue<string>(), o["draining"]!.GetValue<bool>(), o["in_flight"]!.GetValue<int>()));
        Assert.Equal((1, 1), (o["served"]!["dashboards"]!.GetValue<int>(), o["served"]!["reports"]!.GetValue<int>()));
        Assert.Empty(o["not_served"]!.AsArray());
        Assert.True(o["cache"]!["enabled"]!.GetValue<bool>());
        Assert.True(o["cache"]!["hits"]!.GetValue<long>() > 0);
        Assert.False(o["export"]!["available"]!.GetValue<bool>());
        Assert.Contains("export unavailable", o["export"]!["reason"]!.GetValue<string>());
        Assert.Equal("Healthy", await new HttpClient().GetStringAsync(h.Address + "/health/live"));
        Assert.Equal(ServingHealthState.Ok, h.Module.Health().State);
    }

    // ---- the registry's dependency form

    [Fact]
    public void A_models_dependency_on_its_data_source_reads_as_kind_colon_name_which_the_registry_accepts()
    {
        var database = engines.Provision(StateEngineIds.Sqlite).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: new StateTestClock(ViewsHarness.Start));
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("data-source", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(2) });
        var publisher = new DefinitionPublisher(database, kinds, null, "serving");
        publisher.Publish(new PublishRequest("data-source", "warehouse", "kind: sqlserver", "ada", "prod", PublishMode.Serve));

        // the form the Proxy wrote before DBR11 is not a dependency the registry can read; the form it writes now is
        var old = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("semantic-model", "sales_catalog", ViewsHarness.ModelContent, "ada", "prod", PublishMode.Serve, ["data-source/warehouse"])));
        Assert.Contains("not written as kind:name@version", old.Message);
        var now = publisher.Publish(new PublishRequest("semantic-model", "sales_catalog", ViewsHarness.ModelContent, "ada", "prod", PublishMode.Serve, ["data-source:warehouse"]));
        Assert.Equal(["data-source:warehouse"], now.Version.DependsOn);
        // and a dependency on a source nothing serves is refused naming it
        Assert.Contains("data-source:missing", Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("semantic-model", "other", ViewsHarness.ModelContent, "ada", "prod", PublishMode.Serve, ["data-source:missing"]))).Message);
    }
}
