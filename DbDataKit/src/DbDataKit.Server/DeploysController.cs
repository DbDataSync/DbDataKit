using DbDataKit.State;
using System.Text.Json.Nodes;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>The body of <c>POST api/deploys</c>: the build (Workspace server, project, workspace, build), the target, and what the deploy of its kind takes (<see cref="DeployBuildRequest"/>); <c>previewInstance</c> sends an app build as that instance's preview.</summary>
public sealed record DeployBody(string? WorkspaceServer, string? Project, string? Workspace, string? Build, string? Target, string? Channel = null, bool Serve = false, int? ExpectedVersion = null,
    Dictionary<string, string>? Bind = null, string? DataSource = null, List<string>? Adopt = null, string? Model = null, string? Note = null, bool ConfirmPreflight = false, string? PreviewInstance = null);

/// <summary>The body of <c>POST api/deploys/handover</c>: the Gateway, and the clients whose definitions the console takes over (by default the Workspace servers' publisher keys the Gateway lists).</summary>
public sealed record DeployHandoverBody(string? Gateway, List<string>? Clients = null);

/// <summary>The body of <c>POST api/deploys/{id}/activate</c>: whether the person confirms the errors the preflight found (the Runner keeps the confirmation with the activation).</summary>
public sealed record DeployActivateBody(bool Confirm = false);

/// <summary>
/// The body of <c>POST api/deploys/{id}/runs</c>: the task, its inputs (names and text values), whether the version the channel serves runs it (<c>served</c>) rather than the version the deploy staged, and whether the call waits for
/// the run's answer (<c>answer</c>: the rows of a preview, a strategy's candidates, passed on and kept nowhere).
/// </summary>
public sealed record DeployRunBody(string? Task, Dictionary<string, string>? Inputs = null, bool Served = false, bool Answer = false);

/// <summary>The body of <c>POST api/deploys/{id}/binding-test</c>: the slot of the package and the role of its login (default, read or write).</summary>
public sealed record DeployBindingTestBody(string? Slot, string? Role = null);

/// <summary>The body of <c>POST api/deploys/{id}/contract</c>: the run of <c>contract</c> that read the plan, the plan's hash the person approves, and the consumers the person allows (<c>&lt;name&gt;@&lt;channel&gt;</c>, <c>gateway:&lt;name&gt;</c>).</summary>
public sealed record DeployContractBody(string? PlanRun, string? Approve, List<string>? AllowConsumer = null);

/// <summary>
/// The deploy of a build from a Workspace server to a Runner or a Gateway, by the console as the signed-in person (<see cref="ConsoleDeploys"/>): <c>POST api/deploys</c> deploys, <c>GET api/deploys?workspace_server=&amp;project=&amp;workspace=&amp;build=</c>
/// lists what was deployed from a Workspace server, newest first (each parameter after the server narrows the one before), the record the Builds and Runs tabs read. The runs of a deploy to a Runner go through the console too
/// (<see cref="ConsoleDeployRuns"/>): <c>POST api/deploys/{id}/runs</c> starts a task (with <c>answer</c>, waits for its answer), <c>GET api/deploys/{id}/runs/{run}/answer</c> takes the answer of a run started earlier, <c>GET api/deploys/{id}/runs/{run}?step=</c> reads a run and its steps, <c>GET api/deploys/{id}/runs?task=&amp;step=</c> reads the newest run of a task in the deploy's channel, <c>POST api/deploys/{id}/binding-test</c> tests a slot's login on the Runner,
/// <c>POST api/deploys/{id}/contract</c> runs a Store model's approved contract, and <c>POST api/deploys/{id}/activate</c> activates a version a deploy staged in production for its preflight. <c>POST api/deploys/handover</c> makes the console the owner, on a Gateway, of what the
/// Workspace servers' publisher keys published there.
/// </summary>
[ApiController]
[Route("api/deploys")]
public sealed class DeploysController(ConsoleDeploys deploys, ConsoleDeployRuns runs) : ControllerBase
{
    private RelayPerson Person => new(User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown", User.Identity?.Name, User.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer);

    // a deploy reads the build, sends its files and, in prod, waits for the preflight (ConsoleDeploys.PreflightWait, two minutes): longer than the host's default request timeout,
    // which would cancel it midway and answer 504 with no body; the deploy bounds its own waits
    [HttpPost]
    [Authorize(Policies.Viewer)]
    [DisableRequestTimeout]
    public async Task<IActionResult> Deploy([FromBody] DeployBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.WorkspaceServer) || string.IsNullOrWhiteSpace(body.Project) || string.IsNullOrWhiteSpace(body.Workspace) || string.IsNullOrWhiteSpace(body.Build) || string.IsNullOrWhiteSpace(body.Target))
            return BadRequest(new { error = "Name the build (\"workspaceServer\", \"project\", \"workspace\", \"build\") and the Runner or Gateway it goes to (\"target\")." });
        var done = await deploys.DeployAsync(new DeployBuildRequest(body.WorkspaceServer, body.Project, body.Workspace, body.Build, body.Target, body.Channel, body.Serve, body.ExpectedVersion, body.Bind, body.DataSource, body.Adopt,
            body.Model, body.Note, body.ConfirmPreflight, body.PreviewInstance), Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    /// <summary><c>POST api/deploys/handover</c>: the console takes over, on one Gateway, what the Workspace servers' publisher keys published there, as the person (an administrator of that Gateway; <see cref="ConsoleDeploys.HandOverAsync"/>).</summary>
    [HttpPost("handover")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> HandOver([FromBody] DeployHandoverBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Gateway)) return BadRequest(new { error = "Name the Gateway (\"gateway\") on which the console takes over what the Workspace servers' publisher keys published." });
        var done = await deploys.HandOverAsync(body.Gateway.Trim(), body.Clients, Person, ct);
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

    [HttpPost("{id}/runs")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Run(string id, [FromBody] DeployRunBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Task))
            return BadRequest(new { error = "The body is a JSON object with the \"task\" to run (a manual-only task of the version the deploy staged, or any task with \"served\": true) and optionally its \"inputs\", an object of names and text values." });
        var done = await runs.StartAsync(id, body.Task.Trim(), body.Inputs ?? [], body.Served, Person, ct, body.Answer);
        return StatusCode(done.Status, done.Body);
    }

