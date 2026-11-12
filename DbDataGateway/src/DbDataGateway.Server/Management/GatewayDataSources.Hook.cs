using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The data source's side of the shared publish API (plan 30 PB02): what the per-kind route <c>PUT /api/v1/data-sources/{name}</c> checks and does, for <c>PUT /api/v1/definitions/data-source/{name}</c>. The manifest is the whole <c>connection/2</c> file.
/// A publishing client's file carries its own <c>managed_by</c> (the manifest is signed, so the Gateway does not add it as the route does); what a client may send (the serving policy, no <c>allow_writes</c>), the owner rules, the data source made by hand
/// that is replaced only with <c>adopt: true</c> in the body (recorded, with the changes the replacement makes in the answer), the validation of the file among the others and where the file is served from are the route's, in its words.
/// </summary>
public sealed partial class GatewayDataSources
{
    private sealed record Pending(string? Existing, bool Adopting, bool ToGroup, JsonArray Diagnostics);

    private readonly ConditionalWeakTable<PublishHookContext, Pending> _pending = new();

    private static ManagementCaller? Of(DefinitionsCaller caller) => caller.Context as ManagementCaller;

    private static DefinitionsRefusalException Refusal(ManagementResult result) =>
        new(result.Status, (result.Body as JsonObject)?["error"]?.GetValue<string>() ?? "This call is refused.") { Body = result.Body as JsonObject };

    internal string? OwnerOfShared(string name) =>
        TextOf(name) is { } text ? DbDataKit.Connections.ConnectionDefinitionLoader.LoadDatabaseConnection(text, name + ".yaml").Definition?.ManagedBy : null;

    /// <summary>The route's checks before anything is stored, for a publish through the shared API.</summary>
    internal void CheckSharedPublish(PublishHookContext context)
    {
        if (Of(context.Caller) is not { } caller) return;
        var name = context.Name;
        var text = context.Manifest;
        if (!NamePattern().IsMatch(name)) throw new DefinitionsRefusalException(400, "A data source name is a letter followed by letters, digits, '.', '_' or '-' (up to 64).");

        var existing = TextOf(name);
        var publishing = !IsAdministrator(caller);
        var adopting = publishing && context.Body["adopt"] is JsonValue flag && flag.TryGetValue<bool>(out var asked) && asked && existing is not null && IsHandMade(name);
        if (publishing)
        {
            var refused = adopting ? AdoptScope(caller, name) : Scope(caller, existing is not null ? ObjectAction.Change : ObjectAction.Create, name);
            if (refused is not null)
            {
                Record(caller, "data-source put", name, "refused");
                throw Refusal(existing is not null && IsHandMade(name) ? ManagementResult.Problem(409, NotAdoptedSentence(name)) : refused);
            }
            var sent = DbDataKit.Connections.ConnectionDefinitionLoader.LoadDatabaseConnection(text, $"{name}.yaml").Definition;
            var client = OwnerScope.Stamp(caller.Token!);
            if (DbDataKit.Connections.ServingPolicy.BlockOf(text) is { } block && DbDataKit.Connections.ServingPolicy.Check(block) is { Count: > 0 } problems)
            {
                Record(caller, "data-source write", name, "refused");
                var blocked = problems.FirstOrDefault(p => p.IsBlocked);
                if (blocked is not null)
                    throw new DefinitionsRefusalException(403, $"{blocked.Message.Replace("<name>", name, StringComparison.Ordinal)} It is on line {blocked.At.Line} of the data source text sent. {blocked.Fix}"
                        + (problems.Count > 1 ? $" Other problems: {string.Join(" ", problems.Where(p => !p.IsBlocked).Select(p => $"{p.Message} (line {p.At.Line})"))}" : ""));
                throw new DefinitionsRefusalException(400, string.Join(" ", problems.Select(p => $"{p.Message} (line {p.At.Line}){(p.Fix is null ? "" : " " + p.Fix)}")));
            }
            if (!string.IsNullOrEmpty(sent?.ManagedBy) && sent.ManagedBy != client)
            {
                Record(caller, "data-source write", name, "refused");
                throw new DefinitionsRefusalException(403, "The file names another client in managed_by.");
            }
            if (string.IsNullOrEmpty(sent?.ManagedBy))
                throw new DefinitionsRefusalException(400, $"The file of a data source a publishing client sends says whose it is: add managed_by: {client} (the manifest is signed, so the Gateway does not add the line as the per-kind route does).");
        }

        // the file as it would be, among the others: refused with the reasons if it would make the configuration invalid, before anything is stored
        var candidate = validation.ValidateFiles(new Dictionary<string, string> { [$"connections/{name}.yaml"] = text });
        if (candidate.Body["valid"]?.GetValue<bool>() != true)
            throw new DefinitionsRefusalException(400, "The data source is not valid, so nothing was written.")
            {
                Body = new JsonObject { ["error"] = "The data source is not valid, so nothing was written.", ["valid"] = false, ["diagnostics"] = candidate.Body["diagnostics"]!.DeepClone() },
            };
        _pending.AddOrUpdate(context, new Pending(existing, adopting, ToGroup(name, caller), new JsonArray([.. candidate.Body["diagnostics"]!.AsArray().Select(d => d!.DeepClone())])));
    }

