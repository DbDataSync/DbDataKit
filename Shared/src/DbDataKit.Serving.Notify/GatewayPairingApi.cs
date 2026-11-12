using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Serving.Notify;

/// <summary>
/// The serving groups a server is paired with and what its notifier did, on the server's management API (plan 24a UC13): the same calls for Sync, the Runner and any other product that pairs only to tell a group of data changes.
/// <list type="bullet">
/// <item><c>GET {prefix}/gateways</c> (viewer): the pairings (name, address, instance id, when) and the notifier's counters.</item>
/// <item><c>POST {prefix}/gateways</c> (admin): <c>{"name", "url", "code"}</c>, what a console brokering a pairing sends (<c>/api/instances/link</c>, T11): the code is the Gateway's own, made for this server just now.</item>
/// <item><c>DELETE {prefix}/gateways/{name}</c> (admin): forgets a pairing; the server stops telling that group.</item>
/// </list>
/// </summary>
public static class GatewayPairingApi
{
    public static void Map(IEndpointRouteBuilder app, string prefix, GatewayPairingsFile pairings, Func<Task<ConsoleKeyStore>> identity, Func<IDataChangeNotifier?> notifier, string clientName)
    {
        var route = prefix.TrimEnd('/') + "/gateways";

        app.MapGet(route, (HttpContext context) =>
        {
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer).Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(Describe(pairings, notifier()));
        });

        app.MapPost(route, async (HttpContext context) =>
        {
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            string? name, address, code;
            try
            {
                var body = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted);
                (name, address, code) = (body?["name"]?.GetValue<string>(), body?["url"]?.GetValue<string>(), body?["code"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { name = address = code = null; }
            if (string.IsNullOrWhiteSpace(name) || !IsValidName(name) || string.IsNullOrWhiteSpace(code) || !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                return Results.Json(new { error = "The body is a JSON object with a \"name\" (lower-case letters, digits, '-' and '_'), the gateway's https \"url\" and a pairing \"code\"." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var paired = await pairings.PairAsync(await identity(), name, url, code, clientName, context.RequestAborted);
                return Results.Json(new { name = paired.Name, url = paired.Url, instance_id = paired.InstanceId, paired_at = paired.PairedAt });
            }
            catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapDelete(route + "/{name}", (string name, HttpContext context) =>
        {
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            return pairings.Remove(name) ? Results.Json(new { removed = name }) : Results.NotFound(new { error = $"This server is not paired with a gateway called '{name}'." });
        });
    }

    /// <summary>A name is what the console and the command line call the pairing: lower-case letters, digits, <c>-</c> and <c>_</c>.</summary>
    public static bool IsValidName(string name) => name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>The document of <c>GET .../gateways</c>.</summary>
    public static JsonObject Describe(IGatewayPairings pairings, IDataChangeNotifier? notifier)
    {
        var stats = notifier?.Stats;
        return new JsonObject
        {
            ["gateways"] = new JsonArray([.. pairings.All().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["url"] = p.Url, ["instance_id"] = p.InstanceId, ["paired_at"] = p.PairedAt })]),
            ["notifications"] = stats is null ? null : new JsonObject
            {
                ["sent"] = stats.Sent, ["not_served"] = stats.NotServed, ["retried"] = stats.Retried, ["failed"] = stats.Failed,
                ["last_sent_at"] = stats.LastSentAt, ["last_failure_at"] = stats.LastFailureAt, ["last_failure"] = stats.LastFailure,
            },
        };
    }
}
