using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;
using DbDataKit.Scheduling;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// What a Build server offers over its management API (plan 13, R01 and R03): the commands of a project, run in a child process with the same arguments and the same JSON document the command line gives.
/// <list type="bullet">
/// <item><c>GET /api/v1/projects</c>: the projects served (one for now).</item>
/// <item><c>GET /api/v1/projects/{project}/commands</c>: the commands on offer, each with the role it needs.</item>
/// <item><c>POST /api/v1/projects/{project}/commands/{command}</c>: runs one; the body is its inputs as a JSON object (the command's name with underscores, as the MCP server names it). The answer is <c>{exit, document}</c>, or <c>{exit, text}</c> when the command printed no document.</item>
/// </list>
/// Which commands, and which of their options, follow the rules the MCP server and the local web page use (<see cref="ToolSurface"/>): no option that shows values, no path outside the project. The role a command needs
/// comes from its effect class (<see cref="ManagementAuthorization"/>): a viewer reads, an author changes the project's files, an operator refreshes data, an approver applies a plan to a target, an admin changes the tracking tables. The caller's
/// token (<see cref="ManagementAuthentication"/>) has to carry it for this project. Approving is applying: the person who holds the approver role and applies a plan is the approver, and the command records them (and the console they came through) in the
/// tracking tables as the one who applied it (<see cref="Actor"/>).
/// </summary>
internal static class BuildManagementApi
{
    /// <summary>The commands a read-only server offers: those that change nothing, not even the project's own files.</summary>
    public static ToolSurface ReadOnlySurface(string projectRoot, IEnumerable<CommandInfo> all) =>
        new(projectRoot, all.Where(c => c.Impact == Impact.None), withholdWriteFlags: true);

    /// <summary>
    /// The commands a server offers by default: all of them but the ones that start a user interface on the server's own terminal or browser. Whether a call may run is the role its effect class needs, checked on every call; the
    /// options that show values and the paths outside the project stay refused. A server started with <c>--read-only</c> offers <see cref="ReadOnlySurface"/> instead.
    /// </summary>
    public static ToolSurface FullSurface(string projectRoot, IEnumerable<CommandInfo> all) =>
        new(projectRoot, all.Where(c => c.Name is not ("ui terminal" or "ui mcp" or "ui web")));

    /// <summary>The person a call is for, as the tracking tables record them: "Ada via Main console".</summary>
    internal static string ActorOf(ValidatedToken token) => $"{token.SubjectName ?? token.SubjectId} via {token.Client.Name}";

    /// <param name="projects">The projects served, read again on every call so one added or removed is served or gone at once.</param>
    /// <param name="commands">All the commands there are; each project is offered the read-only ones.</param>
    /// <param name="surfaceFor">Which of them a project is offered and how their inputs are checked; the read-only rules unless a test or a later slice says otherwise.</param>
    public static void Map(IEndpointRouteBuilder app, ProjectsFile projects, IReadOnlyList<CommandInfo> commands, IProjectCommandRunner runner, Func<string, IReadOnlyList<CommandInfo>, ToolSurface>? surfaceFor = null, BuildScheduler? scheduler = null)
    {
        surfaceFor ??= FullSurface;
        var specs = CommandSpecs.All.ToDictionary(s => s.Name);

        app.MapGet("/api/v1/projects", () => Results.Json(new
        {
            projects = projects.All().Select(p => new { id = p.Id, name = p.Id, source = p.IsRemote ? "git" : "path", branch = p.Branch }),
        }));

        app.MapGet("/api/v1/projects/{project}/commands", (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var projectId = hosted.Id;
            var surface = surfaceFor(projects.RootOf(hosted), commands);
            var token = context.ManagementToken();
            return Results.Json(new
            {
                commands = surface.Tools.Values.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new
                {
                    tool = ToolSurface.ToolName(c),
                    name = c.Name,
                    purpose = c.Purpose,
                    effect = c.Effect,
                    role = specs.TryGetValue(c.Name, out var spec) ? ManagementAuthorization.RoleFor(spec.Effect).ToString().ToLowerInvariant() : null,
                    allowed = specs.TryGetValue(c.Name, out var s) && ManagementAuthorization.Authorize(token, s, projectId).Allowed,
                }),
            });
        });

        // the scheduled plans of a project, whether or not this server runs them (the scheduler is opt-in): their schedule, the next time each is due and how the last run went
        app.MapGet("/api/v1/projects/{project}/schedule", (string project) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var now = TimeProvider.System.GetUtcNow();
            var jobs = scheduler is not null
                ? scheduler.Jobs(hosted)
                : [.. RefreshJobs.Discover(projects.RootOf(hosted)).Select(j => (j, (DateTimeOffset?)DbDataKit.Scheduling.ScheduleEvaluator.NextAfter(j.Schedule, now), (JobState?)null))];
            return Results.Json(new
            {
                enabled = scheduler is not null,
                jobs = jobs.Select(j => new
                {
                    connection = j.Job.Connection,
                    plan = j.Job.Plan,
                    schedule = j.Job.Schedule.Cron,
                    time_zone = j.Job.Schedule.TimeZoneId,
                    next_due = j.NextDue,
                    last_started = j.State?.LastStarted,
                    last_exit = j.State?.LastExit,
                }),
            });
        });

        app.MapPost("/api/v1/projects/{project}/commands/{tool}", async (string project, string tool, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var projectId = hosted.Id;
            var projectRoot = projects.RootOf(hosted);
            if (!Directory.Exists(projectRoot))
                return Results.Json(new { error = $"The project '{projectId}' has no working copy on this server yet. Run: dbdatabuild server project pull {projectId}" }, statusCode: StatusCodes.Status409Conflict);
            var surface = surfaceFor(projectRoot, commands);
            if (!surface.Tools.TryGetValue(tool, out var command) || !specs.TryGetValue(command.Name, out var spec))
                return Results.Json(new { error = $"`{tool}` is not a command this server runs." }, statusCode: StatusCodes.Status404NotFound);

            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), spec, projectId);
            if (!decision.Allowed)
                return Results.Json(new { error = $"`{command.Name}` needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

            JsonObject arguments;
            try
            {
                arguments = context.Request.ContentLength is 0 or null && !context.Request.HasJsonContentType()
                    ? new JsonObject()
                    : await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "The body is a JSON object of the command's inputs." }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!surface.TryBuildArguments(command, arguments, out var argv, out var problem))
                return Results.Json(new { error = problem }, statusCode: StatusCodes.Status400BadRequest);

            var result = await runner.RunAsync(argv, projectRoot, ActorOf(context.ManagementToken()), context.RequestAborted);
            JsonNode? document = null;
            try { document = JsonNode.Parse(result.Out); } catch (JsonException) { }
            return Results.Json(new JsonObject { ["exit"] = result.Exit, ["document"] = document, ["text"] = document is null ? (result.Out.Length > 0 ? result.Out : result.Err) : null });
        });
    }
}
