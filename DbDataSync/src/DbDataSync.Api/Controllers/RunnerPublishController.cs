using DbDataKit.Runner.Client;
using DbDataKit.Web.Auth;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Runner;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>The body of a binding: the package's slots in the linked channel, each to the name of the connection it is bound to on the runner.</summary>
public sealed record BindRunnerSlotsRequest(IReadOnlyDictionary<string, string> Slots);

/// <summary>
/// Publishing the replication set to the paired runner (plan 28c E08): what the verb <c>dbdatasync package publish --to runner</c> does, as calls the Runner area of the Sync screens makes. It uses the link <c>PUT /api/runner</c> stores
/// (server, channel, package, client directory). A viewer reads what the runner holds and what a publish would hold; an administrator publishes (stage or serve) and binds. A refusal answers 409 with the words of whoever refused: the verb's
/// (a mapping whose column cache is missing, two replications that make one task name) or the runner's (an unbound slot and its channel); an unreachable runner answers 502.
/// </summary>
[ApiController]
[Route("api/runner")]
public sealed class RunnerPublishController(ReplicationSetPublishing publishing, CurrentUser currentUser) : ControllerBase
{
    private string Actor => currentUser.Name ?? currentUser.Author.Name;

    private string UserId => currentUser.Id ?? Actor;

    [Authorize(Policies.Viewer)]
    [HttpGet("package")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        try { return Ok(await publishing.StatusAsync(cancellationToken)); }
        catch (Exception ex) when (ex is RunnerClientException or HttpRequestException) { return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message }); }
    }

    [Authorize(Policies.Viewer)]
    [HttpPost("package/check")]
    public async Task<IActionResult> Check(CancellationToken cancellationToken)
    {
        try { return Ok(await publishing.CheckAsync(cancellationToken)); }
        catch (ReplicationSetRefusedException ex) { return Conflict(new { error = ex.Message }); }
    }

    [Authorize(Policies.Admin)]
    [HttpPost("package/publish")]
    public async Task<IActionResult> Publish([FromBody] PublishRunnerRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await publishing.PublishAsync(request, UserId, Actor, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) when (ex is ReplicationSetRefusedException or OwnershipException or RunnerClientException) { return Conflict(new { error = ex.Message }); }
    }

    [Authorize(Policies.Admin)]
    [HttpPut("package/bindings")]
    public async Task<IActionResult> Bind([FromBody] BindRunnerSlotsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await publishing.BindAsync(request.Slots ?? new Dictionary<string, string>(), UserId, Actor, cancellationToken);
            return Ok(await publishing.StatusAsync(cancellationToken));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) when (ex is OwnershipException or RunnerClientException) { return Conflict(new { error = ex.Message }); }
    }
}
