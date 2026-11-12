using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>
/// The Store's own notification feed for the console (plan 21 CH07): <c>GET /api/v1/notifications[?since_id=n]</c>, oldest first, in the shape the Gateway's feed has (<c>id</c>, <c>kind</c>, <c>created</c>, <c>message</c>), so the
/// console's Monitoring reads both the same way. The preview expiry's notices (<see cref="StoreDraftSweep.WarningKind"/>, <see cref="StoreDraftSweep.RemovedKind"/>) are in it. A viewer may read it, as for the events.
/// </summary>
public static class StoreNotifications
{
    public static JsonObject Of(INotificationFeed feed, long? sinceId) => new()
    {
        ["notifications"] = new JsonArray([.. feed.List(sinceId).Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["kind"] = n.Kind, ["created"] = n.CreatedAtUtc, ["message"] = n.Message })]),
    };

    public static void Map(IEndpointRouteBuilder app, INotificationFeed feed) =>
        app.MapGet("/api/v1/notifications", (HttpContext context, long? since_id) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            return decision.Allowed ? Results.Json(Of(feed, since_id)) : Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
        });
}
