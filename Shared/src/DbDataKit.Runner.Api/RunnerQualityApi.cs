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
/// <item><c>GET {prefix}/quality</c> (viewer): every check with its latest result and block. <c>can_run</c> is false: a dataset's checks are evaluated by the task that loads it (a refresh, after its loads), not on request here.</item>
/// <item><c>GET {prefix}/quality/history?dataset=&amp;check=</c> (viewer): the newest results of one check, with the run that made each.</item>
/// <item><c>POST {prefix}/quality/release</c> with <c>{"dataset": ..., "check": ...}</c> (administrator): releases the block a check set while it still fails (<see cref="QualityStore.Release"/>), audited as <c>runner.quality.release</c>, a refusal too.</item>
/// </list>
/// </summary>
public static class RunnerQualityApi
{
    public const string ReleaseVerb = "runner.quality.release";

    /// <summary>What the overview says about evaluating on request.</summary>
    public const string RunNote = "The checks of a dataset are evaluated on this Runner by the task that loads it (a Build refresh, after its loads) and recorded under that run. Run the task again to evaluate them.";

    public static void Map(IEndpointRouteBuilder app, StateDatabase state, string prefix, RunnerCallerResolver callerOf, string serviceId = RunnerNames.ServiceId, TimeProvider? time = null)
    {
        var root = prefix.TrimEnd('/');
        var audit = new AuditLog(state, serviceId);
        var store = new QualityStore(state);
        time ??= TimeProvider.System;

        app.MapGet($"{root}/quality", (HttpContext context) =>
            !callerOf(context).Authorize(ServerRole.Viewer).Allowed ? Error(403, "This needs the viewer role.") : Results.Json(QualityDocuments.Overview(store, canRun: false, RunNote, canRelease: true)));

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
