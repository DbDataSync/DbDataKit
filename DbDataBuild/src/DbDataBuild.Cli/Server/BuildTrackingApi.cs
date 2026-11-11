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
/// resolves an open one through <see cref="TrackingFollower.ResolveAsync"/> and records who did it in the server's audit. The console's screen reads these; nothing here needs the runner.
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

            var database = state.Value;
            var divergences = new StateTrackingDivergences(database);
            var divergence = divergences.List().FirstOrDefault(d => d.Id == id);
            if (divergence is null) return Results.Json(new { error = $"There is no divergence {id}." }, statusCode: StatusCodes.Status404NotFound);
            if (!divergence.IsOpen) return Results.Json(new { error = $"The divergence {id} is already {divergence.Status}." }, statusCode: StatusCodes.Status409Conflict);
            if (BuildStreams.All.All(s => s.Name != divergence.Stream)) return Results.Json(new { error = $"The divergence {id} is of '{divergence.Stream}', which is not a stream of Build's tracking tables, so this server does not resolve it." }, statusCode: StatusCodes.Status409Conflict);

            var store = new BuildTrackingStore(database);
            var follower = new TrackingFollower(divergence.Identity, BuildStreams.Get(divergence.Stream).Stream, store, store, new ProgressStore(database, BuildTrackingState.ProgressKey), divergences);
            var actor = BuildManagementApi.ActorOf(token);
            var resolved = await follower.ResolveAsync(resolution.Value, actor, context.RequestAborted);
            audit?.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, token.SubjectName ?? token.SubjectId, false, "tracking.resolve",
                $"{divergence.Identity}/{divergence.Stream}", resolved ? "ok" : "failed", $"divergence {id}, {(resolution == TrackingResolution.Archive ? "archive" : "reset")}"));
            return resolved
                ? Results.Json(new { resolved = true, divergence = Divergence(divergences.List().First(d => d.Id == id)) })
                : Results.Json(new { error = $"The divergence {id} could not be resolved (it was closed meanwhile)." }, statusCode: StatusCodes.Status409Conflict);
        });
    }

    /// <summary>The document of <c>GET /api/v1/tracking</c>.</summary>
    internal static JsonObject Document(StateDatabase database, TrackingSettings settings)
    {
        var store = new BuildTrackingStore(database);
        var progress = new ProgressStore(database, BuildTrackingState.ProgressKey);
        var divergences = new StateTrackingDivergences(database).List();
        var identities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in divergences) identities.Add(d.Identity);
        foreach (var identity in store.Identities()) identities.Add(identity);

        var copies = new JsonArray();
        foreach (var identity in identities)
        {
            var streams = new JsonArray();
            foreach (var stream in BuildStreams.All)
            {
                var checkpoint = progress.GetCheckpoint(TrackingFollower.CheckpointNameOf(stream.Name), identity);
                var open = divergences.FirstOrDefault(d => d.IsOpen && d.Identity == identity && d.Stream == stream.Name);
                streams.Add(new JsonObject
                {
                    ["stream"] = stream.Name,
                    ["rows"] = store.Count(identity, stream.Name),
                    ["checkpoint"] = checkpoint is not null && checkpoint.Parts.TryGetValue(TrackingFollower.OrderPartName, out var order) && checkpoint.Parts.TryGetValue(TrackingFollower.KeyPartName, out var key)
                        ? new JsonObject { ["order"] = order.AsInt, ["key"] = key.AsText, ["time"] = BuildStreams.TimeText(BuildStreams.TimeOf(order.AsInt)) } : null,
                    ["diverged"] = open is not null,
                });
            }

            copies.Add(new JsonObject { ["identity"] = identity, ["streams"] = streams });
        }

        return new JsonObject
        {
            ["mode"] = settings.Mode.ToString().ToLowerInvariant(),
            ["primary"] = settings.Primary?.ToString().ToLowerInvariant(),
            ["copies"] = copies,
            ["divergences"] = new JsonArray([.. divergences.OrderByDescending(d => d.Id).Select(d => (JsonNode)Divergence(d))]),
        };
    }

    private static JsonObject Position(TrackingPosition? p) => p is null ? null! : new JsonObject { ["order"] = p.Order, ["key"] = p.Key, ["time"] = BuildStreams.TimeText(BuildStreams.TimeOf(p.Order)) };

    internal static JsonObject Divergence(TrackingDivergence d) => new()
    {
        ["id"] = d.Id, ["identity"] = d.Identity, ["stream"] = d.Stream, ["status"] = d.Status, ["open"] = d.IsOpen, ["reason"] = d.Reason,
        ["common"] = Position(d.Common), ["primary_high"] = Position(d.PrimaryHigh), ["copy_high"] = Position(d.CopyHigh),
        ["diverged_at"] = d.DivergedAt, ["resolved_at"] = d.ResolvedAt, ["resolved_by"] = d.ResolvedBy,
    };
}
