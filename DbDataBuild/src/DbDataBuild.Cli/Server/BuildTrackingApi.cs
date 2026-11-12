using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The Build server's view of the copy of its projects' tracking (plan 28c E18, first slice): <c>GET /api/v1/tracking</c> (viewer) says the mode and primary of the <c>tracking:</c> setting, for every identity and stream the copy
/// holds its checkpoint, how many rows and how far the copy is, and every divergence, open or resolved, with the common point; <c>POST /api/v1/tracking/divergences/{id}/resolve</c> (administrator, <c>{"resolution":"archive"|"reset"}</c>)
/// resolves an open one through <see cref="TrackingFollower.ResolveAsync"/> and records who did it in the server's audit. The console's screen reads these; nothing here needs the runner. The document and the resolve are
/// <see cref="BuildTrackingDocument"/>, shared with the runner, whose copy is resolved by the same rules.
/// </summary>
internal static class BuildTrackingApi
{
    public static void Map(IEndpointRouteBuilder app, Lazy<StateDatabase> state, string serverDirectory, IAuditLog? audit)
    {
        app.MapGet("/api/v1/tracking", (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            TrackingSettings settings;
            try { settings = BuildTrackingSettings.Read(serverDirectory); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError); }
            return Results.Json(Document(state.Value, settings));
        });

        app.MapPost("/api/v1/tracking/divergences/{id:long}/resolve", async (long id, HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) return Results.Json(new { error = "Resolving a divergence needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (System.Text.Json.JsonException) { return Results.Json(new { error = "The body is not JSON." }, statusCode: StatusCodes.Status400BadRequest); }
            var resolution = (string?)body?["resolution"] switch
            {
                "archive" => TrackingResolution.Archive,
                "reset" => TrackingResolution.ResetCopy,
                _ => (TrackingResolution?)null,
            };
            if (resolution is null) return Results.Json(new { error = "Say how: `resolution` is `archive` (keep the copy's later rows apart) or `reset` (delete them)." }, statusCode: StatusCodes.Status400BadRequest);

            var actor = BuildManagementApi.ActorOf(token);
            var result = await BuildTrackingDocument.ResolveAsync(state.Value, id, resolution.Value, actor, context.RequestAborted);
            if (result.Divergence is { } divergence && result.Outcome is TrackingResolveOutcome.Resolved or TrackingResolveOutcome.ClosedMeanwhile)
                audit?.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, token.SubjectName ?? token.SubjectId, false, "tracking.resolve",
                    $"{divergence.Identity}/{divergence.Stream}", result.Outcome == TrackingResolveOutcome.Resolved ? "ok" : "failed", $"divergence {id}, {(resolution == TrackingResolution.Archive ? "archive" : "reset")}"));
            return result.Outcome switch
            {
                TrackingResolveOutcome.Resolved => Results.Json(new { resolved = true, divergence = BuildTrackingDocument.Divergence(result.Divergence!) }),
                TrackingResolveOutcome.NotFound => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status409Conflict),
            };
        });
    }

    /// <summary>The document of <c>GET /api/v1/tracking</c>.</summary>
    internal static JsonObject Document(StateDatabase database, TrackingSettings settings) =>
        BuildTrackingDocument.Build(database, settings.Mode.ToString().ToLowerInvariant(), settings.Primary?.ToString().ToLowerInvariant(), "build-server");
}
