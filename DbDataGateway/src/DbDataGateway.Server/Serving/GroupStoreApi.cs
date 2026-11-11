using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.Store;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;

namespace DbDataGateway.Server.Serving;

/// <summary>An answer of the group's store: the status and the JSON body a DbDataStore server would give.</summary>
public sealed record StoreAnswer(int Status, JsonNode Body)
{
    public static StoreAnswer Ok(JsonNode body) => new(200, body);

    public static StoreAnswer Error(int status, string message) => new(status, new JsonObject { ["error"] = message });
}

/// <summary>
/// The group's master data, read and written through the entity models published to it (plan 20 O08a), with no transport of its own: the <c>store</c> module answers it over its listener (<see cref="Hosting.GatewayStoreModule"/>),
/// and the apps host answers an app's data calls with it in the same process (O08c), so both follow the same rules. Every call names who makes it (<see cref="StoreCaller"/>): viewer reads, author writes, a change request
/// when the model asks for approval of the caller's writes, and its decision as the model says. The data source the model was published with (O04b) is opened for each call.
/// </summary>
public sealed class GroupStoreApi(Func<GatewayOptions> options, SecretsRuntime secrets, PublishedEntityModels models, GroupChangeRequests requests)
{
    /// <summary>The model's default copy is the one the group serves; models with copies of their own (instances) are not served by the group yet.</summary>
    public const string DefaultCopy = "default";

    private static StoreAnswer? Needs(StoreCaller caller, ServerRole role) =>
        caller.Holds(role) || caller.Holds(ServerRole.Admin) ? null : StoreAnswer.Error(403, $"This needs the {role.ToString().ToLowerInvariant()} role on this group.");

    private (ServedEntityModel? Model, StoreAnswer? Problem) Served(string name, string? entity = null)
    {
        if (models.Find(name) is not { } served) return (null, StoreAnswer.Error(404, $"No entity model '{name}' is published to this group."));
        if (entity is not null && served.Model.Entity(entity) is null) return (null, StoreAnswer.Error(404, $"The model '{name}' has no entity '{entity}'."));
        return (served, null);
    }

