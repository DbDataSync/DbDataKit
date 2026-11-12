using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// The merge of a staged set into the changes the Store is sent (plan 32 DBR53b). The same rules as <c>stageChange</c> of <c>@dbdatakit/visuals</c> hold for the shell; this is the server's own check, because the page is not trusted:
/// every line is a <c>change</c> of <b>one model</b> (a set over two models is refused naming both), the set has at most <see cref="ViewsOptions.MaxBatchChanges"/> lines, edits of the same row are **one** change (the later write of a
/// field wins; the row's version is the first one given, since the Store applies a batch row by row and a second edit over the same version would conflict with the first), a delete of a row drops the edits staged for it, and an edit of a row
/// staged for delete is refused.
/// </summary>
public static class ViewsBatches
{
    public sealed record Refusal(string Code, string Message, int? Line = null);

    /// <summary>The changes of <paramref name="lines"/> (the resolved actions in staged order), or the refusal.</summary>
    public static (IReadOnlyList<BatchChange>? Changes, string? Model, Refusal? Refusal) Merge(IReadOnlyList<ResolvedAction> lines, int limit)
    {
        if (lines.Count == 0) return (null, null, new Refusal("invalid", "A batch has at least one line."));
        if (lines.Count > limit) return (null, null, new Refusal("limit", $"A set has at most {limit} changes; submit these first."));
        var model = lines[0].Model!;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Kind != "change") return (null, null, new Refusal("not_a_change", $"The line {i + 1} (`{l.Link}`) runs a task, and a task is confirmed once, not staged.", i));
            if (!string.Equals(l.Model, model, StringComparison.Ordinal))
                return (null, null, new Refusal("two_models", $"A set of changes is for one model: it has a change to `{model}` and a change to `{l.Model}`. Submit them separately.", i));
        }
        string KeyOf(ResolvedAction a) => a.Entity + "|" + string.Join("&", a.Key.Select(k => $"{k.Name}={k.Value}"));
        var changes = new List<(ResolvedAction Action, List<int> Lines)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Operation == "insert") { changes.Add((l, [i])); continue; }
            var existing = changes.FindIndex(c => c.Action.Operation != "insert" && KeyOf(c.Action) == KeyOf(l));
            if (existing < 0) { changes.Add((l, [i])); continue; }
            var (have, from) = changes[existing];
            if (have.Operation == "delete")
            {
                if (l.Operation == "delete") { changes[existing].Lines.Add(i); continue; }
                return (null, null, new Refusal("staged_for_delete", $"The line {i + 1} edits a row that an earlier line deletes: remove the delete from the set to edit it.", i));
            }
            if (l.Operation == "delete") { changes[existing] = (l with { TypedVersion = have.TypedVersion ?? l.TypedVersion }, [.. from, i]); continue; }   // the delete drops the edits staged for the row
            // two edits of one row: one change, the later value of a field wins, the first version stands
            var typed = have.TypedWrites.ToDictionary(p => p.Key, p => p.Value);
            foreach (var (k, v) in l.TypedWrites) typed[k] = v;
            var writes = have.Writes.Where(w => !l.Writes.Any(x => x.Name == w.Name)).Concat(l.Writes).ToList();
            changes[existing] = (have with { TypedWrites = typed, Writes = writes, TypedVersion = have.TypedVersion ?? l.TypedVersion, Version = have.Version ?? l.Version }, [.. from, i]);
        }
        return ([.. changes.Select(c => new BatchChange(c.Action, c.Lines))], model, null);
    }
}

public sealed partial class ViewsApi
{
    /// <summary>One line of a batch body, read: the action link, the widget, and what the action is derived from.</summary>
    private sealed record Line(ViewLink Link, string Widget, JsonObject Row, JsonObject Parameters, Dictionary<string, string?> Answers);

