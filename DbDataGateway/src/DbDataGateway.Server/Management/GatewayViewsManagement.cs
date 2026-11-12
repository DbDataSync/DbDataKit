using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Views;
using DbDataKit.Views.Hosting;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The views module on the management API (plan 32, DBR11b): what a console relays (<c>/relay/&lt;gateway&gt;/api/v1/dashboards...</c>) and what the command line publishes through. The routes and their bodies are the module's own
/// (<c>DbDataKit.Views.Hosting</c>), so the console's <c>moduleClient</c> is written once for the module's listener and for this:
/// <code>
/// GET  /api/v1/views/status                       {enabled, state, detail, address, dashboards, reports}  (viewer; says "enabled": false when the module is off)
/// GET  /api/v1/dashboards | /reports              the list the caller may open
/// GET  /api/v1/dashboards/{name} | /reports/{name} the document as published
/// POST /api/v1/dashboards/{name}/render           the rendered widgets, as the person the console vouches for
/// GET  /api/v1/dashboards/{name}/export?format=&amp;layout=&amp;widget=&amp;p.name=   a download as the person, as JSON <c>{file_name, content_type, size, content_base64, headers}</c> (the relay carries JSON only; DBR27's print preview); the module's own refusals, 501 <c>export_unavailable</c> when the host has no browser for a PDF
/// POST /api/v1/dashboards/{name}/actions/{link}[/forecast]   runs (or forecasts) an action link's change or task as that person (DBR53; viewer to call, the Store's own roles decide the write)
/// POST /api/v1/dashboards/{name}/batches[/forecast]   submits (or forecasts) a staged set of changes as one batch, as that person (DBR53b)
/// POST /api/v1/views/preview                      the live preview of an unsaved draft (DBR28), as that person, stored nowhere (viewer; the module's own checks refuse what a publish would refuse)
/// PUT  /api/v1/dashboards/{name} | /reports/{name} {text, expected_version?, note?} publishes it to the group (author, publisher or administrator)
/// GET  /api/v1/dashboards/{name}/versions | /reports/{name}/versions   {name, kind, active, previous, versions: [{version, published_by, published_at, note, active, depends_on}]} (the same roles as publishing; no document text)
/// POST /api/v1/dashboards/{name}/switch | /reports/{name}/switch       {version, expected_version?} serves an earlier (or any stored) version, with the model versions it was published against (the same roles)
/// POST /api/v1/dashboards/{name}/rollback | /reports/{name}/rollback   {expected_version?} returns to the version before the current one (the same roles)
/// DELETE /api/v1/dashboards/{name} | /reports/{name}?expected_version=n  retires it: it is no longer served, its versions stay and a publish brings it back (the same roles)
/// </code>
/// Reading is <b>as the person</b>: a console's token names them, and a person who signs in through the identity home is that home user, so their groups decide which data sources every widget may read. Publishing needs no module: the document is checked and
/// stored for every instance that runs the module.
/// </summary>
public sealed class GatewayViewsManagement(GatewayViewsPublishing publishing, GatewayViews? views, IAuditLog? audit = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public const string Prefix = GatewayManagement.Prefix;

    public static bool Handles(string route) =>
        route.StartsWith(Prefix + "/my/", StringComparison.Ordinal) || route is Prefix + "/dashboards" or Prefix + "/reports" or Prefix + "/views/status" or Prefix + "/views/preview"
        || route.StartsWith(Prefix + "/dashboards/", StringComparison.Ordinal) || route.StartsWith(Prefix + "/reports/", StringComparison.Ordinal);

    private static ManagementResult Problem(int status, string code, string message) =>
        new(status, new JsonObject { ["error"] = message, ["code"] = code, ["message"] = message });

    public async Task<ManagementResult> HandleAsync(string method, string route, string? query, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (route == Prefix + "/views/status")
            return method == "GET" ? (caller.Holds(ServerRole.Viewer) ? Status() : Problem(403, "denied", "This needs the viewer role.")) : Problem(405, "invalid", "The status is read with GET.");

        if (route == Prefix + "/views/preview") return await PreviewAsync(method, body, caller, cancellationToken);

        // a person's own dashboards and reports (DBR12): the module's own routes under /my/, as the person the console vouches for
        if (route.StartsWith(Prefix + "/my/", StringComparison.Ordinal)) return await GatewayUserViews.HandleAsync(views, method, route, query, body, caller, cancellationToken);

        var parts = route[(Prefix.Length + 1)..].Split('/');
        var kind = parts[0] == "reports" ? ViewDefinitions.Report : ViewDefinitions.Dashboard;
        var name = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        var action = parts.Length > 2 ? parts[2] : null;
        // an action link of a document (plan 32 DBR53): .../{name}/actions/{link}[/forecast]
        var isAction = action == "actions" && parts.Length is 4 or 5 && (parts.Length == 4 || parts[4] == "forecast");
        // a staged set as one batch (DBR53b): .../{name}/batches[/forecast]
        var isBatch = action == "batches" && parts.Length is 3 or 4 && (parts.Length == 3 || parts[3] == "forecast");
        if (!isAction && !isBatch && (parts.Length > 3 || (action is not null && action is not ("render" or "export" or "versions" or "switch" or "rollback")))) return Problem(404, "not_found", "There is no such call.");

        if (name is not null && action is null && method == "PUT") return Publish(kind, name, body, caller);
        // the lifecycle of a published document is the author's, as publishing is
        if (name is not null && ((action is null && method == "DELETE") || (action == "versions" && method == "GET") || (action is "switch" or "rollback" && method == "POST")))
        {
            if (!CanPublish(caller)) return Problem(403, "denied", "Changing which version of a document is served needs the author, publisher or administrator role.");
            return action switch
            {
                "versions" => Versions(kind, name),
                "switch" => Switch(kind, name, body, caller),
                "rollback" => Rollback(kind, name, body, caller),
                _ => Retire(kind, name, query, caller),
            };
        }

        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "denied", "This needs the viewer role.");
        if (views is null) return Problem(404, "not_found", "This gateway does not serve the views module: list `views` under `modules:` in server.yaml.");
        if (caller.Token is not { } token) return Problem(403, "denied", "Dashboards and reports are read as a person: sign in through a console, or call the module's own address with a token of the identity home.");
        var who = await views.ConsolePrincipalAsync(token, cancellationToken);
        var api = views.Module.Api;
        if (api is null) return Problem(503, "unavailable", "The views module has not started.");
        var trace = System.Diagnostics.Activity.Current?.TraceId.ToString();
        ViewsAnswer answer;
        if (isBatch)
        {
            if (method != "POST") return Problem(405, "invalid", "A batch is sent with POST.");
            JsonObject? sent;
            try { sent = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject ?? throw new JsonException("The body must be a JSON object."); }
            catch (JsonException ex) { return Problem(400, "invalid", $"The body is not JSON: {ex.Message}"); }
            var batch = parts.Length == 4 ? await api.ForecastBatchAsync(who, kind, name!, sent, trace, cancellationToken) : await api.RunBatchAsync(who, kind, name!, sent, trace, cancellationToken);
            return new ManagementResult(batch.Status, batch.Body);
        }
        if (isAction)
        {
            if (method != "POST") return Problem(405, "invalid", "An action is sent with POST.");
            var link = Uri.UnescapeDataString(parts[3]);
            if (parts.Length == 5) { var forecast = await api.ForecastActionAsync(who, kind, name!, link, trace, cancellationToken); return new ManagementResult(forecast.Status, forecast.Body); }
            JsonObject? sent;
            try { sent = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject ?? throw new JsonException("The body must be a JSON object."); }
            catch (JsonException ex) { return Problem(400, "invalid", $"The body is not JSON: {ex.Message}"); }
            var done = await api.RunActionAsync(who, kind, name!, link, sent, trace, cancellationToken);
            return new ManagementResult(done.Status, done.Body);
        }
        switch (method, name, action)
        {
            case ("GET", null, null): answer = api.List(who, kind, trace); break;
            case ("GET", not null, null): answer = api.Open(who, kind, name, trace); break;
            case ("GET", not null, "export"):
                return await ExportAsync(api, who, kind, name, query, trace, cancellationToken);
            case ("POST", not null, "render"):
                JsonObject? json;
                try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject ?? throw new JsonException("The body must be a JSON object."); }
                catch (JsonException ex) { return Problem(400, "invalid", $"The body is not JSON: {ex.Message}"); }
                answer = await api.RenderAsync(who, kind, name, json, null, trace, cancellationToken);
                break;
            default: return Problem(405, "invalid", "That method is not allowed here.");
        }
        return new ManagementResult(answer.Status, answer.Body);
    }

    /// <summary>The most a download may be to travel as JSON (base64 makes it a third larger): a console's print preview is a few pages, and a bigger file is taken from the module's own listener.</summary>
    public const int MaxRelayedDownloadBytes = 24 * 1024 * 1024;

    /// <summary>
    /// A download on the management API (plan 32 DBR27's print preview): the module's own <see cref="ViewsApi.ExportAsync"/>, in process, as the person, with the same query (<c>format</c>, <c>layout</c>, <c>widget</c>, <c>p.name</c>), the same refusals and the same audit row.
    /// The relay carries JSON only, so a file comes back as <c>{file_name, content_type, size, content_base64, headers}</c> (<c>headers</c> are the module's <c>X-DbData-*</c> ones: the page count, the browser); a refusal (<c>export_unavailable</c> naming the
    /// <c>views.export</c> setting, a widget that failed, a limit) is the module's JSON as it is.
    /// </summary>
    private static async Task<ManagementResult> ExportAsync(ViewsApi api, ViewsPrincipal who, string kind, string name, string? query, string? trace, CancellationToken cancellationToken)
    {
        var q = query is null ? new System.Collections.Specialized.NameValueCollection() : System.Web.HttpUtility.ParseQueryString(query);
        var parameters = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var key in q.AllKeys.OfType<string>())
        {
            if (!key.StartsWith("p.", StringComparison.Ordinal) || key.Length == 2) continue;
            var values = q.GetValues(key) ?? [];
            parameters[key[2..]] = values.Length == 1 ? JsonValue.Create(values[0]) : new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        static string? One(System.Collections.Specialized.NameValueCollection collection, string key) => collection[key] is { Length: > 0 } v ? v : null;
        var answer = await api.ExportAsync(who, kind, name, One(q, "format"), One(q, "layout"), One(q, "widget"), parameters, null, trace, cancellationToken);
        if (answer.File is not { } file) return new ManagementResult(answer.Status, answer.Body);
        if (file.Content.Length > MaxRelayedDownloadBytes)
            return Problem(413, "too_large", $"The file is {file.Content.Length} bytes and the management API carries at most {MaxRelayedDownloadBytes} as JSON: take it from the views module's own listener (GET /api/v1/{ViewsApi.Collection(kind)}/{name}/export).");
        var headers = new JsonObject();
        foreach (var (key, value) in answer.Headers ?? new Dictionary<string, string>())
            if (key.StartsWith("X-DbData-", StringComparison.Ordinal)) headers[key] = value;
        return new ManagementResult(200, new JsonObject
        {
            ["file_name"] = file.FileName, ["content_type"] = file.ContentType, ["size"] = file.Content.Length, ["content_base64"] = Convert.ToBase64String(file.Content), ["headers"] = headers,
        });
    }

    /// <summary>The module's own <see cref="ViewsApi.PreviewAsync"/>, in process, as the person the console's token names (DBR28): the same roles and refusals as a render, the same 413 over the module's limit for a draft.</summary>
    private async Task<ManagementResult> PreviewAsync(string method, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (method != "POST") return Problem(405, "invalid", "A preview is asked with POST.");
        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "denied", "This needs the viewer role.");
        if (views is null) return Problem(404, "not_found", "This gateway does not serve the views module: list `views` under `modules:` in server.yaml.");
        if (caller.Token is not { } token) return Problem(403, "denied", "A preview is made as a person: sign in through a console, or call the module's own address with a token of the identity home.");
        var limit = new ViewsOptions().MaxPreviewBytes;
        if (body is not null && System.Text.Encoding.UTF8.GetByteCount(body) > limit) return Problem(413, "too_large", $"A preview request is at most {limit} bytes.");
        JsonObject? json;
        try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject ?? throw new JsonException("The body must be a JSON object."); }
        catch (JsonException ex) { return Problem(400, "invalid", $"The body is not JSON: {ex.Message}"); }
        var who = await views.ConsolePrincipalAsync(token, cancellationToken);
        var api = views.Module.Api;
        if (api is null) return Problem(503, "unavailable", "The views module has not started.");
        var answer = await api.PreviewAsync(who, json, null, System.Diagnostics.Activity.Current?.TraceId.ToString(), cancellationToken);
        return new ManagementResult(answer.Status, answer.Body);
    }

    private ManagementResult Publish(string kind, string name, string? body, ManagementCaller caller)
    {
        // a document is an author's; the publisher role of a server client (a Build server) and an administrator publish too
        if (!CanPublish(caller)) return Problem(403, "denied", "Publishing a document needs the author, publisher or administrator role.");
        string? text; int? expected = null; string? note = null;
        try
        {
            var json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject;
            text = json?["text"]?.GetValue<string>();
            note = json?["note"]?.GetValue<string>();
            if (json?["expected_version"] is { } e)
                expected = e.GetValueKind() == JsonValueKind.String ? int.Parse(e.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : e.GetValue<int>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Problem(400, "invalid", "The body is {\"text\": \"<the document's yaml or json>\", \"expected_version\": <the version read, or absent for a new document>, \"note\": \"<why>\"}.");
        }
        var outcome = publishing.Publish(kind, name, text ?? "", Who(caller), expected, note);
        Record(caller, $"{kind} publish", name, outcome.Status == 200 ? $"version {(string?)outcome.Body["version"]}{(outcome.Body["unchanged"]?.GetValue<bool>() == true ? " (unchanged)" : "")}" : "refused");
        return new ManagementResult(outcome.Status, outcome.Body);
    }

    private static bool CanPublish(ManagementCaller caller) => caller.Holds(ServerRole.Author) || caller.Holds(ServerRole.Publisher) || caller.Holds(ServerRole.Admin);

    private string Who(ManagementCaller caller) { var entry = caller.Audit("", null, "", _clock.GetUtcNow()); return entry.UserName ?? entry.UserId; }

    private void Record(ManagementCaller caller, string command, string target, string outcome) => audit?.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));

    private static int? ExpectedOf(string? text) => int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private ManagementResult Versions(string kind, string name)
    {
        var store = publishing.Store;
        var versions = store.Versions(kind, name);
        if (versions.Count == 0) return Problem(404, "not_found", $"There is no {kind} '{name}'.");
        var head = publishing.Publisher.Head(kind, name);
        var active = head?.Active is > 0 ? head.Active : null;
        return ManagementResult.Ok(new JsonObject
        {
            ["kind"] = kind, ["name"] = name, ["active"] = active, ["previous"] = head?.Previous is > 0 ? head.Previous : null,
            ["versions"] = new JsonArray([.. versions.OrderByDescending(v => v.Version).Select(v => (JsonNode)new JsonObject
            {
                ["version"] = v.Version, ["published_by"] = v.PublishedBy, ["published_at"] = v.PublishedAt, ["note"] = v.Note, ["active"] = v.Version == active,
                ["depends_on"] = new JsonArray([.. v.DependsOn.Select(d => (JsonNode)d)]),
            })]),
        });
    }

    private ManagementResult Conflict(string kind, string name, int? active, string message) =>
        new(409, new JsonObject { ["error"] = message, ["code"] = "version_conflict", ["message"] = message, ["current_version"] = active });

    /// <summary>Runs a lifecycle change on the registry and answers in the registry's own words; the caller's version (what they saw served) is checked first.</summary>
    private ManagementResult Lifecycle(string kind, string name, int? expected, ManagementCaller caller, string command, Func<string, object> change)
    {
        var activeNow = publishing.Publisher.Head(kind, name)?.Active;
        int? current = activeNow is > 0 ? activeNow : null;
        if (expected is { } e && e != (current ?? 0))
        {
            Record(caller, command, name, "stale");
            return Conflict(kind, name, current, $"The {kind} '{name}' has version {current ?? 0} served, not {e}: it changed since you looked. Read it again and decide.");
        }
        try
        {
            var done = change(Who(caller));
            return ManagementResult.Ok(JsonSerializer.SerializeToNode(done, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!);
        }
        catch (DbDataKit.State.DefinitionNotFoundException ex) { Record(caller, command, name, "refused"); return Problem(404, "not_found", ex.Message); }
        catch (DbDataKit.State.DefinitionConflictException ex) { Record(caller, command, name, "stale"); return Conflict(kind, name, null, ex.Message); }
        catch (DbDataKit.State.DefinitionRejectedException ex) { Record(caller, command, name, "refused"); return Problem(409, "refused", ex.Message); }
    }

    private ManagementResult Switch(string kind, string name, string? body, ManagementCaller caller)
    {
        int? version = null, expected = null;
        try
        {
            var json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject;
            version = json?["version"]?.GetValue<int>();
            expected = json?["expected_version"]?.GetValue<int>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { version = null; }
        if (version is not > 0) return Problem(400, "invalid", "The body is {\"version\": <a stored version number>, \"expected_version\": <the version served when you looked, optional>}.");
        var wanted = version.Value;
        return Lifecycle(kind, name, expected, caller, $"{kind} switch", by =>
        {
            var moved = publishing.Publisher.PromoteWithDependencies(kind, name, wanted, by);
            Record(caller, $"{kind} switch", name, $"version {wanted}");
            return new { kind, name, version = wanted, moved = moved.Select(m => $"{m.Kind}:{m.Name}@{m.Version}").ToArray() };
        });
    }

    private ManagementResult Rollback(string kind, string name, string? body, ManagementCaller caller)
    {
        int? expected = null;
        try { expected = (string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject)?["expected_version"]?.GetValue<int>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return Problem(400, "invalid", "The body is {\"expected_version\": <the version served when you looked, optional>}."); }
        return Lifecycle(kind, name, expected, caller, $"{kind} rollback", by =>
        {
            var moved = publishing.Publisher.RollbackWithDependencies(kind, name, by);
            Record(caller, $"{kind} rollback", name, $"version {moved[0].Version}");
            return new { kind, name, version = moved[0].Version, moved = moved.Select(m => $"{m.Kind}:{m.Name}@{m.Version}").ToArray() };
        });
    }

    private ManagementResult Retire(string kind, string name, string? query, ManagementCaller caller)
    {
        var expected = ExpectedOf(System.Web.HttpUtility.ParseQueryString(query ?? "")["expected_version"]);
        return Lifecycle(kind, name, expected, caller, $"{kind} retire", by =>
        {
            if (!publishing.Publisher.Retire(kind, name, by)) throw new DbDataKit.State.DefinitionNotFoundException($"The {kind} '{name}' is not served.");
            Record(caller, $"{kind} retire", name, "retired");
            return new { kind, name, retired = true };
        });
    }

    private ManagementResult Status()
    {
        var found = new JsonObject { ["enabled"] = views is not null };
        found["dashboards"] = publishing.List(ViewDefinitions.Dashboard).Count(h => h.Active is > 0);
        found["reports"] = publishing.List(ViewDefinitions.Report).Count(h => h.Active is > 0);
        if (views is not null)
        {
            var health = views.Module.Health();
            found["state"] = health.State.ToString().ToLowerInvariant();
            found["detail"] = health.Detail;
            found["address"] = views.Module.Api is null ? null : views.Address;
        }
        return ManagementResult.Ok(found);
    }
}