    public StoreAnswer List(StoreCaller caller)
    {
        if (Needs(caller, ServerRole.Viewer) is { } denied) return denied;
        return StoreAnswer.Ok(new JsonObject
        {
            ["models"] = new JsonArray([.. models.All.OrderBy(m => m.Model.Name, StringComparer.Ordinal).Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Model.Name, ["title"] = m.Model.Title, ["version"] = m.Version, ["entities"] = new JsonArray([.. m.Model.Entities.Select(e => (JsonNode)e.Name)]),
            })]),
        });
    }

    /// <summary>
    /// The model's schema (<see cref="ModelSchemaJson"/>), for a viewer. <paramref name="mayRead"/>, when given (an app's scope), keeps only the entities it says yes to; a model with none of them is refused, so an app never learns
    /// the shape of an entity its manifest does not list.
    /// </summary>
    public StoreAnswer Schema(StoreCaller caller, string name, Func<string, bool>? mayRead = null)
    {
        if (Needs(caller, ServerRole.Viewer) is { } denied) return denied;
        var (served, missing) = Served(name);
        if (missing is not null) return missing;
        var schema = ModelSchemaJson.Of(served!.Model);
        if (mayRead is not null)
        {
            var kept = ((JsonArray)schema["entities"]!).OfType<JsonObject>().Where(e => mayRead((string)e["name"]!)).Select(e => (JsonNode)JsonNode.Parse(e.ToJsonString())!).ToArray();
            if (kept.Length == 0) return StoreAnswer.Error(403, $"This app may not read any entity of '{name}': its manifest does not list one.");
            schema["entities"] = new JsonArray(kept);
        }
        return StoreAnswer.Ok(schema);
    }

    /// <summary>One row by its key (<c>key.&lt;attribute&gt;</c>); <c>as_of</c> the version that held then; <c>history=true</c> every version.</summary>
    public async Task<StoreAnswer> RowAsync(StoreCaller caller, string name, string entity, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (Needs(caller, ServerRole.Viewer) is { } denied) return denied;
        var (served, missing) = Served(name, entity);
        if (missing is not null) return missing;
        var key = query.Where(q => q.Key.StartsWith("key.", StringComparison.Ordinal)).ToDictionary(q => q.Key[4..], q => (object?)q.Value);
        return await WithWriter(served!, async writer =>
        {
            if (query.GetValueOrDefault("history") == "true")
                return StoreAnswer.Ok(new JsonObject { ["versions"] = new JsonArray([.. (await writer.HistoryAsync(entity, key, cancellationToken)).Select(r => (JsonNode)RowJson.Row(r))]) });
            StoredRow? row;
            if (query.GetValueOrDefault("as_of") is { Length: > 0 } asOf)
            {
                if (!DateTimeOffset.TryParse(asOf, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment)) return StoreAnswer.Error(400, $"'{asOf}' is not a time.");
                row = await writer.GetAsOfAsync(entity, key, moment, cancellationToken);
            }
            else row = await writer.GetAsync(entity, key, cancellationToken);
            return row is null ? StoreAnswer.Error(404, $"`{entity}` has no such row.") : StoreAnswer.Ok(RowJson.Row(row));
        }, cancellationToken);
    }

    /// <summary>A page of rows in key order: <c>limit</c> (100) and <c>offset</c> (0).</summary>
    public async Task<StoreAnswer> RowsAsync(StoreCaller caller, string name, string entity, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (Needs(caller, ServerRole.Viewer) is { } denied) return denied;
        var (served, missing) = Served(name, entity);
        if (missing is not null) return missing;
        if (!int.TryParse(query.GetValueOrDefault("limit") is { Length: > 0 } l ? l : "100", out var limit) || !int.TryParse(query.GetValueOrDefault("offset") is { Length: > 0 } o ? o : "0", out var offset))
            return StoreAnswer.Error(400, "limit and offset are whole numbers.");
        return await WithWriter(served!, async writer =>
            StoreAnswer.Ok(new JsonObject { ["rows"] = new JsonArray([.. (await writer.ListAsync(entity, limit, offset, cancellationToken)).Select(r => (JsonNode)RowJson.Row(r))]), ["limit"] = limit, ["offset"] = offset }),
            cancellationToken);
    }

    /// <summary>
    /// <c>{"changes": [...], "description": "..."}</c>: written at once, or kept as a change request (202) when the model asks for approval of this caller's writes. <paramref name="mayWrite"/>, when given, is asked about each
    /// change's entity first (an app's scope); a change outside it refuses the whole batch.
    /// </summary>
    public async Task<StoreAnswer> ChangesAsync(StoreCaller caller, string name, byte[] body, Func<string, bool>? mayWrite, CancellationToken cancellationToken, Func<string, bool>? mayApply = null)
    {
        if (Needs(caller, ServerRole.Author) is { } denied) return denied;
        var (served, missing) = Served(name);
        if (missing is not null) return missing;
        if (Parse(body) is not { Ok: true, Node: var node }) return StoreAnswer.Error(400, "The body is not JSON.");
        if (node?["changes"] is not JsonArray list || list.Count == 0) return StoreAnswer.Error(400, "The body needs a non-empty \"changes\" array.");
        var (changes, bad) = RowJson.Changes(list);
        if (bad is not null) return StoreAnswer.Error(400, bad);
        if (mayWrite is not null && changes!.Select(c => c.Entity).FirstOrDefault(e => !mayWrite(e)) is { } outside)
            return StoreAnswer.Error(403, $"This app may not write `{outside}`: its manifest does not give it that entity to write.");
        // an app that may only propose (the manifest's `change request`) never applies: its batch waits for approval whatever the model says, even if the model would apply a write at once
        if (Waits(ChangeApproval.Needs(served!.Model, caller.IsAgent), changes!.Select(c => c.Entity), mayApply))
        {
            var request = requests.Propose(name, caller, (string?)node["description"], (JsonArray)JsonNode.Parse(list.ToJsonString())!);
            return new StoreAnswer(202, request.ToJson(withChanges: false));
        }
        return await WithWriter(served, async writer =>
        {
            await writer.ApplyAsync(changes!, new WriteActor(caller.UserId, caller.AuditName), cancellationToken);
            return StoreAnswer.Ok(new JsonObject { ["applied"] = changes!.Count });
        }, cancellationToken);
    }

    /// <summary>Whether a batch waits for approval: the model asks for it, or the caller may apply only some of its entities (an app whose manifest gives `change request` and not `write` for one of them).</summary>
    internal static bool Waits(bool modelNeedsApproval, IEnumerable<string> entities, Func<string, bool>? mayApply) =>
        modelNeedsApproval || (mayApply is not null && entities.Any(e => !mayApply(e)));

    public StoreAnswer Requests(StoreCaller caller, string name, string? state)
    {
        if (Needs(caller, ServerRole.Viewer) is { } denied) return denied;
        if (state is { Length: > 0 } && state is not (GroupChangeRequest.Pending or GroupChangeRequest.Approved or GroupChangeRequest.Rejected)) return StoreAnswer.Error(400, "state is pending, approved or rejected.");
        return StoreAnswer.Ok(new JsonObject { ["requests"] = new JsonArray([.. requests.List(name, state is { Length: > 0 } ? state : null).Select(r => (JsonNode)r.ToJson(withChanges: false))]) });
    }

    public StoreAnswer Request(StoreCaller caller, string name, string id)
    {
        if (Needs(caller, ServerRole.Viewer) is { } denied) return denied;
        return requests.Find(name, id) is { } request ? StoreAnswer.Ok(request.ToJson()) : StoreAnswer.Error(404, "There is no such change request.");
    }

    /// <summary>Approves (the proposed changes are applied unchanged) or rejects a pending change request, with an optional <c>{"note": "..."}</c>; who may decide is the model's approval.</summary>
    public async Task<StoreAnswer> DecideAsync(StoreCaller caller, string name, string id, byte[] body, bool approve, CancellationToken cancellationToken)
    {
        var (served, missing) = Served(name);
        if (missing is not null) return missing;
        if (Needs(caller, ChangeApproval.AdministratorDecides(served!.Model) ? ServerRole.Admin : ServerRole.Author) is { } denied) return denied;
        if (caller.IsAgent) return StoreAnswer.Error(403, "An agent cannot decide a change request.");
        if (requests.Find(name, id) is not { } request) return StoreAnswer.Error(404, "There is no such change request.");
        if (request.State != GroupChangeRequest.Pending) return StoreAnswer.Error(409, $"This change request was already {request.State}.");
        if (Parse(body) is not { Ok: true, Node: var node }) return StoreAnswer.Error(400, "The body is not JSON.");
        var note = (string?)node?["note"];
        if (!approve)
            return requests.Decide(request, GroupChangeRequest.Rejected, caller, note) ? StoreAnswer.Ok(requests.Find(name, id)!.ToJson(withChanges: false))
                : StoreAnswer.Error(409, "This change request was decided meanwhile.");
        var (changes, bad) = RowJson.Changes(request.Changes);
        if (bad is not null) return StoreAnswer.Error(400, bad);
        return await WithWriter(served, async writer =>
        {
            await writer.ApplyAsync(changes!, new WriteActor(caller.UserId, $"{caller.Name ?? caller.UserId}, approving {request.ProposedByName ?? request.ProposedBy}'s change request {request.Id}"), cancellationToken);
            requests.Decide(request, GroupChangeRequest.Approved, caller, note);
            return StoreAnswer.Ok(requests.Find(name, id)!.ToJson(withChanges: false));
        }, cancellationToken);
    }

    /// <summary>An empty body is no JSON at all (<c>Node</c> null, still <c>Ok</c>); anything else must parse.</summary>
    private static (bool Ok, JsonNode? Node) Parse(byte[] body)
    {
        if (body.Length == 0) return (true, null);
        try { return (true, JsonNode.Parse(body)); }
        catch (JsonException) { return (false, null); }
    }

    /// <summary>Opens the model's data source (published with it, O04b) and runs <paramref name="work"/> with a writer on the model's tables; the writer's refusals become the answers a DbDataStore server gives.</summary>
    private async Task<StoreAnswer> WithWriter(ServedEntityModel served, Func<ModelWriter, Task<StoreAnswer>> work, CancellationToken cancellationToken)
    {
        var current = options();
        if (!current.DataSources.TryGetValue(served.DataSource, out var source))
            return StoreAnswer.Error(502, $"The data source '{served.DataSource}' the model's tables are on is not served here.");
        var engine = source.Provider.ToLowerInvariant() switch { "sqlserver" => "sqlserver", "postgres" => "postgres", _ => null };
        if (engine is null) return StoreAnswer.Error(502, $"The data source '{served.DataSource}' is {source.Provider}; a model's tables are on SQL Server or PostgreSQL.");
        string text;
        try { text = secrets.ResolveConnectionString($"Gateway:DataSources:{served.DataSource}:ConnectionString", source.ConnectionString, source.Provider) ?? source.ConnectionString; }
        catch (Exception ex) when (ex is SecretNotFoundException or InvalidOperationException)
        { return StoreAnswer.Error(502, $"The login of the data source '{served.DataSource}' cannot be found here."); }
        try
        {
            await using DbConnection connection = engine == "sqlserver" ? new Microsoft.Data.SqlClient.SqlConnection(text) : new Npgsql.NpgsqlConnection(text);
            await connection.OpenAsync(cancellationToken);
            return await work(new ModelWriter(served.Model, connection, engine, schema: served.Model.Name));
        }
        catch (WriteRefusedException refused) { return new StoreAnswer(422, RowJson.Refusal(refused)); }
        catch (VersionConflictException conflict) { return new StoreAnswer(409, new JsonObject { ["error"] = conflict.Message, ["expected_version"] = conflict.Expected, ["current_version"] = conflict.Current }); }
        catch (RowNotFoundException ex) { return StoreAnswer.Error(404, ex.Message); }
        catch (Exception ex) when (ex is StoreNotSupportedException or DbDataKit.DataModel.Storage.StorageException) { return StoreAnswer.Error(422, ex.Message); }
        catch (DbException) { return StoreAnswer.Error(502, $"The database of the data source '{served.DataSource}' could not be reached."); }
    }
}