    private (List<Line>? Lines, ViewsAnswer? Refusal) ReadLines(ViewsPrincipal who, string verb, string kind, PublishedView view, JsonObject? body, string? traceId, out string key)
    {
        key = "";
        try
        {
            if (body is null) throw new ArgumentException("A batch request has `lines` and `idempotency_key`.");
            foreach (var k in body.Select(p => p.Key))
                if (k is not ("lines" or "idempotency_key")) throw new ArgumentException($"Unknown key `{k}`: a batch request has `lines` and `idempotency_key`.");
            key = verb == ViewsAuditVerbs.BatchForecast && body["idempotency_key"] is null ? "forecast"
                : body["idempotency_key"] is JsonValue kv && kv.TryGetValue<string>(out var ks) && ks.Length is > 0 and <= MaximumKeyLength && ks.All(c => c > ' ' && c < 127)
                    ? ks : throw new ArgumentException($"`idempotency_key` is a text of 1 to {MaximumKeyLength} printable characters (the page makes one when the review opens).");
            var list = body["lines"] as JsonArray ?? throw new ArgumentException("`lines` is the list of staged changes.");
            if (list.Count > options.MaxBatchChanges) return (null, Refuse(who, verb, kind, view, 400, "limit", $"A set has at most {options.MaxBatchChanges} changes; submit these first.", traceId));
            var lines = new List<Line>();
            for (var i = 0; i < list.Count; i++)
            {
                var o = list[i] as JsonObject ?? throw new ArgumentException($"The line {i + 1} must be an object.");
                foreach (var k in o.Select(p => p.Key))
                    if (k is not ("link" or "widget" or "row" or "parameters" or "answers")) throw new ArgumentException($"The line {i + 1} has an unknown key `{k}`: a line has `link`, `widget`, `row`, `parameters` and `answers`.");
                var link = o["link"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : throw new ArgumentException($"The line {i + 1} names its `link`.");
                if (!view.Document.Links.TryGetValue(link, out var l) || l.Action is null) return (null, Refuse(who, verb, kind, view, 404, "unknown_action", $"The {kind} '{view.Name}' has no action link named '{link}' (line {i + 1}).", traceId, ("link", link)));
                var widget = o["widget"] is JsonValue wv && wv.TryGetValue<string>(out var w) && WidgetIdPattern.IsMatch(w) && view.Document.Widgets.Any(x => x.Id == w) ? w : throw new ArgumentException($"The line {i + 1} names the `widget` it came from, which the document must have.");
                var row = o["row"] is null ? [] : o["row"] as JsonObject ?? throw new ArgumentException($"The line {i + 1}'s `row` is an object.");
                var parameters = o["parameters"] is null ? [] : o["parameters"] as JsonObject ?? throw new ArgumentException($"The line {i + 1}'s `parameters` is an object.");
                var answers = new Dictionary<string, string?>();
                if (o["answers"] is { } a)
                    foreach (var (an, av) in a as JsonObject ?? throw new ArgumentException($"The line {i + 1}'s `answers` is an object of text."))
                        answers[an] = av is null ? null : av is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"The answer `{an}` of line {i + 1} is text.");
                lines.Add(new Line(l, widget, row, parameters, answers));
            }
            return (lines, null);
        }
        catch (ArgumentException e)
        {
            return (null, Refuse(who, verb, kind, view, 400, "invalid", e.Message, traceId));
        }
    }

