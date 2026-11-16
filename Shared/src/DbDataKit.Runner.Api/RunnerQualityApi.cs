using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The results of the data quality checks the group's tasks evaluated (plan 35 I3), over the group's <c>quality</c> module, in the shape every server that keeps results answers (<see cref="QualityDocuments"/>), so the console's Quality page joins
/// them with no product of its own:
/// <list type="bullet">
/// <item><c>GET {prefix}/quality</c> (viewer): every check with its latest result and block. <c>can_run</c> is true when the runner's engine is given: a re-check is the <c>check</c> task of the package that recorded the dataset's results (below).</item>
/// <item><c>GET {prefix}/quality/history?dataset=&amp;check=</c> (viewer): the newest results of one check, with the run that made each.</item>
/// <item><c>POST {prefix}/quality/run</c> with <c>{"dataset": ..., "on_behalf_of": ...}</c> (operator, as starting any task is; plan 35 I3a): queues the <c>check</c> task of the package and channel whose run recorded the dataset's newest result, with the
/// dataset as its input, so the checks are evaluated again with that package's own slots and nothing else (202 with the run id; 404 for a dataset with no result here; 409 when that package declares no <c>check</c> or one is queued or running
/// already). The run is audited as any run is (<c>runner.task.run</c>); a refusal for the role is audited as <c>runner.quality.run</c>.</item>
/// <item><c>POST {prefix}/quality/release</c> with <c>{"dataset": ..., "check": ...}</c> (administrator): releases the block a check set while it still fails (<see cref="QualityStore.Release"/>), audited as <c>runner.quality.release</c>, a refusal too.</item>
/// </list>
/// </summary>
public static class RunnerQualityApi
{
    public const string ReleaseVerb = "runner.quality.release";

    /// <summary>The verb a refused re-check is audited under (a queued one is the run's own <c>runner.task.run</c>).</summary>
    public const string RunVerb = "runner.quality.run";

    /// <summary>The task a re-check starts (<c>QualityCheckTask.Name</c> of <c>DbDataKit.Quality.Runner</c>, which this library does not reference).</summary>
    public const string CheckTask = "check";

    /// <summary>What the overview says about evaluating on request when the runner cannot start a re-check (no engine).</summary>
    public const string RunNote = "The checks of a dataset are evaluated on this Runner by the task that loads it (a Build refresh, after its loads) and recorded under that run. Run the task again to evaluate them.";

    /// <summary>What the overview says when it can.</summary>
    public const string RecheckNote = "A re-check starts the `check` task of the package that recorded the dataset's results, with that package's own logins; it loads nothing. The results show here when the run ends. A package published before it had the task cannot.";