    /// <summary>What the route does after the version is stored: the file is served from where the route puts it (the group's state, or this instance's folder with its history), the audit and the adoption record, and the answer says what the replacement changed.</summary>
    internal void AfterSharedPublish(PublishHookContext context, PublishResult result, JsonObject response)
    {
        if (Of(context.Caller) is not { } caller || !_pending.TryGetValue(context, out var pending)) return;
        var name = context.Name;
        var text = context.Manifest;
        var created = pending.Existing is null;
        if (result.Mode == PublishMode.Serve && (result.Changed || created))
        {
            if (pending.ToGroup) published!.ServeNow(name);
            else
            {
                var file = FileOf(name);
                // a file made by hand that a publisher replaces goes to the history as it was, whether or not anything had committed it before: nothing is lost
                if (pending.Adopting && File.Exists(file)) Committed(caller, $"Data source {name} as it was before {OwnerScope.Stamp(caller.Token!)} adopted it", [file]);
                Directory.CreateDirectory(Folder);
                var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                File.Move(temp, file, overwrite: true);
                Committed(caller, pending.Adopting ? $"Data source {name} adopted by {OwnerScope.Stamp(caller.Token!)}: the definition replaces the file made by hand" : $"{(created ? "Add" : "Change")} data source {name}", [file]);
                reload();
            }
        }
        Record(caller, "data-source write", name, created ? "created" : "changed");
        if (pending.Adopting) Record(caller, "data-source adopt", name, $"adopted by {OwnerScope.Stamp(caller.Token!)}");
        response["created"] = created;
        response["group"] = pending.ToGroup;
        response["adopted"] = pending.Adopting;
        response["changes"] = pending.Existing is null ? new JsonArray() : DefinitionChanges.Between(pending.Existing, text);
        response["diagnostics"] = pending.Diagnostics.DeepClone();
    }

    /// <summary>The read rules of the route: a publishing client reaches only the data sources it made (a hand-made one is refused, which is how a publisher learns it must adopt).</summary>
    internal void CheckSharedRead(string name, DefinitionsCaller caller)
    {
        if (Of(caller) is not { } called || TextOf(name) is null || Scope(called, ObjectAction.Change, name) is not { } refused) return;
        Record(called, "data-source get", name, "refused");   // the route audits a refused read as it does a refused write
        throw Refusal(refused);
    }
}

/// <summary>The hook of the <c>data-source</c> kind on the Gateway's shared publish API: <see cref="GatewayDataSources"/>' checks and effects, and the owner the file names.</summary>
public sealed class DataSourceKindHook(GatewayDataSources dataSources, SignaturePolicy? policy = null) : IDefinitionKindHook
{
    public string Kind => PublishedDataSources.DefinitionKind;

    // while the per-kind routes publish unsigned versions, only a group an administrator set to `required` refuses an unsigned one (as its instances do, plan 30 PB03)
    public bool? SignaturesRequired => policy?.RequiredByAdministrator(Kind);

    public void BeforePublish(PublishHookContext context) => dataSources.CheckSharedPublish(context);

    public void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response) => dataSources.AfterSharedPublish(context, result, response);

    public string? OwnerOf(string name) => dataSources.OwnerOfShared(name);

    public void BeforeRead(string name, DefinitionsCaller caller) => dataSources.CheckSharedRead(name, caller);
}
