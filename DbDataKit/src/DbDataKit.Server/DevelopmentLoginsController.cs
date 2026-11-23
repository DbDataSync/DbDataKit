using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>
/// A development login for a project of a Workspace server (<see cref="ConsoleDevelopmentLogins"/>): <c>POST api/workspace-dev-logins/{workspace}/{project}</c> with <c>{"runner", "channel", "connection", "user"?, "password"}</c> puts the login in
/// the Runner's secret store for the project's package, the channel and the connection's slot, as the signed-in person. Any signed-in person may ask: the Workspace server decides whether they may read the project, and the Runner
/// whether they may store a secret. The answer names the secrets stored, never a value.
/// </summary>
[ApiController]
[Route("api/workspace-dev-logins/{workspace}/{project}")]
[Authorize(Policies.Viewer)]
public sealed class DevelopmentLoginsController(ConsoleDevelopmentLogins logins, CurrentUser current) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SetUp(string workspace, string project, [FromBody] DevelopmentLoginBody body, CancellationToken ct)
    {
        if (current.Id is null) return Unauthorized(new { error = "Sign in first." });
        var answer = await logins.SetUpAsync(workspace, project, body, ct);
        return StatusCode(answer.Status, answer.Body);
    }
}
