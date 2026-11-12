using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The semantic model's side of the shared publish API (plan 30 PB02): the checks and the effects the per-kind route <c>PUT /api/v1/semantic-models/{name}</c> has, run for <c>PUT /api/v1/definitions/semantic-model/{name}</c>. The manifest is
/// <see cref="PublishedSemanticModels.Content"/> (<c>catalog</c>, <c>managedBy</c>, <c>sources</c>, <c>ssas</c>); the request body may carry <c>adopt</c> as the route's does. The refusals are the route's, in its words and under its statuses.
/// </summary>
public sealed partial class GatewaySemanticModels
{
    private readonly ConditionalWeakTable<PublishHookContext, Adoption> _asked = new();

    private static ManagementCaller? Of(DefinitionsCaller caller) => caller.Context as ManagementCaller;

    private static DefinitionsRefusalException Refusal(ManagementResult result) =>
        new(result.Status, (result.Body as JsonObject)?["error"]?.GetValue<string>() ?? "This call is refused.") { Body = result.Body as JsonObject };

    /// <summary>Everything the route checks before it stores a version, for a publish through the shared API; throws <see cref="DefinitionsRefusalException"/> (and records the refusal in the audit as the route does).</summary>
    internal async Task CheckSharedPublishAsync(PublishHookContext context, CancellationToken ct)
    {
        if (Of(context.Caller) is not { } caller) return;   // a host that does not say who is calling (a test of the library) has no owner rules to apply
        var name = context.Name;
        if (!PublishedSemanticModels.ValidName(name)) throw new DefinitionsRefusalException(400, "A model name is a letter followed by letters, digits, '.', '_' or '-' (up to 64).");
        if (name != name.ToLowerInvariant())
            throw new DefinitionsRefusalException(400, $"A model is published under its lower-case name ('{name.ToLowerInvariant()}'): model names do not differ by case, and the group keeps one entry for each.");
        if (PublishedSemanticModels.ContentOf(context.Manifest) is not { Catalog.Length: > 0 } content)
            throw new DefinitionsRefusalException(400, "The manifest is a JSON object: {\"catalog\": \"<the catalog's yaml>\", \"managedBy\": \"<the publishing client>\", \"sources\": {\"<catalog source>\": \"<data source>\"}, \"ssas\": {\"<catalog source>\": \"secret:...\"}}.");

        var created = store.Current(name) is null;
        if (Scope(caller, created ? ObjectAction.Create : ObjectAction.Change, name) is { } scoped) { Record(caller, "semantic-model publish", name, "refused"); throw Refusal(scoped); }
        // a publishing client's model is its own: the manifest is signed, so the stamp is the client's to write, and it is checked here
        var client = Client(caller);
        if (client is not null && content.ManagedBy != client)
        {
            Record(caller, "semantic-model publish", name, "refused");
            throw new DefinitionsRefusalException(403, $"The manifest names {(string.IsNullOrEmpty(content.ManagedBy) ? "no client" : $"'{content.ManagedBy}'")} in managedBy; a publishing client's model carries its own client id ('{client}').");
        }
        if (created && models.Find(name) is not null)
            throw new DefinitionsRefusalException(409, $"A model named '{name}' is set up in server.yaml; a published model needs a name of its own.");

        var sources = content.Sources ?? [];
        var ssas = content.Ssas ?? [];
        Catalog parsed;
        try { parsed = CatalogLoader.LoadText(content.Catalog); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Invalid(caller, name, "The catalog does not load, so nothing was published.", ex.Message); }
        if (!string.Equals(parsed.Name, name, StringComparison.OrdinalIgnoreCase))
            throw Invalid(caller, name, "The catalog is named '" + parsed.Name + "', and the call names '" + name + "'; they are the same name.");

        var adoption = new Adoption(context.Body["adopt"]);
        var problem = await CheckSourcesAsync(parsed, sources, ssas, caller, adoption, ct).ConfigureAwait(false);
        if (problem is not null) throw Invalid(caller, name, problem, extra: adoption.Hint);
        _asked.AddOrUpdate(context, adoption);
    }

    private DefinitionsRefusalException Invalid(ManagementCaller caller, string name, string error, string? detail = null, JsonObject? extra = null) => Refusal(Refused(caller, name, error, detail, extra));

    /// <summary>What the route does after it stored a version: the model is served here at once (the other instances serve it when they next look), the audit says what came of it and each data source adopted is recorded.</summary>
    internal void AfterSharedPublish(PublishHookContext context, PublishResult result, JsonObject response)
    {
        if (Of(context.Caller) is not { } caller) return;
        var name = context.Name;
        if (result.Mode == PublishMode.Serve) store.ServeNow(name);
        var created = result.Changed && result.Version.Version == 1;
        Record(caller, "semantic-model publish", name, created ? "created" : result.Changed ? "changed" : "unchanged");
        response["created"] = created;
        response["unchanged"] = !result.Changed;
        if (!_asked.TryGetValue(context, out var adoption) || adoption.Adopted.Count == 0) return;
        foreach (var dataSource in adoption.Adopted)
        {
            adoptions?.Record(dataSource, Client(caller)!, Who(caller), name, "semantic-model", _clock.GetUtcNow());
            Record(caller, "data-source adopt", dataSource, $"adopted by {Client(caller)} ({Who(caller)}) for the model {name}");
        }
        response["adopted"] = new JsonArray([.. adoption.Adopted.Select(a => (JsonNode?)a)]);
    }

    /// <summary>The read rules of the route: a publishing client reads only its own models.</summary>
    internal void CheckSharedRead(string name, DefinitionsCaller caller)
    {
        if (Of(caller) is { } called && Scope(called, ObjectAction.Change, name) is { } refused && store.Current(name.ToLowerInvariant()) is not null) throw Refusal(refused);
    }
}

/// <summary>The hook of the <c>semantic-model</c> kind on the Gateway's shared publish API: <see cref="GatewaySemanticModels"/>' checks and effects, and the owner a model's content names.</summary>
public sealed class SemanticModelKindHook(GatewaySemanticModels models, PublishedSemanticModels store, SignaturePolicy? policy = null) : IDefinitionKindHook
{
    public string Kind => PublishedSemanticModels.DefinitionKind;

    // while the per-kind routes publish unsigned versions, only a group an administrator set to `required` refuses an unsigned one (as its instances do, plan 30 PB03)
    public bool? SignaturesRequired => policy?.RequiredByAdministrator(Kind);

    public Task BeforePublishAsync(PublishHookContext context, CancellationToken ct) => models.CheckSharedPublishAsync(context, ct);

    public void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response) => models.AfterSharedPublish(context, result, response);

    public string? OwnerOf(string name) => store.OwnerOf(name.ToLowerInvariant());

    public void BeforeRead(string name, DefinitionsCaller caller) => models.CheckSharedRead(name, caller);
}
