using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// The replication-wide Provisioning tab's backend — phase 105. Distinct from
/// <see cref="ProvisioningController"/> beside it, which is the per-mapping Setup card: this aggregates
/// every table mapping's plan into one page, grouped by server and deduplicated, rather than answering
/// for one mapping at a time.
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/provisioning")]
public sealed class ReplicationProvisioningController(ProvisioningService provisioningService, DbDataSync.Api.Runner.RunnerProvisioning onRunner, DbDataKit.Web.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>The replication-wide Apply on the runner (plan 28c E21, step d): the chosen steps become one run of the set's task <c>provision</c> for the signed-in person (202 with the run to follow).</summary>
    [Authorize(Policies.Admin)]
    [HttpPost("run")]
    public async Task<IActionResult> RunOnRunner(string replicationName, [FromBody] ApplyReplicationProvisioningRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await ProvisioningOnRunner.StartAsync(() => onRunner.ApplyStepsAsync(replicationName, request.StepIds ?? [], ProvisioningOnRunner.PersonOf(currentUser), cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>A provisioning run as the runner holds it: its outcome and, once it ended, the steps it reported (404 when the runner has no such run of the task).</summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> Run(string replicationName, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await onRunner.RunAsync(runId, cancellationToken) is { } run ? Ok(run) : NotFound(new { error = $"The runner has no provisioning run {runId}." });
        }
        catch (DbDataSync.Api.Runner.OwnershipException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (DbDataKit.Runner.Client.RunnerClientException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = $"The runner could not be asked: {ex.Message}" });
        }
    }

    [Authorize(Policies.Viewer)]
    [HttpGet]
    public async Task<ActionResult<ReplicationProvisioningPlan>> GetPlan(
        string replicationName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.GetReplicationPlanAsync(replicationName, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Applies exactly the selected steps, re-planning first — see
    /// <see cref="ProvisioningService.ApplyReplicationPlanAsync"/> for why. Never a 4xx for a step id the
    /// fresh plan no longer contains: that comes back as an ordinary
    /// <see cref="ProvisioningStepOutcome.NoLongerNeeded"/> result, not a request failure.
    /// </summary>
    /// <remarks>Runs DDL. Was Admin-only only by the fallback policy — the comment this replaced
    /// claimed the fallback was "any authenticated user," which is not what <c>Program.cs</c>'s
    /// <c>AddAuthorizationBuilder</c> actually configures (<c>RequireAuthenticatedUser().RequireRole(Admin)</c>,
    /// checked directly, not assumed). Not a behavior change: a Viewer could never reach this either
    /// way. Stated explicitly now, matching <see cref="ProvisioningController.Apply"/> beside it and
    /// every other Admin controller in this app.</remarks>
    [Authorize(Policies.Admin)]
    [HttpPost("apply")]
    public async Task<ActionResult<ReplicationProvisioningApplyResult>> Apply(
        string replicationName, [FromBody] ApplyReplicationProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.ApplyReplicationPlanAsync(
                replicationName, request.StepIds ?? [], cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record ApplyReplicationProvisioningRequest(IReadOnlyList<string>? StepIds);

/// <summary>The shared answer of the two Apply routes that start the provisioning task on the runner (plan 28c E21, step d).</summary>
internal static class ProvisioningOnRunner
{
    /// <summary>The person the console vouched for, as the run records them beside this server; the system's name where nobody signed in.</summary>
    public static string PersonOf(DbDataKit.Web.Auth.CurrentUser user) => user.Name is { Length: > 0 } name ? name : user.Author.Name;

    public static async Task<IActionResult> StartAsync(Func<Task<DbDataSync.Api.Runner.ProvisioningRunStarted>> start)
    {
        try
        {
            return new ObjectResult(await start()) { StatusCode = StatusCodes.Status202Accepted };
        }
        catch (DbDataSync.Api.Runner.OwnershipException ex)
        {
            return new ConflictObjectResult(new { error = ex.Message });
        }
        catch (DbDataKit.Runner.Client.RunnerClientException ex)
        {
            return new ConflictObjectResult(new { error = $"The runner refused: {ex.Message}" });
        }
    }
}
