using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// What a Build server offers over its management API (plan 13, R01): the commands that only read, run for a project in a child process, with the same arguments and the same JSON document the command line gives.
/// <list type="bullet">
/// <item><c>GET /api/v1/projects</c>: the projects served (one for now).</item>
/// <item><c>GET /api/v1/projects/{project}/commands</c>: the commands on offer, each with the role it needs.</item>
/// <item><c>POST /api/v1/projects/{project}/commands/{command}</c>: runs one; the body is its inputs as a JSON object (the command's name with underscores, as the MCP server names it). The answer is <c>{exit, document}</c>, or <c>{exit, text}</c> when the command printed no document.</item>
/// </list>
/// Which commands, and which of their options, are the same rules the MCP server and the local web page use (<see cref="ToolSurface"/>): nothing that writes, no option that shows values, no path outside the project. The role a command needs
/// comes from its effect class (<see cref="ManagementAuthorization"/>), and the caller's token (<see cref="ManagementAuthentication"/>) has to carry it for this project.
/// </summary>
internal static class BuildManagementApi
{
    /// <summary>The commands a read-only server offers: those that change nothing, not even the project's own files.</summary>
    public static ToolSurface ReadOnlySurface(string projectRoot, IEnumerable<CommandInfo> all) =>
        new(projectRoot, all.Where(c => c.Impact == Impact.None), withholdWriteFlags: true);

    public static void Map(IEndpointRouteBuilder app, string projectId, string projectRoot, ToolSurface surface, IProjectCommandRunner runner)
    {
        var specs = CommandSpecs.All.ToDictionary(s => s.Name);

        app.MapGet("/api/v1/projects", () => Results.Json(new { projects = new[] { new { id = projectId, name = projectId } } }));

        app.MapGet("/api/v1/projects/{project}/commands", (string project, HttpContext context) =>
        {
            if (project != projectId) return Results.NotFound();
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

        app.MapPost("/api/v1/projects/{project}/commands/{tool}", async (string project, string tool, HttpContext context) =>
        {
            if (project != projectId) return Results.NotFound();
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

            var result = await runner.RunAsync(argv, projectRoot, context.RequestAborted);
            JsonNode? document = null;
            try { document = JsonNode.Parse(result.Out); } catch (JsonException) { }
            return Results.Json(new JsonObject { ["exit"] = result.Exit, ["document"] = document, ["text"] = document is null ? (result.Out.Length > 0 ? result.Out : result.Err) : null });
        });
    }
}
