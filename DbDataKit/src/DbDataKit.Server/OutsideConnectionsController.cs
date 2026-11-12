using DbDataKit.Connections;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record SecretBody(string Value);

/// <summary>A pasted webhook address, with (for a service the editor does not know) the path segments (from 0) and query keys that are secret.</summary>
public sealed record WebhookBody(string Url, int[]? PathSegments = null, string[]? QueryKeys = null);

/// <summary>
/// The connections of outside systems the console keeps (plan 27a IX01): list with the kind, check and save a file (the shared loader's diagnostics with line), remove, keep a typed secret in the secret store. Reading is for anyone signed in and never
/// holds a secret (a reference, and whether it can be read); changing is for an administrator. A typed secret is in no answer, no audit row and no file.
/// </summary>
[ApiController]
[Route("api/outside-connections")]
public sealed class OutsideConnectionsController(ConsoleConnections connections, ConsoleAgents agents) : ControllerBase
{
    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";

    private void Audit(string action, string name, string detail) => agents.Store.RecordAudit(null, null, action, PersonId, "console", "1", "console", name, null, detail);

    [HttpGet]
    [Authorize(Policies.Viewer)]
    public IActionResult List() => Ok(new { kinds = new[] { ConnectionKinds.Http, ConnectionKinds.ModelProvider, ConnectionKinds.Smtp }, flavors = new[] { "slack", "teams", "signed", "oauth" }, connections = connections.List() });

    [HttpGet("template")]
    [Authorize(Policies.Admin)]
    public IActionResult Template([FromQuery] string kind, [FromQuery] string name, [FromQuery] string? flavor = null) =>
        !ConsoleConnections.IsName(name) ? BadRequest(new { error = "A connection's name starts with a letter and has letters, digits, '-', '_' and '.'." })
        : flavor is not null && (kind != ConnectionKinds.Http || flavor is not ("slack" or "teams" or "signed" or "oauth")) ? BadRequest(new { error = "A flavor is slack, teams, signed or oauth, and goes with kind http." })
        : kind is ConnectionKinds.Http or ConnectionKinds.ModelProvider or ConnectionKinds.Smtp ? Ok(new { yaml = ConsoleConnections.Template(kind, name, flavor) })
        : BadRequest(new { error = $"The console keeps connections of kind {ConnectionKinds.Http}, {ConnectionKinds.ModelProvider} and {ConnectionKinds.Smtp}." });

    [HttpPost("{name}/check")]
    [Authorize(Policies.Admin)]
    public IActionResult Check(string name, [FromBody] YamlBody body) =>
        ConsoleConnections.IsName(name) ? Ok(new { diagnostics = connections.ViewOf(name, body.Yaml, ConsoleConnections.Check(name, body.Yaml)).Diagnostics }) : BadRequest(new { error = "That is not a connection's name." });

