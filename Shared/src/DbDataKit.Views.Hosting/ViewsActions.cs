using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>What the host's store (or runner) did with an action (plan 32 DBR53).</summary>
public enum ActionStatus
{
    /// <summary>The change was made (200).</summary>
    Applied,
    /// <summary>The Store answered 202: the change is kept as a change request and waits for approval (202).</summary>
    Waiting,
    /// <summary>The task was queued or started (202).</summary>
    Started,
    /// <summary>The row's version moved since it was read (409).</summary>
    Conflict,
    /// <summary>The model's rules refused it: every broken rule is in <see cref="ActionResult.Violations"/> (422).</summary>
    Refused,
    /// <summary>The viewer's role, or the app's manifest, does not allow it (403).</summary>
    Denied,
    /// <summary>It could not be done (the store or the runner could not be reached, no runner is connected): 502.</summary>
    Failed,
}

/// <summary>One rule the Store says was broken, with the field it is about.</summary>
public sealed record ActionViolation(string? Rule, string? Attribute, string Message);

public sealed record ActionResult(ActionStatus Status, string? Message = null, string? RequestId = null, string? RunId = null, IReadOnlyList<ActionViolation>? Violations = null)
{
    public static ActionResult Applied(string? message = null) => new(ActionStatus.Applied, message);
}

/// <summary>What will happen when the action is sent: it applies at once, it waits for approval (<see cref="ActionForecast.Note"/> says by whom), or a task runs.</summary>
public enum ActionWill { Apply, Wait, Run }

public sealed record ActionForecast(ActionWill Will, string? Note = null);

/// <summary>
/// One action as the module hands it to the host (<see cref="IViewsActions"/>): the viewer, **the action derived from the published document** (never from the page), the audit words and the idempotency key. The host calls the
/// Store or the runner as <see cref="ViewsPrincipal.Caller"/>, never as itself. <see cref="Via"/> is what the Store's audit row says the change went through: <c>sales-portal 3, widget orders, link close-order</c>, or
/// <c>widget orders, link close-order</c> outside an app.
/// </summary>
public sealed record ActionCall(ViewsPrincipal Who, ResolvedAction Action, string Via, string IdempotencyKey, string Document, int Version);

/// <summary>One change of a batch: the action as the document and the rows derived it, with every staged line that was merged into it (the same row staged more than once is one change in the Store's batch).</summary>
public sealed record BatchChange(ResolvedAction Action, IReadOnlyList<int> Lines);

/// <summary>One staged set sent as one batch (plan 32 DBR53b): the viewer, the one model, the changes in order, what the audit says (<see cref="Via"/>, <see cref="BatchId"/>) and the one idempotency key.</summary>
public sealed record BatchCall(ViewsPrincipal Who, string Model, IReadOnlyList<BatchChange> Changes, int LineCount, string Via, string BatchId, string IdempotencyKey, string Document, int Version);

/// <summary>A reason a batch was refused, against the <see cref="Change"/> of the call it is about (null: the set as a whole): the Store's rule, the field and the sentence.</summary>
public sealed record BatchProblem(int? Change, string? Rule, string? Attribute, string Message);

/// <summary>What the host's store did with a batch: <see cref="Count"/> changes applied together (200), kept as one change request (202), or none made (409, 422, 403, 502) with the <see cref="Problems"/> by change.</summary>
public sealed record BatchResult(ActionStatus Status, int Count, string? Message = null, string? RequestId = null, IReadOnlyList<BatchProblem>? Problems = null);

/// <summary>
/// What answers an action (plan 32 DBR53): in the Gateway, the group's Store (<c>change</c>: one change as the viewer with the row's version, answered 200, 202 for a change request, 409 for a stale version, 422 with every broken
/// rule) and, when a runner is connected, <c>RunNow</c> under the viewer's role (<c>task</c>). The module checks the document, the row and the answers first; this checks the viewer's rights and does the work.
/// </summary>
public interface IViewsActions
{
    /// <summary>What would happen for this viewer: the model's approval for a change, a run for a task. Null when the host cannot say.</summary>
    Task<ActionForecast?> ForecastAsync(ViewsPrincipal who, ActionDefinition action, CancellationToken cancellationToken);

    Task<ActionResult> RunAsync(ActionCall call, CancellationToken cancellationToken);

    /// <summary>What would happen to a set of changes to <paramref name="model"/> for this viewer: they apply together, or wait for approval as one request. Null when the host cannot say.</summary>
    Task<ActionForecast?> ForecastBatchAsync(ViewsPrincipal who, string model, CancellationToken cancellationToken);

