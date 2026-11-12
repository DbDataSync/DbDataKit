using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>What a person asks the console to promote (plan 30 PB06): a package of a runner group (the embedded runner is <c>console-runner</c>) to another group, in the channel asked there.</summary>
/// <param name="From">The server the version is read from (its id or name).</param>
/// <param name="To">The server it is published to.</param>
/// <param name="Version">The version to send; absent is the one <paramref name="FromChannel"/> serves in the source group.</param>
/// <param name="Bindings">The target channel's own bindings (slot to the name of what it is bound to there, never a value), written before the version is published.</param>
public sealed record PromotionBody(string From, string To, string Name, string FromChannel, string ToChannel, int? Version = null, string? Mode = null, Dictionary<string, string>? Bindings = null, int? ExpectedVersion = null, string? Note = null, string? Kind = null);

/// <summary>
/// The Promote action of the runner's Packages screen (plan 30 PB06): <c>POST api/promotions</c> sends a version of a package from one group to another as the signed-in person (<see cref="ConsolePromotions"/>): the files the target holds are not sent again,
/// the signature travels and the target verifies it, a slot the channel does not bind and a dependency that is not active there refuse the move naming it. Administrators only (the target is written as an administrator); each group records the person.
/// </summary>
[ApiController]
[Route("api/promotions")]
public sealed class PromotionsController(ConsolePromotions promotions) : ControllerBase
{
    /// <summary>The one kind a console promotes today: the runner's packages (the rows of <see cref="ServerProducts.RunnerRoutes"/> name it).</summary>
    public const string Kind = "integration";

    private RelayPerson Person => new(User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown", User.Identity?.Name, User.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer);

    [HttpPost]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Promote([FromBody] PromotionBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.From) || string.IsNullOrWhiteSpace(body.To) || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.FromChannel) || string.IsNullOrWhiteSpace(body.ToChannel))
            return BadRequest(new { error = "Name the package, the server and channel it is read from and the server and channel it goes to." });
        if (body.Kind is not null && body.Kind != Kind) return BadRequest(new { error = $"The console promotes packages ('{Kind}'); '{body.Kind}' is not one." });
        if (body.Mode is not (null or "serve" or "stage")) return BadRequest(new { error = "The mode is 'serve' or 'stage'." });
        try
        {
            var done = await promotions.PromoteAsync(body.From, body.To, new PromoteRequest(Kind, body.Name, body.FromChannel, body.ToChannel, body.Version, body.Mode, body.Bindings, body.ExpectedVersion, body.Note), Person, ct);
            return Ok(new
            {
                kind = done.Kind, name = done.Name, fromChannel = done.FromChannel, toChannel = done.ToChannel, sourceVersion = done.SourceVersion, targetVersion = done.TargetVersion, changed = done.Changed,
                mode = done.Mode, signature = done.Signature, sent = done.Sent, skipped = done.Skipped, bound = done.Bound,
            });
        }
        catch (DefinitionPromotionException ex)
        {
            return StatusCode(ex.Status is >= 400 and < 600 ? ex.Status : 502, new { error = ex.Message, code = ex.Code });
        }
    }
}
