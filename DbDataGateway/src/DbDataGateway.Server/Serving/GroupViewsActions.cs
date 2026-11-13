using System.Text.Json.Nodes;
using DbDataKit.Views;
using DbDataKit.Views.Hosting;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// An action link's change made by the group's Store as the viewer (plan 32 DBR53): the views module derived the change from the published document and checked the row and the answers, and here it becomes one change of
/// <see cref="GroupStoreApi.ChangesAsync"/> as <b>the person</b> (the roles the identity home gives them on this group, never the Gateway's own), with the row's version, so it applies at once, waits as a change request (202) when
/// the model asks for approval, or is refused in the Store's words (409 the version moved, 422 every broken rule by field). The audit of the Store names the person and what they went through
/// (<c>Ada Lovelace, through sales-portal 3, widget orders, link close-order</c>). A <c>task</c> runs through <paramref name="runTask"/> (the runner's <c>RunNow</c> under the viewer's role); a Gateway with no runner says so.
/// </summary>
public sealed class GroupViewsActions(GroupStoreApi store, HomeCallers callers, Func<ActionCall, CancellationToken, Task<ActionResult>>? runTask = null) : IViewsActions
{
    private async Task<StoreCaller?> CallerAsync(ViewsPrincipal who, string? via, CancellationToken ct) =>
        (await callers.GetAsync(ct).ConfigureAwait(false)).CallerFor(who.Caller.Id) is { } c ? c with { Via = via } : null;

    public async Task<ActionForecast?> ForecastAsync(ViewsPrincipal who, ActionDefinition action, CancellationToken cancellationToken)
    {
        if (action.Kind == "task") return new ActionForecast(ActionWill.Run, runTask is null ? "This host has no runner connected: the task cannot run." : null);
        if (await CallerAsync(who, null, cancellationToken).ConfigureAwait(false) is not { } caller) return null;
        if (store.Forecast(caller, action.Model!) is not { } forecast) return null;
        return forecast.Waits
            ? new ActionForecast(ActionWill.Wait, $"This will wait for approval by {forecast.Who}: it is kept as a change request and applied when it is approved.")
            : new ActionForecast(ActionWill.Apply);
    }

