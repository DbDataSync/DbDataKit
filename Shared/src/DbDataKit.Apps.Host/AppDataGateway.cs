using System.Text.RegularExpressions;
using DbDataKit.Identity;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace DbDataKit.Apps;

/// <summary>Which of the data calls an app may make: one row, a page of rows, a batch of changes, or a model's schema (the entities its manifest lists; AP25).</summary>
public enum AppDataRoute { Row, Rows, Changes, Schema }

/// <summary>
/// One data call of an app, checked by the host: the person (the subject of the app's token, with the roles the identity home gave them on the app's instance), the app's instance, the scope of its manifest's <c>models</c>
/// (<c>model/entity:read|write</c>), and what it asks for. <see cref="Via"/> is what the servers' audit says the call went through for a trusted app (its name, version and approval: <see cref="AppApproval.AuditLabel"/>), null for a sandboxed one. <see cref="Copy"/> is the copy of the model's data the instance is bound to, or the one the path named; null is the model's default copy.
/// </summary>
public sealed record AppDataCall(
    string App, string SubjectId, string? SubjectName, ServerRole Roles, IReadOnlyList<string> Scope, AppDataRoute Route, string Model, string? Entity, string? Copy,
    IReadOnlyDictionary<string, string> Query, byte[] Body, string? Via = null, string? Draft = null)
{
    /// <summary>Whether the manifest lets the app read (or, with <paramref name="write"/>, write) the entity; write includes read.</summary>
    public bool Allows(string entity, bool write) =>
        Scope.Contains(TokenScope.Entry(Model, entity, write: true), StringComparer.Ordinal)
        || (!write && (Scope.Contains(TokenScope.Entry(Model, entity, write: false), StringComparer.Ordinal) || Scope.Contains(TokenScope.ProposeEntry(Model, entity), StringComparer.Ordinal)));

    /// <summary>Whether the manifest lets the app propose a change to the entity: a write, or a <c>change request</c> (<see cref="TokenScope.ProposeEntry"/>).</summary>
    public bool Proposes(string entity) =>
        Scope.Contains(TokenScope.Entry(Model, entity, write: true), StringComparer.Ordinal) || Scope.Contains(TokenScope.ProposeEntry(Model, entity), StringComparer.Ordinal);
}

/// <summary>Which of the semantic calls an app may make: the models its manifest lists, one model's catalog, or a query.</summary>
public enum AppSemanticRoute { Models, Catalog, Query }

/// <summary>One semantic call of an app, checked by the host: the person and the roles the identity home gave them on the instance, the models the manifest's <c>semantic</c> lists (<see cref="Allowed"/>; the host has checked that <see cref="Model"/> is one), and the request body of a query.</summary>
public sealed record AppSemanticCall(string App, string SubjectId, string? SubjectName, ServerRole Roles, IReadOnlyList<string> Allowed, AppSemanticRoute Route, string Model, byte[] Body, string? Via = null);

/// <summary>What answers an app's semantic calls: in the serving layer, the semantic module running the query as the person (plan 22a GP35c).</summary>
public interface IAppSemantic
{
    Task<AppResponse> SendAsync(AppSemanticCall call, CancellationToken cancellationToken);
}

/// <summary>What answers an app's data calls: in the serving layer, the group's published entity models, read and written in the same process (plan 20 O08c).</summary>
public interface IAppData
{
    Task<AppResponse> SendAsync(AppDataCall call, CancellationToken cancellationToken);
}

