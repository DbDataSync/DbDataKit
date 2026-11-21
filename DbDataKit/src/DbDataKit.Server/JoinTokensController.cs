using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <param name="Product">The server the token is for: <c>gateway</c>, <c>runner</c> or <c>workspace</c>.</param>
/// <param name="Roles">The role ceiling the console is paired with on that server; admin when empty.</param>
/// <param name="Group">The group the server joins; none when empty.</param>
/// <param name="Minutes">How long the token works, 1 to 1440; 15 when not given.</param>
/// <param name="Url">The address the server's machine reaches the console at; the console's own public address when empty.</param>
public sealed record JoinTokenRequest(string? Product, IReadOnlyList<string>? Roles, string? Group, int? Minutes, string? Url);

/// <summary>A token just made: the command to run on the server's machine, shown once, and the token's id, scope and end.</summary>
/// <param name="Loopback">The command names a loopback address, which only a server on the console's machine reaches.</param>
public sealed record JoinTokenMade(string Command, string TokenId, string Product, IReadOnlyList<string> Roles, string? Group, DateTimeOffset Expires, bool Loopback);

/// <summary>A token that waits to be used, as the Add a server screen lists it: never its value.</summary>
public sealed record WaitingJoinToken(string Id, string Product, IReadOnlyList<string> Roles, string? Group, string MadeBy, DateTimeOffset MadeAt, DateTimeOffset Expires, int Tries);

/// <summary>
/// The join tokens of the Add a server screen: make one and get the command that spends it, list the ones still waiting, cancel one. Administrators only,
/// by the fallback policy: a token lets a server join with the console's signing key. Making and cancelling a token are audited by
/// <see cref="ConsoleJoins"/> (<c>server.invite</c>, <c>server.invite.cancel</c>), never with the token's value.
/// Design: docs/install.md#pairing
/// </summary>
[ApiController]
[Route("api/join-tokens")]
public sealed class JoinTokensController(ConsoleJoins joins, ConsoleSettings settings) : ControllerBase
{
    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";

    [HttpGet]
    public ActionResult<IReadOnlyList<WaitingJoinToken>> Waiting() =>
        Ok(joins.Waiting().Select(t => new WaitingJoinToken(t.Id, t.Product, [.. ServerRoles.Names(t.Ceiling)], t.Group, t.MadeByName ?? t.MadeBy, t.MadeAt, t.ExpiresAt, t.Tries)).ToList());

    [HttpPost]
    public ActionResult<JoinTokenMade> Make([FromBody] JoinTokenRequest request)
    {
        var product = (request.Product ?? "").Trim().ToLowerInvariant();
        if (!ConsoleJoins.Products.ContainsKey(product)) return BadRequest(new { error = "Choose the server that joins: a Gateway, a Runner or a Workspace." });
        var ceiling = ServerRole.Admin;
        if (request.Roles is { Count: > 0 } roles && (!ServerRoles.TryParse(roles, out ceiling) || ceiling == ServerRole.None))
            return BadRequest(new { error = $"'{string.Join(',', roles)}' is not a role ceiling: choose from viewer, operator, author, approver, admin and publisher." });
        if (request.Minutes is { } minutes && (minutes < 1 || minutes > JoinTokens.MaxLifetime.TotalMinutes))
            return BadRequest(new { error = $"A token works from 1 to {JoinTokens.MaxLifetime.TotalMinutes:0} minutes." });
        var scope = new JoinScope(product, ceiling, string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim());
        if (scope.Problem() is { } problem) return BadRequest(new { error = $"The join token cannot be made: {problem}." });

        var given = string.IsNullOrWhiteSpace(request.Url) ? settings.Public.GetLeftPart(UriPartial.Authority) : request.Url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(given, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps || address.AbsolutePath != "/" || address.Query.Length > 0)
            return BadRequest(new { error = $"The console's address '{given}' cannot be used: it is https://, a host and a port, such as https://console.example.com:7800." });
        address = ServerJoin.AddressFor(address);

        var (minted, command) = joins.Invite(scope, address, PersonId, User.Identity?.Name, request.Minutes is { } m ? TimeSpan.FromMinutes(m) : null);
        // the answer holds the token: no cache along the way keeps it
        Response.Headers.CacheControl = "no-store";
        return Ok(new JoinTokenMade(command, minted.Record.Id, product, [.. ServerRoles.Names(ceiling)], scope.Group, minted.Record.ExpiresAt, address.IsLoopback));
    }

    [HttpDelete("{id}")]
    public IActionResult Cancel(string id)
    {
        if (joins.Cancel(id, PersonId) is not { } token) return NotFound(new { error = "There is no such join token." });
        return token.StateAt(DateTimeOffset.UtcNow) == JoinTokenState.Cancelled
            ? NoContent()
            : Conflict(new { error = token.SpentAt is not null ? "That token was already used: the server it was made for has joined." : "That token no longer works: it ended." });
    }
}
