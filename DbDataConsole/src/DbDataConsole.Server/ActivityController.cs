using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataConsole.Server;

/// <summary>
/// What every service of the install did, read from one database (plan 29a Q19). When the install's state is on a server database the services write their runs, audit
/// entries and notifications there and the console reads them with one query each; <c>shared</c> says so. On the console's own SQLite file nothing else writes, so the
/// answer is <c>shared: false</c> with nothing in it, and the screens keep asking each server as before.
/// </summary>
[ApiController]
[Route("api/activity")]
public sealed class ActivityController(StateDatabase state, ConsoleActivitySource source) : ControllerBase
{
    [HttpGet("runs")]
    [Authorize(Policies.Viewer)]
    public IActionResult Runs([FromQuery] string? service, [FromQuery] string? job, [FromQuery] RunOutcome? outcome, [FromQuery] DateTimeOffset? before, [FromQuery] int limit = 100)
    {
        if (limit is < 1 or > 500) return BadRequest(new { error = "limit is between 1 and 500." });
        if (!source.Shared) return Ok(new { shared = false, runs = Array.Empty<object>() });
        var runs = new RunStore(state, ConsoleState.ServiceId).Read(new RunQuery(service, job, outcome, StartedBefore: before, Limit: limit));
        return Ok(new { shared = true, runs = runs.Select(r => new { id = r.Id, service = r.Service, job = r.Run.JobId, started = r.Run.Started, finished = r.Run.Finished, outcome = r.Run.Outcome, rows = r.Run.Rows, failedItem = r.Run.FailedItem, failureKind = r.Run.Failure?.Kind, failureMessage = r.Run.Failure?.Message }) });
    }

    [HttpGet("audit")]
    [Authorize(Policies.Admin)]
    public IActionResult Audit([FromQuery] string? service, [FromQuery] string? verbPrefix, [FromQuery] long? beforeId, [FromQuery] int limit = 100)
    {
        if (limit is < 1 or > 500) return BadRequest(new { error = "limit is between 1 and 500." });
        if (!source.Shared) return Ok(new { shared = false, entries = Array.Empty<object>() });
        var entries = new AuditLog(state, ConsoleState.ServiceId).Read(new AuditQuery(BeforeId: beforeId, VerbPrefix: verbPrefix, ServiceId: service, Limit: limit));
        return Ok(new { shared = true, entries = entries.Select(e => new { id = e.Id, service = e.ServiceId, at = e.At, actor = e.Entry.ActorId, verb = e.Entry.Verb, outcome = e.Entry.Outcome, targetKind = e.Entry.TargetKind, targetId = e.Entry.TargetId }) });
    }

    [HttpGet("notifications")]
    [Authorize(Policies.Viewer)]
    public IActionResult Notifications([FromQuery] long? sinceId)
    {
        if (!source.Shared) return Ok(new { shared = false, notifications = Array.Empty<object>() });
        return Ok(new { shared = true, notifications = new NotificationFeedStore(state, ConsoleState.ServiceId, allServices: true).List(sinceId) });
    }
}

/// <summary>Whether the install's state is shared with its services: true on a server database, false on the console's own SQLite file.</summary>
public sealed record ConsoleActivitySource(bool Shared);