    /// <summary>The resolved actions of the lines, or the refusal: each line's answers are checked and its values must be given by its row, and an app's token must carry the write scope of each entity.</summary>
    private (List<ResolvedAction>? Resolved, ViewsAnswer? Refusal) ResolveLines(ViewsPrincipal who, string verb, string kind, PublishedView view, List<Line> lines, string? traceId)
    {
        var resolved = new List<ResolvedAction>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (Allowed(who, l.Link.Action!) is { } why) return (null, Refuse(who, verb, kind, view, 403, "action_not_allowed", why, traceId, ("link", l.Link.Name), ("line", i)));
            var problems = ActionResolution.CheckAnswers(l.Link.Action!.Ask.Values, l.Answers);
            if (problems.Count > 0) return (null, Refuse(who, verb, kind, view, 400, "invalid_answer", $"Line {i + 1}: {string.Join(" ", problems.Values)}", traceId, ("link", l.Link.Name), ("line", i)));
            var r = ActionResolution.Resolve(l.Link, l.Row, l.Parameters, l.Answers);
            if (r.Missing.Count > 0) return (null, Refuse(who, verb, kind, view, 400, "missing_values", $"Line {i + 1}: the row does not give {string.Join(", ", r.Missing)}, so it cannot be sent.", traceId, ("link", l.Link.Name), ("line", i)));
            resolved.Add(r);
        }
        return (resolved, null);
    }

    /// <summary>What will happen to a set of changes (<c>POST .../batches/forecast</c> with the lines): apply together or wait for approval as one request, by the model's approval for this caller.</summary>
    public async Task<ViewsAnswer> ForecastBatchAsync(ViewsPrincipal who, string kind, string name, JsonObject? body, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.BatchForecast, traceId);
            if (found.View is not { } view) return found.Refusal!;
            if (options.Actions is not { } actions) return NoActions(who, ViewsAuditVerbs.BatchForecast, kind, view, traceId);
            var (lines, refusal) = ReadLines(who, ViewsAuditVerbs.BatchForecast, kind, view, body, traceId, out _);
            if (lines is null) return refusal!;
            var (resolved, bad) = ResolveLines(who, ViewsAuditVerbs.BatchForecast, kind, view, lines, traceId);
            if (resolved is null) return bad!;
            var (_, model, broken) = ViewsBatches.Merge(resolved, options.MaxBatchChanges);
            if (broken is not null) return Refuse(who, ViewsAuditVerbs.BatchForecast, kind, view, 400, broken.Code, broken.Message, traceId);
            try
            {
                var forecast = await actions.ForecastBatchAsync(who, model!, cancellationToken).ConfigureAwait(false);
                return Finish(who, ViewsAuditVerbs.BatchForecast, kind, view.Name, view.Version, AuditEvent.Ok, Detail(traceId, ("lines", lines.Count), ("will", forecast?.Will.ToString().ToLowerInvariant())),
                    new ViewsAnswer(200, new JsonObject { ["will"] = forecast is null ? null : forecast.Will.ToString().ToLowerInvariant(), ["note"] = forecast?.Note }));
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure or OperationCanceledException))
            {
                Record(who, ViewsAuditVerbs.BatchForecast, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("error", e.GetType().Name)));
                return ViewsAnswer.Error(500, "error", $"What will happen could not be worked out ({e.GetType().Name}).");
            }
        }
    }

    /// <summary>
    /// <b>Submits a staged set as one batch as the viewer</b> (<c>POST /api/v1/dashboards/{name}/batches</c>, plan 32 DBR53b). The body is <c>{lines: [{link, widget, row, parameters?, answers?}], idempotency_key}</c>: each line is
    /// what the shell staged, and each change is derived from the published document as for a single action, so a page cannot say what to write. The set is one model (a set over two is <c>400 two_models</c> naming both), at most
    /// <see cref="ViewsOptions.MaxBatchChanges"/> lines, and an app's token must carry every entity's write scope. The host applies **all of it in one transaction** or keeps **one change request of all of it** (<c>202</c>),
    /// or none: <c>409</c> a row's version moved, <c>422</c> every broken rule, each <c>problems</c> entry naming the <c>line</c> it is about. One key makes the whole submit one call. Audited as <c>view.batch</c> with a batch id,
    /// the number of lines and changes and the entities, never a value; the Store's audit rows say <c>Ada Lovelace, through sales-portal 3, widget grid, batch 1f2e...</c>.
    /// </summary>
    public async Task<ViewsAnswer> RunBatchAsync(ViewsPrincipal who, string kind, string name, JsonObject? body, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Batch, traceId);
            if (found.View is not { } view) return found.Refusal!;
            if (options.Actions is not { } actions) return NoActions(who, ViewsAuditVerbs.Batch, kind, view, traceId);
            var (lines, refusal) = ReadLines(who, ViewsAuditVerbs.Batch, kind, view, body, traceId, out var key);
            if (lines is null) return refusal!;
            var (resolved, bad) = ResolveLines(who, ViewsAuditVerbs.Batch, kind, view, lines, traceId);
            if (resolved is null) return bad!;
            var (changes, model, broken) = ViewsBatches.Merge(resolved, options.MaxBatchChanges);
            if (broken is not null) return Refuse(who, ViewsAuditVerbs.Batch, kind, view, 400, broken.Code, broken.Message, traceId, ("line", broken.Line));

            var batchId = Guid.NewGuid().ToString("N");
            var widgets = lines.Select(l => l.Widget).Distinct().Order(StringComparer.Ordinal).ToList();
            var via = $"{(who.App is { } app ? (app.Label ?? app.Instance) + ", " : "")}widget {string.Join(" ", widgets)}, batch {batchId}";
            var call = new BatchCall(who, model!, changes!, lines.Count, via, batchId, key, view.Name, view.Version);
            BatchResult result;
            bool replayed;
            try
            {
                (result, replayed) = await BatchIdempotency.RunAsync($"{who.Caller.Id}\n{kind}/{view.Name}\nbatch", key, () => actions.RunBatchAsync(call, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure))
            {
                Record(who, ViewsAuditVerbs.Batch, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("batch_id", batchId), ("lines", lines.Count), ("error", e.GetType().Name)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(502, "failed", $"The batch could not be done ({e.GetType().Name}).");
            }
            return Answer(who, kind, view, traceId, lines, changes!, result, batchId, replayed);
        }
    }

    private ViewsAnswer Answer(ViewsPrincipal who, string kind, PublishedView view, string? traceId, List<Line> lines, IReadOnlyList<BatchChange> changes, BatchResult result, string batchId, bool replayed)
    {
        var status = result.Status.ToString().ToLowerInvariant();
        var outcome = result.Status is ActionStatus.Applied or ActionStatus.Waiting ? AuditEvent.Ok : result.Status == ActionStatus.Failed ? AuditEvent.Failed : AuditEvent.Refused;
        var json = new JsonObject { ["status"] = status, ["count"] = result.Count, ["message"] = result.Message, ["request_id"] = result.RequestId, ["batch_id"] = batchId };
        var problems = new JsonArray();
        foreach (var p in result.Problems ?? [])
        {
            // a problem about a change is about every staged line merged into it; one about the set has no line
            var about = p.Change is { } c && c >= 0 && c < changes.Count ? changes[c].Lines : [];
            if (about.Count == 0) problems.Add(new JsonObject { ["line"] = null, ["rule"] = p.Rule, ["attribute"] = p.Attribute, ["message"] = p.Message });
            else foreach (var line in about) problems.Add(new JsonObject { ["line"] = line, ["rule"] = p.Rule, ["attribute"] = p.Attribute, ["message"] = p.Message });
        }
        if (problems.Count > 0) json["problems"] = problems;
        var http = result.Status switch { ActionStatus.Applied => 200, ActionStatus.Waiting => 202, ActionStatus.Conflict => 409, ActionStatus.Refused => 422, ActionStatus.Denied => 403, _ => 502 };
        if (outcome != AuditEvent.Ok) Interlocked.Increment(ref refused);
        var entities = changes.Select(c => c.Action.Entity).Distinct().Order(StringComparer.Ordinal).ToList();
        return Finish(who, ViewsAuditVerbs.Batch, kind, view.Name, view.Version, outcome,
            Detail(traceId, ("batch_id", batchId), ("lines", lines.Count), ("changes", changes.Count), ("model", changes[0].Action.Model), ("entities", entities), ("status", status), ("replayed", replayed ? true : null), ("request_id", result.RequestId)),
            new ViewsAnswer(http, json));
    }
}
