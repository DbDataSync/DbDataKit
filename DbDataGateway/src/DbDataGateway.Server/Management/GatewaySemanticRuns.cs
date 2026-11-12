using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Views;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The record of a person's statements on the management API (plan 14a SL23/SL24, D21): <c>GET /api/v1/my/runs</c> (the caller's own, newest first; <c>?model=</c>, <c>?limit=</c>; an administrator may ask <c>?all=true</c> for
/// everyone's), <c>GET /api/v1/my/runs/{id}</c> (one, with the request document, warnings and plan), <c>GET /api/v1/my/runs/counts</c> (the statements that ended with each error code in the last <c>?days=</c>, 7 by default) and
/// <c>POST /api/v1/my/runs/{id}/replay</c> (the record's request is run again, as the caller, on the model version the record names; the body may carry <c>keep</c> and <c>view</c> as a run does, D38: the view is drawn and never stored in the record). A record of another person is not found, whoever asks without being an administrator. A replay
/// on a version that is gone (pruned, or the model unpublished) is answered 410 and says which version. A replay writes a record of its own that names the one it re-ran. The viewer role is enough; the caller is the person the
/// console's token names, so the views module must be on.
/// </summary>
public sealed class GatewaySemanticRuns(SemanticRunStore store, SemanticModels models, PublishedSemanticModels published, IServingServices serving, IServingServices semantic, GatewayViews? views, IAuditLog audit, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public const string Route = GatewayManagement.Prefix + "/my/runs";

    public static bool Handles(string route) => route == Route || route.StartsWith(Route + "/", StringComparison.Ordinal);

    private static ManagementResult Problem(int status, string message) => ManagementResult.Problem(status, message);

    public async Task<ManagementResult> HandleAsync(string method, string route, string? query, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "This needs the viewer role.");
        if (views is null) return Problem(404, "This gateway does not serve the views module, which tells who a person is: list `views` under `modules:` in server.yaml.");
        if (caller.Token is not { } token) return Problem(403, "Run records are a person's: sign in through a console.");
        var who = (await views.ConsolePrincipalAsync(token, cancellationToken)).Caller;
        var admin = caller.Holds(ServerRole.Admin);
        var q = query is null ? new System.Collections.Specialized.NameValueCollection() : System.Web.HttpUtility.ParseQueryString(query);
        var wantsAll = string.Equals(q["all"], "true", StringComparison.OrdinalIgnoreCase);
        if (wantsAll && !admin) return Problem(403, "Everyone's runs are an administrator's to read.");
        var parts = route.Length > Route.Length ? route[(Route.Length + 1)..].Split('/') : [];

        if (parts.Length == 0 && method == "POST") return await RunAsync(body, who, cancellationToken);
        if (parts.Length == 0) return method == "GET" ? List(who, wantsAll, q["model"], int.TryParse(q["limit"], out var limit) ? limit : 100) : Problem(405, "The list is read with GET.");
        if (parts is ["counts"]) return method == "GET" ? Counts(who, wantsAll, int.TryParse(q["days"], out var days) ? Math.Clamp(days, 1, 365) : 7) : Problem(405, "The counts are read with GET.");
        if (parts is [var kept, "render"] && !long.TryParse(kept, out _)) return method == "POST" ? Render(kept, body, who) : Problem(405, "A view is rendered with POST.");
        if (!long.TryParse(parts[0], out var id) || parts.Length > 2) return Problem(404, "There is no such call.");
        var record = store.Get(id, SemanticCallers.KeyOf(who), admin);
        if (record is null) return Problem(404, $"There is no run {id} of yours.");
        if (parts.Length == 1) return method == "GET" ? ManagementResult.Ok(Detail(record)) : Problem(405, "A run is read with GET.");
        if (parts[1] != "replay") return Problem(404, "There is no such call.");
        return method == "POST" ? await ReplayAsync(record, body, who, caller, cancellationToken) : Problem(405, "A replay is started with POST.");
    }

    private sealed record RunBody(string? Model, string? Sql, JsonObject? Request, JsonObject? Parameters, bool? Explain, int? Limit, bool? Keep, JsonObject? View);

    /// <summary>
    /// <c>POST /api/v1/my/runs</c> <c>{model, sql | request, parameters?, explain?, limit?}</c>: the explorer's call (plan 14a SL14, SL15, D36). The statement runs as the caller through the semantic module, so it is checked, limited and recorded like one from any other
    /// protocol, and the answer is <see cref="SemanticRest"/>'s JSON (<c>status</c>, <c>columns</c>, <c>rows</c>, <c>warnings</c>, <c>request_hash</c>, <c>remaining</c>). A refusal is a 400 that carries the engine's own <c>code</c>.
    /// </summary>
    private async Task<ManagementResult> RunAsync(string? body, ServingCaller who, CancellationToken cancellationToken)
    {
        RunBody? run;
        try { run = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<RunBody>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { run = null; }
        if (run is null || string.IsNullOrWhiteSpace(run.Model) || (run.Sql is null) == (run.Request is null))
            return Problem(400, "The body is {\"model\": \"<name>\", \"sql\": \"<semantic sql>\"} or {\"model\": ..., \"request\": {...}} (one of them), with optional \"parameters\", \"explain\" and \"limit\".");
        if (models.Find(run.Model) is not { } model) return Problem(404, $"There is no model '{run.Model}'.");
        var limit = Math.Clamp(run.Limit ?? 1000, 1, SemanticRest.MaxRows);
        var context = new ServingContext(who, ClientAddress: null);
        var source = SemanticModels.SourcePrefix + model.Name;
        try
        {
            IServingAnswer answer;
            if (run.Sql is not null)
                answer = await semantic.Queries.RunAsync(context, new ServingRequest.SemanticSql(source, (run.Explain == true ? "EXPLAIN " : "") + WithLimit(run.Sql, limit), new ServingLimits(MaxRows: limit), Bind(run.Parameters)), cancellationToken);
            else
            {
                var request = run.Request!.DeepClone().AsObject();
                if (request["query"] is JsonObject inner && inner["limit"] is null) inner["limit"] = limit;
                if (run.Explain == true && request["kind"]?.GetValue<string>() == "query") request["kind"] = "explain";
                if (run.Parameters is { Count: > 0 } && request["parameters"] is null) request["parameters"] = run.Parameters.DeepClone();
                answer = await semantic.Queries.RunAsync(context, new ServingRequest.Semantic(source, request.ToJsonString(), new ServingLimits(MaxRows: limit)), cancellationToken);
            }
            await using var _ = answer;
            var result = await ShapeAsync(answer, limit, (run.View is not null || run.Keep == true) && run.Explain != true, run.View, who, cancellationToken);
            result["model_version"] = model.Version;
            return ManagementResult.Ok(result);
        }
        catch (ViewsAnswerException ex) { return new ManagementResult(400, new JsonObject { ["error"] = ex.Message, ["code"] = ex.Code }); }
        catch (ServingException ex)
        {
            var status = ex switch { ServingNotFoundException => 404, ServingDeniedException or ServingUnauthenticatedException => 403, ServingBackendException => 502, _ => 400 };
            return new ManagementResult(status, new JsonObject { ["error"] = ex.Message, ["code"] = ex is ServingLimitException l ? l.Limit : SemanticRest.CodeOf(ex.Message) });
        }
    }

    /// <summary>
    /// The one place an answer becomes the JSON the explorer reads, for a run and for a replay (plan 14a D37, D38). Without <paramref name="draw"/> (no <c>keep</c>, no <c>view</c>) it is the REST answer. A complete answer that will be drawn is read once, the way a
    /// dashboard widget's answer is, kept for a while (<c>result_id</c>) and drawn as the view says (<c>rendered_view</c>). An answer with a column the views cannot read (a time of day, a binary, a list...) is not failed: it is answered through the REST path
    /// as a table, nothing kept and nothing drawn, with a <c>notices</c> entry that names the column and its type, so the person sees the table and why there is no matrix or chart.
    /// </summary>
    internal async Task<JsonObject> ShapeAsync(IServingAnswer answer, int limit, bool draw, JsonObject? view, ServingCaller who, CancellationToken cancellationToken)
    {
        if (!draw || SemanticRest.StatusOf(answer) != "complete") return await SemanticRest.ResultAsync(answer, cancellationToken);
        if (AnswerViews.UnreadableColumn(answer) is { } unreadable)
        {
            var table = await SemanticRest.ResultAsync(answer, cancellationToken);
            table["notices"] = new JsonArray(new JsonObject
            {
                ["code"] = "view_unavailable", ["column"] = unreadable.Name, ["type"] = unreadable.Type,
                ["message"] = $"matrix and chart are unavailable: column {unreadable.Name} has type {unreadable.Type}, which a view cannot read. The table shows what the REST answer holds of it (a value of that type is empty).",
            });
            return table;
        }
        // a result that will be drawn is read once, the way a dashboard widget's answer is, and kept for a while so another view of it is not another run
        var document = await AnswerViews.ReadAsync(answer, limit, cancellationToken);
        var result = SemanticRest.ResultFrom(answer, document);
        result["result_id"] = Hold(SemanticCallers.KeyOf(who), document);
        if (view is not null) result["rendered_view"] = AnswerViews.Render(view.DeepClone().AsObject(), document);
        return result;
    }

    // ---- results kept for rendering (D37) ----

    private sealed record Held(string Owner, JsonObject Document, DateTimeOffset Expires);

    private const int HeldMost = 32;
    private static readonly TimeSpan HeldFor = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, Held> _held = [];

    /// <summary>Keeps a result document for its owner for ten minutes (at most 32 at a time on this instance, the oldest dropped first) and returns the id a render names it by.</summary>
    private string Hold(string owner, JsonObject document)
    {
        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        lock (_held)
        {
            foreach (var key in _held.Where(h => h.Value.Expires <= now).Select(h => h.Key).ToList()) _held.Remove(key);
            while (_held.Count >= HeldMost) _held.Remove(_held.MinBy(h => h.Value.Expires).Key);
            _held[id] = new Held(owner, document, now + HeldFor);
        }
        return id;
    }

    /// <summary>
    /// <c>POST /api/v1/my/runs/{result_id}/render</c> <c>{view}</c>: draws the result a run kept (<c>result_id</c> of a run that asked <c>keep</c> or sent a <c>view</c>) as the view document says, through the engine's
    /// renderer, so the explorer and a dashboard cannot disagree on a cell. Nothing is run again and nothing is read from a source. Only the person who ran it can render it; a result that is gone (ten minutes, another instance,
    /// the 33rd result) is a 410 that says to run the statement again. A refused encoding is a 200 whose <c>rendered_view</c> has <c>status: failed</c> and the engine's <c>errors</c>.
    /// </summary>
    private ManagementResult Render(string resultId, string? body, ServingCaller who)
    {
        JsonObject? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { request = null; }
        if (request?["view"] is not JsonObject view) return Problem(400, "The body is {\"view\": {<a view document: spec_version, kind, result, views>}}.");
        Held? held;
        lock (_held) held = _held.TryGetValue(resultId, out var h) && h.Owner == SemanticCallers.KeyOf(who) && h.Expires > _clock.GetUtcNow() ? h : null;
        if (held is null)
            return new ManagementResult(410, new JsonObject { ["error"] = "That result is no longer kept here (a result is kept for ten minutes, on the instance that ran it). Run the statement again.", ["code"] = "result_gone" });
        return ManagementResult.Ok(new JsonObject { ["result_id"] = resultId, ["rendered_view"] = AnswerViews.Render(view.DeepClone().AsObject(), held.Document) });
    }

    /// <summary>The engine wants every query to state its limit; the explorer's text may leave it out, and then it gets the call's (a text that says LIMIT keeps its own).</summary>
    internal static string WithLimit(string sql, int limit) =>
        System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bLIMIT\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? sql : sql.TrimEnd().TrimEnd(';') + " LIMIT " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The values of a JSON object as the statement's bound parameters, by name.</summary>
    internal static IReadOnlyList<ServingParameter>? Bind(JsonObject? values)
    {
        if (values is not { Count: > 0 }) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return [.. values.Select(v => v.Value switch
        {
            null => new ServingParameter(v.Key, "null", null),
            JsonArray a => new ServingParameter(v.Key, "list", a.ToJsonString()),
            JsonValue j when j.TryGetValue<bool>(out var b) => new ServingParameter(v.Key, "bool", b ? "true" : "false"),
            JsonValue j when j.TryGetValue<long>(out var n) => new ServingParameter(v.Key, "int64", n.ToString(inv)),
            JsonValue j when j.TryGetValue<double>(out var d) => new ServingParameter(v.Key, "double", d.ToString("R", inv)),
            var other => new ServingParameter(v.Key, "string", other.GetValue<string>()),
        })];
    }

    private ManagementResult List(ServingCaller who, bool all, string? model, int limit)
    {
        var items = new JsonArray([.. store.List(SemanticCallers.KeyOf(who), all, model, limit).Select(r => (JsonNode)Summary(r))]);
        return ManagementResult.Ok(new JsonObject { ["runs"] = items, ["all"] = all, ["retention_days"] = SemanticRunsModule.RetentionDays });
    }

    private ManagementResult Counts(ServingCaller who, bool all, int days)
    {
        var counts = new JsonObject();
        foreach (var (code, count) in store.CountsByCode(_clock.GetUtcNow().AddDays(-days), SemanticCallers.KeyOf(who), all)) counts[code] = count;
        return ManagementResult.Ok(new JsonObject { ["days"] = days, ["by_code"] = counts });
    }

    private static JsonObject Summary(SemanticRunRecord r) => new()
    {
        ["id"] = r.Id, ["caller"] = r.Run.Caller, ["model"] = r.Run.Model, ["model_version"] = r.Run.ModelVersion, ["kind"] = r.Run.Kind, ["text"] = r.Run.Text, ["status"] = r.Run.Status,
        ["error_code"] = r.Run.ErrorCode, ["request_hash"] = r.Run.RequestHash, ["started_at"] = r.Run.StartedAt, ["duration_ms"] = r.Run.DurationMs, ["rows"] = r.Run.Rows, ["replay_of"] = r.Run.ReplayOf,
    };

    private static JsonObject Detail(SemanticRunRecord r)
    {
        var body = Summary(r);
        body["message"] = r.Run.Message;
        body["request"] = r.Run.Request?.DeepClone();
        body["warnings"] = r.Run.Warnings is { } w ? JsonNode.Parse(w) : null;
        body["plan"] = r.Run.Plan;
        return body;
    }

    private sealed record ReplayBody(bool? Keep, JsonObject? View);

    private async Task<ManagementResult> ReplayAsync(SemanticRunRecord record, string? body, ServingCaller who, ManagementCaller caller, CancellationToken cancellationToken)
    {
        // the body is optional: {keep?, view?} as a run takes them (D38); the view is drawn, never stored in the record
        ReplayBody? options;
        try { options = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ReplayBody>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Problem(400, "The body of a replay is empty or {\"keep\": true, \"view\": {<a view document>}}."); }
        var run = record.Run;
        if (run.Request is null || run.Kind is not ("query" or "explain" or "members"))
            return Problem(400, $"Run {record.Id} has no request to run again: it was {(run.Request is null ? "refused before it became one" : "a " + run.Kind)}.");

        // the version the record names: the one this instance serves, else the published version loaded as it was; a version that is gone is said so
        SemanticModel? model;
        var current = models.Find(run.Model);
        if (run.ModelVersion is not { } version) model = current;
        else if (current?.Version == version) model = current;
        else
        {
            try { model = published.Load(run.Model, version); }
            catch (InvalidOperationException) { return Gone(record, $"Version {version} of the model {run.Model} is gone (pruned, or never published to this group), so run {record.Id} cannot be replayed on it. Run its text again on the version now served."); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return new ManagementResult(409, new JsonObject { ["error"] = $"Version {version} of the model {run.Model} no longer loads: {ex.Message}", ["code"] = "version_unloadable" }); }
        }
        if (model is null) return Gone(record, $"The model {run.Model} is not served any more, so run {record.Id} cannot be replayed.");

        var request = run.Request.DeepClone() as JsonObject ?? new JsonObject();
        if (run.Kind == "explain")
        {
            // the record keeps the query the explain planned; an explain is that query under kind explain
            var explain = new JsonObject { ["kind"] = "explain", ["query"] = request["query"]?.DeepClone() };
            foreach (var (key, value) in request) if (key is not ("kind" or "query")) explain[key] = value?.DeepClone();
            request = explain;
        }

        // the replay runs on a model set of its own that holds the one version, so the statement meets that version whatever is served now; it writes its own record below
        var own = new SemanticModels { Level = models.Level, UserQueries = models.UserQueries };
        own.Set(model);
        var started = _clock.GetUtcNow();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var context = new ServingContext(who, ClientAddress: null);
        try
        {
            await using var answer = await ((IServingServices)serving).WithSemantic(own).Queries.RunAsync(context, new ServingRequest.Semantic(SemanticModels.SourcePrefix + model.Name, request.ToJsonString(), new ServingLimits(MaxRows: SemanticRest.MaxRows)), cancellationToken);
            var result = await ShapeAsync(answer, SemanticRest.MaxRows, (options?.View is not null || options?.Keep == true) && run.Kind != "explain", options?.View, who, cancellationToken);
            var hash = result["request_hash"]?.GetValue<string>();
            var status = result["status"]?.GetValue<string>() ?? "complete";
            var rows = result["rows"] is JsonArray r ? r.Count : 0;
            var id = store.Add(new SemanticRun(SemanticCallers.KeyOf(who), model.Name, model.Version, run.Kind, run.Text, run.Request, hash, status == "ok" ? "complete" : status, null, null,
                result["warnings"] is JsonArray { Count: > 0 } w ? w.ToJsonString() : null, null, started, clock.ElapsedMilliseconds, rows, record.Id));
            Record(caller, $"{model.Name}/{record.Id}", "replayed");
            result["replay_of"] = record.Id;
            result["run_id"] = id;
            result["model_version"] = model.Version;
            result["same_hash"] = hash is not null && string.Equals(hash, run.RequestHash, StringComparison.Ordinal);
            return ManagementResult.Ok(result);
        }
        catch (ViewsAnswerException ex)
        {
            var id = store.Add(new SemanticRun(SemanticCallers.KeyOf(who), model.Name, model.Version, run.Kind, run.Text, run.Request, run.RequestHash, "failed", null, ex.Message, null, null, started, clock.ElapsedMilliseconds, 0, record.Id));
            Record(caller, $"{model.Name}/{record.Id}", "replay failed");
            return new ManagementResult(400, new JsonObject { ["error"] = ex.Message, ["code"] = ex.Code, ["run_id"] = id, ["replay_of"] = record.Id });
        }
        catch (ServingException ex)
        {
            var code = ex is ServingInvalidException ? "refused" : ex is ServingLimitException ? "limit" : "failed";
            var id = store.Add(new SemanticRun(SemanticCallers.KeyOf(who), model.Name, model.Version, run.Kind, run.Text, run.Request, run.RequestHash, code, null, ex.Message, null, null, started, clock.ElapsedMilliseconds, 0, record.Id));
            Record(caller, $"{model.Name}/{record.Id}", "replay " + code);
            var status = ex switch { ServingNotFoundException => 404, ServingDeniedException or ServingUnauthenticatedException => 403, ServingBackendException => 502, _ => 400 };
            return new ManagementResult(status, new JsonObject { ["error"] = ex.Message, ["run_id"] = id, ["replay_of"] = record.Id });
        }
    }

    private ManagementResult Gone(SemanticRunRecord record, string message)
    {
        return new ManagementResult(410, new JsonObject { ["error"] = message, ["code"] = "version_gone", ["model"] = record.Run.Model, ["model_version"] = record.Run.ModelVersion });
    }

    private void Record(ManagementCaller caller, string target, string outcome) => audit.Append(caller.Audit("semantic-run replay", target, outcome, _clock.GetUtcNow()));
}
