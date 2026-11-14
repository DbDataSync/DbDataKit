using DbDataKit.Runner.Client;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Runner;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>The runner the replications run on: where it is, the channel and the package its replications are published to.</summary>
public sealed record SetRunnerLinkRequest(string Server, string Channel, string Package, string? ClientDirectory = null);

/// <summary>One replication's task as the runner says it (its row: next due, running, queued, paused, last run), or why the runner could not be asked.</summary>
public sealed record ReplicationRunnerView(string Replication, string Task, RunnerLink? Link, ClientTaskStatus? Runner, string? RunnerError);

/// <summary>
/// The runner of this server's replications (plan 28c E22: the Runner is Sync's only scheduler, owner decision S2 of 2026-11-14): the link, read by any viewer and set by an administrator, and a
/// replication's own row on the runner. There is no owner to move any more.
/// </summary>
[ApiController]
[Route("api")]
public sealed class RunnerLinkController(RunnerAccess runner) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/runner")]
    public async Task<ActionResult<ReplicationRunnerView>> Get(string name, CancellationToken cancellationToken)
    {
        var task = RunnerAccess.TaskOf(name);
        ClientTaskStatus? status = null;
        string? error = null;
        if (runner.Open() is { } sync)
        {
            try { status = await sync.StatusAsync(task, cancellationToken); }
            catch (Exception ex) when (ex is RunnerClientException or HttpRequestException or TaskCanceledException) { error = ex.Message; }
        }
        else
        {
            error = "No runner is linked: this server runs nothing until one is set and the replication set is published to it.";
        }

        return Ok(new ReplicationRunnerView(name, task, runner.Link, status, error));
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
        try
        {
            runner.File.SetLink(new RunnerLink(request.Server.Trim(), request.Channel.Trim(), request.Package.Trim(), string.IsNullOrWhiteSpace(request.ClientDirectory) ? null : request.ClientDirectory.Trim()));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        return Ok(runner.Link);
    }
}