    /// <summary>One batch (plan 32 DBR53b): every change in one transaction of the Store as the viewer, or one change request holding all of them; nothing applied when one is refused.</summary>
    Task<BatchResult> RunBatchAsync(BatchCall call, CancellationToken cancellationToken);
}

/// <summary>
/// One call at a time per key (plan 32 DBR53): a double click, or a retry of a call whose answer was lost, is one change. The first call with a key (for the same person, document and link) runs; a second one with the same key
/// while it runs waits for it and gets its answer; one after it finished gets the **same answer** without running again, for <see cref="Retention"/>. A call whose answer <c>keeps</c> says false (a refused, conflicting or failed one)
/// is not kept, so the person can fix the cause and send again with the same key (the popup does).
/// </summary>
public sealed class IdempotentCalls<T>(TimeProvider time, TimeSpan retention, Func<T, bool> keeps, int capacity = 5000)
{
    public TimeSpan Retention { get; } = retention;

    private sealed class Entry
    {
        public readonly TaskCompletionSource<T> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset At;
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public async Task<(T Result, bool Replayed)> RunAsync(string scope, string key, Func<Task<T>> run)
    {
        var id = scope + "\n" + key;
        Sweep();
        var mine = new Entry { At = time.GetUtcNow() };
        var entry = entries.GetOrAdd(id, mine);
        if (!ReferenceEquals(entry, mine)) return (await entry.Done.Task.ConfigureAwait(false), true);
        try
        {
            var result = await run().ConfigureAwait(false);
            if (!keeps(result)) entries.TryRemove(id, out _);
            mine.Done.TrySetResult(result);
            return (result, false);
        }
        catch (Exception e)
        {
            entries.TryRemove(id, out _);
            mine.Done.TrySetException(e);
            throw;
        }
    }

    private void Sweep()
    {
        if (entries.Count < capacity / 2) return;
        var cutoff = time.GetUtcNow() - Retention;
        foreach (var (k, v) in entries)
            if (v.Done.Task.IsCompleted && v.At < cutoff) entries.TryRemove(k, out _);
        // over capacity after the sweep: the oldest finished entries go
        while (entries.Count > capacity && entries.Where(p => p.Value.Done.Task.IsCompleted).OrderBy(p => p.Value.At).FirstOrDefault() is { Key: { } oldest }) entries.TryRemove(oldest, out _);
    }
}

public sealed partial class ViewsApi
{
    /// <summary>The longest idempotency key (a GUID is 36).</summary>
    public const int MaximumKeyLength = 100;

    // made with the module, not on first use: two calls with one key arrive together, and each must see the same table
    private readonly IdempotentCalls<ActionResult> idempotency = new(options.Time, options.ActionRetention, r => r.Status is ActionStatus.Applied or ActionStatus.Waiting or ActionStatus.Started);
    private readonly IdempotentCalls<BatchResult> batchIdempotency = new(options.Time, options.ActionRetention, r => r.Status is ActionStatus.Applied or ActionStatus.Waiting);
    private IdempotentCalls<ActionResult> Idempotency => idempotency;
    private IdempotentCalls<BatchResult> BatchIdempotency => batchIdempotency;

    /// <summary>The action link of a published document, or the refusal (404 for a link that is not declared or is not an action).</summary>
    private (ViewLink? Link, ViewsAnswer? Refusal) ActionLink(ViewsPrincipal who, string verb, string kind, PublishedView view, string link, string? traceId)
    {
        if (view.Document.Links.TryGetValue(link, out var l) && l.Action is not null) return (l, null);
        return (null, Refuse(who, verb, kind, view, 404, "unknown_action", $"The {kind} '{view.Name}' has no action link named '{link}'.", traceId, ("link", link)));
    }