    public static void Map(IEndpointRouteBuilder app, StateDatabase state, string prefix, RunnerCallerResolver callerOf, string serviceId = RunnerNames.ServiceId, TimeProvider? time = null, Func<RunnerHost>? engine = null)
    {
        var root = prefix.TrimEnd('/');
        var audit = new AuditLog(state, serviceId);
        var store = new QualityStore(state);
        time ??= TimeProvider.System;

        app.MapGet($"{root}/quality", (HttpContext context) =>
            !callerOf(context).Authorize(ServerRole.Viewer).Allowed ? Error(403, "This needs the viewer role.") : Results.Json(QualityDocuments.Overview(store, canRun: engine is not null, engine is null ? RunNote : RecheckNote, canRelease: true)));

        if (engine is not null)
            app.MapPost($"{root}/quality/run", async (HttpContext context) =>
            {
                var caller = callerOf(context);
                JsonNode? body;
                try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
                catch (JsonException) { body = null; }
                var dataset = Text(body, "dataset");
                var onBehalfOf = Text(body, "on_behalf_of");
                var decision = caller.Authorize(ServerRole.Operator);
                if (!decision.Allowed)
                {
                    audit.Record(new AuditEvent("person", caller.Actor.Id, RunVerb, AuditEvent.Refused, ViaKind: caller.Actor.ViaKind, ViaId: caller.Actor.ViaId, TargetKind: "dataset", TargetId: dataset.Length > 200 ? dataset[..200] : dataset,
                        Detail: JsonSerializer.Serialize(new { status = 403, reason = decision.Reason ?? "the role is not enough" })));
                    return Error(403, "A re-check starts a task, so it needs the operator role.");
                }

                if (dataset.Length == 0) return Error(400, "Name the dataset: `dataset` (its lineage identity).");
                if (onBehalfOf.Length > RunnerControl.MaxOnBehalfOf) return Error(400, $"on_behalf_of is the name of a person, text of at most {RunnerControl.MaxOnBehalfOf} characters.");
                if (OwnerOf(state, store, dataset) is not { } owner) return Error(404, $"No result of {dataset} on this Runner names a run it still knows, so no package is known to evaluate it again; run the task that loads it.");
                if (engine().Control is not { } control) return Error(503, "The runner is starting; try again in a moment.");
                var task = new TaskRef(owner.Package, owner.Channel, CheckTask);
                RunNowResult queued;
                try { queued = control.RunNow(task, new Dictionary<string, string> { ["dataset"] = dataset }, caller.Actor, onBehalfOf: onBehalfOf.Length == 0 ? null : onBehalfOf); }
                catch (UnknownTaskException) { return Error(409, $"The package {owner.Package} on {owner.Channel}, whose run recorded these results, has no `check` task: publish it again with a version that has one, or run the task that loads the dataset."); }
                return queued.Queued
                    ? Results.Json(new JsonObject { ["queued"] = true, ["run_id"] = queued.RunId!.Value.ToString(), ["task"] = task.ToString(), ["dataset"] = dataset }, statusCode: StatusCodes.Status202Accepted)
                    : Error(409, $"{task} is queued or running already; its results show here when it ends.");
            });

        app.MapGet($"{root}/quality/history", (HttpContext context) =>
        {
            if (!callerOf(context).Authorize(ServerRole.Viewer).Allowed) return Error(403, "This needs the viewer role.");
            var dataset = context.Request.Query["dataset"].ToString();
            var check = context.Request.Query["check"].ToString();
            if (dataset.Length == 0 || check.Length == 0) return Error(400, "Name the check: `dataset` (its lineage identity) and `check` (its id).");
            var limit = int.TryParse(context.Request.Query["limit"], out var n) ? n : QualityDocuments.HistoryLimit;
            return Results.Json(QualityDocuments.History(store, dataset, check, limit));
        });

        app.MapPost($"{root}/quality/release", async (HttpContext context) =>
        {
            var caller = callerOf(context);
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { body = null; }
            var dataset = Text(body, "dataset");
            var check = Text(body, "check");
            var target = Target(dataset, check);

            var decision = caller.Authorize(ServerRole.Admin);
            if (!decision.Allowed)
            {
                Record(audit, caller.Actor, target, AuditEvent.Refused, new { status = 403, reason = decision.Reason ?? "the role is not enough" });
                return Error(403, "Releasing a block lets what depends on the dataset run on data that fails the check, so it needs the admin role.");
            }

            if (dataset.Length == 0 || check.Length == 0) return Error(400, "Name the check: `dataset` (its lineage identity) and `check` (its id).");
            var released = store.Release(dataset, check, caller.Actor.Id, time.GetUtcNow());
            Record(audit, caller.Actor, target, released ? AuditEvent.Ok : AuditEvent.Refused, new { dataset, check, reason = released ? null : "the check does not block" });
            return released ? Results.Json(new { released = true }) : Error(409, $"The check {check} of {dataset} does not block now: there is nothing to release.");
        });
    }

    /// <summary>The package and channel of the run that recorded the dataset's newest result; null when it has none, or the run is not known here (its record was pruned).</summary>
    private static (string Package, string Channel)? OwnerOf(StateDatabase state, QualityStore store, string dataset)
    {
        var runs = new RunStore(state, RunnerNames.EngineService);
        foreach (var status in store.StatusesOf(dataset).OrderByDescending(s => s.EvaluatedAt))
            foreach (var result in store.History(dataset, status.CheckId, 5))
                if (Guid.TryParse(result.RunId, out var id) && runs.Get(id) is { Package: { Length: > 0 } package, Channel: { Length: > 0 } channel })
                    return (package, channel);
        return null;
    }

    private static string Text(JsonNode? body, string name) => (body as JsonObject)?[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";

    private static string Target(string dataset, string check)
    {
        var text = $"{check}@{dataset}";
        return text.Length > 200 ? text[..200] : text;
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static void Record(AuditLog audit, RunnerActor actor, string target, string outcome, object detail) =>
        audit.Record(new AuditEvent("person", actor.Id, ReleaseVerb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: "check", TargetId: target, Detail: JsonSerializer.Serialize(detail)));
}
