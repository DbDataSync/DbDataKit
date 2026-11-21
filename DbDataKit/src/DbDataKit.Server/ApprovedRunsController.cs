using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>What a person approved before a DDL task runs: the exact text of the statements they were shown, that they approve it, and the run's inputs and version.</summary>
/// <param name="Statements">The exact statements the person was shown; the Runner records their digest with the run.</param>
/// <param name="Approved">True when the person approved the statements; anything else is refused.</param>
/// <param name="Inputs">The inputs of the run, all of them (a plan that travels as an input is what was approved).</param>
/// <param name="Version">The version of the package to run, or null for the served one.</param>
public sealed record ApprovedRunBody(string? Statements, bool Approved, Dictionary<string, string>? Inputs = null, string? Version = null);

/// <summary>
/// The only way a DDL task (a task that creates, alters or drops tables) starts: <c>POST api/runner-runs/{server}/{package}/{channel}/{task}</c> after a person approved the exact statements. The console signs the request with its own key and the Runner
/// refuses a DDL run without that signature, so the Runner's own screens, its command line and a paired server cannot start one. The person must hold the operator role on the Runner.
/// </summary>
[ApiController]
[Route("api/runner-runs")]
public sealed class ApprovedRunsController(ConsoleInstances instances, ConsoleRelay relay) : ControllerBase
{
    [HttpPost("{server}/{package}/{channel}/{task}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Run(string server, string package, string channel, string task, [FromBody] ApprovedRunBody body, CancellationToken ct)
    {
        if (!body.Approved || string.IsNullOrWhiteSpace(body.Statements))
            return BadRequest(new { error = "A person approves the exact statements first: send them as \"statements\" with \"approved\": true." });
        if (TaskRef.Problem(package, channel, task) is { } problem) return BadRequest(new { error = problem });
        if (instances.Registry.Find(server) is not { } instance) return NotFound(new { error = $"No server '{server}' is registered." });
        if ((await instances.StatusAsync(instance, ct)).Product != ServerProducts.Runner) return Conflict(new { error = $"'{instance.Name}' is not a runner." });
        if (User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value is not { } personId || !Enum.TryParse<UserRole>(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var role))
            return Unauthorized(new { error = "Sign in first." });

        var (status, answer) = await relay.RunApprovedAsync(instance, personId, User.Identity?.Name, role, new TaskRef(package, channel, task), body.Version, body.Inputs ?? [], body.Statements, ct);
        return StatusCode(status, answer);
    }
}
