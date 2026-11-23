using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>
/// The read of a database's shape for a project of a Workspace server, from a channel of a Runner (<see cref="ConsoleCatalogRead"/>):
/// <list type="bullet">
/// <item><c>GET api/workspace-catalog/{workspace}/{project}/channels?runner=&amp;connection=</c>: the channels of the project's package on the Runner, whether each binds the connection's slot, and the one to offer first.</item>
/// <item><c>POST api/workspace-catalog/{workspace}/{project}/read</c> with <c>{"runner", "channel", "connection", "schema"?, "table"?}</c>: starts the read as the signed-in person and waits for it up to ten seconds.</item>
/// <item><c>GET api/workspace-catalog/{workspace}/{project}/read/{run}?runner=&amp;channel=&amp;connection=</c>: the read after the console stopped waiting.</item>
/// </list>
/// Any signed-in person may ask: the Workspace server decides whether they may read the project, and the Runner whether they may start the read (the token the console sends carries what they hold on each). A read in production goes
/// ahead; the screen warns before it asks.
/// </summary>
[ApiController]
[Route("api/workspace-catalog/{workspace}/{project}")]
[Authorize(Policies.Viewer)]
public sealed class CatalogReadController(ConsoleCatalogRead reads, CurrentUser current) : ControllerBase
{
    [HttpGet("channels")]
    public async Task<IActionResult> Channels(string workspace, string project, [FromQuery] string? runner, [FromQuery] string? connection, CancellationToken ct) =>
        Answer(await reads.ChannelsAsync(workspace, project, runner, connection, ct));

    [HttpPost("read")]
    public async Task<IActionResult> Read(string workspace, string project, [FromBody] CatalogReadBody body, CancellationToken ct)
    {
        if (current.Id is not { } id) return Unauthorized(new { error = "Sign in first." });
        return Answer(await reads.StartAsync(workspace, project, body, current.Name is { Length: > 0 } name ? name : id, ct));
    }

    [HttpGet("read/{run}")]
    public async Task<IActionResult> Follow(string workspace, string project, string run, [FromQuery] string? runner, [FromQuery] string? channel, [FromQuery] string? connection, CancellationToken ct) =>
        Answer(await reads.FollowAsync(workspace, project, runner, channel, connection, run, ct));

    private ObjectResult Answer(CatalogReadAnswer answer) => StatusCode(answer.Status, answer.Body);
}