    [HttpGet("{id}/runs/{run}/answer")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> AnswerOf(string id, string run, CancellationToken ct)
    {
        if (!Guid.TryParse(run, out var runId)) return NotFound(new { error = $"'{run}' is not the id of a run." });
        var done = await runs.AnswerAsync(id, runId, Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    [HttpGet("{id}/runs")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Newest(string id, [FromQuery] string? task, [FromQuery] string? step, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > 64)
            return BadRequest(new { error = "Name the task whose newest run in the deploy's channel to read: ?task=, and optionally ?step= for the steps it recorded." });
        var done = await runs.NewestAsync(id, task.Trim(), step, Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    [HttpGet("{id}/runs/{run}")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> RunOf(string id, string run, [FromQuery] string? step, CancellationToken ct)
    {
        if (!Guid.TryParse(run, out var runId)) return NotFound(new { error = $"'{run}' is not the id of a run." });
        var done = await runs.RunAsync(id, runId, step, Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    [HttpPost("{id}/binding-test")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> TestBinding(string id, [FromBody] DeployBindingTestBody body, CancellationToken ct)
    {
        var slot = body.Slot?.Trim();
        var role = string.IsNullOrWhiteSpace(body.Role) ? "default" : body.Role.Trim();
        if (string.IsNullOrWhiteSpace(slot) || slot.Length > 64 || role is not ("default" or "read" or "write"))
            return BadRequest(new { error = "The body is a JSON object with the \"slot\" to try (the connection's slot in the deployed package) and optionally the \"role\" of its login (default, read or write)." });
        var done = await runs.TestBindingAsync(id, slot, role, Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    [HttpPost("{id}/activate")]
    [Authorize(Policies.Viewer)]
    [DisableRequestTimeout]   // waits for the Runner's instances to have the staged version ready
    public async Task<IActionResult> Activate(string id, [FromBody] DeployActivateBody? body, CancellationToken ct)
    {
        var done = await runs.ActivateAsync(id, body?.Confirm ?? false, Person, ct);
        return StatusCode(done.Status, done.Body);
    }

    [HttpPost("{id}/contract")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Contract(string id, [FromBody] DeployContractBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Approve) || !Guid.TryParse(body.PlanRun, out var planRun))
            return BadRequest(new { error = "The body is a JSON object with \"planRun\" (the run of contract that read the plan), \"approve\" (that plan's hash) and optionally \"allowConsumer\", a list of <name>@<channel> or gateway:<name>." });
        var allowed = (body.AllowConsumer ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        var done = await runs.ContractAsync(id, planRun, body.Approve.Trim(), allowed, Person, ct);
        return StatusCode(done.Status, done.Body);
    }
}
