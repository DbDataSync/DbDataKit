using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DbDataKit.DataModel;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// Entity models through the management API (plan 20 O10): a DbDataStore server, paired as a publishing client, publishes the master data models it manages, with their rules and approval, to the group; an
/// administrator may too. <c>PUT /api/v1/entity-models/{name}</c> (<c>{"model": "&lt;model/1 yaml&gt;", "data_source": "&lt;name&gt;", "expected_version": n}</c>) is refused unless the model loads, carries the
/// name of the call, and its data source is one of this server's (for a publishing client, one it made); <c>GET</c> lists and reads, <c>DELETE</c> stops serving it. Every instance of the group serves what is published
/// (<see cref="PublishedEntityModels"/>). The structure stays the store's server's to change: it publishes the model its tables have.
/// </summary>
public sealed partial class GatewayEntityModels(PublishedEntityModels models, Func<GatewayOptions> options, Func<string, string?> ownerOfDataSource, IAuditLog audit, TimeProvider? clock = null, DataSourceAdoptions? adoptions = null, Func<RuledTables>? ruled = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _publishing = new(1, 1);

    private sealed record PublishRequest(string? Model, [property: JsonPropertyName("data_source")] string? DataSource, [property: JsonPropertyName("expected_version")] int? ExpectedVersion, string? Adopt = null);

    public async Task<ManagementResult> HandleAsync(string method, string[] rest, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (rest.Length == 0) return method == "GET" ? ManagementResult.Ok(List(caller)) : ManagementResult.Problem(405, "Entity models are listed with GET.");
        var name = Uri.UnescapeDataString(rest[0]);
        if (!NamePattern().IsMatch(name)) return ManagementResult.Problem(400, "A model name is lower-case letters, digits, '-' and '_', starting with a letter or digit (up to 64).");
        if (rest.Length != 1) return ManagementResult.Problem(404, "There is no such call.");
        return method switch
        {
            "GET" => Read(name, caller),
            "PUT" => await PublishAsync(name, body, caller, cancellationToken),
            "DELETE" => await RetireAsync(name, caller, cancellationToken),
            _ => ManagementResult.Problem(405, "An entity model is read with GET, published with PUT, removed with DELETE."),
        };
    }

    private static bool IsAdministrator(ManagementCaller caller) => caller.Holds(ServerRole.Admin);

    internal static string? Client(ManagementCaller caller) => IsAdministrator(caller) ? null : OwnerScope.Stamp(caller.Token!);

    internal ManagementResult? Scope(ManagementCaller caller, ObjectAction action, string name)
    {
        if (IsAdministrator(caller)) return null;
        var owner = models.Current(name) is { } current ? PublishedEntityModels.Parse(current).ManagedBy : null;
        var decision = OwnerScope.Authorize(caller.Token!, action, action == ObjectAction.Create ? null : new ManagedObject(owner));
        return decision.Allowed ? null : ManagementResult.Problem(403, decision.Reason ?? "This client may not do this to that model.");
    }

    private JsonObject List(ManagementCaller caller)
    {
        var client = Client(caller);
        var items = new JsonArray();
        foreach (var version in models.Published())
        {
            var content = PublishedEntityModels.Parse(version);
            if (client is not null && content.ManagedBy != client) continue;
            var served = models.Find(version.Name);
            items.Add(new JsonObject
            {
                ["name"] = version.Name, ["current_version"] = version.Version, ["data_source"] = content.DataSource, ["managed_by"] = content.ManagedBy,
                // what this instance serves of it, which lags the group's by a poll at most, and a version it could not load
                ["served_version"] = served?.Version, ["failed_version"] = models.Failed(version.Name)?.Version, ["failure"] = models.Failed(version.Name)?.Reason,
                ["entities"] = served is null ? null : new JsonArray([.. served.Model.Entities.Select(e => (JsonNode)e.Name)]),
            });
        }
        return new JsonObject { ["models"] = items };
    }

    private ManagementResult Read(string name, ManagementCaller caller)
    {
        if (models.Current(name) is not { } current) return ManagementResult.Problem(404, $"There is no published entity model '{name}'.");
        if (Scope(caller, ObjectAction.Change, name) is { } refused) return refused;
        var content = PublishedEntityModels.Parse(current);
        return ManagementResult.Ok(new JsonObject
        {
            ["name"] = name, ["current_version"] = current.Version, ["model"] = content.Model, ["data_source"] = content.DataSource, ["managed_by"] = content.ManagedBy,
            ["versions"] = new JsonArray([.. models.Versions(name).Select(v => (JsonNode)new JsonObject { ["version"] = v.Version, ["published_by"] = v.PublishedBy, ["published_at"] = v.PublishedAt })]),
        });
    }

    private async Task<ManagementResult> PublishAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        PublishRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<PublishRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request?.Model is not { Length: > 0 } text || string.IsNullOrWhiteSpace(request.DataSource))
            return ManagementResult.Problem(400, "The body is a JSON object: {\"model\": \"<the model/1 yaml>\", \"data_source\": \"<the data source its tables are on>\", \"expected_version\": <number, or absent to create>}.");

        var created = models.Current(name) is null;
        if (Scope(caller, created ? ObjectAction.Create : ObjectAction.Change, name) is { } refused) { Record(caller, "entity-model publish", name, "refused"); return refused; }

        await _publishing.WaitAsync(cancellationToken);
        try
        {
            if (models.Current(name)?.Version != request.ExpectedVersion)
            {
                Record(caller, "entity-model publish", name, "stale");
                return new ManagementResult(409, new JsonObject { ["error"] = "The model changed since it was read; read it again and publish on that.", ["current_version"] = models.Current(name)?.Version });
            }
            var loaded = DataModelLoader.Load(text, name + ".yaml");
            if (!loaded.Ok)
                return Refused(caller, name, "The model does not load, so nothing was published.", new JsonArray([.. loaded.Diagnostics.Select(d => (JsonNode)d.ToString())]));
            if (loaded.Model!.Name != name)
                return Refused(caller, name, $"The model is named '{loaded.Model.Name}', and the call names '{name}'; they are the same name.");
            var dataSource = request.DataSource!;
            if (!options().DataSources.ContainsKey(dataSource))
                return Refused(caller, name, $"'{dataSource}' is not a data source of this server; publish the data source the model's tables are on first.");
            var client = Client(caller);
            var adopting = false;
            if (client is not null && ownerOfDataSource(dataSource) != client)
            {
                // plan 33 SC21f: a data source made by hand (nothing manages it) may carry this client's model when the request says so by name, and the Gateway records who, when and which; one another client made never
                var owner = ownerOfDataSource(dataSource);
                var madeBy = string.IsNullOrEmpty(owner) ? "by hand or by an administrator" : "by another server client";
                if (!string.IsNullOrEmpty(owner))
                    return Refused(caller, name, $"'{dataSource}' is not a data source this client made: it was made {madeBy}, and a publishing client does not take over another client's data source; ask an administrator to hand it over.");
                if (!string.Equals(request.Adopt, dataSource, StringComparison.Ordinal))
                {
                    var hint = new JsonObject { ["data_source"] = dataSource, ["made_by"] = "hand", ["publish_adopt"] = true, ["field"] = "adopt" };
                    return Refused(caller, name, $"'{dataSource}' is not a data source this client made: it was made {madeBy}. Publish again with \"adopt\": \"{dataSource}\" to put the model on it; the Gateway records who adopted it and when, in its audit and in the data source's metadata.",
                        extra: hint);
                }
                if (OwnerScope.AuthorizePublishingOver(caller.Token!, new ManagedObject(null)) is { Allowed: false } denied)
                    return Refused(caller, name, denied.Reason ?? $"This client may not put a model on '{dataSource}'.");
                adopting = true;
            }

            var was = models.Current(name)?.Version;
            // a publishing client's model is its own; one an administrator changes keeps the owner it had
            var managedBy = client ?? (models.Current(name) is { } held ? PublishedEntityModels.Parse(held).ManagedBy : null);
            DefinitionVersion version;
            try { version = models.Publish(name, text, dataSource, managedBy, Who(caller), loaded.Model.SchemaVersion); }
            catch (PublishRefusedException groupRefused)
            {
                // the group cannot take this format (24a UC01): a live instance of it does not read it; the message names the instance
                Record(caller, "entity-model publish", name, "refused");
                return new ManagementResult(409, new JsonObject { ["error"] = groupRefused.Message, ["valid"] = true });
            }
            var unchanged = was == version.Version;
            Record(caller, "entity-model publish", name, created ? "created" : unchanged ? "unchanged" : "changed");
            if (adopting)
            {
                var at = _clock.GetUtcNow();
                adoptions?.Record(dataSource, client!, Who(caller), name, "entity-model", at);
                Record(caller, "data-source adopt", dataSource, $"adopted by {client} ({Who(caller)}) for the model {name}");
            }
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["version"] = version.Version, ["created"] = created, ["unchanged"] = unchanged, ["adopted"] = adopting ? dataSource : null, ["warnings"] = AccessPublishNotes.ForEntity(loaded.Model, dataSource, ruled?.Invoke()) });
        }
        finally { _publishing.Release(); }
    }

    private async Task<ManagementResult> RetireAsync(string name, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (models.Current(name) is null) return ManagementResult.Problem(404, $"There is no published entity model '{name}'.");
        if (Scope(caller, ObjectAction.Delete, name) is { } refused) { Record(caller, "entity-model remove", name, "refused"); return refused; }
        await _publishing.WaitAsync(cancellationToken);
        try { models.Retire(name, Who(caller)); }
        finally { _publishing.Release(); }
        Record(caller, "entity-model remove", name, "removed");
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true });
    }

    internal string Who(ManagementCaller caller) { var entry = caller.Audit("", null, "", _clock.GetUtcNow()); return entry.UserName ?? entry.UserId; }

    internal ManagementResult Refused(ManagementCaller caller, string name, string error, JsonArray? diagnostics = null, JsonObject? extra = null)
    {
        Record(caller, "entity-model publish", name, "invalid");
        var body = new JsonObject { ["error"] = error, ["valid"] = false };
        if (diagnostics is not null) body["diagnostics"] = diagnostics;
        if (extra is not null) body["adopt"] = extra;
        return new ManagementResult(400, body);
    }

    internal void Record(ManagementCaller caller, string command, string target, string outcome) => audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();
}
