using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataKit.Secrets;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Serving;

namespace DbDataProxy.Server.Management;

/// <summary>
/// Semantic models through the management API (plan 19, J13): publish a model (its catalog, the data source each of its sources maps to, the connection of an Analysis Services source), list and read them, roll back, remove.
/// A publish is checked before anything is written: the catalog loads and carries the name of the call, every source maps to a data source that exists (and, for a publishing client, one that client made, so a model never
/// reaches data its publisher could not), every data source answers, and an Analysis Services connection (Windows only) is a secret reference for a publishing client. The new version is written whole and only then made current; a publish that fails at any
/// step leaves the version that was serving. Every version is kept, so a rollback is a change of which one is current. A model named in <c>server.yaml</c> is not replaced by a publish.
/// </summary>
public sealed class ProxySemanticModels(
    PublishedSemanticModels store, SemanticModels models, Func<ProxyOptions> options, string configDirectory, Func<DataSourceCatalog> catalog, IAuditLog audit, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _publishing = new(1, 1);

    private sealed record PublishRequest(string? Catalog, Dictionary<string, string>? Sources, Dictionary<string, string>? Ssas, [property: JsonPropertyName("expected_version")] int? ExpectedVersion);

    private sealed record RollbackRequest([property: JsonPropertyName("to_version")] int? ToVersion);

    public async Task<ManagementResult> HandleAsync(string method, string[] rest, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (rest.Length == 0) return method == "GET" ? ManagementResult.Ok(List(caller)) : ManagementResult.Problem(405, "Semantic models are listed with GET.");
        var name = Uri.UnescapeDataString(rest[0]);
        if (!PublishedSemanticModels.ValidName(name)) return ManagementResult.Problem(400, "A model name is a letter followed by letters, digits, '.', '_' or '-' (up to 64).");

        if (rest.Length == 2 && rest[1] == "rollback") return method == "POST" ? await RollbackAsync(name, body, caller) : ManagementResult.Problem(405, "A model is rolled back with POST.");
        if (rest.Length != 1) return ManagementResult.Problem(404, "There is no such call.");
        return method switch
        {
            "GET" => Read(name, caller),
            "PUT" => await PublishAsync(name, body, caller, cancellationToken),
            "DELETE" => await DeleteAsync(name, caller),
            _ => ManagementResult.Problem(405, "A model is read with GET, published with PUT, removed with DELETE."),
        };
    }

    private static bool IsAdministrator(ManagementCaller caller) => caller.Holds(ServerRole.Admin);

    private static string? Client(ManagementCaller caller) => IsAdministrator(caller) ? null : OwnerScope.Stamp(caller.Token!);

    private string? OwnerOf(string name) => store.Current(name) is { } n ? store.Version(name, n)?.ManagedBy : null;

    private ManagementResult? Scope(ManagementCaller caller, ObjectAction action, string name)
    {
        if (IsAdministrator(caller)) return null;
        var decision = OwnerScope.Authorize(caller.Token!, action, action == ObjectAction.Create ? null : new ManagedObject(OwnerOf(name)));
        return decision.Allowed ? null : ManagementResult.Problem(403, decision.Reason ?? "This client may not do this to that model.");
    }

    private JsonObject List(ManagementCaller caller)
    {
        var owner = Client(caller);
        var items = new JsonArray();
        foreach (var name in store.Names())
        {
            var current = store.Current(name);
            var meta = current is { } n ? store.Version(name, n) : null;
            if (owner is not null && meta?.ManagedBy != owner) continue;
            items.Add(new JsonObject { ["name"] = name, ["current_version"] = current, ["versions"] = store.Versions(name).Count, ["managed_by"] = meta?.ManagedBy, ["served"] = models.Find(name) is not null });
        }
        return new JsonObject { ["models"] = items };
    }

    private ManagementResult Read(string name, ManagementCaller caller)
    {
        if (store.Current(name) is not { } current) return ManagementResult.Problem(404, $"There is no published model '{name}'.");
        if (Scope(caller, ObjectAction.Change, name) is { } refused) return refused;
        var versions = new JsonArray([.. store.Versions(name).Select(v => (JsonNode)new JsonObject
        {
            ["version"] = v.Number, ["published_by"] = v.PublishedBy, ["managed_by"] = v.ManagedBy, ["published_at"] = v.PublishedAt,
        })]);
        var meta = store.Version(name, current)!;
        return ManagementResult.Ok(new JsonObject
        {
            ["name"] = name, ["current_version"] = current, ["catalog"] = store.CatalogText(name, current),
            ["sources"] = JsonSerializer.SerializeToNode(meta.Sources, Json), ["ssas"] = new JsonArray([.. meta.Ssas.Keys.Select(k => (JsonNode)k)]), ["versions"] = versions,
        });
    }

    private async Task<ManagementResult> PublishAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        PublishRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<PublishRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request?.Catalog is not { Length: > 0 } text)
            return ManagementResult.Problem(400, "The body is a JSON object: {\"catalog\": \"<the catalog's yaml>\", \"sources\": {\"<catalog source>\": \"<data source>\"}, \"ssas\": {\"<catalog source>\": \"secret:...\"}, \"expected_version\": <number, or absent to create>}.");

        var created = store.Current(name) is null;
        if (Scope(caller, created ? ObjectAction.Create : ObjectAction.Change, name) is { } refused) { Record(caller, "semantic-model publish", name, "refused"); return refused; }
        if (created && models.Find(name) is not null)
            return ManagementResult.Problem(409, $"A model named '{name}' is set up in server.yaml; a published model needs a name of its own.");

        await _publishing.WaitAsync(cancellationToken);
        try
        {
            if (store.Current(name) != request.ExpectedVersion)
            {
                Record(caller, "semantic-model publish", name, "stale");
                return new ManagementResult(409, new JsonObject { ["error"] = "The model changed since it was read; read it again and publish on that.", ["current_version"] = store.Current(name) });
            }

            var sources = request.Sources ?? [];
            var ssas = request.Ssas ?? [];
            Catalog parsed;
            try { parsed = CatalogLoader.LoadText(text); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the catalog readers throw what the YAML and JSON readers do for a text that is not a catalog at all
                return Refused(caller, name, "The catalog does not load, so nothing was published.", ex.Message);
            }
            if (!string.Equals(parsed.Name, name, StringComparison.OrdinalIgnoreCase))
                return Refused(caller, name, "The catalog is named '" + parsed.Name + "', and the call names '" + name + "'; they are the same name.");

            var problem = await CheckSourcesAsync(parsed, sources, ssas, caller, cancellationToken);
            if (problem is not null) return Refused(caller, name, problem);

            var version = store.Publish(name, text, Client(caller), Who(caller), new(sources), new(ssas), _clock.GetUtcNow());
            try { models.Set(store.Load(name, version.Number)); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                // cannot happen after the check above, but the old version must keep serving if it does
                if (store.Versions(name).Where(v => v.Number < version.Number).Select(v => v.Number).DefaultIfEmpty(0).Max() is > 0 and var previous) store.Point(name, previous);
                else store.Delete(name);
                return Refused(caller, name, "The model did not load once written, so the previous version still serves.", ex.Message);
            }
            Record(caller, "semantic-model publish", name, created ? "created" : "changed");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["version"] = version.Number, ["created"] = created });
        }
        finally { _publishing.Release(); }
    }

    /// <summary>The first reason the mapping cannot be served, or null.</summary>
    private async Task<string?> CheckSourcesAsync(Catalog parsed, Dictionary<string, string> sources, Dictionary<string, string> ssas, ManagementCaller caller, CancellationToken cancellationToken)
    {
        var current = options();
        var owner = Client(caller);
        foreach (var (source, definition) in parsed.Doc.Sources.Where(s => !s.Key.Contains('.')))
        {
            if (definition.Kind == "ssas")
            {
                // a source with no connection is allowed: a query that needs it is refused with a message, as for a model set up in server.yaml
                if (!ssas.TryGetValue(source, out var connection)) continue;
                if (string.IsNullOrWhiteSpace(connection)) return $"The connection of '{source}' is empty.";
                if (!OperatingSystem.IsWindows()) return "Analysis Services sources run only on Windows, and this server does not.";
                // a publishing client may not put a password into a request: it keeps it as a secret of its own (PUT /api/v1/secrets) and sends the reference
                if (owner is not null && !ConnectionStringReferences.IsWholeReference(connection, out _)) return $"The connection of '{source}' must be a secret reference ('secret:...') for a publishing client.";
                continue;
            }
            var dataSource = sources.TryGetValue(source, out var mapped) ? mapped : source;
            if (!current.DataSources.ContainsKey(dataSource)) return $"Source '{source}' maps to '{dataSource}', which is not a data source of this server.";
            if (owner is not null && ConnectionOwner(dataSource) != owner) return $"Source '{source}' maps to '{dataSource}', which this client did not make; a publishing client's models read only its own data sources.";
            try
            {
                var resolved = catalog().Resolve(new ProxyPrincipal("management", "management", ["*"]), dataSource);
                await using var result = await resolved.Executor.ExecuteQueryAsync(new DbDataProxy.Data.QueryRequest("SELECT 1", 1, 15), cancellationToken);
                await foreach (var _ in result.ReadBatchesAsync(cancellationToken)) break;
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or DbDataKit.Connections.ConnectionFailureException)
            {
                return $"Data source '{dataSource}' (for source '{source}') did not answer a test query.";
            }
            // every table the source's facts and joins name, so a mapping to the wrong database is refused here and not at the first query
            var tables = parsed.Doc.Sources.Where(s => SemanticModel.BaseSource(s.Key) == source)
                .SelectMany(s => s.Value.Joins.Values.Select(j => j.Table).Append(s.Value.Fact.Table))
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                if (table.Contains(';')) return $"The table '{table}' of source '{source}' is not a table name.";
                try
                {
                    var resolved = catalog().Resolve(new ProxyPrincipal("management", "management", ["*"]), dataSource);
                    await using var result = await resolved.Executor.ExecuteQueryAsync(new DbDataProxy.Data.QueryRequest("SELECT 1 FROM " + table + " WHERE 1 = 0", 1, 15), cancellationToken);
                    await foreach (var _ in result.ReadBatchesAsync(cancellationToken)) break;
                }
                catch (Exception ex) when (ex is Grpc.Core.RpcException or DbDataKit.Connections.ConnectionFailureException)
                {
                    return $"The table '{table}' of source '{source}' could not be read on data source '{dataSource}'.";
                }
            }
        }
        foreach (var extra in sources.Keys.Concat(ssas.Keys).Where(k => !parsed.Doc.Sources.ContainsKey(k) && !parsed.Doc.Sources.Keys.Any(id => SemanticModel.BaseSource(id) == k)).Distinct())
            return $"The catalog has no source '{extra}'.";
        return null;
    }

    private string? ConnectionOwner(string dataSource)
    {
        var file = Path.Combine(configDirectory, "connections", dataSource + ".yaml");
        return File.Exists(file) ? DbDataKit.Connections.ConnectionDefinitionLoader.Load(File.ReadAllText(file), file).Definition?.ManagedBy : null;
    }

    private async Task<ManagementResult> RollbackAsync(string name, string? body, ManagementCaller caller)
    {
        if (store.Current(name) is not { } current) return ManagementResult.Problem(404, $"There is no published model '{name}'.");
        if (Scope(caller, ObjectAction.Change, name) is { } refused) { Record(caller, "semantic-model rollback", name, "refused"); return refused; }
        RollbackRequest? request = null;
        try { if (!string.IsNullOrWhiteSpace(body)) request = JsonSerializer.Deserialize<RollbackRequest>(body, Json); }
        catch (JsonException) { return ManagementResult.Problem(400, "The body is {\"to_version\": <number>} or empty for the version before the current one."); }

        await _publishing.WaitAsync();
        try
        {
            var target = request?.ToVersion ?? store.Versions(name).Where(v => v.Number < current).Select(v => v.Number).DefaultIfEmpty(0).Max();
            if (target <= 0 || store.Version(name, target) is null) return ManagementResult.Problem(404, request?.ToVersion is null ? "There is no earlier version to go back to." : $"Model '{name}' has no version {target}.");
            SemanticModel loaded;
            try { loaded = store.Load(name, target); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { return Refused(caller, name, $"Version {target} does not load, so the current one still serves.", ex.Message); }
            store.Point(name, target);
            models.Set(loaded);
            Record(caller, "semantic-model rollback", name, $"to version {target}");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["version"] = target, ["from_version"] = current });
        }
        finally { _publishing.Release(); }
    }

    private async Task<ManagementResult> DeleteAsync(string name, ManagementCaller caller)
    {
        if (store.Current(name) is null) return ManagementResult.Problem(404, $"There is no published model '{name}'.");
        if (Scope(caller, ObjectAction.Delete, name) is { } refused) { Record(caller, "semantic-model remove", name, "refused"); return refused; }
        await _publishing.WaitAsync();
        try
        {
            models.Remove(name);
            store.Delete(name);
        }
        finally { _publishing.Release(); }
        Record(caller, "semantic-model remove", name, "removed");
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true });
    }

    private string Who(ManagementCaller caller) { var entry = caller.Audit("", null, "", _clock.GetUtcNow()); return entry.UserName ?? entry.UserId; }

    private ManagementResult Refused(ManagementCaller caller, string name, string error, string? detail = null)
    {
        Record(caller, "semantic-model publish", name, "invalid");
        var body = new JsonObject { ["error"] = error, ["valid"] = false };
        if (detail is not null) body["detail"] = detail;
        return new ManagementResult(400, body);
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) => audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));
}
