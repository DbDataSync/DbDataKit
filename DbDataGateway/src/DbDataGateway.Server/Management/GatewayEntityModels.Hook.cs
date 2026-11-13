using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.DataModel;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The entity model's side of the shared publish API (plan 30 PB02): what <c>PUT /api/v1/entity-models/{name}</c> checks and does, for <c>PUT /api/v1/definitions/entity-model/{name}</c>. The manifest is <see cref="PublishedEntityModels.Content"/>
/// (<c>model</c>, <c>dataSource</c>, <c>managedBy</c>); the body may carry <c>adopt</c> (the name of a data source made by hand the model goes on). The owner rules, the model that must load and carry the name, the data source that must be one of this
/// server's (and, for a publishing client, one it made or adopts), the audit and the adoption record are the route's, in its words.
/// </summary>
public sealed partial class GatewayEntityModels
{
    private readonly ConditionalWeakTable<PublishHookContext, string> _adopting = new();
    private readonly ConditionalWeakTable<PublishHookContext, string> _slotUsed = new();

    private static ManagementCaller? Of(DefinitionsCaller caller) => caller.Context as ManagementCaller;

    private static DefinitionsRefusalException Refusal(ManagementResult result) =>
        new(result.Status, (result.Body as JsonObject)?["error"]?.GetValue<string>() ?? "This call is refused.") { Body = result.Body as JsonObject };

    internal string? OwnerOfShared(string name) => models.Current(name) is { } current ? PublishedEntityModels.Parse(current).ManagedBy : null;

    private static PublishedEntityModels.Content? ContentOf(string manifest)
    {
        try { return JsonSerializer.Deserialize<PublishedEntityModels.Content>(manifest, Json); }
        catch (JsonException) { return null; }
    }

    internal void CheckSharedPublish(PublishHookContext context)
    {
        if (Of(context.Caller) is not { } caller) return;
        var name = context.Name;
        if (!NamePattern().IsMatch(name)) throw new DefinitionsRefusalException(400, "A model name is lower-case letters, digits, '-' and '_', starting with a letter or digit (up to 64).");
        if (ContentOf(context.Manifest) is not { Model.Length: > 0 } content || string.IsNullOrWhiteSpace(content.DataSource))
            throw new DefinitionsRefusalException(400, "The manifest is a JSON object: {\"model\": \"<the model/1 yaml>\", \"dataSource\": \"<the data source its tables are on>\", \"managedBy\": \"<the publishing client>\"}.");

        var created = models.Current(name) is null;
        var client = Client(caller);
        if (Scope(caller, created ? ObjectAction.Create : ObjectAction.Change, name) is { } scoped) { Record(caller, "entity-model publish", name, "refused"); throw Refusal(scoped); }
        // a publishing client's model is its own: the manifest is signed, so the stamp is the client's to write
        if (client is not null && content.ManagedBy != client)
        {
            Record(caller, "entity-model publish", name, "refused");
            throw new DefinitionsRefusalException(403, $"The manifest names {(string.IsNullOrEmpty(content.ManagedBy) ? "no client" : $"'{content.ManagedBy}'")} in managedBy; a publishing client's model carries its own client id ('{client}').");
        }

        var loaded = DataModelLoader.Load(content.Model, name + ".yaml");
        if (!loaded.Ok) throw Invalid(caller, name, "The model does not load, so nothing was published.", new JsonArray([.. loaded.Diagnostics.Select(d => (JsonNode)d.ToString())]));
        if (loaded.Model!.Name != name) throw Invalid(caller, name, $"The model is named '{loaded.Model.Name}', and the call names '{name}'; they are the same name.");
        var dataSource = content.DataSource;
        if (!options().DataSources.ContainsKey(dataSource))
            throw Invalid(caller, name, $"'{dataSource}' is not a data source of this server; publish the data source the model's tables are on first.");
        ServingSlots.CheckDeclared(content.Slots, [dataSource]);
        if (client is null || ownerOfDataSource(dataSource) == client) return;

        // plan 33 SW01: a data source made by hand that the model names as a slot is the group's to allow, by binding the slot (an administrator's act, recorded); the registry refuses the version naming the slot while it is unbound
        if (string.IsNullOrEmpty(ownerOfDataSource(dataSource)) && ServingSlots.Declared(content.Slots, dataSource))
        {
            if (ServingSlots.Bound(content.Slots, context, dataSource)) _slotUsed.AddOrUpdate(context, dataSource);
            return;
        }

        // plan 33 SC21f: a data source made by hand may carry this client's model when the request says so by name, and the Gateway records who, when and which; one another client made never
        var owner = ownerOfDataSource(dataSource);
        var madeBy = string.IsNullOrEmpty(owner) ? "by hand or by an administrator" : "by another server client";
        if (!string.IsNullOrEmpty(owner))
            throw Invalid(caller, name, $"'{dataSource}' is not a data source this client made: it was made {madeBy}, and a publishing client does not take over another client's data source; ask an administrator to hand it over.");
        if (context.Body["adopt"] is not JsonValue asked || !asked.TryGetValue<string>(out var adopt) || !string.Equals(adopt, dataSource, StringComparison.Ordinal))
            throw Invalid(caller, name, $"'{dataSource}' is not a data source this client made: it was made {madeBy}. Publish again with \"adopt\": \"{dataSource}\" to put the model on it; the Gateway records who adopted it and when, in its audit and in the data source's metadata.",
                extra: new JsonObject { ["data_source"] = dataSource, ["made_by"] = "hand", ["publish_adopt"] = true, ["field"] = "adopt" });
        if (OwnerScope.AuthorizePublishingOver(caller.Token!, new ManagedObject(null)) is { Allowed: false } denied)
            throw Invalid(caller, name, denied.Reason ?? $"This client may not put a model on '{dataSource}'.");
        _adopting.AddOrUpdate(context, dataSource);
    }

