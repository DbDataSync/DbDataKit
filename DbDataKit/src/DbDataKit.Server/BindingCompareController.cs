using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>An adopt or a publish as the screen sends it: the item and the other side's value it saw (<c>expected</c>).</summary>
public sealed record CompareDecisionBody(string Server, string Family, string Name, string? DefinitionKind, string? Slot, string? Channel, string? Expected);

/// <summary>
/// The compare screen's API (plan 27a IX25, <c>docs/channel-bindings.md</c>): <c>GET api/bindings/compare?server=</c> lists what the console and a Runner or a Gateway both hold, marked same, differs, only here or only there (a viewer's
/// read, made as the signed-in person); <c>POST api/bindings/compare/adopt</c> takes the other side's value into the console's own files and <c>POST api/bindings/compare/publish</c> puts the console's value on the other server through plan 30's shared
/// route (an administrator's act, audited here and by the server that was written); <c>GET api/bindings/posture</c> is the one combined, read-only list of the posture choices of the console's table and the Runners'.
/// </summary>
[ApiController]
[Route("api/bindings")]
public sealed class BindingCompareController(ConsoleBindingCompare compare) : ControllerBase
{
    private RelayPerson Person => new(User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown", User.Identity?.Name, User.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer);

    [HttpGet("compare")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Compare([FromQuery] string? server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server)) return BadRequest(new { error = "Name the Runner or Gateway to compare with (its id or name)." });
        var (view, status, error) = await compare.CompareAsync(server, Person, ct);
        return view is null ? StatusCode(status, new { error }) : Ok(view);
    }

    private static IActionResult Answer(ControllerBase c, CompareOutcome o) =>
        o.Done ? c.Ok(new { done = true, item = o.Item, diagnostics = o.Diagnostics }) : c.StatusCode(o.Status, new { done = false, error = o.Error, diagnostics = o.Diagnostics });

    private static CompareDecision DecisionOf(CompareDecisionBody b) => new(b.Server, b.Family, b.Name, b.DefinitionKind, b.Slot, b.Channel, b.Expected);

    private IActionResult? Invalid(CompareDecisionBody body) =>
        string.IsNullOrWhiteSpace(body.Server) || string.IsNullOrWhiteSpace(body.Name) ? BadRequest(new { error = "Name the server and the item." })
        : body.Family is not (ConsoleBindingCompare.BindingFamily or ConsoleBindingCompare.ConnectionFamily) ? BadRequest(new { error = "The family is 'binding' or 'connection'." })
        : body.Family == ConsoleBindingCompare.BindingFamily && (string.IsNullOrWhiteSpace(body.DefinitionKind) || string.IsNullOrWhiteSpace(body.Slot) || string.IsNullOrWhiteSpace(body.Channel)) ? BadRequest(new { error = "A binding is named by the kind of its definition, the definition, the slot and the release channel." })
        : null;

    [HttpPost("compare/adopt")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Adopt([FromBody] CompareDecisionBody body, CancellationToken ct) =>
        Invalid(body) ?? Answer(this, await compare.AdoptAsync(DecisionOf(body), Person, ct));

    [HttpPost("compare/publish")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Publish([FromBody] CompareDecisionBody body, CancellationToken ct) =>
        Invalid(body) ?? Answer(this, await compare.PublishAsync(DecisionOf(body), Person, ct));

    /// <summary>Every posture choice of the console's table and of each runner's, once, with who holds it; what could not be read is named.</summary>
    [HttpGet("posture")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Posture(CancellationToken ct) => Ok(await compare.PostureAsync(Person, ct));
}
