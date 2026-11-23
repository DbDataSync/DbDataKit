using DbDataKit.State;
using System.Text.Json.Nodes;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>The body of <c>POST api/deploys</c>: the build (Workspace server, project, workspace, build), the target, and what the deploy of its kind takes (<see cref="DeployBuildRequest"/>).</summary>
public sealed record DeployBody(string? WorkspaceServer, string? Project, string? Workspace, string? Build, string? Target, string? Channel = null, bool Serve = false, int? ExpectedVersion = null,
    Dictionary<string, string>? Bind = null, string? DataSource = null, List<string>? Adopt = null, string? Model = null, string? Note = null);

/// <summary>
/// The deploy of a build from a Workspace server to a Runner or a Gateway, by the console as the signed-in person (<see cref="ConsoleDeploys"/>): <c>POST api/deploys</c> deploys, <c>GET api/deploys?workspace_server=&amp;project=&amp;workspace=&amp;build=</c>
/// lists what was deployed from a Workspace server, newest first (each parameter after the server narrows the one before), the record the Builds and Runs tabs read.
/// </summary>
[ApiController]
[Route("api/deploys")]
public sealed class DeploysController(ConsoleDeploys deploys) : ControllerBase
{
    private RelayPerson Person => new(User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown", User.Identity?.Name, User.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer);

    [HttpPost]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Deploy([FromBody] DeployBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.WorkspaceServer) || string.IsNullOrWhiteSpace(body.Project) || string.IsNullOrWhiteSpace(body.Workspace) || string.IsNullOrWhiteSpace(body.Build) || string.IsNullOrWhiteSpace(body.Target))
            return BadRequest(new { error = "Name the build (\"workspaceServer\", \"project\", \"workspace\", \"build\") and the Runner or Gateway it goes to (\"target\")." });
        var done = await deploys.DeployAsync(new DeployBuildRequest(body.WorkspaceServer, body.Project, body.Workspace, body.Build, body.Target, body.Channel, body.Serve, body.ExpectedVersion, body.Bind, body.DataSource, body.Adopt,
            body.Model, body.Note), Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    [HttpGet]
    [Authorize(Policies.Viewer)]
    public IActionResult List([FromQuery(Name = "workspace_server")] string? workspaceServer, [FromQuery] string? project, [FromQuery] string? workspace, [FromQuery] string? build)
    {
        if (string.IsNullOrWhiteSpace(workspaceServer)) return BadRequest(new { error = "Name the Workspace server (workspace_server) whose builds' deploys to list." });
        var found = deploys.List(workspaceServer, project, workspace, build);
        return Ok(new JsonObject { ["deploys"] = new JsonArray([.. found.Select(d => (JsonNode)ConsoleDeploys.Describe(d))]) });
    }
}
