using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The approvals of the <c>mcp</c> module on the management API, for the console's inbox (plan 27a AG33): <c>GET /api/v1/mcp/approvals[?state=all]</c> and <c>POST /api/v1/mcp/approvals/{id}/approve|deny</c>. The person is the one the console's
/// token names (a person who signed in to the console, so an answer is a person's and never a token handed to an agent), with the roles the token carries on this server; the rules are <see cref="McpApprovals"/>'s, the same as on the module's own port.
/// </summary>
public static class GatewayMcpManagement
{
    public const string Prefix = "/api/v1/mcp/approvals";

    /// <summary>The person a management token names, as the module's rules see them: an interactive person with the token's roles.</summary>
    public static StoreCaller PersonOf(ValidatedToken token) => new(token.SubjectId, token.SubjectName, IsAgent: false, token.Roles, Interactive: token.IsConsole);

    public static async Task<(int Status, JsonNode Body)> HandleAsync(McpApprovals approvals, ValidatedToken token, string method, string path, string? body, CancellationToken cancellationToken)
    {
        var person = PersonOf(token);
        var rest = path.Length > Prefix.Length ? path[Prefix.Length..] : "";
        var query = rest.IndexOf('?') is var q and >= 0 ? rest[q..] : "";
        if (query.Length > 0) rest = rest[..rest.IndexOf('?')];
        if (rest.Length == 0)
        {
            if (method != "GET") return (405, new JsonObject { ["error"] = "The list is read with GET." });
            if (!McpApprovals.MayAnswer(person)) return (403, new JsonObject { ["error"] = "Listing approval requests needs the approver or admin role on this server." });
            return (200, new JsonObject { ["approvals"] = new JsonArray([.. approvals.List(pendingOnly: !query.Contains("state=all", StringComparison.Ordinal)).Select(a => (JsonNode)McpApprovals.Json(a))]) });
        }
        var parts = rest.Trim('/').Split('/');
        if (parts.Length == 2 && method == "POST" && parts[1] is "approve" or "deny" && Guid.TryParse(parts[0], out var id))
        {
            string? note = null;
            try { note = body is { Length: > 0 } ? JsonNode.Parse(body)?["note"]?.ToString() : null; } catch (System.Text.Json.JsonException) { }
            var decision = await approvals.DecideAsync(person, id, parts[1] == "approve", note, cancellationToken);
            return (decision.Status, decision.Body);
        }
        return (404, new JsonObject { ["error"] = "There is no such approval route." });
    }
}
