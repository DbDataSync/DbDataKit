using System.Text.Json.Nodes;
using DbDataKit.Quality;
using DbDataKit.State;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// The results of the checks stored with the replications (plan 23a FQ17), in the form every server sends a console (<see cref="QualityDocuments"/>): <c>GET /api/quality</c> lists every dataset with its checks, the latest result of each and the
/// block, <c>GET /api/quality/history</c> is one check's results, and <c>POST /api/quality/run</c> evaluates the checks of the replication that owns a dataset now, as the tick of <see cref="ReplicationChecksService"/> does. Reading is for a
/// viewer; evaluating opens connections to the target, so it is for an administrator (the fallback policy).
/// </summary>
[ApiController]
[Route("api/quality")]
public sealed class QualityController(ReplicationQuality quality, DbDataSync.State.StateDatabase state, DbDataSync.Api.Configuration.ApiOptions options) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<JsonObject> Overview() => Ok(QualityDocuments.Overview(new QualityStore(state), canRun: true, canRelease: true));

    [Authorize(Policies.Viewer)]
    [HttpGet("history")]
    public ActionResult<JsonObject> History([FromQuery] string? dataset, [FromQuery] string? check, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(dataset) || string.IsNullOrWhiteSpace(check)) return BadRequest(new { error = "Say which check: `dataset` and `check`." });
        return Ok(QualityDocuments.History(new QualityStore(state), dataset, check, limit ?? QualityDocuments.HistoryLimit));
    }

    /// <summary>
    /// An administrator's release of a block (plan 23a FQ17): the check still fails and is still recorded, what depends on the dataset is no longer held for it, and a pass ends the release. Audited in the management audit log: who and which check, never a value.
    /// </summary>
    [HttpPost("release")]
    public ActionResult<JsonObject> Release([FromBody] QualityReleaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Dataset) || string.IsNullOrWhiteSpace(request.Check)) return BadRequest(new { error = "Say which check: `dataset` and `check`." });
        var by = User.Identity?.Name ?? "unknown";
        var released = new QualityStore(state).Release(request.Dataset, request.Check, by, TimeProvider.System.GetUtcNow());
        new DbDataKit.Web.Trust.FileAuditLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.StateDbPath))!, "management-audit.log")).Append(new DbDataKit.Web.Trust.AuditEntry(
            TimeProvider.System.GetUtcNow(), "command", "dbdatasync", by, by, false, "quality.release", $"{request.Dataset}/{request.Check}", released ? "ok" : "not blocking", ""));
        return released ? Ok(new JsonObject { ["released"] = true }) : Conflict(new { error = $"The check '{request.Check}' does not block '{request.Dataset}' now, so there is nothing to release." });
    }

    /// <summary>Evaluates the checks of the replication that has <c>dataset</c> (an identity as <c>GET /api/quality</c> lists it) and records the results; the answer is how many passed, failed or could not run.</summary>
    [HttpPost("run")]
    public async Task<ActionResult<JsonObject>> Run([FromBody] QualityRunRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Dataset)) return BadRequest(new { error = "Say which dataset to evaluate: `dataset`." });
        var replication = await quality.ReplicationOfAsync(request.Dataset, cancellationToken);
        if (replication is null) return NotFound(new { error = $"No replication has checks about '{request.Dataset}'." });
        try
        {
            var (report, notes) = await quality.EvaluateAsync(replication, runId: null, cancellationToken, request.Dataset);
            return Ok(new JsonObject
            {
                ["replication"] = replication, ["dataset"] = request.Dataset, ["evaluated"] = report.Evaluated, ["passed"] = report.Evaluated - report.Failed - report.Errors,
                ["failed"] = report.Failed, ["errors"] = report.Errors, ["blocking"] = report.Blocking,
                ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            });
        }
        catch (UnknownDatasetException ex) { return NotFound(new { error = ex.Message }); }
    }
}

public sealed record QualityRunRequest(string? Dataset);

/// <summary>The body of a release of a block: the check's dataset (an identity as the overview lists it) and the check's id.</summary>
public sealed record QualityReleaseRequest(string? Dataset, string? Check);
