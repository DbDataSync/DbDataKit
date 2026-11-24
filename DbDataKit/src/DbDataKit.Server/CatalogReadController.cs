using DbDataKit.State;
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
/// <item><c>POST api/workspace-catalog/{workspace}/{project}/describe</c> with <c>{"runner", "channel", "connection", "sql"}</c>: the columns of a query, described (never run) by the Runner's built-in task as the signed-in person; it waits
/// up to ten seconds, and <c>GET .../describe/{run}?runner=&amp;channel=&amp;connection=</c> answers it after that.</item>
/// <item><c>POST api/workspace-catalog/{workspace}/{project}/gateway-read</c> with <c>{"gateway", "connection", "data_source"?, "adopt"?}</c>: the shape of a Gateway's data source, read by the Gateway with its own login, asked as the
/// signed-in person (<see cref="ConsoleCatalogRead.GatewayAsync"/>).</item>
/// <item><c>POST api/workspace-catalog/{workspace}/{project}/binding-test</c> with <c>{"runner", "channel", "connection", "role"?}</c>: the Runner's test of the connection's slot in the channel, with no version needed, asked as the
/// signed-in person (<see cref="ConsoleDraftBindingTest"/>).</item>
/// </list>
/// Any signed-in person may ask: the Workspace server decides whether they may read the project, and the Runner or the Gateway whether they may read or test there (the token the console sends carries what they hold on each). A read in
/// production goes ahead; the screen warns before it asks.
/// </summary>
[ApiController]
[Route("api/workspace-catalog/{workspace}/{project}")]
[Authorize(Policies.Viewer)]
public sealed class CatalogReadController(ConsoleCatalogRead reads, ConsoleDraftBindingTest tests, CurrentUser current) : ControllerBase
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

    [HttpPost("describe")]
    public async Task<IActionResult> Describe(string workspace, string project, [FromBody] DescribeBody body, CancellationToken ct)
    {
        if (current.Id is not { } id) return Unauthorized(new { error = "Sign in first." });
        return Answer(await reads.StartDescribeAsync(workspace, project, body, current.Name is { Length: > 0 } name ? name : id, ct));
    }

    [HttpGet("describe/{run}")]
    public async Task<IActionResult> FollowDescribe(string workspace, string project, string run, [FromQuery] string? runner, [FromQuery] string? channel, [FromQuery] string? connection, CancellationToken ct) =>
        Answer(await reads.FollowDescribeAsync(workspace, project, runner, channel, connection, run, ct));

    [HttpPost("gateway-read")]
    public async Task<IActionResult> GatewayRead(string workspace, string project, [FromBody] GatewayReadBody body, CancellationToken ct)
    {
        if (current.Id is not { } id) return Unauthorized(new { error = "Sign in first." });
        return Answer(await reads.GatewayAsync(workspace, project, body, new RelayPerson(id, current.Name, current.Role ?? UserRole.Viewer), ct));
    }

    [HttpPost("binding-test")]
    public async Task<IActionResult> BindingTest(string workspace, string project, [FromBody] DraftBindingTestBody body, CancellationToken ct)
    {
        if (current.Id is not { } id) return Unauthorized(new { error = "Sign in first." });
        return Answer(await tests.TestAsync(workspace, project, body, id, ct));
    }

    private ObjectResult Answer(CatalogReadAnswer answer) => StatusCode(answer.Status, answer.Body);
}
