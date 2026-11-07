using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataRunner.Server;

/// <summary>
/// The management API of a runner (plan 28a R10). Every call needs a token a paired console issued, within the roles the pairing allowed. A viewer reads; an operator (or an administrator, who is one) starts, pauses and resumes
/// tasks, and each of those is written to the audit (<see cref="RunnerControl"/>), refused attempts included.
/// <list type="bullet">
/// <item><c>GET /api/v1/status</c>: the engine of the state, the channels served, what this machine reports (<c>LimitCapabilities</c>), this instance's id and every task's status: next due, last run, what is running or waiting, whether it is paused.</item>
/// <item><c>GET /api/v1/runs[?package=&amp;channel=&amp;task=&amp;outcome=&amp;limit=&amp;before=]</c>: the run history, newest first; <c>task</c> needs the package and the channel.</item>
/// <item><c>POST /api/v1/tasks/{package}/{channel}/{task}/run</c> with an optional <c>{"inputs": {"name": "value"}}</c>: queues a run now (202 with its run id; 409 when one is queued or running already). <c>.../pause</c> and <c>.../resume</c>.</item>
/// </list>
/// </summary>
public static class RunnerApi
{
    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerEngine> engine, string instanceId)
    {
        app.MapGet("/api/v1/status", (HttpContext context) =>
        {
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            return Results.Json(Status(state, engine(), instanceId));
        });

        app.MapGet("/api/v1/runs", (HttpContext context) =>
        {
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            var query = context.Request.Query;
            string? package = Text(query["package"]), channel = Text(query["channel"]), task = Text(query["task"]);
            foreach (var (what, value) in new[] { ("package", package), ("channel", channel), ("task", task) })
                if (value is not null && !PackageManifestValidator.NameRule.IsMatch(value))
                    return Results.BadRequest(new { error = $"'{value}' is not a {what} name." });
            if (task is not null && (package is null || channel is null)) return Results.BadRequest(new { error = "A task filter needs the package and the channel." });
            RunOutcome? outcome = null;
            if (Text(query["outcome"]) is { } outcomeText)
            {
                if (!Enum.TryParse<RunOutcome>(outcomeText, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return Results.BadRequest(new { error = $"'{outcomeText}' is not an outcome ({string.Join(", ", Enum.GetNames<RunOutcome>().Select(n => n.ToLowerInvariant()))})." });
                outcome = parsed;
            }

            var limit = 100;
            if (Text(query["limit"]) is { } limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 500))
                return Results.BadRequest(new { error = "limit is a whole number from 1 to 500." });
            DateTimeOffset? before = null;
            if (Text(query["before"]) is { } beforeText)
            {
                if (!DateTimeOffset.TryParse(beforeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return Results.BadRequest(new { error = "before is a time, such as 2026-11-07T12:00:00Z." });
                before = at;
            }

            var job = task is null ? null : $"{package}/{channel}/{task}";
            var runs = new RunStore(state, RunnerState.EngineService).Read(new RunQuery(RunnerState.EngineService, job, outcome, StartedBefore: before, Limit: limit, Package: package, Channel: channel));
            return Results.Json(new JsonObject { ["runs"] = new JsonArray(runs.Select(r => (JsonNode)Run(r)).ToArray()) });
        });

        app.MapPost("/api/v1/tasks/{package}/{channel}/{task}/run", async (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.RunVerb, engine(), reference!) is { } denied) return denied;
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (context.Request.ContentLength is null or > 0)
            {
                // a body is optional; a client may send it without a length (chunked), so it is read, not asked about
                using var reader = new StreamReader(context.Request.Body);
                var text = await reader.ReadToEndAsync(context.RequestAborted);
                if (text.Length > 1_000_000) return Results.BadRequest(new { error = "The body is larger than 1 MB." });
                if (!string.IsNullOrWhiteSpace(text))
                {
                    JsonNode? parsed;
                    try { parsed = JsonNode.Parse(text); }
                    catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
                    if (parsed is not JsonObject body) return Results.BadRequest(new { error = "The body is a JSON object, such as {\"inputs\": {\"name\": \"value\"}}." });
                    if (body["inputs"] is { } given)
                    {
                        if (given is not JsonObject map) return Results.BadRequest(new { error = "inputs is an object of names and text values." });
                        foreach (var (name, value) in map)
                            inputs[name] = value is JsonValue v && v.TryGetValue<string>(out var valueText) ? valueText : value?.ToJsonString() ?? "";
                    }
                }
            }

            return Control(engine(), control =>
            {
                var result = control.RunNow(reference!, inputs, ActorOf(context));
                return result.Queued
                    ? Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["run_id"] = result.RunId!.Value.ToString() }, statusCode: StatusCodes.Status202Accepted)
                    : Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["queued"] = false, ["error"] = $"{reference} is queued or running already." }, statusCode: StatusCodes.Status409Conflict);
            });
        });

        app.MapPost("/api/v1/tasks/{package}/{channel}/{task}/pause", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.PauseVerb, engine(), reference!) is { } denied) return denied;
            return Control(engine(), control =>
            {
                control.Pause(reference!, ActorOf(context));
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["paused"] = true });
            });
        });

        app.MapPost("/api/v1/tasks/{package}/{channel}/{task}/resume", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.ResumeVerb, engine(), reference!) is { } denied) return denied;
            return Control(engine(), control =>
            {
                control.Resume(reference!, ActorOf(context));
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["paused"] = false });
            });
        });
    }

    public static JsonObject Status(StateDatabase state, RunnerEngine engine, string instanceId)
    {
        var status = new JsonObject
        {
            ["product"] = RunnerSettings.Product,
            ["version"] = RunnerServer.Version,
            ["instance_id"] = instanceId,
            ["runner_instance"] = engine.InstanceId?.ToString(),
            ["running"] = engine.Control is not null,
            ["state_engine"] = state.Dialect.Engine,
            ["capabilities"] = new JsonObject(engine.Capabilities.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
        };
        if (engine.Control is not { } control)
        {
            status["channels"] = new JsonArray();
            status["tasks"] = new JsonArray();
            status["problems"] = new JsonArray();
            return status;
        }

        var scheduler = control.Status();
        status["channels"] = new JsonArray(engine.Channels.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        status["worker_id"] = scheduler.WorkerId;
        status["running_here"] = scheduler.RunningHere;
        status["max_concurrent_runs"] = scheduler.MaxConcurrentRuns;
        status["tasks"] = new JsonArray(scheduler.Tasks.Select(t => (JsonNode)new JsonObject
        {
            ["package"] = t.Task.Package,
            ["channel"] = t.Task.Channel,
            ["task"] = t.Task.Task,
            ["version"] = t.Version,
            ["next_due"] = t.NextDue?.UtcDateTime.ToString("O"),
            ["last_run"] = t.LastRun is null ? null : Run(t.LastRun),
            ["running"] = t.Running,
            ["queued"] = t.Queued,
            ["next_attempt_at"] = t.NextAttemptAt?.UtcDateTime.ToString("O"),
            ["paused"] = t.Paused,
            ["paused_by"] = t.PausedBy,
            ["problem"] = t.Problem,
        }).ToArray());
        status["triggers"] = new JsonArray(control.Triggers.Statuses().Select(t => (JsonNode)new JsonObject
        {
            ["package"] = t.Task.Package, ["channel"] = t.Task.Channel, ["task"] = t.Task.Task, ["kind"] = t.Kind, ["id"] = t.Id, ["state"] = t.State,
            ["last_fired"] = t.LastFiredAt?.UtcDateTime.ToString("O"), ["problem"] = t.Problem,
        }).ToArray());
        status["problems"] = new JsonArray(scheduler.Problems.Select(p => (JsonNode)new JsonObject { ["subject"] = p.Subject, ["message"] = p.Message, ["at"] = p.At.UtcDateTime.ToString("O") }).ToArray());
        return status;
    }

    public static JsonObject Run(StoredRun run)
    {
        var task = TaskRef.TryParse(run.Run.JobId, out var parsed) ? parsed.Task : run.Run.JobId;
        return new JsonObject
        {
            ["id"] = run.Id.ToString(),
            ["package"] = run.Package,
            ["package_version"] = run.PackageVersion,
            ["channel"] = run.Channel,
            ["task"] = task,
            ["attempt"] = run.Attempt,
            ["outcome"] = run.Run.Outcome.ToString().ToLowerInvariant(),
            ["started"] = run.Run.Started.UtcDateTime.ToString("O"),
            ["finished"] = run.Run.Finished?.UtcDateTime.ToString("O"),
            ["rows"] = run.Run.Rows,
            ["failed_item"] = run.Run.FailedItem,
            ["failure"] = run.Run.Failure is null ? null : new JsonObject { ["kind"] = run.Run.Failure.Kind, ["message"] = run.Run.Failure.Message },
        };
    }

    private static string? Text(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 0 || string.IsNullOrWhiteSpace(values[0]) ? null : values[0];

    private static bool ParseTask(string package, string channel, string task, out TaskRef? reference, out IResult? bad)
    {
        reference = null;
        bad = null;
        if (TaskRef.Problem(package, channel, task) is { } problem)
        {
            bad = Results.BadRequest(new { error = problem });
            return false;
        }

        reference = new TaskRef(package, channel, task);
        return true;
    }

    private static RunnerActor ActorOf(HttpContext context)
    {
        var token = context.ManagementToken();
        return new RunnerActor(token.SubjectId, token.SubjectName, token.Client.Kind == ClientKind.Console ? "console" : "api", token.Client.Id);
    }

    private static IResult Control(RunnerEngine engine, Func<RunnerControl, IResult> act)
    {
        if (engine.Control is not { } control) return Results.Json(new { error = "The runner is starting; try again in a moment." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        try { return act(control); }
        catch (UnknownTaskException ex) { return Results.NotFound(new { error = ex.Message }); }
    }

    private static IResult? Deny(HttpContext context, ServerRole needed, string? verb = null, RunnerEngine? engine = null, TaskRef? task = null)
    {
        var decision = ManagementAuthorization.Authorize(context.ManagementToken(), needed);
        if (decision.Allowed) return null;
        // a refused act is on record too, so an operator reading the audit sees who tried
        if (verb is not null && task is not null && engine?.Control is { } control) control.Refused(verb, task, ActorOf(context), decision.Reason ?? "the role is not enough");
        return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
    }
}
