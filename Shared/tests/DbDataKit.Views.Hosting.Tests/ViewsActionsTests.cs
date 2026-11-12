using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>What the host's store does for an action: a test double that records the calls and answers as told.</summary>
internal sealed class FakeActions : IViewsActions
{
    public List<ActionCall> Calls { get; } = [];
    public ActionResult Answer { get; set; } = ActionResult.Applied();
    public ActionForecast? Forecast { get; set; } = new(ActionWill.Wait, "This will wait for approval by the sales owner.");
    public TaskCompletionSource? Gate { get; set; }
    public List<BatchCall> Batches { get; } = [];
    public BatchResult BatchAnswer { get; set; } = new(ActionStatus.Applied, 0);

    public Task<ActionForecast?> ForecastAsync(ViewsPrincipal who, ActionDefinition action, CancellationToken cancellationToken) => Task.FromResult(Forecast);

    public Task<ActionForecast?> ForecastBatchAsync(ViewsPrincipal who, string model, CancellationToken cancellationToken) => Task.FromResult(Forecast);

    public async Task<BatchResult> RunBatchAsync(BatchCall call, CancellationToken cancellationToken)
    {
        lock (Batches) Batches.Add(call);
        if (Gate is { } gate) await gate.Task;
        return BatchAnswer.Status is ActionStatus.Applied or ActionStatus.Waiting ? BatchAnswer with { Count = call.Changes.Count } : BatchAnswer;
    }

    public async Task<ActionResult> RunAsync(ActionCall call, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add(call);
        if (Gate is { } gate) await gate.Task;
        return Answer;
    }
}

