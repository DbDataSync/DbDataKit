using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The record of a person's statements on the management API (plan 14a SL23/SL24, D21): <c>GET /api/v1/my/runs</c> (the caller's own, newest first; <c>?model=</c>, <c>?limit=</c>; an administrator may ask <c>?all=true</c> for
/// everyone's), <c>GET /api/v1/my/runs/{id}</c> (one, with the request document, warnings and plan), <c>GET /api/v1/my/runs/counts</c> (the statements that ended with each error code in the last <c>?days=</c>, 7 by default) and
/// <c>POST /api/v1/my/runs/{id}/replay</c> (the record's request is run again, as the caller, on the model version the record names). A record of another person is not found, whoever asks without being an administrator. A replay
/// on a version that is gone (pruned, or the model unpublished) is answered 410 and says which version. A replay writes a record of its own that names the one it re-ran. The viewer role is enough; the caller is the person the
/// console's token names, so the views module must be on.
/// </summary>
public sealed class GatewaySemanticRuns(SemanticRunStore store, SemanticModels models, PublishedSemanticModels published, IServingServices serving, GatewayViews? views, IAuditLog audit, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public const string Route = GatewayManagement.Prefix + "/my/runs";

    public static bool Handles(string route) => route == Route || route.StartsWith(Route + "/", StringComparison.Ordinal);

    private static ManagementResult Problem(int status, string message) => ManagementResult.Problem(status, message);

    public async Task<ManagementResult> HandleAsync(string method, string route, string? query, ManagementCaller caller, CancellationToken cancellationToken)
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

        if (parts.Length == 0) return method == "GET" ? List(who, wantsAll, q["model"], int.TryParse(q["limit"], out var limit) ? limit : 100) : Problem(405, "The list is read with GET.");
        if (parts is ["counts"]) return method == "GET" ? Counts(who, wantsAll, int.TryParse(q["days"], out var days) ? Math.Clamp(days, 1, 365) : 7) : Problem(405, "The counts are read with GET.");
        if (!long.TryParse(parts[0], out var id) || parts.Length > 2) return Problem(404, "There is no such call.");
        var record = store.Get(id, SemanticCallers.KeyOf(who), admin);
        if (record is null) return Problem(404, $"There is no run {id} of yours.");
        if (parts.Length == 1) return method == "GET" ? ManagementResult.Ok(Detail(record)) : Problem(405, "A run is read with GET.");
        if (parts[1] != "replay") return Problem(404, "There is no such call.");
        return method == "POST" ? await ReplayAsync(record, who, caller, cancellationToken) : Problem(405, "A replay is started with POST.");
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

    private async Task<ManagementResult> ReplayAsync(SemanticRunRecord record, ServingCaller who, ManagementCaller caller, CancellationToken cancellationToken)
    {
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
            var result = await SemanticRest.RunAsync(((IServingServices)serving).WithSemantic(own), context, model.Name, request.ToJsonString(), SemanticRest.MaxRows, cancellationToken);
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
