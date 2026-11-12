using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Serving;
using DbDataKit.Views;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The rows of a custom page on the apps host (plan 32 DBR33): the queries its manifest entry declares run through the serving core as the signed-in person, by <see cref="WidgetResolver.ResolveCustomViewAsync"/> (the same run a custom widget gets, so a
/// parameter's <c>{param: name}</c> markers, the row limit and the core's own refusals are the same). As for an app's semantic calls (<see cref="GroupAppSemantic"/>), the person is a user of the identity home and the data sources they may use are the ones
/// their home groups reach, so a page shows what the person could ask for themselves, never more; a person of no home user is refused. The manifest's <c>semantic</c> list narrows the page: a declared query against a model it does not list is refused whatever the
/// entry says (the publish check refuses it as well, so this is a second lock).
/// </summary>
public sealed class GroupCustomPageRows(IServingServices serving, HomeCallers homeCallers, IOptionsMonitor<GatewayOptions> options) : ICustomPageRows
{
    public async Task<CustomPageAnswer> ResolveAsync(AppSession session, ResolvedInstance instance, CustomView view, IReadOnlyDictionary<string, JsonNode?> parameters, CancellationToken cancellationToken)
    {
        foreach (var query in view.Queries.Where(q => q.Form == "semantic"))
            if (!instance.Manifest.SemanticModels.Contains(query.Model, StringComparer.OrdinalIgnoreCase))
                return Fail(403, "denied", $"The query `{query.Name}` of the page '{view.Name}' uses the semantic model '{query.Model}', which the app's manifest does not list under `semantic`.");
        var callers = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        if (callers.UserOf(session.Identity.UserId) is not { } user)
            return Fail(403, "denied", "A page's data is for the people of the identity home; this sign-in is not one of them.");
        var context = new ServingContext(new GatewayCaller(HomePrincipals.Create(callers, user, "app", options.CurrentValue)), ClientAddress: null);
        var resolver = new WidgetResolver(serving.Queries, _ => null, new ResolverOptions(), serving.Entities);
        try
        {
            var entry = await resolver.ResolveCustomViewAsync(context, view, $"{instance.Manifest.Name}/{view.Export}", parameters, cancellationToken).ConfigureAwait(false);
            return new CustomPageAnswer(200, new JsonObject { ["data"] = entry["data"]?.DeepClone() ?? new JsonObject(), ["params"] = entry["params"]?.DeepClone() ?? new JsonObject() });
        }
        catch (CustomViewException e)
        {
            return Fail(e.Code is "denied" or "unauthenticated" ? 403 : e.Code == "not_found" ? 404 : 422, e.Code, e.Message);
        }
    }

    private static CustomPageAnswer Fail(int status, string code, string message) => new(status, new JsonObject { ["code"] = code, ["message"] = message });
}