    public async Task<ActionResult> RunAsync(ActionCall call, CancellationToken cancellationToken)
    {
        var a = call.Action;
        if (a.Kind == "task")
            return runTask is null ? new ActionResult(ActionStatus.Failed, "This Gateway is not connected to a runner, so it cannot run the task.") : await runTask(call, cancellationToken).ConfigureAwait(false);
        if (await CallerAsync(call.Who, call.Via, cancellationToken).ConfigureAwait(false) is not { } caller)
            return new ActionResult(ActionStatus.Denied, "Your sign-in is not a person of this group's identity home, so the Store has no role to give you.");

        var (keyAttributes, problem) = store.KeyOf(a.Model!, a.Entity!);
        if (problem is not null) return Map(problem);
        var change = new JsonObject { ["action"] = a.Operation, ["entity"] = a.Entity };
        if (a.Operation != "insert")
        {
            var key = new JsonObject();
            if (a.TypedKey.Count == 1 && a.TypedKey.ContainsKey("key"))
            {
                if (keyAttributes!.Count != 1) return new ActionResult(ActionStatus.Failed, $"`{a.Entity}` has a key of {keyAttributes.Count} fields ({string.Join(", ", keyAttributes)}): name them under `key` in the link.");
                key[keyAttributes[0]] = a.TypedKey["key"]?.DeepClone();
            }
            else foreach (var (name, value) in a.TypedKey) key[name] = value?.DeepClone();
            if (keyAttributes!.Any(k => key[k] is null)) return new ActionResult(ActionStatus.Failed, $"The key of `{a.Entity}` is {string.Join(", ", keyAttributes!)}, and the link does not give all of it.");
            change["key"] = key;
            var version = a.TypedVersion;
            if (version is null)
            {
                // no version in the link: the change is made over the version at this moment (the Store still refuses a change that races it)
                var query = key.ToDictionary(p => $"key.{p.Key}", p => p.Value?.ToString() ?? "");
                var read = await store.RowAsync(caller, a.Model!, a.Entity!, query, cancellationToken, forWrite: true).ConfigureAwait(false);
                if (read.Status != 200) return Map(read);
                version = read.Body["version"]?.GetValue<long>();
            }
            change["expected_version"] = version;
        }
        if (a.Operation != "delete")
        {
            var values = new JsonObject();
            foreach (var (name, value) in a.TypedWrites) values[name] = value?.DeepClone();
            change["values"] = values;
        }
        var body = new JsonObject { ["changes"] = new JsonArray(change), ["description"] = $"{a.Title} ({call.Via})" };
        var answer = await store.ChangesAsync(caller, a.Model!, System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), null, cancellationToken).ConfigureAwait(false);
        return Map(answer);
    }

    public async Task<ActionForecast?> ForecastBatchAsync(ViewsPrincipal who, string model, CancellationToken cancellationToken)
    {
        if (await CallerAsync(who, null, cancellationToken).ConfigureAwait(false) is not { } caller) return null;
        if (store.Forecast(caller, model) is not { } forecast) return null;
        return forecast.Waits
            ? new ActionForecast(ActionWill.Wait, $"This will wait for approval by {forecast.Who} as one request: nothing changes until it is approved, and then all of it applies together.")
            : new ActionForecast(ActionWill.Apply);
    }

    /// <summary>
    /// A staged set as **one** <see cref="GroupStoreApi.ChangesAsync"/> call as the person: the Store's <c>ModelWriter</c> applies the whole array in one transaction (every broken rule reported at once, its cross-row rules once
    /// per batch), or <c>GroupChangeRequests</c> keeps it as one request holding all of it. A broken rule or a moved version is matched back to the change it is about by entity and key, so the shell shows it against its lines.
    /// </summary>
    public async Task<BatchResult> RunBatchAsync(BatchCall call, CancellationToken cancellationToken)
    {
        if (await CallerAsync(call.Who, call.Via, cancellationToken).ConfigureAwait(false) is not { } caller)
            return new BatchResult(ActionStatus.Denied, 0, "Your sign-in is not a person of this group's identity home, so the Store has no role to give you.");
        var changes = new JsonArray();
        var keys = new List<(string Entity, string Key)>();
        foreach (var (a, _) in call.Changes.Select(c => (c.Action, c.Lines)))
        {
            var (keyAttributes, problem) = store.KeyOf(a.Model!, a.Entity!);
            if (problem is not null) return Failed(Map(problem).Message ?? "The model is not published here.");
            var change = new JsonObject { ["action"] = a.Operation, ["entity"] = a.Entity };
            JsonObject? key = null;
            if (a.Operation != "insert")
            {
                key = new JsonObject();
                if (a.TypedKey.Count == 1 && a.TypedKey.ContainsKey("key"))
                {
                    if (keyAttributes!.Count != 1) return Failed($"`{a.Entity}` has a key of {keyAttributes.Count} fields ({string.Join(", ", keyAttributes)}): name them under `key` in the link.");
                    key[keyAttributes[0]] = a.TypedKey["key"]?.DeepClone();
                }
                else foreach (var (name, value) in a.TypedKey) key[name] = value?.DeepClone();
                if (keyAttributes!.Any(k => key[k] is null)) return Failed($"The key of `{a.Entity}` is {string.Join(", ", keyAttributes!)}, and a link does not give all of it.");
                change["key"] = key;
                var version = a.TypedVersion;
                if (version is null)
                {
                    var read = await store.RowAsync(caller, a.Model!, a.Entity!, key.ToDictionary(p => $"key.{p.Key}", p => p.Value?.ToString() ?? ""), cancellationToken, forWrite: true).ConfigureAwait(false);
                    if (read.Status != 200) { var r = Map(read); return new BatchResult(r.Status, 0, r.Message, Problems: r.Violations?.Select(v => new BatchProblem(null, v.Rule, v.Attribute, v.Message)).ToList()); }
                    version = read.Body["version"]?.GetValue<long>();
                }
                change["expected_version"] = version;
            }
            if (a.Operation != "delete")
            {
                var values = new JsonObject();
                foreach (var (name, value) in a.TypedWrites) values[name] = value?.DeepClone();
                change["values"] = values;
            }
            changes.Add(change);
            keys.Add((a.Entity!, key is null ? "" : KeyText(key)));
        }
        var body = new JsonObject { ["changes"] = changes, ["description"] = $"{call.LineCount} staged change{(call.LineCount == 1 ? "" : "s")} ({call.Via})" };
        var answer = await store.ChangesAsync(caller, call.Model, System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), null, cancellationToken).ConfigureAwait(false);
        var mapped = Map(answer);
        var problems = new List<BatchProblem>();
        foreach (var v in mapped.Violations ?? [])
        {
            // the Store names the entity and key of each broken rule: that is the change it is about
            var violation = (answer.Body["violations"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => (string?)o["message"] == v.Message && (string?)o["rule"] == v.Rule && (string?)o["attribute"] == v.Attribute);
            var index = violation is null ? -1 : keys.FindIndex(k => k.Entity == (string?)violation["entity"] && k.Key == (violation["key"] is JsonObject vk ? KeyText(vk) : ""));
            problems.Add(new BatchProblem(index < 0 ? null : index, v.Rule, v.Attribute, v.Message));
        }
        return new BatchResult(mapped.Status, mapped.Status == ActionStatus.Applied || mapped.Status == ActionStatus.Waiting ? call.Changes.Count : 0, mapped.Message, mapped.RequestId, problems.Count > 0 ? problems : null);
    }

    private static BatchResult Failed(string message) => new(ActionStatus.Failed, 0, message);

    private static string KeyText(JsonObject key) => string.Join("&", key.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value?.ToString()}"));

    /// <summary>The Store's answer as the module's: 200 applied, 202 a change request, 409 a moved version, 422 the broken rules, 403 denied, anything else failed.</summary>
    internal static ActionResult Map(StoreAnswer answer)
    {
        var message = (string?)answer.Body["error"];
        switch (answer.Status)
        {
            case 200: return ActionResult.Applied();
            case 202: return new ActionResult(ActionStatus.Waiting, RequestId: (string?)answer.Body["id"]);
            case 409: return new ActionResult(ActionStatus.Conflict, $"The row was changed by someone else{(answer.Body["current_version"] is { } v ? $" (it is now version {v})" : "")}: reload and try again.");
            case 422 or 404:
                var violations = (answer.Body["violations"] as JsonArray)?.OfType<JsonObject>().Select(v => new ActionViolation((string?)v["rule"], (string?)v["attribute"], (string?)v["message"] ?? "")).ToList();
                return new ActionResult(ActionStatus.Refused, message, Violations: violations);
            case 403: return new ActionResult(ActionStatus.Denied, message);
            default: return new ActionResult(ActionStatus.Failed, message ?? $"The store answered {answer.Status}.");
        }
    }
}
