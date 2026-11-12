using System.Text.Json.Nodes;
using DbDataKit.State.Tracking;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Tracking;
using Resolution = DbDataKit.State.Tracking.TrackingResolution;
using SettingResolution = DbDataSync.Core.Config.TrackingResolution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Where one replication keeps its tracking state and why (plan 28c E17, the Sync half of E18's screen): the mode asked for (or the default), the mode that applies, the primary and the reason
/// when they differ, and, when the state is on the target, the state's copy of the units (how far the follower is, the open divergence). Read-only here: the setting is the <c>tracking:</c> section
/// of the replication's file, edited with the rest of it. Resolving a divergence is <c>POST api/v1/tracking/divergences/{id}/resolve</c>.
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/tracking")]
public sealed class ReplicationTrackingController(ConfigRepository configRepository, SyncTrackingService tracking, StateDatabase state) : ControllerBase
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
        var resolved = SettingResolution.Resolve(task.Tracking, driver);
        var retention = SettingResolution.RetentionOf(task.Tracking);
        return Ok(new ReplicationTrackingStatus(
            resolved.RequestedMode, resolved.RequestedPrimary, resolved.EffectiveMode, resolved.EffectivePrimary,
            resolved.Defaulted, resolved.Reason, driver, driver is not null && SettingResolution.TargetsThatCanHoldState.Contains(driver),
            driver is not null && SettingResolution.TargetWriterEngines.Contains(driver),
            resolved.EffectiveMode == "state" ? null : CopyOf(replicationName), retention.EffectiveUnits(), retention.EffectiveDays()));
    }

    /// <summary>Follows the replication's target now: copies the units recorded since the last time and checks the positions. 409 when the replication keeps its state in the runner's state only.</summary>
    [Authorize(Policies.Admin)]
    [HttpPost("catch-up")]
    public async Task<ActionResult<ReplicationTrackingStatus>> CatchUp(string replicationName, CancellationToken cancellationToken)
    {
        if (await tracking.FollowAsync(replicationName, cancellationToken) is null)
            return Conflict(new { error = $"Replication '{replicationName}' keeps its state in the runner's state only (or its target is not known), so there are no units on a target to copy." });
        return Get(replicationName);
    }

    private TrackingCopyStatus? CopyOf(string replication)
    {
        var last = tracking.Last(replication);
        var divergences = new StateTrackingDivergences(state).List().Where(d => d.Stream == SyncUnitStream.Name && d.Identity.StartsWith("syncunit:", StringComparison.Ordinal) && SyncUnitStream.ReplicationOf(d.Identity) == replication).ToList();
        var open = divergences.FirstOrDefault(d => d.IsOpen);
        if (last is null && divergences.Count == 0) return null;
        var rows = last is null ? 0 : new StateTrackingStore(state).Count(last.Identity, SyncUnitStream.Name);
        return new TrackingCopyStatus(last?.Identity, rows, last?.At, last?.CaughtUp ?? false, last?.Error, open?.Id, open?.Reason, open?.DivergedAt, divergences.Count);
    }
}

/// <summary>The answer of <c>GET api/replications/{name}/tracking</c>.</summary>
/// <param name="TargetDriver">The driver of the replication's target connection; null when the replication names none at its level.</param>
/// <param name="TargetCanHoldState">Whether that driver's writers can carry a state table once they are built.</param>
/// <param name="TargetWritersBuilt">Whether the writers that keep the state on this target engine are built (SQL Server and PostgreSQL).</param>
/// <param name="Copy">The state's copy of the target's units; null when the state is the runner's only or nothing was followed yet.</param>
/// <param name="RetentionUnits">How many of a mapping's newest units the target's table always keeps (<c>tracking.retention</c>).</param>
/// <param name="RetentionDays">How many days of units it always keeps.</param>
public sealed record ReplicationTrackingStatus(
    string RequestedMode, string? RequestedPrimary, string EffectiveMode, string? EffectivePrimary, bool Defaulted, string Reason,
    string? TargetDriver, bool TargetCanHoldState, bool TargetWritersBuilt, TrackingCopyStatus? Copy, int RetentionUnits, double RetentionDays);

/// <summary>What the follower of one replication has done: the rows the state's copy holds, when it last looked, whether it holds everything, what went wrong, and the open divergence.</summary>
public sealed record TrackingCopyStatus(
    string? Identity, int Rows, DateTimeOffset? FollowedAt, bool CaughtUp, string? Error, long? DivergenceId, string? DivergenceReason, DateTimeOffset? DivergedAt, int Divergences);

/// <summary>
/// The Sync server's side of the console's <c>/tracking</c> page (plan 28c E17, E18): <c>GET api/v1/tracking</c> in the shape Build's server and the runner answer with, and the resolve of a divergence
/// (administrator; <c>{"resolution":"archive"|"reset"}</c>) through the same follower rules as Build's, audited in the server's management audit.
/// </summary>
[ApiController]
public sealed class TrackingV1Controller(ConfigRepository configRepository, StateDatabase state, ApiOptions options) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet("~/api/v1/tracking")]
    public IActionResult Get() =>
        Ok(SyncTrackingDocument.Build(state, replication =>
        {
            try
            {
                var task = configRepository.LoadReplicationTask(replication);
                string? driver = null;
                if (task.Endpoints.Target?.ConnectionName is { Length: > 0 } connection)
                    try { driver = configRepository.LoadConnection(connection).DriverType; } catch (FileNotFoundException) { }
                var resolved = SettingResolution.Resolve(task.Tracking, driver);
                return (resolved.EffectiveMode, resolved.EffectivePrimary);
            }
            catch (Exception ex) when (ex is FileNotFoundException or ConfigFileException or ConfigValidationException) { return null; }
        }));

    [Authorize(Policies.Admin)]
    [HttpPost("~/api/v1/tracking/divergences/{id:long}/resolve")]
    public async Task<IActionResult> Resolve(long id, [FromBody] JsonObject? body, CancellationToken cancellationToken)
    {
        var resolution = (string?)body?["resolution"] switch
        {
            "archive" => Resolution.Archive,
            "reset" => Resolution.ResetCopy,
            _ => (Resolution?)null,
        };
        if (resolution is null) return BadRequest(new { error = "Say how: `resolution` is `archive` (keep the copy's later rows apart) or `reset` (delete them)." });

        var actor = User.Identity?.Name ?? "unknown";
        var result = await SyncTrackingDocument.ResolveAsync(state, id, resolution.Value, actor, cancellationToken);
        if (result.Divergence is { } divergence && result.Outcome is SyncResolveOutcome.Resolved or SyncResolveOutcome.ClosedMeanwhile)
            new DbDataKit.Web.Trust.FileAuditLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.StateDbPath))!, "management-audit.log")).Append(new DbDataKit.Web.Trust.AuditEntry(
                TimeProvider.System.GetUtcNow(), "command", "dbdatasync", actor, actor, false, "tracking.resolve", $"{divergence.Identity}/{divergence.Stream}",
                result.Outcome == SyncResolveOutcome.Resolved ? "ok" : "failed", $"divergence {id}, {(resolution == Resolution.Archive ? "archive" : "reset")}"));
        return result.Outcome switch
        {
            SyncResolveOutcome.Resolved => Ok(new JsonObject { ["resolved"] = true }),
            SyncResolveOutcome.NotFound => NotFound(new { error = result.Message }),
            _ => Conflict(new { error = result.Message }),
        };
    }
}
