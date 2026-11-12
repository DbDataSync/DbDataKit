using System.Text.Json.Nodes;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Management;
using DbDataSync.Core.Secrets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// The serving groups (Gateways) this server tells when a run wrote rows (plan 24a UC13), and what it did so far: <c>GET /api/gateways</c> (viewer) lists the pairings and the notifier's counters, <c>POST /api/gateways</c> (admin) pairs with one
/// (<c>{"name", "url", "code"}</c>: what a console that links the two sends, with a code the Gateway just made for this server), <c>DELETE /api/gateways/{name}</c> (admin) forgets one. The pairings are in <c>gateways.json</c> of the repository root and
/// the key this server signs with is its own, in its secret store; the same three calls as <see cref="GatewayPairingApi"/> gives a server on minimal routes.
/// </summary>
[ApiController]
[Route("api/gateways")]
public sealed class GatewaysController(SyncDataChanges changes, SecretStore secrets) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<JsonObject> List() => GatewayPairingApi.Describe(changes.Pairings, changes.Notifier);

    [Authorize(Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Pair([FromBody] JsonObject? body, CancellationToken cancellationToken)
    {
        var (name, address, code) = (Text(body, "name"), Text(body, "url"), Text(body, "code"));
        if (name is null || !GatewayPairingApi.IsValidName(name) || code is null || !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { error = "The body is a JSON object with a \"name\" (lower-case letters, digits, '-' and '_'), the gateway's https \"url\" and a pairing \"code\"." });
        try
        {
            var paired = await changes.Pairings.PairAsync(SyncDataChanges.IdentityIn(secrets), name, url, code, "DbDataSync server", cancellationToken);
            return Ok(new { name = paired.Name, url = paired.Url, instance_id = paired.InstanceId, paired_at = paired.PairedAt });
        }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    [Authorize(Policies.Admin)]
    [HttpDelete("{name}")]
    public IActionResult Forget(string name) =>
        changes.Pairings.Remove(name) ? Ok(new { removed = name }) : NotFound(new { error = $"This server is not paired with a gateway called '{name}'." });

    private static string? Text(JsonObject? body, string key) => body?[key] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
