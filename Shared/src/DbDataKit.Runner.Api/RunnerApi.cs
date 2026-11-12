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

namespace DbDataKit.Runner.Api;

/// <summary>
/// The management API of a runner (plan 28a R10). Every call needs a token a paired console issued, within the roles the pairing allowed. A viewer reads; an operator (or an administrator, who is one) starts, pauses and resumes
/// tasks, and each of those is written to the audit (<see cref="RunnerControl"/>), refused attempts included.
/// <list type="bullet">
/// <item><c>GET /api/v1/status</c>: the engine of the state, the channels served, what this machine reports (<c>LimitCapabilities</c>), this instance's id and every task's status: next due, last run, what is running or waiting, whether it is paused.</item>
/// <item><c>GET /api/v1/runs[?package=&amp;channel=&amp;task=&amp;outcome=&amp;limit=&amp;before=]</c>: the run history, newest first; <c>task</c> needs the package and the channel.</item>
/// <item><c>GET /api/v1/runs/{id}</c> and <c>.../runs/{id}/logs[?after=&amp;limit=]</c>: one run and its log in order, a page at a time by line id (plan 28c E13); <c>.../tasks/{package}/{channel}/{task}/steps/{name}[?limit=&amp;before=&amp;key=]</c>: the completed steps of that name across the task's runs, newest first. A viewer reads all three.</item>
/// <item><c>.../tasks/{package}/{channel}/{task}/steps/{name}/payloads/{id}</c>: the file a completed step of that name kept as its streamed result (plan 28c E06e); 404 when it is not one of that task's or the retention removed it. A viewer reads it.</item>
/// <item><c>.../tasks/{package}/{channel}/{task}/schedule</c>, <c>.../owner</c> and <c>.../decisions</c>: who schedules a task while a product's own scheduler is still in the picture (plan 28c E09), see <c>MapOwnership</c>.</item>
/// <item><c>.../tasks/{package}/{channel}/{task}/checkpoints</c> (every checkpoint with its items), <c>.../checkpoints/{name}</c> (one, as a product reads it), <c>.../checkpoints/{name}/history[?key=&amp;limit=]</c> (its changes, newest first) and <c>POST .../checkpoints/{name}/rewind</c> with <c>{"history_id": n, "key": ""}</c>: back to what it was before that change, as a new version under the operator's name, audited as <c>runner.checkpoint.rewind</c> (409 when a run moved it meanwhile). An operator, for the reads too.</item>
/// <item>The operator's answers (plan 28a R25): in-doubt steps and their resolution, dead letters (list, replay, discard), the units and steps of a run; see <see cref="RunnerOperatorApi"/>.</item>
/// <item><c>POST /api/v1/tasks/{package}/{channel}/{task}/run</c> with an optional <c>{"inputs": {"name": "value"}}</c>: queues a run now (202 with its run id; 409 when one is queued or running already). <c>.../pause</c> and <c>.../resume</c>.</item>
/// </list>
/// </summary>
public static class RunnerApi
{
    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerHost> engine, string instanceId, RunnerApiOptions options)
    {
        var root = options.Prefix.TrimEnd('/');
        var caller = options.Caller;
        IResult? Deny(HttpContext context, ServerRole needed, string? verb = null, RunnerHost? host = null, TaskRef? task = null) => DenyCall(caller(context), needed, verb, host, task);
        RunnerActor ActorOf(HttpContext context) => caller(context).Actor;

        app.MapGet($"{root}/status", (HttpContext context) =>
        {
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            return Results.Json(Status(state, engine(), instanceId, options));
        });

        // a task's checkpoint as it stands (plan 28c E12c): what a product reads to take the position back when it schedules the task again (rollback). Operator, as the owner's move is.
        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/checkpoints/{{name}}", (string package, string channel, string task, string name, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator) is { } denied) return denied;
            if (!PackageManifestValidator.NameRule.IsMatch(name)) return Results.BadRequest(new { error = $"'{name}' is not a checkpoint name." });
            var store = new ProgressStore(state, new ProgressKey(package, channel, task));
            var items = new JsonArray();
            foreach (var key in store.ItemKeys(name))
            {
                if (store.GetCheckpoint(name, key) is not { } checkpoint) continue;
                var parts = new JsonObject();
                foreach (var (part, value) in checkpoint.Parts)
                    parts[part] = value.Value switch { DateTimeOffset at => at.UtcDateTime.ToString("O"), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var other => other.ToString() };
                items.Add(new JsonObject { ["key"] = key, ["version"] = checkpoint.Version, ["parts"] = parts });
            }

            return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["name"] = name, ["items"] = items });
        });

        // every checkpoint of a task with its items, and the history of one (plan 29a Q20, plan 28a R25): what the console's checkpoint screen reads. Operator, as the single read above is.
        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/checkpoints", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator) is { } denied) return denied;
            var store = new ProgressStore(state, new ProgressKey(package, channel, task));
            var checkpoints = new JsonArray();
            foreach (var name in store.CheckpointNames())
            {
                var items = new JsonArray();
                foreach (var key in store.ItemKeys(name))
                {
                    if (store.GetCheckpoint(name, key) is not { } checkpoint) continue;
                    var parts = new JsonObject();
                    foreach (var (part, value) in checkpoint.Parts) parts[part] = new JsonObject { ["type"] = value.Type.ToString().ToLowerInvariant(), ["value"] = PartText(value) };
                    items.Add(new JsonObject { ["key"] = key, ["version"] = checkpoint.Version, ["updated"] = checkpoint.UpdatedAt.UtcDateTime.ToString("O"), ["updated_by"] = checkpoint.UpdatedBy, ["parts"] = parts });
                }

                checkpoints.Add(new JsonObject { ["name"] = name, ["items"] = items });
            }

            return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["checkpoints"] = checkpoints });
        });

        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/checkpoints/{{name}}/history", (string package, string channel, string task, string name, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator) is { } denied) return denied;
            if (!PackageManifestValidator.NameRule.IsMatch(name)) return Results.BadRequest(new { error = $"'{name}' is not a checkpoint name." });
            var limit = 100;
            if (Text(context.Request.Query["limit"]) is { } limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 1000))
                return Results.BadRequest(new { error = "limit is a whole number from 1 to 1000." });
            var key = Text(context.Request.Query["key"]) ?? "";
            var changes = new ProgressStore(state, new ProgressKey(package, channel, task)).History(name, key, limit);
            return Results.Json(new JsonObject
            {
                ["task"] = reference!.ToString(), ["name"] = name, ["key"] = key,
                ["changes"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id, ["part"] = c.Part, ["type"] = c.Type.ToString().ToLowerInvariant(), ["old"] = c.Old, ["new"] = c.New, ["version"] = c.HeadVersion,
                    ["run_id"] = c.RunId, ["actor"] = c.Actor, ["at"] = c.At.UtcDateTime.ToString("O"),
                }).ToArray()),
            });
        });

        // the rewind: back to what the checkpoint was before one change of its history, as a new version under the operator's name, audited. 409 when a run moved the checkpoint meanwhile.
        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/checkpoints/{{name}}/rewind", async (string package, string channel, string task, string name, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.RewindVerb, engine(), reference!) is { } denied) return denied;
            if (!PackageManifestValidator.NameRule.IsMatch(name)) return Results.BadRequest(new { error = $"'{name}' is not a checkpoint name." });
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            JsonObject? body;
            try { body = JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            if (body?["history_id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var historyId))
                return Results.BadRequest(new { error = "The body is {\"history_id\": <id of the change to go back before>, \"key\": \"\"}." });
            var key = body["key"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var keyText) ? keyText : "";
            return Control(engine(), control =>
            {
                try
                {
                    if (!control.RewindCheckpoint(reference!, name, key, historyId, ActorOf(context)))
                        return Results.Json(new { error = $"The checkpoint '{name}' of {reference} moved while it was being rewound, or that change is not one of its own. Read its history again." }, statusCode: StatusCodes.Status409Conflict);
                }
                catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
                var now = new ProgressStore(state, new ProgressKey(package, channel, task)).GetCheckpoint(name, key);
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["name"] = name, ["key"] = key, ["version"] = now?.Version });
            });
        });

        // an item for the task's own queue, the one a run of the task claims from (plan 28c E12c): how a product hands work to a run that is up already, since a task runs one at a time and a second start is refused. Operator.
        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/work", async (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator) is { } denied) return denied;
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            if (text.Length > 1_000_000) return Results.BadRequest(new { error = "The body is larger than 1 MB." });
            JsonObject? body;
            try { body = JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            if (body is null) return Results.BadRequest(new { error = "The body is a JSON object with kind, subject and, optionally, part, resource, lane and payload." });
            string? Field(string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
            if (string.IsNullOrWhiteSpace(Field("kind")) || string.IsNullOrWhiteSpace(Field("subject"))) return Results.BadRequest(new { error = "A work item has a kind and a subject." });
            var queue = new DbDataKit.Scheduling.ModuleWorkQueueStore(state, "runner.work");
            var runId = queue.Enqueue($"{package}/{channel}/{task}", Field("kind")!, Field("subject")!, Field("part") ?? "", Field("resource") ?? "", Field("lane") ?? "", Field("payload"));
            return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["run_id"] = runId.ToString() }, statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet($"{root}/runs", (HttpContext context) =>
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
            var runs = new RunStore(state, RunnerNames.EngineService).Read(new RunQuery(RunnerNames.EngineService, job, outcome, StartedBefore: before, Limit: limit, Package: package, Channel: channel));
            return Results.Json(new JsonObject { ["runs"] = new JsonArray(runs.Select(r => (JsonNode)Run(r)).ToArray()) });
        });

        // one run and its log, by id (plan 28c E13): what a product's screen reads for a run of its own task, for the lines of a run that is still going and for the ones after it ended. Viewer, as the list is.
        app.MapGet($"{root}/runs/{{id:guid}}", (Guid id, HttpContext context) =>
        {
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            return new RunStore(state, RunnerNames.EngineService).Get(id) is { } run && run.Service == RunnerNames.EngineService ? Results.Json(Run(run)) : Results.NotFound(new { error = $"There is no run {id}." });
        });

        app.MapGet($"{root}/runs/{{id:guid}}/logs", (Guid id, HttpContext context) =>
        {
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            var query = context.Request.Query;
            long? after = null;
            if (Text(query["after"]) is { } afterText)
            {
                if (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return Results.BadRequest(new { error = "after is the id of the last line seen." });
                after = parsed;
            }

            var limit = 200;
            if (Text(query["limit"]) is { } limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 1000))
                return Results.BadRequest(new { error = "limit is a whole number from 1 to 1000." });
            var runs = new RunStore(state, RunnerNames.EngineService);
            if (runs.Get(id) is not { } found || found.Service != RunnerNames.EngineService) return Results.NotFound(new { error = $"There is no run {id}." });
            var lines = runs.Logs(id, after, limit);
            return Results.Json(new JsonObject
            {
                ["run"] = id.ToString(),
                ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
                {
                    ["id"] = l.Id, ["at"] = l.At.UtcDateTime.ToString("O"), ["level"] = l.Level, ["item"] = l.Item, ["message"] = l.Message,
                }).ToArray()),
            });
        });

        // what the runs of a task recorded as steps, newest first (plan 28c E13): the outputs a product keeps with its runs (Sync's pass records and polling checks). Viewer.
        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/steps/{{name}}", (string package, string channel, string task, string name, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            if (!PackageManifestValidator.NameRule.IsMatch(name)) return Results.BadRequest(new { error = $"'{name}' is not a step name." });
            var query = context.Request.Query;
            var limit = 100;
            if (Text(query["limit"]) is { } limitText && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 500))
                return Results.BadRequest(new { error = "limit is a whole number from 1 to 500." });
            DateTimeOffset? before = null;
            if (Text(query["before"]) is { } beforeText)
            {
                if (!DateTimeOffset.TryParse(beforeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return Results.BadRequest(new { error = "before is a time, such as 2026-11-07T12:00:00Z." });
                before = at;
            }

            var steps = new StepStore(state, new ProgressKey(package, channel, task)).Recent(name, limit, before, Text(query["key"]));
            return Results.Json(new JsonObject
            {
                ["task"] = reference!.ToString(),
                ["step"] = name,
                ["steps"] = new JsonArray(steps.Select(step => (JsonNode)new JsonObject
                {
                    ["key"] = step.Key, ["started"] = step.StartedAt.UtcDateTime.ToString("O"), ["completed"] = step.CompletedAt?.UtcDateTime.ToString("O"),
                    ["result"] = step.ResultJson is { } json ? JsonNode.Parse(json) : null,
                }).ToArray()),
            });
        });

        // the streamed result of a step (plan 28c E06e): the file a step kept as a payload of its run (Sync's verification result). Viewer, as the steps are. Only a payload that is the result of a completed step of that name of that task is served.
        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/steps/{{name}}/payloads/{{id:guid}}", (string package, string channel, string task, string name, Guid id, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out _, out var bad)) return bad!;
            if (Deny(context, ServerRole.Viewer) is { } denied) return denied;
            if (!PackageManifestValidator.NameRule.IsMatch(name)) return Results.BadRequest(new { error = $"'{name}' is not a step name." });
            var gone = Results.NotFound(new { error = $"There is no result {id} of the step {name} of {package}/{channel}/{task} (the retention may have removed it)." });
            if (!new StepStore(state, new ProgressKey(package, channel, task)).IsResultPayload(name, id)) return gone;
            PayloadReader reader;
            try { reader = new PayloadStore(state).OpenRead(id); }
            catch (FileNotFoundException) { return gone; }
            return Results.Stream(reader, reader.Info.MediaType ?? "application/octet-stream");
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/run", async (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.RunVerb, engine(), reference!) is { } denied) return denied;
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            var dryRun = false;
            string? onBehalfOf = null;
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
                    if (body["dry_run"] is { } dry)
                    {
                        if (dry is not JsonValue dryValue || !dryValue.TryGetValue<bool>(out dryRun)) return Results.BadRequest(new { error = "dry_run is true or false." });
                    }

                    // a paired server that starts a run for a person it signed in names them (plan 28c E21); the run's record shows the person with the caller beside them
                    if (body["on_behalf_of"] is { } person)
                    {
                        if (person is not JsonValue personValue || !personValue.TryGetValue<string>(out onBehalfOf) || onBehalfOf.Length > RunnerControl.MaxOnBehalfOf)
                            return Results.BadRequest(new { error = $"on_behalf_of is the name of a person, text of at most {RunnerControl.MaxOnBehalfOf} characters." });
                    }

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
                var result = control.RunNow(reference!, inputs, ActorOf(context), dryRun, onBehalfOf);
                return result.Queued
                    ? Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["run_id"] = result.RunId!.Value.ToString() }, statusCode: StatusCodes.Status202Accepted)
                    : Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["queued"] = false, ["error"] = $"{reference} is queued or running already." }, statusCode: StatusCodes.Status409Conflict);
            });
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/pause", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.PauseVerb, engine(), reference!) is { } denied) return denied;
            return Control(engine(), control =>
            {
                control.Pause(reference!, ActorOf(context));
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["paused"] = true });
            });
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/resume", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (Deny(context, ServerRole.Operator, RunnerControl.ResumeVerb, engine(), reference!) is { } denied) return denied;
            return Control(engine(), control =>
            {
                control.Resume(reference!, ActorOf(context));
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["paused"] = false });
            });
        });

        RunnerOperatorApi.Map(app, root, state, engine, (context, needed, verb, reference) => Deny(context, needed, verb, engine(), reference), ActorOf);

        MapOwnership(app, root, engine, context => Deny(context, ServerRole.Viewer), (context, verb, reference) => Deny(context, ServerRole.Operator, verb, engine(), reference), ActorOf);
    }

    /// <summary>
    /// Who schedules a task (plan 28c E09): <c>GET .../tasks/{package}/{channel}/{task}/schedule</c> (owner, position, pause, items in flight), <c>PUT .../owner</c> with <c>{"owner": "old|observe|runner", "reason": "...", "handled_up_to": "...", "paused_by": "...",
    /// "paused_at": "..."}</c> (an operator; the seed is the position the product's scheduler had reached, <c>paused_by</c> its pause), <c>GET .../decisions[?since=]</c> and <c>POST .../decisions</c> with <c>{"at": "...", "missed": 0, "paused": false}</c>
    /// (the product reports what its own scheduler queued for an observed task).
    /// </summary>
    private static void MapOwnership(IEndpointRouteBuilder app, string root, Func<RunnerHost> engine, Func<HttpContext, IResult?> viewer, Func<HttpContext, string, TaskRef, IResult?> operatorOnly, Func<HttpContext, RunnerActor> actorOf)
    {
        static string? Time(DateTimeOffset? at) => at?.UtcDateTime.ToString("O");

        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/schedule", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (viewer(context) is { } denied) return denied;
            return Control(engine(), control =>
            {
                var state = control.ScheduleState(reference!);
                var record = control.Owner(reference!).Record;
                return Results.Json(new JsonObject
                {
                    ["task"] = reference!.ToString(), ["owner"] = state.Owner, ["owner_set_by"] = record?.ChangedBy, ["owner_set_at"] = Time(record?.ChangedAt), ["reason"] = record?.Reason,
                    ["handled_up_to"] = Time(state.HandledUpTo), ["paused"] = state.PausedBy is not null, ["paused_by"] = state.PausedBy, ["paused_at"] = Time(state.PausedAt),
                    ["running"] = state.Running, ["queued"] = state.Queued, ["version"] = state.Version,
                });
            });
        });

        app.MapPut($"{root}/tasks/{{package}}/{{channel}}/{{task}}/owner", async (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (operatorOnly(context, RunnerControl.OwnerVerb, reference!) is { } denied) return denied;
            if (await ReadObject(context) is not { } body) return Results.BadRequest(new { error = "The body is a JSON object, such as {\"owner\": \"runner\", \"handled_up_to\": \"2026-11-07T12:00:00Z\"}." });
            var owner = Field(body, "owner");
            if (!TaskOwners.IsOwner(owner)) return Results.BadRequest(new { error = $"owner is one of {string.Join(", ", TaskOwners.All)}." });
            DateTimeOffset? handled = null, pausedAt = null;
            foreach (var (name, set) in new (string, Action<DateTimeOffset>)[] { ("handled_up_to", t => handled = t), ("paused_at", t => pausedAt = t) })
            {
                if (Field(body, name) is not { } text) continue;
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return Results.BadRequest(new { error = $"{name} is a time, such as 2026-11-07T12:00:00Z." });
                set(parsed);
            }

            var pausedBy = Field(body, "paused_by");
            if (handled is null && (pausedBy is not null || pausedAt is not null)) return Results.BadRequest(new { error = "A pause is handed over with the position it belongs to: give handled_up_to too." });
            var seed = handled is { } at ? new ScheduleSeed(at, pausedBy, pausedAt) : null;
            return Control(engine(), control =>
            {
                control.SetOwner(reference!, owner!, Field(body, "reason"), seed, actorOf(context));
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["owner"] = owner });
            });
        });

        app.MapGet($"{root}/tasks/{{package}}/{{channel}}/{{task}}/decisions", (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (viewer(context) is { } denied) return denied;
            DateTimeOffset? since = null;
            if (Text(context.Request.Query["since"]) is { } text)
            {
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return Results.BadRequest(new { error = "since is a time, such as 2026-11-07T12:00:00Z." });
                since = parsed;
            }

            return Control(engine(), control => Results.Json(new JsonObject
            {
                ["task"] = reference!.ToString(),
                ["decisions"] = new JsonArray(control.Decisions(reference!, since).Select(d => (JsonNode)new JsonObject { ["source"] = d.Source, ["at"] = Time(d.DecidedAt), ["missed"] = d.Missed, ["paused"] = d.Paused }).ToArray()),
            }));
        });

        app.MapPost($"{root}/tasks/{{package}}/{{channel}}/{{task}}/decisions", async (string package, string channel, string task, HttpContext context) =>
        {
            if (!ParseTask(package, channel, task, out var reference, out var bad)) return bad!;
            if (operatorOnly(context, RunnerControl.OwnerVerb, reference!) is { } denied) return denied;
            if (await ReadObject(context) is not { } body || Field(body, "at") is not { } atText
                || !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return Results.BadRequest(new { error = "The body is a JSON object with the time the product's scheduler queued the run: {\"at\": \"2026-11-07T12:00:00Z\", \"missed\": 0, \"paused\": false}." });
            var missed = body["missed"] is JsonValue m && m.TryGetValue<int>(out var count) && count >= 0 ? count : 0;
            var paused = body["paused"] is JsonValue p && p.TryGetValue<bool>(out var flag) && flag;
            return Control(engine(), control =>
            {
                control.RecordOldDecision(reference!, at, missed, paused);
                return Results.Json(new JsonObject { ["task"] = reference!.ToString(), ["recorded"] = true });
            });
        });
    }

    private static string? Field(JsonObject body, string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static async Task<JsonObject?> ReadObject(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var text = await reader.ReadToEndAsync(context.RequestAborted);
        if (text.Length is 0 or > 100_000) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }


    public static JsonObject Status(StateDatabase state, RunnerHost engine, string instanceId, RunnerApiOptions options)
    {
        var status = new JsonObject
        {
            ["product"] = options.Product,
            ["version"] = options.Version,
            ["instance_id"] = instanceId,
            ["runner_instance"] = engine.InstanceId?.ToString(),
            ["running"] = engine.Control is not null,
            ["state_engine"] = state.Dialect.Engine,
            ["group"] = engine.Group,
            ["capabilities"] = new JsonObject(engine.Capabilities.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
        };
        if (engine.Control is not { } control)
        {
            status["channels"] = new JsonArray();
            status["tasks"] = new JsonArray();
            status["problems"] = new JsonArray();
            status["instances"] = new JsonArray();
            options.DecorateStatus?.Invoke(status);
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
            ["owner"] = t.Owner,
            ["problem"] = t.Problem,
            ["required_labels"] = new JsonArray((t.RequiredLabels ?? []).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            ["waiting_reason"] = t.WaitingReason,
        }).ToArray());
        status["instances"] = RunnerGroupApi.Instances(state, engine);
        status["triggers"] = new JsonArray(control.Triggers.Statuses().Select(t => (JsonNode)new JsonObject
        {
            ["package"] = t.Task.Package, ["channel"] = t.Task.Channel, ["task"] = t.Task.Task, ["kind"] = t.Kind, ["id"] = t.Id, ["state"] = t.State,
            ["last_fired"] = t.LastFiredAt?.UtcDateTime.ToString("O"), ["problem"] = t.Problem,
        }).ToArray());
        status["problems"] = new JsonArray(scheduler.Problems.Select(p => (JsonNode)new JsonObject { ["subject"] = p.Subject, ["message"] = p.Message, ["at"] = p.At.UtcDateTime.ToString("O") }).ToArray());
        options.DecorateStatus?.Invoke(status);
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

    private static string PartText(ProgressValue value) => value.Value switch { DateTimeOffset at => at.UtcDateTime.ToString("O"), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var other => other.ToString() ?? "" };

    private static string? Text(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 0 || string.IsNullOrWhiteSpace(values[0]) ? null : values[0];

    internal static bool ParseTask(string package, string channel, string task, out TaskRef? reference, out IResult? bad)
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

    private static IResult Control(RunnerHost engine, Func<RunnerControl, IResult> act)
    {
        if (engine.Control is not { } control) return Results.Json(new { error = "The runner is starting; try again in a moment." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        try { return act(control); }
        catch (UnknownTaskException ex) { return Results.NotFound(new { error = ex.Message }); }
    }

    private static IResult? DenyCall(RunnerCaller caller, ServerRole needed, string? verb, RunnerHost? engine, TaskRef? task)
    {
        var decision = caller.Authorize(needed);
        if (decision.Allowed) return null;
        // a refused act is on record too, so an operator reading the audit sees who tried
        if (verb is not null && task is not null && engine?.Control is { } control) control.Refused(verb, task, caller.Actor, decision.Reason ?? "the role is not enough");
        return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
    }
}