/// <summary>
/// The way a sandboxed app reaches master data (plan 26 section 3; plan 20 O08c): <c>/apps/&lt;instance&gt;/_data/api/v1/models/&lt;model&gt;/...</c> on the apps host. The app sends the token the shell brokered (audience
/// <c>app:&lt;instance&gt;</c>, made by the identity home); the host checks it and hands the call, with the person, the app and the scope of the manifest's <c>models</c>, to <see cref="IAppData"/>: the models published to the group, so an app goes on
/// with the store's server stopped and a model in development is never what an app sees. The browser reaches nothing else, needs no CORS but this route's, and the app never holds a token anything else accepts. Only row reads
/// and the changes endpoint are answered.
/// </summary>
public sealed class AppDataGateway(AppInstanceStore instances, IAppData data, AppTokenValidator tokens, JwksTokenVerifier? keys = null, AppApprovalStore? approvals = null, IAppSemantic? semantic = null, IAppViews? views = null)
{
    private static readonly Regex SemanticPath = new(@"^api/v1/semantic/models(/(?<model>[^/]+)/(?<kind>catalog|query))?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DataPath = new(@"^api/v1/models/(?<model>[^/]+)(/instances/(?<copy>[^/]+))?/(entities/(?<entity>[^/]+)/(?<kind>rows?)|(?<kind>changes)|(?<kind>schema))$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // the path of a call as apps made it before the group served their data: a paired store's id first
    private static readonly Regex StorePath = new(@"^[^/]+/api/v1/", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The instance setting of a preview that turns its writes on: they are kept in the preview's draft (plan 21 CH01).</summary>
    public const string PreviewWritesSetting = "preview.writes";

    /// <summary>Checks one call and returns the answer of <see cref="IAppData"/>, or the host's own refusal.</summary>
    public async Task<AppResponse> ForwardAsync(string instance, string path, string method, string query, string? authorization, byte[] body, CancellationToken ct)
    {
        if (instances.Resolve(instance) is not { } resolved) return Refuse(404, "No such app.");
        // a token lives five minutes: an approval revoked in that time still stops the data
        // and the approval that stands goes with every call, for the servers' audit
        string? via = null;
        if (resolved.Manifest.Trust == AppTrust.Trusted)
        {
            var gate = approvals?.Gate(resolved);
            if (gate is not { Allowed: true, Approval: { } standing }) return Refuse(403, $"'{resolved.Manifest.Name}' asks to be trusted and no approval stands for this version.");
            via = standing.AuditLabel;
        }
        // the identity home's access token for this instance: its signature, issuer and expiry are the home's, its audience must be this instance (instance B refuses instance A's token), and what it allows is already the approved manifest's scope
        // narrowed by the roles the home gave the person on the instance (AppScope.Narrow)
        var bearer = authorization is { } h && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..] : null;
        if (keys is not null) await keys.PrepareAsync(bearer, ct);
        if (tokens.Validate(bearer, instance).Token is not { } token) return Refuse(401, "A token for this app is needed.");

        var trimmed = path.Trim('/');
        if (trimmed.StartsWith("api/v1/semantic/", StringComparison.Ordinal) || trimmed == "api/v1/semantic")
            return await ForwardSemanticAsync(resolved, instance, trimmed, method, body, token, via, ct);
        if (AppViewsRoute.Parse(trimmed) is { } document)
            return await ForwardViewsAsync(resolved, instance, document, method, body, token, via, ct);
        var match = DataPath.Match(trimmed);
        if (!match.Success)
            return StorePath.IsMatch(trimmed)
                ? Refuse(410, $"An app's data is the group's now, not a paired store's: call /apps/{instance}/_data/api/v1/models/<model>/... (the path without the store's id).")
                : Refuse(403, "Apps reach row reads, the schema and changes only.");
        var route = match.Groups["kind"].Value switch { "row" => AppDataRoute.Row, "rows" => AppDataRoute.Rows, "schema" => AppDataRoute.Schema, _ => AppDataRoute.Changes };
        if (route == AppDataRoute.Changes ? !HttpMethods.IsPost(method) : !HttpMethods.IsGet(method))
            return Refuse(405, route == AppDataRoute.Changes ? "Changes are sent with POST." : "Rows and the schema are read with GET.");

        // a preview reads what its instance reads and is read-only by default (plan 21 decision 2); the author turns writes on with the instance setting `preview.writes`, and then its writes go to the draft layer of the preview
        // (CH01: kept apart from the data, seen by this preview only, never history unless submitted as change requests), and what it reads shows the draft on top
        string? draft = null;
        if (AppInstanceName.Split(instance).Channel != DbDataKit.State.DefinitionStore.Production)
        {
            if (resolved.Instance.Settings.TryGetValue(PreviewWritesSetting, out var writes) && string.Equals(writes, "true", StringComparison.OrdinalIgnoreCase)) draft = instance;
            else if (route == AppDataRoute.Changes)
                return Refuse(403, $"This is a preview: it reads the instance's data and keeps no change. Turn writes on for the preview with the instance setting `{PreviewWritesSetting}: true` to keep its changes in a draft, or publish the app to change data.");
        }

        // the instance's binding says which copy of the model's data this app uses, unless the path names one
        var model = Uri.UnescapeDataString(match.Groups["model"].Value);
        var copy = match.Groups["copy"].Success ? Uri.UnescapeDataString(match.Groups["copy"].Value) : resolved.Instance.Bindings.TryGetValue(model, out var bound) ? bound : null;
        var parameters = QueryHelpers.ParseQuery(query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        var call = new AppDataCall(instance, token.SubjectId, token.SubjectName, RolesOf(token), token.Scope, route, model,
            match.Groups["entity"].Success ? Uri.UnescapeDataString(match.Groups["entity"].Value) : null, copy, parameters, body, via, draft);
        var answer = await data.SendAsync(call, ct);
        return answer with { Headers = Cors };
    }

    // The semantic route (plan 22a GP35c): the models the manifest's `semantic:` lists, a model's catalog and a query, as the person the token names. The scope is asked before the host, so nothing says whether a model the manifest
    // does not list exists.
    private async Task<AppResponse> ForwardSemanticAsync(ResolvedInstance resolved, string instance, string trimmed, string method, byte[] body, AppAccessToken token, string? via, CancellationToken ct)
    {
        var match = SemanticPath.Match(trimmed);
        if (!match.Success) return Refuse(403, "Apps reach the semantic models, a model's catalog and its queries only: api/v1/semantic/models[/<model>/catalog|query].");
        var route = !match.Groups["kind"].Success ? AppSemanticRoute.Models : match.Groups["kind"].Value == "catalog" ? AppSemanticRoute.Catalog : AppSemanticRoute.Query;
        if (route == AppSemanticRoute.Query ? !HttpMethods.IsPost(method) : !HttpMethods.IsGet(method))
            return Refuse(405, route == AppSemanticRoute.Query ? "A semantic query is sent with POST." : "The models and a catalog are read with GET.");
        if (semantic is null) return Refuse(404, "This host serves no semantic models.");
        var model = match.Groups["model"].Success ? Uri.UnescapeDataString(match.Groups["model"].Value) : "";
        var listed = resolved.Manifest.SemanticModels;
        if (route != AppSemanticRoute.Models && !listed.Contains(model, StringComparer.OrdinalIgnoreCase))
            return Refuse(403, $"This app may not use the semantic model '{model}': its manifest does not list it under `semantic`.");
        var call = new AppSemanticCall(instance, token.SubjectId, token.SubjectName, RolesOf(token), listed, route, model, body, via);
        return (await semantic.SendAsync(call, ct)) with { Headers = Cors };
    }

    // The views route (plan 32 DBR13): an app's dashboard or report, the text of the file the manifest lists under `views` and its rendered widgets as the person the token names. What is read is the package's own file; what is rendered is
    // the serving layer's (IAppViews), whose queries run as the person and are limited to what the manifest lists.
    private async Task<AppResponse> ForwardViewsAsync(ResolvedInstance resolved, string instance, (string Kind, string File, bool Render) route, string method, byte[] body, AppAccessToken token, string? via, CancellationToken ct)
    {
        if (route.Render ? !HttpMethods.IsPost(method) : !HttpMethods.IsGet(method))
            return Refuse(405, route.Render ? "A render is sent with POST." : "A document is read with GET.");
        if (AppViewsRoute.TextOf(resolved, route.File) is not { } text)
            return Refuse(404, $"This app has no dashboard or report '{route.File}': it is not a file of its manifest's `views` list.");
        if (!route.Render) return AppViewsRoute.Open(route.Kind, route.File, text);
        if (views is null) return Refuse(404, "This host renders no dashboards or reports.");
        var call = new AppViewsCall(instance, resolved, route.File, token.SubjectId, token.SubjectName, RolesOf(token), body, via);
        return (await views.SendAsync(call, ct)) with { Headers = Cors };
    }

    /// <summary>The roles the token names (the home's role names on the instance) as the flags the servers' checks use.</summary>
    private static ServerRole RolesOf(AppAccessToken token)
    {
        var roles = ServerRole.None;
        foreach (var name in token.Roles)
            if (Enum.TryParse<ServerRole>(name, ignoreCase: true, out var role) && role != ServerRole.None) roles |= role;
        return roles;
    }

    public static readonly IReadOnlyDictionary<string, string> Cors = new Dictionary<string, string>
    {
        // the app's frame has an opaque origin ("null"); the token, not the origin, is the credential, and no cookie is involved
        ["Access-Control-Allow-Origin"] = "*", ["Access-Control-Allow-Headers"] = "Authorization, Content-Type", ["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS",
        ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff",
    };

    private static AppResponse Refuse(int status, string message) => new(status, "text/plain; charset=utf-8", Cors, System.Text.Encoding.UTF8.GetBytes(message));
}