    /// <summary>
    /// <b>What will happen</b> for an action, before the person confirms (<c>POST /api/v1/dashboards/{name}/actions/{link}/forecast</c>): the model's approval decides whether a change applies or waits, so the popup can say so.
    /// The answer is <c>{will: apply|wait|run, note?}</c>; a host with no <see cref="IViewsActions"/> answers 501.
    /// </summary>
    public async Task<ViewsAnswer> ForecastActionAsync(ViewsPrincipal who, string kind, string name, string link, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.ActionForecast, traceId);
            if (found.View is not { } view) return found.Refusal!;
            if (options.Actions is not { } actions) return NoActions(who, ViewsAuditVerbs.ActionForecast, kind, view, traceId);
            var (l, refusal) = ActionLink(who, ViewsAuditVerbs.ActionForecast, kind, view, link, traceId);
            if (l is null) return refusal!;
            if (Allowed(who, l.Action!) is { } why) return Refuse(who, ViewsAuditVerbs.ActionForecast, kind, view, 403, "action_not_allowed", why, traceId, ("link", link));
            try
            {
                var forecast = await actions.ForecastAsync(who, l.Action!, cancellationToken).ConfigureAwait(false);
                var body = new JsonObject { ["will"] = forecast is null ? null : forecast.Will.ToString().ToLowerInvariant(), ["note"] = forecast?.Note };
                return Finish(who, ViewsAuditVerbs.ActionForecast, kind, view.Name, view.Version, AuditEvent.Ok, Detail(traceId, ("link", link), ("will", forecast?.Will.ToString().ToLowerInvariant())), new ViewsAnswer(200, body));
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure or OperationCanceledException))
            {
                Record(who, ViewsAuditVerbs.ActionForecast, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("link", link), ("error", e.GetType().Name)));
                return ViewsAnswer.Error(500, "error", $"What will happen could not be worked out ({e.GetType().Name}).");
            }
        }
    }

    /// <summary>
    /// <b>Runs an action as the viewer</b> (<c>POST /api/v1/dashboards/{name}/actions/{link}</c>, plan 32 DBR53). The body is <c>{widget, row, parameters?, answers?, idempotency_key}</c>: the page names the link and gives the row it
    /// was clicked on, the parameters in force and what the person typed, **and nothing else**: the change or the task is derived from the published document, so a widget cannot make one request look like another, and
    /// a value the row does not give is refused. The answers are checked against the link's <c>ask</c>. An app's token must carry the write scope of the entity (<see cref="ViewsAppScope.Writes"/>). The key makes a double click one
    /// call (<see cref="IdempotentCalls{T}"/>). The answer is <c>{status, message?, request_id?, run_id?, violations?}</c>: 200 applied, 202 waiting for approval (the Store's change request) or started, 409 conflict (the row's version
    /// moved), 422 refused with every broken rule by field, 403 denied, 502 failed. Every call is audited (<c>view.action</c>: who, through what, the document, version and link, the outcome; never a value), and the Store's own audit row names
    /// the person and what they went through (<c>Ada Lovelace, through sales-portal 3, widget orders, link close-order</c>).
    /// </summary>
    public async Task<ViewsAnswer> RunActionAsync(ViewsPrincipal who, string kind, string name, string link, JsonObject? body, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Action, traceId);
            if (found.View is not { } view) return found.Refusal!;
            if (options.Actions is not { } actions) return NoActions(who, ViewsAuditVerbs.Action, kind, view, traceId);
            var (l, refusal) = ActionLink(who, ViewsAuditVerbs.Action, kind, view, link, traceId);
            if (l is null) return refusal!;

            string widget, key;
            JsonObject row, parameters;
            Dictionary<string, string?> answers = [];
            try
            {
                if (body is null) throw new ArgumentException("An action request has `widget`, `row`, `idempotency_key` and, where the link asks, `answers`.");
                foreach (var k in body.Select(p => p.Key))
                    if (k is not ("widget" or "row" or "parameters" or "answers" or "idempotency_key"))
                        throw new ArgumentException($"Unknown key `{k}`: an action request has `widget`, `row`, `parameters`, `answers` and `idempotency_key`.");
                widget = body["widget"] is JsonValue wv && wv.TryGetValue<string>(out var w) && WidgetIdPattern.IsMatch(w) ? w : throw new ArgumentException("`widget` is the id of the widget the click came from.");
                if (!view.Document.Widgets.Any(x => x.Id == widget)) throw new ArgumentException($"The document has no widget `{widget}`.");
                key = body["idempotency_key"] is JsonValue kv && kv.TryGetValue<string>(out var ks) && ks.Length is > 0 and <= MaximumKeyLength && ks.All(c => c > ' ' && c < 127)
                    ? ks : throw new ArgumentException($"`idempotency_key` is a text of 1 to {MaximumKeyLength} printable characters (the page makes one when the popup opens).");
                row = body["row"] is null ? [] : body["row"] as JsonObject ?? throw new ArgumentException("`row` is the clicked row as an object.");
                parameters = body["parameters"] is null ? [] : body["parameters"] as JsonObject ?? throw new ArgumentException("`parameters` is an object of values by parameter name.");
                if (body["answers"] is { } a)
                    foreach (var (an, av) in a as JsonObject ?? throw new ArgumentException("`answers` is an object of text by field name."))
                        answers[an] = av is null ? null : av is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"The answer `{an}` is text.");
            }
            catch (ArgumentException e)
            {
                return Refuse(who, ViewsAuditVerbs.Action, kind, view, 400, "invalid", e.Message, traceId, ("link", link));
            }

            if (Allowed(who, l.Action!) is { } why) return Refuse(who, ViewsAuditVerbs.Action, kind, view, 403, "action_not_allowed", why, traceId, ("link", link), ("widget", widget));
            var problems = ActionResolution.CheckAnswers(l.Action!.Ask.Values, answers);
            if (problems.Count > 0)
                return Refuse(who, ViewsAuditVerbs.Action, kind, view, 400, "invalid_answer", string.Join(" ", problems.Values), traceId, ("link", link), ("widget", widget), ("fields", problems.Keys.ToList()));
            var resolved = ActionResolution.Resolve(l, row, parameters, answers);
            if (resolved.Missing.Count > 0)
                return Refuse(who, ViewsAuditVerbs.Action, kind, view, 400, "missing_values", $"The row does not give {string.Join(", ", resolved.Missing)}, so the action cannot be sent.", traceId, ("link", link), ("widget", widget));

            var via = ViaOf(who, widget, link);
            var call = new ActionCall(who, resolved, via, key, view.Name, view.Version);
            ActionResult result;
            bool replayed;
            try
            {
                (result, replayed) = await Idempotency.RunAsync($"{who.Caller.Id}\n{kind}/{view.Name}\n{link}", key, () => actions.RunAsync(call, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure))
            {
                Record(who, ViewsAuditVerbs.Action, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("link", link), ("widget", widget), ("error", e.GetType().Name)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(502, "failed", $"The action could not be done ({e.GetType().Name}).");
            }

            var status = result.Status.ToString().ToLowerInvariant();
            var outcome = result.Status is ActionStatus.Applied or ActionStatus.Waiting or ActionStatus.Started ? AuditEvent.Ok : result.Status == ActionStatus.Failed ? AuditEvent.Failed : AuditEvent.Refused;
            var json = new JsonObject { ["status"] = status, ["message"] = result.Message, ["request_id"] = result.RequestId, ["run_id"] = result.RunId };
            if (result.Violations is { Count: > 0 } vs)
                json["violations"] = new JsonArray([.. vs.Select(v => (JsonNode)new JsonObject { ["rule"] = v.Rule, ["attribute"] = v.Attribute, ["message"] = v.Message })]);
            var http = result.Status switch
            {
                ActionStatus.Applied => 200, ActionStatus.Waiting or ActionStatus.Started => 202, ActionStatus.Conflict => 409, ActionStatus.Refused => 422, ActionStatus.Denied => 403, _ => 502,
            };
            if (outcome != AuditEvent.Ok) Interlocked.Increment(ref refused);
            // the audit names the link, the widget, what kind of action and what became of it: never a value, a key or a message that could hold one
            return Finish(who, ViewsAuditVerbs.Action, kind, view.Name, view.Version, outcome,
                Detail(traceId, ("link", link), ("widget", widget), ("action", $"{resolved.Kind}:{resolved.Operation}"), ("target", resolved.Target), ("status", status), ("replayed", replayed ? true : null), ("request_id", result.RequestId), ("run_id", result.RunId)),
                new ViewsAnswer(http, json));
        }
    }

    /// <summary>What an app's token adds for a <c>change</c>: its manifest must give the entity <c>write</c> or <c>change request</c> (null scope: no write at all). A person's own session is not narrowed here: the Store checks their role.</summary>
    private static string? Allowed(ViewsPrincipal who, ActionDefinition action)
    {
        if (who.App is not { } app || action.Kind != "change") return null;
        var entry = $"{action.Model}/{action.Entity}";
        return app.Writes is { } writes && writes.Contains(entry, StringComparer.OrdinalIgnoreCase) ? null
            : $"The app '{app.Instance}' may not change `{action.Model}.{action.Entity}`: its manifest does not give the entity `write` or `change request` (or the token carries no write scope).";
    }

    /// <summary>The words the Store's audit row carries: <c>sales-portal 3, widget orders, link close-order</c> (the app's name and version as its approval says them, the widget and the link).</summary>
    internal static string ViaOf(ViewsPrincipal who, string widget, string link) =>
        $"{(who.App is { } app ? (app.Label ?? app.Instance) + ", " : "")}widget {widget}, link {link}";

    private ViewsAnswer NoActions(ViewsPrincipal who, string verb, string kind, PublishedView view, string? traceId) =>
        Refuse(who, verb, kind, view, 501, "actions_unavailable", "This host has no store or runner to run an action: `ViewsOptions.Actions` is not set.", traceId);
}
