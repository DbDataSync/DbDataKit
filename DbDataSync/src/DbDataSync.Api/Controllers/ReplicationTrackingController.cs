using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Where one replication keeps its tracking state and why (plan 28c E17, the Sync half of E18's screen): the mode asked for (or the default), the mode that applies, the primary and the reason
/// when they differ. Read-only: the setting is the <c>tracking:</c> section of the replication's file, edited with the rest of it.
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/tracking")]
public sealed class ReplicationTrackingController(ConfigRepository configRepository) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<ReplicationTrackingStatus> Get(string replicationName)
    {
        ReplicationTaskConfig task;
        try { task = configRepository.LoadReplicationTask(replicationName); }
        catch (FileNotFoundException) { return NotFound(); }

        string? driver = null;
        if (task.Endpoints.Target?.ConnectionName is { Length: > 0 } connection)
        {
            try { driver = configRepository.LoadConnection(connection).DriverType; }
            catch (FileNotFoundException) { }
        }
        var resolved = TrackingResolution.Resolve(task.Tracking, driver);
        return Ok(new ReplicationTrackingStatus(
            resolved.RequestedMode, resolved.RequestedPrimary, resolved.EffectiveMode, resolved.EffectivePrimary,
            resolved.Defaulted, resolved.Reason, driver, driver is not null && TrackingResolution.TargetsThatCanHoldState.Contains(driver),
            driver is not null && TrackingResolution.TargetWriterEngines.Contains(driver)));
    }
}

/// <summary>The answer of <c>GET api/replications/{name}/tracking</c>.</summary>
/// <param name="TargetDriver">The driver of the replication's target connection; null when the replication names none at its level.</param>
/// <param name="TargetCanHoldState">Whether that driver's writers can carry a state table once they are built.</param>
/// <param name="TargetWritersBuilt">Whether the writers that keep the state on this target engine are built (SQL Server and PostgreSQL).</param>
public sealed record ReplicationTrackingStatus(
    string RequestedMode, string? RequestedPrimary, string EffectiveMode, string? EffectivePrimary, bool Defaulted, string Reason,
    string? TargetDriver, bool TargetCanHoldState, bool TargetWritersBuilt);
