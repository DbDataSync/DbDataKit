using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// An app's semantic calls answered by the serving layer (plan 22a GP35c): the apps host checked the app's token and that the manifest's <c>semantic</c> lists the model, and here the query runs through the same core services as a
/// Flight SQL client's, as the person the token names. That person is a user of the identity home; the data sources they may use are the ones their home groups reach (<see cref="HomePrincipals"/>), so what an app shows
/// a person is what that person could ask for themselves, never more, and an app that lists a model gives nobody a source they have no right to. A person of no home user (a console's own administrator) has no data source and is refused.
/// The answer is <see cref="SemanticRest"/>'s JSON.
/// </summary>
public sealed class GroupAppSemantic(IServingServices serving, SemanticModels models, HomeCallers homeCallers, IOptionsMonitor<GatewayOptions> options) : IAppSemantic
{
    public async Task<AppResponse> SendAsync(AppSemanticCall call, CancellationToken cancellationToken)
    {
        try
        {
            if (call.Route == AppSemanticRoute.Models) return Json(200, SemanticRest.Models(models, call.Allowed));
            var callers = await homeCallers.GetAsync(cancellationToken);
            if (callers.UserOf(call.SubjectId) is not { } user)
                return Json(403, Error("denied", "Semantic data is for the people of the identity home; this sign-in is not one of them."));
            var caller = new GatewayCaller(HomePrincipals.Create(callers, user, "app", options.CurrentValue));
            var context = new ServingContext(caller, ClientAddress: null);
            if (models.Find(call.Model) is null) return Json(404, Error("not_found", $"There is no semantic model '{call.Model}' on this server."));
            return call.Route == AppSemanticRoute.Catalog
                ? Json(200, await SemanticRest.CatalogAsync(serving, context, call.Model, cancellationToken))
                : Json(200, await SemanticRest.QueryAsync(serving, context, call.Model, Encoding.UTF8.GetString(call.Body), cancellationToken));
        }
        catch (ServingException e) when (e is not ServingBackendException)
        {
            return e switch
            {
                ServingNotFoundException => Json(404, Error("not_found", e.Message)),
                ServingDeniedException or ServingUnauthenticatedException => Json(403, Error("denied", e.Message)),
                ServingLimitException => Json(422, Error("invalid", e.Message)),
                _ => Json(400, Error("invalid", e.Message)),
            };
        }
        catch (ServingBackendException e)
        {
            return Json(502, Error("unavailable", e.Message));
        }
    }

    private static JsonObject Error(string code, string message) => new() { ["code"] = code, ["error"] = code, ["message"] = message };

    private static AppResponse Json(int status, JsonObject body) =>
        new(status, "application/json; charset=utf-8", AppDataGateway.Cors, Encoding.UTF8.GetBytes(body.ToJsonString()));
}
