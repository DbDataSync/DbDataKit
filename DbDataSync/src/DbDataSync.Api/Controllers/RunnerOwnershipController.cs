using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Runner;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>The body of a move: who is to schedule the replication (<c>old</c>, <c>observe</c> or <c>runner</c>), and how long a cutover or rollback may wait for work in flight (the default is thirty minutes).</summary>
public sealed record SetOwnerRequest(string Owner, int? WaitSeconds = null);

/// <summary>The runner the replications move to: where it is, the channel and the package its replications are published to.</summary>
public sealed record SetRunnerLinkRequest(string Server, string Channel, string Package, string? ClientDirectory = null);

/// <summary>
/// Who schedules a replication, and the runner it can move to (plan 28c E12): read for any viewer, changed by an administrator. A move is <see cref="ReplicationOwnership"/>: observe, cutover (<c>runner</c>) and rollback (<c>old</c>); a refusal
/// answers 409 with what was found and says the owner is as it was.
/// </summary>
[ApiController]
[Route("api")]
public sealed class RunnerOwnershipController(ReplicationOwnership ownership, RunnerAccess runner, CurrentUser currentUser) : ControllerBase
{
    private string Actor => currentUser.Author.Name;

    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/owner")]
    public async Task<ActionResult<OwnerView>> Get(string name, CancellationToken cancellationToken) => Ok(await ownership.ViewAsync(name, cancellationToken));

    [Authorize(Policies.Admin)]
    [HttpPut("replications/{name}/owner")]
    public async Task<IActionResult> Put(string name, [FromBody] SetOwnerRequest request, CancellationToken cancellationToken)
    {
        if (!TaskOwners.IsOwner(request.Owner))
            return BadRequest(new { error = $"'{request.Owner}' is not an owner ({string.Join(", ", TaskOwners.All)})." });
        TimeSpan? wait = request.WaitSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Max(0, seconds)) : null;
        try
        {
            var change = request.Owner switch
            {
                TaskOwners.Observe => await ownership.ObserveAsync(name, Actor, cancellationToken),
                TaskOwners.Runner => await ownership.CutoverAsync(name, Actor, wait, cancellationToken),
                _ => await ownership.RollbackAsync(name, Actor, wait, cancellationToken),
            };
            return Ok(change);
        }
        catch (Exception ex) when (ex is OwnershipException or RunnerClientException or HttpRequestException)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("runner")]
    public ActionResult<RunnerLink?> GetLink() => Ok(runner.Link);

    [Authorize(Policies.Admin)]
    [HttpPut("runner")]
    public IActionResult PutLink([FromBody] SetRunnerLinkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Channel) || string.IsNullOrWhiteSpace(request.Package))
            return BadRequest(new { error = "The runner needs a server, a channel and a package." });
        runner.File.SetLink(new RunnerLink(request.Server.Trim(), request.Channel.Trim(), request.Package.Trim(), string.IsNullOrWhiteSpace(request.ClientDirectory) ? null : request.ClientDirectory.Trim()));
        return Ok(runner.Link);
    }
}