    [HttpPut("{name}")]
    [Authorize(Policies.Admin)]
    public IActionResult Save(string name, [FromBody] YamlBody body)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        var (saved, diagnostics) = connections.Save(name, body.Yaml);
        if (saved) Audit("connection-saved", name, "the connection's file was saved");
        return saved ? Ok(new { saved, diagnostics }) : UnprocessableEntity(new { saved, diagnostics });
    }

    [HttpDelete("{name}")]
    [Authorize(Policies.Admin)]
    public IActionResult Delete(string name)
    {
        if (!connections.Delete(name)) return NotFound(new { error = $"There is no connection named '{name}'." });
        Audit("connection-removed", name, "the connection's file was removed");
        return NoContent();
    }

    /// <summary>Test (plan 27a IX02): signs in as the file says and calls the system once; says in words whether it was reached and whether it accepted the sign-in. The answer holds no secret.</summary>
    [HttpPost("{name}/test")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Test(string name, CancellationToken cancellation)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        if (await connections.TestAsync(name, cancellation) is not { } result) return NotFound(new { error = $"There is no connection named '{name}'." });
        Audit("connection-tested", name, result.Ok ? "the test passed" : result.Message);
        return Ok(new { result.Ok, result.Reached, result.SignIn, result.Message, kind = result.Kind?.ToString(), result.Temporary, result.Status, result.Milliseconds });
    }

    /// <summary>
    /// Paste a webhook address: it is split into the visible template and the secret parts (a known service by its pattern, another by the segments and query keys the person marks as secret), each part is kept in the secret store, and only the template and the references come back.
    /// A rotation is then one typed secret; the template is untouched. 422 when nothing is recognised and nothing is chosen (the answer says how many path segments there are and names the query keys, so the person can choose).
    /// </summary>
    [HttpPost("{name}/webhook")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Webhook(string name, [FromBody] WebhookBody body)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        if (string.IsNullOrWhiteSpace(body.Url)) return BadRequest(new { error = "Paste the webhook's address." });
        if (await connections.StoreWebhookAsync(name, body.Url, body.PathSegments, body.QueryKeys) is not { } split)
        {
            var m = System.Text.RegularExpressions.Regex.Match(body.Url.Trim(), @"^https?://[^/?#]+(/[^?#]*)?(\?[^#]*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return BadRequest(new { error = "That is not an http or https address." });
            var segments = (m.Groups[1].Success ? m.Groups[1].Value : "").Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            var keys = (m.Groups[2].Success ? m.Groups[2].Value[1..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0]).ToArray();
            return UnprocessableEntity(new { detected = false, error = "This address is not one of the services the editor knows (Slack, Discord, Teams). Say which path segments and query values are secret.", pathSegments = segments, queryKeys = keys });
        }

        Audit("connection-webhook-split", name, $"a {split.Service} address was split: template {split.Template}, secret parts {string.Join(", ", split.Secrets.Keys)}");
        return Ok(new { detected = true, service = split.Service, format = split.Format, template = split.Template, secrets = split.Secrets });
    }

    /// <summary>Authorize (plan 27a IX03): the address of the provider's consent page for an authorization-code connection; the person is sent there and the provider returns them to <see cref="OAuthCallback"/>.</summary>
    [HttpPost("{name}/authorize")]
    [Authorize(Policies.Admin)]
    public IActionResult Authorize(string name)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        var redirect = $"{Request.Scheme}://{Request.Host}/api/outside-connections/oauth/callback";
        if (connections.BeginConsent(name, redirect) is not { } url) return BadRequest(new { error = $"`{name}` is not a saved connection with `sign_in.method: oauth2_authorization_code`." });
        Audit("connection-consent-started", name, "a person was sent to the provider to give consent");
        return Ok(new { url, redirectUri = redirect });
    }

    /// <summary>Where the provider returns the person after consent (the redirect address to register with the provider). It keeps the refresh token as a secret and sends the person back to the Connections screen with the outcome in words.</summary>
    [HttpGet("oauth/callback")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> OAuthCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken cancellation)
    {
        var outcome = await connections.CompleteConsentAsync(state, code, error, cancellation);
        Audit(outcome.Ok ? "connection-consent-given" : "connection-consent-failed", outcome.Connection ?? "-", outcome.Ok ? "the refresh token was stored" : outcome.Message);
        return Redirect($"/connections?consent={(outcome.Ok ? "ok" : "failed")}&connection={Uri.EscapeDataString(outcome.Connection ?? "")}&message={Uri.EscapeDataString(outcome.Message)}");
    }

    /// <summary>Revoke (plan 27a IX03): the provider is asked to revoke the refresh token (when the connection names a revoke address) and the token is forgotten here.</summary>
    [HttpPost("{name}/revoke")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Revoke(string name, CancellationToken cancellation)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        if (await connections.RevokeConsentAsync(name, cancellation) is not { } outcome) return BadRequest(new { error = $"`{name}` is not a saved connection with `sign_in.method: oauth2_authorization_code`." });
        Audit("connection-consent-revoked", name, outcome.Message);
        return Ok(new { outcome.Ok, outcome.Message });
    }

    /// <summary>Keeps a typed secret (a key, a password, a client secret, a certificate) in the console's secret store under the connection's name and gives the reference to write in the file.</summary>
    [HttpPut("{name}/secrets/{field}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetSecret(string name, string field, [FromBody] SecretBody body)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        if (!ConsoleConnections.IsSecretField(field)) return BadRequest(new { error = $"A secret is one of: {string.Join(", ", ConsoleConnections.SecretFields)}, or the name of a placeholder of the endpoint (letters, digits and _)." });
        if (string.IsNullOrWhiteSpace(body.Value)) return BadRequest(new { error = "Type the secret." });
        var reference = await connections.StoreSecretAsync(name, field, body.Value.Trim());
        Audit("connection-secret-set", name, $"a {field} was stored for the connection, as {reference}");
        return Ok(new { stored = true, reference });
    }
}
