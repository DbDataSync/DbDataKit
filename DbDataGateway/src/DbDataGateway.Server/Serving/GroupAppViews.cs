using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.Views;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// An app's dashboards and reports rendered by the serving layer (plan 32 DBR13): the apps host checked the app's token and that the manifest lists the file under <c>views</c>, and here the document is read again with the views loader and
/// resolved by <see cref="WidgetResolver"/> through the same core services as a published document, <b>as the person the token names</b> (a user of the identity home; the data sources they may use are the ones their home groups reach,
/// <see cref="HomePrincipals"/>, so an app shows a person what they could ask for themselves, never more). The manifest narrows what a document may read: a widget whose query names a semantic model the manifest does not list under
/// <c>semantic</c>, or a Store entity it does not list with <c>read</c>, refuses the whole document (the publish check says the same, this is the second lock). A custom widget gets the rows of the queries the manifest declares for it
/// (<see cref="ResolverOptions.CustomViews"/>). The answer is the module's <c>rendered-widgets</c> JSON.
/// </summary>
public sealed class GroupAppViews(IServingServices serving, SemanticModels models, HomeCallers homeCallers, IOptionsMonitor<GatewayOptions> options) : IAppViews
{
    public async Task<AppResponse> SendAsync(AppViewsCall call, CancellationToken cancellationToken)
    {
        if (AppViewsRoute.TextOf(call.Instance, call.File) is not { } text) return Fail(404, "not_found", $"This app has no dashboard or report '{call.File}'.");
        var load = ViewsLoader.Load(text, call.File);
        if (load.Document is not { } document)
            return Fail(422, "invalid", $"The document '{call.File}' does not read: {load.Diagnostics.FirstOrDefault()?.Message ?? "it has errors"} (`app validate` lists them).");
        var manifest = call.Instance.Manifest;
        if (AppViewDocuments.AccessProblems(manifest, document) is { Count: > 0 } forbidden)
            return Fail(403, "denied", string.Join(" ", forbidden));

        RenderRequest request;
        try { request = RenderRequest.Parse(JsonNode.Parse(call.Body.Length == 0 ? "{}" : Encoding.UTF8.GetString(call.Body)) as JsonObject); }
        catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException) { return Fail(400, "invalid", e.Message); }
        var chosen = LayoutSelector.Select(document, request.Viewport);
        if (chosen.Layout is not { } layout) return Fail(400, ResolveCodes.UnknownLayout, chosen.Refusal ?? "No layout fits.");

        var callers = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        if (callers.UserOf(call.SubjectId) is not { } user)
            return Fail(403, "denied", "A dashboard's data is for the people of the identity home; this sign-in is not one of them.");
        var context = new ServingContext(new GatewayCaller(HomePrincipals.Create(callers, user, "app", options.CurrentValue)), ClientAddress: null);

        // the roles of the models' members (what a derived widget needs) and the saved queries, of the models the manifest lists
        var catalogs = new Dictionary<string, ModelCatalog?>(StringComparer.OrdinalIgnoreCase);
        ModelCatalog? Catalog(string name) => catalogs.TryGetValue(name, out var known) ? known
            : catalogs[name] = manifest.SemanticModels.Contains(name, StringComparer.OrdinalIgnoreCase) && models.Find(name) is { } m ? new ModelCatalog(m.Name, m.Version ?? 0, m.Catalog) : null;
        var resolver = new WidgetResolver(serving.Queries, name => Catalog(name)?.Roles, new ResolverOptions
        {
            SavedQueryModel = query => manifest.SemanticModels.FirstOrDefault(m => Catalog(m)?.HasSavedQuery(query) == true),
            CustomViews = component => CustomWidget(manifest, component),
        }, serving.Entities);
        try
        {
            var rendered = await resolver.ResolveAsync(context, document, layout.Name, request.Parameters, request.Only, cancellationToken, request.Orders, expand: request.Expands).ConfigureAwait(false);
            return new AppResponse(200, "application/json; charset=utf-8", AppDataGateway.Cors, Encoding.UTF8.GetBytes(rendered.ToJson().ToJsonString()));
        }
        catch (ViewResolutionException e)
        {
            return Fail(400, e.Code, e.Message);
        }
    }

    /// <summary>The widget the manifest declares for a component named <c>&lt;app&gt;/&lt;Export&gt;</c>; null for another app's, which this host does not serve.</summary>
    private static CustomView? CustomWidget(AppManifest manifest, string component)
    {
        var slash = component.IndexOf('/');
        return slash > 0 && component[..slash] == manifest.Name
            ? manifest.CustomViews.FirstOrDefault(v => v.Kind == CustomViewKind.Widget && v.Export == component[(slash + 1)..])
            : null;
    }

    private static AppResponse Fail(int status, string code, string message) =>
        new(status, "application/json; charset=utf-8", AppDataGateway.Cors, Encoding.UTF8.GetBytes(new JsonObject { ["code"] = code, ["error"] = code, ["message"] = message }.ToJsonString()));
}
