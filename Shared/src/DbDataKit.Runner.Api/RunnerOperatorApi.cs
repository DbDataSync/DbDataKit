using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// What the kit leaves to a person (plan 28a R25), as the console's run and task screens read and answer it. All under <c>/api/v1</c>:
/// <list type="bullet">
/// <item><c>GET .../tasks/{package}/{channel}/{task}/in-doubt</c>: the steps an earlier attempt began and left unanswered (chain, step, key, attempt, when). <c>POST .../tasks/.../steps/{name}/resolve</c> with
/// <c>{"chain": "...", "key": "", "happened": true, "result": &lt;JSON&gt;, "retry": true}</c>: the operator says whether the step happened (and its result) or not, optionally queues a run to go on; audited as <c>runner.step.resolve</c>; 409 when the step is not waiting.</item>
/// <item><c>GET .../tasks/.../dead-letters[?status=open|replayed|discarded]</c>: the records an outside system refused, with why. <c>POST .../dead-letters/{id}/replay</c> turns one into an inbox item of the same task (once, however often it is asked) and queues a run;
/// <c>POST .../dead-letters/{id}/discard</c> gives it up. Audited as <c>runner.deadletter.replay</c> and <c>runner.deadletter.discard</c>; 409 when the letter is not open.</item>
/// <item><c>GET /runs/{id}/units</c> and <c>/runs/{id}/steps</c>: the table of units (done, running, failed, rows, duration) and the steps of the attempt chain the run belongs to. 404 when the chain is not known any more (a retry gave the item another run id).</item>
/// </list>
/// Reads are an operator's for the dead letters and in-doubt steps (they name what a task refused), a viewer's for the units and steps of a run.
/// </summary>
internal static class RunnerOperatorApi
{
    private const int MaxUnits = 500;

