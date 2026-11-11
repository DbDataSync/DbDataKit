using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Sql;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// A person's own saved queries on the management API (plan 14a SL17, D20): <c>/api/v1/my/queries</c> and <c>/api/v1/my/queries/{model}/{name}[/history|restore|shares]</c> over the <see cref="SavedQueryStore"/>, as the person the
/// console's token names (the same person the Flight listener sees, so what they save is what they call: <c>SELECT * FROM name(args)</c>). A query is checked against its model when it is saved: a member the model does not have is a
/// refusal that names it. Sharing shares the text, never data: a query always runs as its caller, so a person without access to the data source is refused when they run it. The viewer role is enough, and a call with no person behind
/// it (a server client) is refused. The person is known through the views module's identity lookup, so the module must be on.
/// </summary>
public sealed class GatewayUserQueries(SavedQueryStore store, SemanticModels models, GatewayViews? views, IAuditLog audit, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public const string Route = GatewayManagement.Prefix + "/my/queries";

    public static bool Handles(string route) => route == Route || route.StartsWith(Route + "/", StringComparison.Ordinal);

    private static ManagementResult Problem(int status, string message) => ManagementResult.Problem(status, message);

    private sealed record SaveBody(string? Description, string? Sql, JsonObject? Request, JsonObject? Parameters, [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] int? ExpectedVersion);

    public async Task<ManagementResult> HandleAsync(string method, string route, string? query, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "This needs the viewer role.");
        if (views is null) return Problem(404, "This gateway does not serve the views module, which tells who a person is: list `views` under `modules:` in server.yaml.");
        if (caller.Token is not { } token) return Problem(403, "Saved queries are a person's: sign in through a console.");
        var who = (await views.ConsolePrincipalAsync(token, cancellationToken)).Caller;
        var owner = who.Name;
        var parts = route.Length > Route.Length ? route[(Route.Length + 1)..].Split('/') : [];
        var q = query is null ? new System.Collections.Specialized.NameValueCollection() : System.Web.HttpUtility.ParseQueryString(query);

        if (parts.Length == 0) return method == "GET" ? List(who, q["model"]) : Problem(405, "The list is read with GET.");
        if (parts.Length is < 2 or > 3) return Problem(404, "There is no such call.");
        var model = Uri.UnescapeDataString(parts[0]);
        var name = Uri.UnescapeDataString(parts[1]);
        var action = parts.Length > 2 ? parts[2] : null;
        try
        {
            return (method, action) switch
            {
                ("GET", null) => Open(who, owner, model, name, q["owner"]),
                ("PUT", null) => Save(owner, model, name, body, caller),
                ("DELETE", null) => Delete(owner, model, name, caller),
                ("GET", "history") => History(owner, model, name),
                ("POST", "restore") => Restore(owner, model, name, body, caller),
                ("GET", "shares") => store.Get(owner, model, name) is null ? Problem(404, $"You have no saved query '{name}' on {model}.") : Ok(new JsonObject { ["grantees"] = Strings(store.Shares(owner, model, name)) }),
                ("PUT", "shares") => Share(owner, model, name, body, caller),
                _ => Problem(405, "That method is not allowed here."),
            };
        }
        catch (SavedQueryConflictException ex)
        {
            Record(caller, "saved-query " + (action ?? method.ToLowerInvariant()), $"{model}/{name}", "conflict");
            return new ManagementResult(409, new JsonObject { ["error"] = ex.Message, ["current_version"] = ex.CurrentVersion });
        }
        catch (ArgumentException ex) { return Problem(400, ex.Message); }
    }

    private static ManagementResult Ok(JsonObject body) => ManagementResult.Ok(body);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

    private static JsonObject Describe(SavedQueryRecord r, string caller) => new()
    {
        ["model"] = r.Model, ["name"] = r.Name, ["owner"] = r.Owner, ["own"] = string.Equals(r.Owner, caller, StringComparison.OrdinalIgnoreCase), ["version"] = r.Version,
        ["description"] = r.Query.Description, ["sql"] = r.Query.Sql, ["request"] = r.Query.Request?.DeepClone(), ["parameters"] = r.Query.Parameters.DeepClone(), ["updated_at"] = r.UpdatedAt,
    };

    /// <summary>The person's own queries (every model, or one) and, for one model, those shared with them.</summary>
    private ManagementResult List(DbDataKit.Serving.ServingCaller who, string? model)
    {
        var items = new JsonArray();
        foreach (var r in store.ListOwned(who.Name, model)) items.Add(Describe(r, who.Name));
        if (!string.IsNullOrWhiteSpace(model))
            foreach (var shared in store.List(who, model).Where(s => !string.Equals(s.Owner, who.Name, StringComparison.OrdinalIgnoreCase)))
                items.Add(new JsonObject { ["model"] = model, ["name"] = shared.Name, ["owner"] = shared.Owner, ["own"] = false, ["description"] = shared.Query.Description, ["sql"] = shared.Query.Sql, ["request"] = shared.Query.Request?.DeepClone(), ["parameters"] = shared.Query.Parameters.DeepClone() });
        return Ok(new JsonObject { ["queries"] = items });
    }

    private ManagementResult Open(DbDataKit.Serving.ServingCaller who, string owner, string model, string name, string? ownerOf)
    {
        if (ownerOf is null || string.Equals(ownerOf, owner, StringComparison.OrdinalIgnoreCase))
            return store.Get(owner, model, name) is { } own ? Ok(Describe(own, who.Name)) : Problem(404, $"You have no saved query '{name}' on {model}.");
        // another person's, only when it is shared with this caller
        var shared = store.List(who, model).FirstOrDefault(s => s.Name == name && string.Equals(s.Owner, ownerOf, StringComparison.OrdinalIgnoreCase));
        if (shared is null) return Problem(404, $"{ownerOf} has shared no saved query '{name}' on {model} with you.");
        return Ok(new JsonObject { ["model"] = model, ["name"] = name, ["owner"] = shared.Owner, ["own"] = false, ["description"] = shared.Query.Description, ["sql"] = shared.Query.Sql, ["request"] = shared.Query.Request?.DeepClone(), ["parameters"] = shared.Query.Parameters.DeepClone() });
    }

    private ManagementResult Save(string owner, string model, string name, string? body, ManagementCaller caller)
    {
        SaveBody? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<SaveBody>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { request = null; }
        if (request is null || (request.Sql is null) == (request.Request is null))
            return Problem(400, "The body is {\"sql\": \"<semantic sql>\"} or {\"request\": {...}} (one of them), with optional \"description\", \"parameters\" and \"expected_version\" (absent to create).");
        if (models.Find(model) is not { } found) return Problem(404, $"There is no model '{model}' to save a query on.");
        var definition = new SavedQueryDef { Description = request.Description, Sql = request.Sql, Request = request.Request, Parameters = request.Parameters ?? new JsonObject() };
        if (Check(found.Catalog, definition) is { } refusal) { Record(caller, "saved-query save", $"{model}/{name}", "invalid"); return new ManagementResult(400, new JsonObject { ["error"] = refusal, ["valid"] = false }); }
        if (found.Catalog.Doc.Queries.ContainsKey(name)) return Problem(409, $"The model {model} has a saved query of its own named '{name}'; choose another name.");
        var saved = store.Save(owner, found.Name, name, definition, owner, request.ExpectedVersion);
        Record(caller, "saved-query save", $"{model}/{name}", saved.Version == 1 ? "created" : "changed");
        return Ok(Describe(saved, owner));
    }

    /// <summary>The first reason the query cannot run on the catalog, in words that name the member, or null.</summary>
    internal static string? Check(Catalog catalog, SavedQueryDef query)
    {
        JsonObject? request = query.Request;
        if (query.Sql is not null)
        {
            var translation = SemanticSqlTranslator.Translate(query.Sql, catalog.Name);
            if (!translation.Ok) return string.Join("; ", translation.Errors.Select(e => e.Message));
            request = translation.Request;
        }
        if (request is null || request["kind"]?.GetValue<string>() != "query" || request["query"] is not JsonObject body) return "A saved query's request is of kind query.";
        var doc = catalog.Doc;
        foreach (var a in body["attributes"] as JsonArray ?? [])
            if (a?.GetValueKind() == JsonValueKind.String && !doc.Attributes.ContainsKey(a.GetValue<string>())) return $"The model {catalog.Name} has no attribute '{a.GetValue<string>()}'.";
        foreach (var m in body["measures"] as JsonArray ?? [])
        {
            var measure = m is JsonObject o ? (o["measure"] ?? o["name"] ?? o["id"])?.GetValue<string>() : m?.GetValue<string>();
            if (measure is not null && !doc.Measures.ContainsKey(measure)) return $"The model {catalog.Name} has no measure '{measure}'.";
        }
        foreach (var f in body["filters"] as JsonArray ?? [])
        {
            if (f is null) continue;
            try
            {
                foreach (var n in Filter.Parse(f).Names().Distinct())
                    if (!doc.Attributes.ContainsKey(n) && !doc.Measures.ContainsKey(n)) return $"The model {catalog.Name} has no member '{n}' to filter by.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or JsonException) { return "A filter is not written as a filter: " + ex.Message; }
        }
        return null;
    }

    private ManagementResult Delete(string owner, string model, string name, ManagementCaller caller)
    {
        if (!store.Delete(owner, model, name)) return Problem(404, $"You have no saved query '{name}' on {model}.");
        Record(caller, "saved-query remove", $"{model}/{name}", "removed");
        return Ok(new JsonObject { ["name"] = name, ["removed"] = true });
    }

    private ManagementResult History(string owner, string model, string name)
    {
        if (store.Get(owner, model, name) is null) return Problem(404, $"You have no saved query '{name}' on {model}.");
        var versions = new JsonArray([.. store.History(owner, model, name).Select(v => (JsonNode)new JsonObject
        {
            ["version"] = v.Version, ["saved_by"] = v.SavedBy, ["saved_at"] = v.SavedAt, ["description"] = v.Query.Description, ["sql"] = v.Query.Sql, ["request"] = v.Query.Request?.DeepClone(), ["parameters"] = v.Query.Parameters.DeepClone(),
        })]);
        return Ok(new JsonObject { ["versions"] = versions });
    }

    private ManagementResult Restore(string owner, string model, string name, string? body, ManagementCaller caller)
    {
        int? version;
        try { version = string.IsNullOrWhiteSpace(body) ? null : (JsonNode.Parse(body) as JsonObject)?["version"]?.GetValue<int>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { version = null; }
        if (version is null) return Problem(400, "The body is {\"version\": <number>}.");
        var restored = store.Restore(owner, model, name, version.Value, owner);
        Record(caller, "saved-query restore", $"{model}/{name}", $"to {version}");
        return Ok(Describe(restored, owner));
    }

    private ManagementResult Share(string owner, string model, string name, string? body, ManagementCaller caller)
    {
        string[]? wanted;
        try { wanted = string.IsNullOrWhiteSpace(body) ? null : (JsonNode.Parse(body) as JsonObject)?["grantees"]?.AsArray().Select(g => g!.GetValue<string>()).ToArray(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { wanted = null; }
        if (wanted is null) return Problem(400, "The body is {\"grantees\": [\"user:<name>\", \"group:<name>\", \"*\"]}: the whole set it is shared with.");
        if (store.Get(owner, model, name) is null) return Problem(404, $"You have no saved query '{name}' on {model}.");
        var now = store.Shares(owner, model, name);
        foreach (var g in wanted.Except(now, StringComparer.Ordinal)) store.Share(owner, model, name, g);
        foreach (var g in now.Except(wanted, StringComparer.Ordinal)) store.Unshare(owner, model, name, g);
        Record(caller, "saved-query share", $"{model}/{name}", string.Join(',', wanted));
        return Ok(new JsonObject { ["grantees"] = Strings(store.Shares(owner, model, name)) });
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) => audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));
}
