using DbDataKit.Connections;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

public sealed record SecretBody(string Value);

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
    public IActionResult List() => Ok(new { kinds = new[] { ConnectionKinds.Http, ConnectionKinds.ModelProvider }, connections = connections.List() });

    [HttpGet("template")]
    [Authorize(Policies.Admin)]
    public IActionResult Template([FromQuery] string kind, [FromQuery] string name) =>
        !ConsoleConnections.IsName(name) ? BadRequest(new { error = "A connection's name starts with a letter and has letters, digits, '-', '_' and '.'." })
        : kind is ConnectionKinds.Http or ConnectionKinds.ModelProvider ? Ok(new { yaml = ConsoleConnections.Template(kind, name) })
        : BadRequest(new { error = $"The console keeps connections of kind {ConnectionKinds.Http} and {ConnectionKinds.ModelProvider}." });

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

    /// <summary>Keeps a typed secret (a key, a password, a client secret, a certificate) in the console's secret store under the connection's name and gives the reference to write in the file.</summary>
    [HttpPut("{name}/secrets/{field}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetSecret(string name, string field, [FromBody] SecretBody body)
    {
        if (!ConsoleConnections.IsName(name)) return BadRequest(new { error = "That is not a connection's name." });
        if (!ConsoleConnections.SecretFields.Contains(field, StringComparer.Ordinal)) return BadRequest(new { error = $"A secret is one of: {string.Join(", ", ConsoleConnections.SecretFields)}." });
        if (string.IsNullOrWhiteSpace(body.Value)) return BadRequest(new { error = "Type the secret." });
        var reference = await connections.StoreSecretAsync(name, field, body.Value.Trim());
        Audit("connection-secret-set", name, $"a {field} was stored for the connection, as {reference}");
        return Ok(new { stored = true, reference });
    }
}