/// <summary>Actions of a published dashboard run as the viewer through the module (plan 32 DBR53): the change derived from the document, the answers' statuses, the idempotency key, the app's scope and the audit.</summary>
public sealed class ViewsActionsTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private static string OrdersDashboard() => $$$"""
        schemaVersion: 1
        kind: dashboard
        title: Orders
        parameters:
          - {name: region, type: string}
        links:
          close-order:
            change: {model: sales, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {status: Closed}}
            confirm: {title: "Close order {{orders.number}}"}
          set-status:
            change: {model: sales, entity: orders, edit: {key: $orders.id}, set: {status: $ask.status, units: $ask.units}}
            ask: {status: {label: Status, choices: [Open, Held]}, units: {type: integer, required: false}}
          bump:
            change: {model: sales, entity: orders, edit: {key: $orders.id, version: $orders.version}, set: {units: $ask.units}}
            ask: {units: {type: integer}}
            stage: true
          flag:
            change: {model: sales, entity: orders, edit: {key: $orders.id}, set: {flag: true}}
            stage: true
          drop:
            change: {model: sales, entity: orders, delete: {key: $orders.id, version: $orders.version}}
            stage: true
          other:
            change: {model: crm, entity: accounts, edit: {key: $orders.id}, set: {tier: Gold}}
            stage: true
          rerun:
            task: {package: nightly, task: load, inputs: {number: $orders.number, region: $params.region}}
          go: {page: detail}
        widgets:
          orders:
            type: table
            encodings: {columns: [{field: customers.region}, {field: orders.net_revenue}]}
            query: {model: {{{ViewsHarness.Model}}}, limit: 100}
        layouts:
          - {name: desk, type: reflow, default: true, grid: {columns: 12, rows: 6}, tiles: [{widget: orders, x: 0, y: 0, w: 12, h: 6}]}
        """;

    private async Task<(ViewsHarness H, FakeActions Fake)> StartAsync(bool withActions = true)
    {
        var fake = new FakeActions();
        var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, actions: withActions ? fake : null);
        h.Publisher.Publish(new PublishRequest("dashboard", "orders", OrdersDashboard(), "ada", "prod", PublishMode.Serve, [$"semantic-model:{ViewsHarness.Model}@1"]));
        h.Module.Refresh();
        return (h, fake);
    }

    private static JsonObject Request(string widget = "orders", string key = "k-1", JsonObject? row = null, JsonObject? answers = null, JsonObject? parameters = null)
    {
        var body = new JsonObject { ["widget"] = widget, ["idempotency_key"] = key, ["row"] = row ?? new JsonObject { ["orders.id"] = 42, ["orders.version"] = 7, ["orders.number"] = "SO-42" } };
        if (answers is not null) body["answers"] = answers;
        if (parameters is not null) body["parameters"] = parameters;
        return body;
    }

    private static Task<Reply> Act(ViewsHarness h, string token, string link, JsonObject? body, string suffix = "") => h.SendAsync(HttpMethod.Post, $"/api/v1/dashboards/orders/actions/{link}{suffix}", token, body);

    [Fact]
    public async Task An_action_is_derived_from_the_document_and_run_as_the_viewer_with_the_rows_version()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var reply = await Act(h, "west", "close-order", Request());
        Assert.Equal(200, reply.Status);
        Assert.Equal("applied", reply.Object["status"]!.GetValue<string>());
        var call = Assert.Single(fake.Calls);
        Assert.Equal("u-west", call.Who.Caller.Id);
        Assert.Equal(("change", "edit", "sales", "orders"), (call.Action.Kind, call.Action.Operation, call.Action.Model, call.Action.Entity));
        Assert.Equal(42L, call.Action.TypedKey["key"]!.GetValue<long>());
        Assert.Equal(7L, call.Action.TypedVersion);
        Assert.Equal("Closed", call.Action.TypedWrites["status"]!.GetValue<string>());
        Assert.Equal("widget orders, link close-order", call.Via);
        Assert.Equal(("orders", 1, "k-1"), (call.Document, call.Version, call.IdempotencyKey));
    }

    [Fact]
    public async Task A_page_cannot_say_what_to_write_only_the_link_and_the_row()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var body = Request();
        body["set"] = new JsonObject { ["status"] = "Open" };
        var reply = await Act(h, "west", "close-order", body);
        Assert.Equal((400, "invalid"), (reply.Status, reply.Object["code"]!.GetValue<string>()));
        Assert.Contains("Unknown key `set`", reply.Object["message"]!.GetValue<string>());
        Assert.Empty(fake.Calls);
        // a row value that is not the clicked row's is the Store's to refuse as the viewer; the written status comes from the document whatever the row says
        var forged = Request(row: new JsonObject { ["orders.id"] = 1, ["orders.version"] = 1, ["status"] = "Open" });
        Assert.Equal(200, (await Act(h, "west", "close-order", forged, "")).Status);
        Assert.Equal("Closed", fake.Calls.Single().Action.TypedWrites["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_outcome_of_the_store_has_its_status_and_the_brokers_reasons_come_through_by_field()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var n = 0;
        async Task<Reply> Send() => await Act(h, "west", "close-order", Request(key: $"k-{++n}"));

        fake.Answer = new ActionResult(ActionStatus.Waiting, RequestId: "cr-9");
        var waiting = await Send();
        Assert.Equal((202, "waiting", "cr-9"), (waiting.Status, waiting.Object["status"]!.GetValue<string>(), waiting.Object["request_id"]!.GetValue<string>()));

        fake.Answer = new ActionResult(ActionStatus.Conflict, "The row was changed by someone else (now version 8).");
        var conflict = await Send();
        Assert.Equal((409, "conflict"), (conflict.Status, conflict.Object["status"]!.GetValue<string>()));
        Assert.Contains("version 8", conflict.Object["message"]!.GetValue<string>());

        fake.Answer = new ActionResult(ActionStatus.Refused, "The change breaks the model's rules.", Violations: [new ActionViolation("allowed", "status", "Closed is not allowed.")]);
        var refused = await Send();
        Assert.Equal(422, refused.Status);
        Assert.Equal(("allowed", "status", "Closed is not allowed."), (refused.Object["violations"]![0]!["rule"]!.GetValue<string>(), refused.Object["violations"]![0]!["attribute"]!.GetValue<string>(), refused.Object["violations"]![0]!["message"]!.GetValue<string>()));

        fake.Answer = new ActionResult(ActionStatus.Denied, "This needs the author role on this group.");
        Assert.Equal(403, (await Send()).Status);
        fake.Answer = new ActionResult(ActionStatus.Started, RunId: "run-1");
        var started = await Send();
        Assert.Equal((202, "run-1"), (started.Status, started.Object["run_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_double_click_is_one_change_and_a_refusal_can_be_sent_again_with_the_same_key()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        fake.Gate = new TaskCompletionSource();
        var first = Act(h, "west", "close-order", Request(key: "same"));
        var second = Act(h, "west", "close-order", Request(key: "same"));
        while (true) { lock (fake.Calls) if (fake.Calls.Count > 0) break; await Task.Delay(10); }
        await Task.Delay(100);
        fake.Gate.SetResult();
        var replies = await Task.WhenAll(first, second);
        Assert.All(replies, r => Assert.Equal(200, r.Status));
        Assert.Single(fake.Calls);                                   // the second waited for the first and got its answer
        Assert.Equal(200, (await Act(h, "west", "close-order", Request(key: "same"))).Status);
        Assert.Single(fake.Calls);                                   // and a later repeat did nothing again
        Assert.Equal(200, (await Act(h, "west", "close-order", Request(key: "other"))).Status);
        Assert.Equal(2, fake.Calls.Count);
        // another person with the same key is another call
        Assert.Equal(200, (await Act(h, "all", "close-order", Request(key: "same"))).Status);
        Assert.Equal(3, fake.Calls.Count);

        fake.Answer = new ActionResult(ActionStatus.Conflict, "moved");
        Assert.Equal(409, (await Act(h, "west", "close-order", Request(key: "retry"))).Status);
        fake.Answer = ActionResult.Applied();
        Assert.Equal(200, (await Act(h, "west", "close-order", Request(key: "retry"))).Status);   // the conflict was not kept
        Assert.Equal(5, fake.Calls.Count);
    }

    [Fact]
    public async Task The_row_the_answers_and_the_link_are_checked_before_anything_is_sent()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var noKey = await Act(h, "west", "close-order", Request(row: new JsonObject { ["orders.number"] = "SO-1" }));
        Assert.Equal((400, "missing_values"), (noKey.Status, noKey.Object["code"]!.GetValue<string>()));
        Assert.Contains("the row key", noKey.Object["message"]!.GetValue<string>());

        var noAnswer = await Act(h, "west", "set-status", Request());
        Assert.Equal((400, "invalid_answer"), (noAnswer.Status, noAnswer.Object["code"]!.GetValue<string>()));
        Assert.Equal(400, (await Act(h, "west", "set-status", Request(answers: new JsonObject { ["status"] = "Lost" }))).Status);
        Assert.Equal(400, (await Act(h, "west", "set-status", Request(answers: new JsonObject { ["status"] = "Held", ["units"] = "1.5" }))).Status);
        Assert.Empty(fake.Calls);

        Assert.Equal(200, (await Act(h, "west", "set-status", Request(answers: new JsonObject { ["status"] = "Held", ["units"] = "12" }))).Status);
        var written = fake.Calls.Single().Action.TypedWrites;
        Assert.Equal(("Held", 12L), (written["status"]!.GetValue<string>(), written["units"]!.GetValue<long>()));

        Assert.Equal((404, "unknown_action"), ((await Act(h, "west", "nope", Request())).Status, (await Act(h, "west", "nope", Request())).Object["code"]!.GetValue<string>()));
        Assert.Equal(404, (await Act(h, "west", "go", Request())).Status);              // a link that is not an action
        Assert.Equal(400, (await Act(h, "west", "close-order", Request(widget: "nothing"))).Status);
        Assert.Equal(400, (await Act(h, "west", "close-order", Request(key: new string('k', 101)))).Status);
        Assert.Equal(400, (await Act(h, "west", "close-order", new JsonObject { ["widget"] = "orders", ["row"] = new JsonObject() })).Status);   // no key
        Assert.Equal(401, (await Act(h, "nobody", "close-order", Request())).Status);
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, "/api/v1/dashboards/none/actions/close-order", "west", Request())).Status);
    }

    [Fact]
    public async Task A_task_is_run_with_its_inputs_from_the_row_and_the_parameters()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var reply = await Act(h, "west", "rerun", Request(parameters: new JsonObject { ["region"] = "EMEA" }));
        Assert.Equal(200, reply.Status);
        var action = fake.Calls.Single().Action;
        Assert.Equal(("task", "run", "nightly", "load"), (action.Kind, action.Operation, action.Package, action.Task));
        Assert.Equal(("SO-42", "EMEA"), (action.TypedWrites["number"]!.GetValue<string>(), action.TypedWrites["region"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_apps_token_may_change_only_what_its_manifest_gives_write_for_and_the_audit_names_the_app()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var denied = await Act(h, "app-sales", "close-order", Request());          // the model is allowed, but the token carries no write scope
        Assert.Equal((403, "action_not_allowed"), (denied.Status, denied.Object["code"]!.GetValue<string>()));
        Assert.Empty(fake.Calls);
        var allowed = await Act(h, "app-writes", "close-order", Request());
        Assert.Equal(200, allowed.Status);
        Assert.Equal("sales-portal 3, widget orders, link close-order", fake.Calls.Single().Via);
        // a task is not narrowed by the manifest's models here: the runner decides by the viewer's role
        Assert.Equal(200, (await Act(h, "app-sales", "rerun", Request(parameters: new JsonObject { ["region"] = "x" }))).Status);
    }

    [Fact]
    public async Task What_will_happen_is_asked_before_the_person_confirms()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var reply = await Act(h, "west", "close-order", null, "/forecast");
        Assert.Equal(200, reply.Status);
        Assert.Equal("wait", reply.Object["will"]!.GetValue<string>());
        Assert.Contains("approval", reply.Object["note"]!.GetValue<string>());
        fake.Forecast = new ActionForecast(ActionWill.Apply);
        Assert.Equal("apply", (await Act(h, "west", "close-order", null, "/forecast")).Object["will"]!.GetValue<string>());
        Assert.Equal(403, (await Act(h, "app-sales", "close-order", null, "/forecast")).Status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Every_call_is_audited_without_a_value_and_a_host_with_no_store_says_so()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        await Act(h, "west", "close-order", Request(row: new JsonObject { ["orders.id"] = 4242, ["orders.version"] = 3, ["orders.number"] = "SECRET-NUMBER" }));
        fake.Answer = new ActionResult(ActionStatus.Conflict, "moved");
        await Act(h, "west", "close-order", Request(key: "k-2"));
        var rows = h.Audit.Read(new AuditQuery(Verb: ViewsAuditVerbs.Action, Limit: 50)).Reverse().Select(r => r.Entry).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal([AuditEvent.Ok, AuditEvent.Refused], rows.Select(r => r.Outcome));
        Assert.Equal("orders@1", rows[0].TargetId);
        Assert.Equal("u-west", rows[0].ActorId);
        var detail = JsonNode.Parse(rows[0].Detail!)!;
        Assert.Equal(("close-order", "orders", "change:edit", "sales.orders", "applied"), (detail["link"]!.GetValue<string>(), detail["widget"]!.GetValue<string>(), detail["action"]!.GetValue<string>(), detail["target"]!.GetValue<string>(), detail["status"]!.GetValue<string>()));
        Assert.DoesNotContain("4242", rows[0].Detail);
        Assert.DoesNotContain("SECRET-NUMBER", rows[0].Detail);
        Assert.DoesNotContain("Closed", rows[0].Detail);

        var (h2, _) = await StartAsync(withActions: false);
        await using var _2 = h2;
        var none = await Act(h2, "west", "close-order", Request());
        Assert.Equal((501, "actions_unavailable"), (none.Status, none.Object["code"]!.GetValue<string>()));
    }

    // ---- batches (DBR53b)

    private static JsonObject Line(string link, int id, string? units = null, string widget = "orders") => new()
    {
        ["link"] = link, ["widget"] = widget,
        ["row"] = new JsonObject { ["orders.id"] = id, ["orders.version"] = 1, ["orders.number"] = $"SO-{id}" },
        ["answers"] = units is null ? new JsonObject() : new JsonObject { ["units"] = units },
    };

    private static JsonObject Batch(string key, params JsonObject[] lines) => new() { ["idempotency_key"] = key, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) };

    private static Task<Reply> Submit(ViewsHarness h, string token, JsonObject body, string suffix = "") => h.SendAsync(HttpMethod.Post, $"/api/v1/dashboards/orders/batches{suffix}", token, body);

    [Fact]
    public async Task Thirty_staged_edits_are_one_call_to_the_store_of_thirty_changes_as_the_viewer()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var lines = Enumerable.Range(1, 30).Select(i => Line("bump", i, (i * 10).ToString())).ToArray();
        var reply = await Submit(h, "west", Batch("b-1", lines));
        Assert.Equal(200, reply.Status);
        Assert.Equal(("applied", 30), (reply.Object["status"]!.GetValue<string>(), reply.Object["count"]!.GetValue<int>()));
        var call = Assert.Single(fake.Batches);
        Assert.Equal(("u-west", "sales", 30, 30), (call.Who.Caller.Id, call.Model, call.Changes.Count, call.LineCount));
        Assert.Equal(10L, call.Changes[0].Action.TypedWrites["units"]!.GetValue<long>());
        Assert.Equal(300L, call.Changes[29].Action.TypedWrites["units"]!.GetValue<long>());
        Assert.Equal([0], call.Changes[0].Lines);
        Assert.StartsWith("widget orders, batch ", call.Via);
        Assert.Equal("b-1", call.IdempotencyKey);
        Assert.Empty(fake.Calls);                                                  // not thirty single calls
        // the audit says what it was without a value
        var row = h.Audit.Read(new AuditQuery(Verb: ViewsAuditVerbs.Batch, Limit: 5)).Single().Entry;
        var detail = JsonNode.Parse(row.Detail!)!;
        Assert.Equal((30, 30, "sales", "applied"), (detail["lines"]!.GetValue<int>(), detail["changes"]!.GetValue<int>(), detail["model"]!.GetValue<string>(), detail["status"]!.GetValue<string>()));
        Assert.Equal(call.BatchId, detail["batch_id"]!.GetValue<string>());
        Assert.DoesNotContain("300", row.Detail);
    }

    [Fact]
    public async Task The_same_row_staged_twice_is_one_change_and_a_delete_drops_its_edits()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        Assert.Equal(200, (await Submit(h, "west", Batch("m-1", Line("bump", 1, "5"), Line("flag", 1), Line("bump", 1, "6"), Line("bump", 2, "7")))).Status);
        var changes = fake.Batches.Single().Changes;
        Assert.Equal(2, changes.Count);
        Assert.Equal([0, 1, 2], changes[0].Lines);
        Assert.Equal((6L, true), (changes[0].Action.TypedWrites["units"]!.GetValue<long>(), changes[0].Action.TypedWrites["flag"]!.GetValue<bool>()));
        Assert.Equal(1L, changes[0].Action.TypedVersion);                          // the first version stands
        Assert.Equal([3], changes[1].Lines);

        Assert.Equal(200, (await Submit(h, "west", Batch("m-2", Line("bump", 1, "5"), Line("drop", 1)))).Status);
        var drop = fake.Batches.Last().Changes.Single();
        Assert.Equal("delete", drop.Action.Operation);
        Assert.Equal([0, 1], drop.Lines);
        var edit = await Submit(h, "west", Batch("m-3", Line("drop", 1), Line("bump", 1, "5")));
        Assert.Equal((400, "staged_for_delete"), (edit.Status, edit.Object["code"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_set_over_two_models_a_task_or_too_many_lines_is_refused_and_nothing_is_sent()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var two = await Submit(h, "west", Batch("t-1", Line("bump", 1, "1"), Line("other", 1)));
        Assert.Equal((400, "two_models"), (two.Status, two.Object["code"]!.GetValue<string>()));
        Assert.Contains("`sales`", two.Object["message"]!.GetValue<string>());
        Assert.Contains("`crm`", two.Object["message"]!.GetValue<string>());
        var rerun = Line("rerun", 1);
        rerun["parameters"] = new JsonObject { ["region"] = "x" };
        var task = await Submit(h, "west", Batch("t-2", Line("bump", 1, "1"), rerun));
        Assert.Equal((400, "not_a_change"), (task.Status, task.Object["code"]!.GetValue<string>()));
        var many = await Submit(h, "west", Batch("t-3", Enumerable.Range(1, 1001).Select(i => Line("bump", i, "1")).ToArray()));
        Assert.Equal((400, "limit"), (many.Status, many.Object["code"]!.GetValue<string>()));
        Assert.Equal(400, (await Submit(h, "west", Batch("t-4"))).Status);                                         // no lines
        Assert.Equal(400, (await Submit(h, "west", Batch("t-5", Line("bump", 1)))).Status);                         // the ask is not answered
        Assert.Equal(404, (await Submit(h, "west", Batch("t-6", Line("nope", 1)))).Status);
        Assert.Equal(400, (await Submit(h, "west", new JsonObject { ["lines"] = new JsonArray(Line("bump", 1, "1")) })).Status);   // no key
        Assert.Equal(401, (await Submit(h, "nobody", Batch("t-7", Line("bump", 1, "1")))).Status);
        Assert.Empty(fake.Batches);
    }

    [Fact]
    public async Task One_broken_rule_applies_none_and_is_shown_against_its_lines_a_request_of_n_changes_waits_and_a_retry_is_one_call()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        fake.BatchAnswer = new BatchResult(ActionStatus.Refused, 0, "The changes break the model's rules, so none was made.", Problems: [new BatchProblem(1, "max", "units", "units is above the maximum of 100.")]);
        var refused = await Submit(h, "west", Batch("r-1", Line("bump", 1, "5"), Line("bump", 2, "500"), Line("bump", 3, "7")));
        Assert.Equal((422, "refused"), (refused.Status, refused.Object["status"]!.GetValue<string>()));
        var problem = Assert.Single(refused.Object["problems"]!.AsArray())!;
        Assert.Equal((1, "units", "max"), (problem["line"]!.GetValue<int>(), problem["attribute"]!.GetValue<string>(), problem["rule"]!.GetValue<string>()));

        fake.BatchAnswer = new BatchResult(ActionStatus.Conflict, 0, "The row was changed by someone else.", Problems: [new BatchProblem(0, null, null, "version moved")]);
        Assert.Equal(409, (await Submit(h, "west", Batch("r-1", Line("bump", 1, "5"), Line("bump", 3, "7")))).Status);

        fake.BatchAnswer = new BatchResult(ActionStatus.Waiting, 0, RequestId: "cr-30");
        var waiting = await Submit(h, "west", Batch("r-1", Line("bump", 1, "5"), Line("bump", 3, "7")));
        Assert.Equal((202, "waiting", 2, "cr-30"), (waiting.Status, waiting.Object["status"]!.GetValue<string>(), waiting.Object["count"]!.GetValue<int>(), waiting.Object["request_id"]!.GetValue<string>()));
        Assert.Equal(3, fake.Batches.Count);                                       // the refusals were not kept: the same key was sent again
        var again = await Submit(h, "west", Batch("r-1", Line("bump", 1, "5"), Line("bump", 3, "7")));
        Assert.Equal(("waiting", 3), (again.Object["status"]!.GetValue<string>(), fake.Batches.Count));    // a kept answer is the same answer, nothing sent again
    }

    [Fact]
    public async Task A_double_click_on_submit_is_one_batch_and_an_apps_token_needs_the_write_scope_of_every_entity()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        fake.Gate = new TaskCompletionSource();
        var first = Submit(h, "west", Batch("d-1", Line("bump", 1, "1")));
        var second = Submit(h, "west", Batch("d-1", Line("bump", 1, "1")));
        while (true) { lock (fake.Batches) if (fake.Batches.Count > 0) break; await Task.Delay(10); }
        await Task.Delay(100);
        fake.Gate.SetResult();
        Assert.All(await Task.WhenAll(first, second), r => Assert.Equal(200, r.Status));
        Assert.Single(fake.Batches);

        Assert.Equal(403, (await Submit(h, "app-sales", Batch("d-2", Line("bump", 1, "1")))).Status);
        var app = await Submit(h, "app-writes", Batch("d-3", Line("bump", 1, "1")));
        Assert.Equal(200, app.Status);
        Assert.StartsWith("sales-portal 3, widget orders, batch ", fake.Batches.Last().Via);
    }

    [Fact]
    public async Task What_will_happen_to_the_set_is_asked_before_the_person_submits_it()
    {
        var (h, fake) = await StartAsync();
        await using var _ = h;
        var forecast = await Submit(h, "west", Batch("f-1", Line("bump", 1, "1"), Line("bump", 2, "1")), "/forecast");
        Assert.Equal(200, forecast.Status);
        Assert.Equal("wait", forecast.Object["will"]!.GetValue<string>());
        Assert.Empty(fake.Batches);
    }
}