    private DefinitionsRefusalException Invalid(ManagementCaller caller, string name, string error, JsonArray? diagnostics = null, JsonObject? extra = null) => Refusal(Refused(caller, name, error, diagnostics, extra));

    internal void AfterSharedPublish(PublishHookContext context, PublishResult result, JsonObject response)
    {
        if (Of(context.Caller) is not { } caller) return;
        var name = context.Name;
        if (result.Mode == PublishMode.Serve) models.ServeNow(name);
        var created = result.Changed && result.Version.Version == 1;
        Record(caller, "entity-model publish", name, created ? "created" : result.Changed ? "changed" : "unchanged");
        response["created"] = created;
        response["unchanged"] = !result.Changed;
        if (ContentOf(context.Manifest) is { Model.Length: > 0 } content && DataModelLoader.Load(content.Model, name + ".yaml") is { Ok: true, Model: { } model } && AccessPublishNotes.ForEntity(model, content.DataSource, ruled?.Invoke()) is { } warnings) response["warnings"] = warnings;
        if (_slotUsed.TryGetValue(context, out var used))
        {
            adoptions?.Record(used, Client(caller)!, Who(caller), name, "entity-model (slot)", _clock.GetUtcNow());
            Record(caller, "data-source use", used, ServingSlots.UseNote(Client(caller)!, Who(caller), name));
            response["slots_used"] = new JsonArray((JsonNode?)used);
        }
        if (!_adopting.TryGetValue(context, out var dataSource)) return;
        adoptions?.Record(dataSource, Client(caller)!, Who(caller), name, "entity-model", _clock.GetUtcNow());
        Record(caller, "data-source adopt", dataSource, $"adopted by {Client(caller)} ({Who(caller)}) for the model {name}");
        response["adopted"] = dataSource;
    }

    /// <summary>The checks around binding the slot of a model (plan 33 SW01): the data source of the same name, one of this Gateway's, made by the binding client or bound by an administrator.</summary>
    internal void CheckSharedBind(BindingHookContext context) => ServingSlots.CheckBind(context, options, ownerOfDataSource);

    internal void CheckSharedRead(string name, DefinitionsCaller caller)
    {
        if (Of(caller) is { } called && models.Current(name) is not null && Scope(called, ObjectAction.Change, name) is { } refused) throw Refusal(refused);
    }
}

/// <summary>The hook of the <c>entity-model</c> kind on the Gateway's shared publish API: <see cref="GatewayEntityModels"/>' checks and effects, and the owner the model names.</summary>
public sealed class EntityModelKindHook(GatewayEntityModels entityModels, SignaturePolicy? policy = null) : IDefinitionKindHook
{
    public string Kind => PublishedEntityModels.DefinitionKind;

    // while the per-kind routes publish unsigned versions, only a group an administrator set to `required` refuses an unsigned one (as its instances do, plan 30 PB03)
    public bool? SignaturesRequired => policy?.RequiredByAdministrator(Kind);

    public void BeforePublish(PublishHookContext context) => entityModels.CheckSharedPublish(context);

    public void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response) => entityModels.AfterSharedPublish(context, result, response);

    public void BeforeBind(BindingHookContext context) => entityModels.CheckSharedBind(context);

    public string? OwnerOf(string name) => entityModels.OwnerOfShared(name);

    public void BeforeRead(string name, DefinitionsCaller caller) => entityModels.CheckSharedRead(name, caller);
}
