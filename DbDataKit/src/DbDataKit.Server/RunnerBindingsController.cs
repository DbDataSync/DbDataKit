using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>A runner slot's new bindings: per channel the connection it uses (a console connection, whose definition the console sends first, or a name the runner knows), the scopes of the production-in-non-production choices the administrator confirms, and a note for the record.</summary>
public sealed record RunnerSlotBody(Dictionary<string, string> Channels, List<string>? Confirm = null, string? Note = null);

/// <summary>
/// The Bindings screen's calls for a runner's package slots (plan 27a IX23, <c>docs/channel-bindings.md</c>): <c>PUT api/bindings/runner/{server}/{package}/{slot}</c> changes a slot's binding (an administrator's act) and
/// <c>POST api/bindings/runner/{server}/connections/{name}/send</c> sends a console connection to the runner again. The console sends the connection's definition with its secret references, never a value, and the runner keeps its own copy;
/// a non-production channel that would use the connection production uses is refused (409, <c>needsConfirmation</c>) until confirmed and is recorded by the runner.
/// </summary>
[ApiController]
[Route("api/bindings/runner")]
public sealed class RunnerBindingsController(ConsoleRunnerBindings runners) : ControllerBase
{
    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";

    private static object SentView(ConnectionSent s) => new { connection = s.Connection, version = s.Version, unresolved = s.Unresolved.Select(u => new { path = u.Path, reference = u.Reference, problem = u.Problem }) };

    /// <summary>Sends the console connection to the runner again, so the runner's copy is the file as it is now. The answer says which references the runner cannot resolve.</summary>
    [HttpPost("{server}/connections/{name}/send")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Send(string server, string name, CancellationToken ct)
    {
        var (step, sent) = await runners.SendAsync(server, name, PersonId, User.Identity?.Name, ct);
        return step.Ok && sent is not null ? Ok(SentView(sent)) : StatusCode(step.Status, new { error = step.Error, diagnostics = step.Diagnostics });
    }

    /// <summary>Binds a runner's slot of a package: per channel one connection. The console sends each console connection's definition first, then binds; a bind the runner refuses until it is confirmed answers 409 with the choices to confirm.</summary>
    [HttpPut("{server}/{package}/{slot}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Bind(string server, string package, string slot, [FromBody] RunnerSlotBody body, CancellationToken ct)
    {
        if (body.Channels.Count == 0) return BadRequest(new { error = "Name a channel and the connection it uses." });
        var sent = new List<ConnectionSent>();
        // each console connection goes first, once: a slot bound to a connection the runner has no copy of cannot run
        foreach (var name in body.Channels.Values.Distinct(StringComparer.Ordinal).Where(n => runners.TextOf(n) is not null))
        {
            var (step, one) = await runners.SendAsync(server, name, PersonId, User.Identity?.Name, ct);
            if (!step.Ok || one is null) return StatusCode(step.Status, new { saved = false, error = step.Error, diagnostics = step.Diagnostics });
            sent.Add(one);
        }

        // production first, so the runner compares each other channel with it as it will be
        var needs = new List<UnconfirmedBinding>();
        foreach (var (channel, connection) in body.Channels.OrderBy(c => c.Key == "prod" ? 0 : 1).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var step = await runners.BindAsync(server, package, channel, slot, connection, body.Confirm, body.Note, PersonId, User.Identity?.Name, ct);
            if (step.Status == 409 && step.Needs.Count > 0) { needs.AddRange(step.Needs); continue; }
            if (!step.Ok) return StatusCode(step.Status, new { saved = false, error = step.Error, sent = sent.Select(SentView) });
        }

        if (needs.Count > 0)
            return Conflict(new { saved = false, error = "Production's connection would serve a non-production release channel. Confirm each choice to record it; the channels that needed no confirmation are bound.", needsConfirmation = needs, sent = sent.Select(SentView) });
        return Ok(new { saved = true, sent = sent.Select(SentView) });
    }
}
