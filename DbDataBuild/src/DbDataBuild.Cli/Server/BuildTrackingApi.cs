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
    public static void Map(IEndpointRouteBuilder app, Lazy<StateDatabase> state, string serverDirectory, IAuditLog? audit, ProjectsFile? projects = null)
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

        // the switch of the primary (plan 28c E16 and E18): the copy is brought level with the target first, and the switch is refused in a sentence while a side is down or a divergence is open; it then writes the server's own setting, which the
        // commands the server starts follow from their next start
        app.MapPost("/api/v1/tracking/primary", async (HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed) return Results.Json(new { error = "Switching the primary needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (System.Text.Json.JsonException) { return Results.Json(new { error = "The body is not JSON." }, statusCode: StatusCodes.Status400BadRequest); }
            var wanted = (string?)body?["primary"] switch { "state" => TrackingSide.State, "target" => TrackingSide.Target, _ => (TrackingSide?)null };
            if (wanted is null) return Results.Json(new { error = "Say which side: `primary` is `state` or `target`." }, statusCode: StatusCodes.Status400BadRequest);

            TrackingSettings settings;
            try { settings = BuildTrackingSettings.Read(serverDirectory); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError); }
            var actor = BuildManagementApi.ActorOf(token);
            var from = settings.PrimarySide.ToString().ToLowerInvariant();
            var to = wanted.Value.ToString().ToLowerInvariant();
            IResult Refuse(string message)
            {
                audit?.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, token.SubjectName ?? token.SubjectId, false, "tracking.switch_primary", "server", "refused", $"{from} to {to}: {message}"));
                return Results.Json(new { switched = false, error = message }, statusCode: StatusCodes.Status409Conflict);
            }

            if (settings.Mode != TrackingMode.Both) return Refuse($"The primary can only be switched when tracking keeps both sides; this server's mode is '{settings.Mode.ToString().ToLowerInvariant()}'. Change the mode first.");
            if (settings.PrimarySide == wanted.Value) return Refuse($"The {to} is already the primary.");
            if (projects is null) return Refuse("This server hosts no projects to compare.");

            // bring the copy level with the target, whatever the primary is now: it proves the target answers and that the two sides hold the same rows (a state that is ahead of the target is a divergence, and is refused below)
            var secrets = ServerSecrets.Open(serverDirectory);
            var problems = new List<string>();
            var host = new BuildTrackingHost(projects, state.Value, settings, ct => ServerSecrets.LoginsAsync(secrets, ct), line => problems.Add(line));
            var results = await host.CopyAsync(null, null, TimeSpan.FromSeconds(60), context.RequestAborted, forSwitch: true);
            foreach (var r in results)
            {
                problems.AddRange(r.Problems);
                if (r.Error is null && r.Problems.Count == 0 && !r.CaughtUp) problems.Add("the copy did not catch up within the time given; try again");
            }

            if (problems.Count > 0) return Refuse($"Not switched: the two sides could not be made level ({string.Join("; ", problems.Distinct().Take(3))}). A side may be down or a divergence open; resolve that and try again.");
            BuildTrackingSettings.Write(serverDirectory, TrackingSettings.Both(wanted.Value));
            audit?.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, token.SubjectName ?? token.SubjectId, false, "tracking.switch_primary", "server", "ok", $"{from} to {to}, {results.Count} tracking store(s) compared"));
            return Results.Json(new { switched = true, mode = "both", primary = to, compared = results.Count, by = actor });
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