    public static void Map(IEndpointRouteBuilder app, string root, StateDatabase state, Func<RunnerHost> engine, Func<HttpContext, ServerRole, string?, TaskRef?, IResult?> deny, Func<HttpContext, RunnerActor> actorOf)
    {
        static string Time(DateTimeOffset? at) => at?.UtcDateTime.ToString("O") ?? "";
        static string? TimeOrNull(DateTimeOffset? at) => at?.UtcDateTime.ToString("O");

        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/in-doubt", (string package, string channel, string task, HttpContext context) =>
        {
            if (!RunnerApi.ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (deny(context, ServerRole.Operator, null, null) is { } denied) return denied;
            var steps = new StepStore(state, new ProgressKey(package, channel, task)).ListInDoubt();
            return Results.Json(new JsonObject
            {
                ["task"] = reference!.ToString(),
                ["steps"] = new JsonArray(steps.Select(s => (JsonNode)new JsonObject
                {
                    ["chain"] = s.Chain, ["name"] = s.Name, ["key"] = s.Key, ["attempt"] = s.Attempt, ["started"] = Time(s.StartedAt), ["note"] = s.Error,
                }).ToArray()),
            });
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/steps/{{name}}/resolve", async (string package, string channel, string task, string name, HttpContext context) =>
        {
            if (!RunnerApi.ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (deny(context, ServerRole.Operator, RunnerControl.ResolveStepVerb, reference) is { } denied) return denied;
            if (!PackageManifestValidator.NameRule.IsMatch(name)) return Results.BadRequest(new { error = $"'{name}' is not a step name." });
            var (body, problem) = await ReadObject(context);
            if (body is null) return Results.BadRequest(new { error = problem });
            if (Text(body, "chain") is not { Length: > 0 } chain) return Results.BadRequest(new { error = "The body names the attempt chain of the step: {\"chain\": \"...\", \"key\": \"\", \"happened\": true|false, \"result\": ..., \"retry\": true|false}." });
            if (body["happened"] is not JsonValue h || !h.TryGetValue<bool>(out var happened)) return Results.BadRequest(new { error = "happened is true (the step did its work) or false (it did not)." });
            var retry = body["retry"] is JsonValue r && r.TryGetValue<bool>(out var wanted) && wanted;
            string? resultJson = null;
            if (happened)
            {
                if (!body.ContainsKey("result")) return Results.BadRequest(new { error = "A step that happened has a result: give \"result\" (JSON, null for no value)." });
                resultJson = body["result"]?.ToJsonString() ?? "null";
            }

            return Control(engine(), control =>
            {
                try
                {
                    var answer = control.ResolveStep(reference!, chain, name, Text(body, "key") ?? "", happened, resultJson, retry, actorOf(context));
                    return answer.Resolved
                        ? Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["step"] = name, ["resolved"] = true, ["happened"] = happened, ["run_id"] = answer.RunId?.ToString(), ["retry_queued"] = answer.RetryQueued })
                        : Results.Json(new { error = $"The step '{name}' of {reference} is not waiting for an answer (it was answered already, or it is not one of that chain's)." }, statusCode: StatusCodes.Status409Conflict);
                }
                catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
        });

        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/dead-letters", (string package, string channel, string task, HttpContext context) =>
        {
            if (!RunnerApi.ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (deny(context, ServerRole.Operator, null, null) is { } denied) return denied;
            var status = context.Request.Query["status"].Count == 0 ? DeadLetter.Open : context.Request.Query["status"][0]!;
            if (status is not (DeadLetter.Open or DeadLetter.Replayed or DeadLetter.Discarded)) return Results.BadRequest(new { error = "status is open, replayed or discarded." });
            var letters = new ProgressStore(state, new ProgressKey(package, channel, task)).DeadLetters(status);
            return Results.Json(new JsonObject
            {
                ["task"] = reference!.ToString(), ["status"] = status,
                ["dead_letters"] = new JsonArray(letters.Select(l => (JsonNode)new JsonObject
                {
                    ["id"] = l.Id.ToString(), ["source"] = l.Source, ["reason"] = l.Error, ["attempts"] = l.Attempts, ["status"] = l.Status,
                    ["first"] = Time(l.FirstFailedAt), ["last"] = Time(l.LastFailedAt), ["resolved"] = TimeOrNull(l.ResolvedAt), ["has_body"] = l.BodyPayload is not null,
                }).ToArray()),
            });
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/dead-letters/{{id}}/replay", (string package, string channel, string task, string id, HttpContext context) =>
        {
            if (!RunnerApi.ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (deny(context, ServerRole.Operator, RunnerControl.ReplayVerb, reference) is { } denied) return denied;
            if (!Guid.TryParse(id, out var letter)) return Results.BadRequest(new { error = $"'{id}' is not a dead letter id." });
            return Control(engine(), control =>
            {
                var replay = control.ReplayDeadLetter(reference!, letter, actorOf(context));
                return replay.Replayed
                    ? Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["id"] = letter.ToString(), ["replayed"] = true, ["inbox_item"] = replay.InboxItem?.ToString(), ["run_queued"] = replay.Queued }, statusCode: StatusCodes.Status202Accepted)
                    : Results.Json(new { error = $"The dead letter {letter} of {reference} is not open (it was replayed or discarded already)." }, statusCode: StatusCodes.Status409Conflict);
            });
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/dead-letters/{{id}}/discard", (string package, string channel, string task, string id, HttpContext context) =>
        {
            if (!RunnerApi.ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (deny(context, ServerRole.Operator, RunnerControl.DiscardVerb, reference) is { } denied) return denied;
            if (!Guid.TryParse(id, out var letter)) return Results.BadRequest(new { error = $"'{id}' is not a dead letter id." });
            return Control(engine(), control => control.DiscardDeadLetter(reference!, letter, actorOf(context))
                ? Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["id"] = letter.ToString(), ["discarded"] = true })
                : Results.Json(new { error = $"The dead letter {letter} of {reference} is not open." }, statusCode: StatusCodes.Status409Conflict));
        });

        app.MapGet($"{root}/runs/{{id:guid}}/units", (Guid id, HttpContext context) =>
        {
            if (deny(context, ServerRole.Viewer, null, null) is { } denied) return denied;
            if (ChainOf(state, id) is not { } found) return Results.NotFound(new { error = $"The attempt chain of run {id} is not known (the run does not exist, or a retry gave its item another run id)." });
            var units = new UnitStore(state, found.Key);
            var summary = units.Summary(found.Chain);
            var list = units.List(found.Chain);
            return Results.Json(new JsonObject
            {
                ["run_id"] = id.ToString(), ["task"] = found.Task, ["chain"] = found.Chain,
                ["summary"] = new JsonObject { ["total"] = summary.Total, ["done"] = summary.Done, ["skipped"] = summary.Skipped, ["failed"] = summary.Failed, ["running"] = summary.Running },
                ["truncated"] = list.Count > MaxUnits,
                ["units"] = new JsonArray(list.Take(MaxUnits).Select(u => (JsonNode)new JsonObject
                {
                    ["name"] = u.Name, ["key"] = u.Key, ["state"] = u.State, ["attempt"] = u.Attempt, ["started"] = Time(u.StartedAt), ["completed"] = TimeOrNull(u.CompletedAt),
                    ["duration_ms"] = u.CompletedAt is { } done ? (long)(done - u.StartedAt).TotalMilliseconds : null,
                    ["rows"] = Rows(u.Counts), ["counts"] = Counts(u.Counts), ["error"] = u.Error,
                }).ToArray()),
            });
        });

        app.MapGet($"{root}/runs/{{id:guid}}/steps", (Guid id, HttpContext context) =>
        {
            if (deny(context, ServerRole.Viewer, null, null) is { } denied) return denied;
            if (ChainOf(state, id) is not { } found) return Results.NotFound(new { error = $"The attempt chain of run {id} is not known (the run does not exist, or a retry gave its item another run id)." });
            var steps = new StepStore(state, found.Key).List(found.Chain);
            return Results.Json(new JsonObject
            {
                ["run_id"] = id.ToString(), ["task"] = found.Task, ["chain"] = found.Chain,
                ["steps"] = new JsonArray(steps.Select(s => (JsonNode)new JsonObject
                {
                    ["name"] = s.Name, ["key"] = s.Key, ["state"] = s.State, ["attempt"] = s.Attempt, ["started"] = Time(s.StartedAt), ["completed"] = TimeOrNull(s.CompletedAt), ["note"] = s.Error,
                }).ToArray()),
            });
        });
    }

    private static long? Rows(IReadOnlyDictionary<string, long> counts) => counts.TryGetValue("rows", out var rows) ? rows : null;

    private static JsonObject Counts(IReadOnlyDictionary<string, long> counts)
    {
        var node = new JsonObject();
        foreach (var (name, value) in counts.OrderBy(c => c.Key, StringComparer.Ordinal)) node[name] = value;
        return node;
    }

    private sealed record Found(ProgressKey Key, string Chain, string Task);

    /// <summary>The attempt chain of a run, from the work item that carries the run id (the item holds the chain in its payload).</summary>
    private static Found? ChainOf(StateDatabase state, Guid runId)
    {
        if (new RunStore(state, RunnerNames.EngineService).Get(runId) is not { } run || run.Service != RunnerNames.EngineService) return null;
        if (!TaskRef.TryParse(run.Run.JobId, out var task)) return null;
        if (new ModuleWorkQueueStore(state, RunnerNames.EngineService).FindByRun(runId) is not { } item) return null;
        if (WorkPayload.TryParse(item.PayloadJson, out _) is not { } payload) return null;
        return new Found(new ProgressKey(task.Package, task.Channel, task.Task), payload.Chain, task.ToString());
    }

    private static async Task<(JsonObject? Body, string? Problem)> ReadObject(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var text = await reader.ReadToEndAsync(context.RequestAborted);
        if (text.Length > 1_000_000) return (null, "The body is larger than 1 MB.");
        try { return JsonNode.Parse(text) is JsonObject body ? (body, null) : (null, "The body is a JSON object."); }
        catch (System.Text.Json.JsonException) { return (null, "The body is not JSON."); }
    }

    private static string? Text(JsonObject body, string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static IResult Control(RunnerHost engine, Func<RunnerControl, IResult> act)
    {
        if (engine.Control is not { } control) return Results.Json(new { error = "The runner is starting; try again in a moment." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        try { return act(control); }
        catch (UnknownTaskException ex) { return Results.NotFound(new { error = ex.Message }); }
    }
}
