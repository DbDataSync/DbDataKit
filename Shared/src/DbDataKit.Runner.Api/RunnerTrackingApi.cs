using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The runner's own copy of Build's tracking (plan 28c E16 and E18, owner decision 2026-11-11): <c>GET {prefix}/tracking</c> (viewer) is the same document the Build server answers (per identity and stream the checkpoint and rows, every divergence with its
/// common point), over the runner's <c>build_tracking</c> module; <c>POST {prefix}/tracking/divergences/{id}/resolve</c> (administrator, <c>{"resolution":"archive"|"reset"}</c>) resolves an open divergence of that copy with the same actions and the
/// same rules as the Build server's (<see cref="BuildTrackingDocument.ResolveAsync"/>) and writes it to the runner's audit as <c>runner.tracking.resolve</c>, a refusal too. The runner's copy is made from the target the project's connection names, so
/// its mode is <c>both</c> and the target is the primary; whether a project keeps a copy at all is the project's setting (<c>tracking: {runner_copy: false}</c>).
/// <para>
/// The same two routes carry the copies of Sync's entries a runner-owned replication's drain made (plan 28c E17, <see cref="SyncPositionLogCopy"/>): the answer lists them beside Build's (each with its replication, mode
/// <c>both</c> and the target as primary: a replication whose state is on the target; the setting itself is in the published definition), and a resolve of such a divergence is the same archive or reset, over the same rows,
/// audited the same way.
/// </para>
/// </summary>
public static class RunnerTrackingApi
{
    public const string ResolveVerb = "runner.tracking.resolve";
    public const string TargetKind = "tracking_divergence";

    public static void Map(IEndpointRouteBuilder app, StateDatabase state, string prefix, RunnerCallerResolver callerOf, string serviceId = RunnerNames.ServiceId)
    {
        var root = prefix.TrimEnd('/');
        var audit = new AuditLog(state, serviceId);

        app.MapGet($"{root}/tracking", (HttpContext context) =>
        {
            if (!callerOf(context).Authorize(ServerRole.Viewer).Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            var document = BuildTrackingDocument.Build(state, "both", "target", "runner");
            var (entries, positionLogDivergences) = SyncPositionLogCopy.Document(state, BuildTrackingState.ProgressKey, _ => ("both", "target"));
            foreach (var copy in entries) document["copies"]!.AsArray().Add(copy!.DeepClone());
            if (positionLogDivergences.Count > 0)
            {
                // newest first across both kinds, as each document is
                var all = document["divergences"]!.AsArray().Concat(positionLogDivergences).Select(d => d!.DeepClone()).OrderByDescending(d => (long)d!["id"]!).ToList();
                document["divergences"] = new JsonArray([.. all]);
            }

            return Results.Json(document);
        });

        app.MapPost($"{root}/tracking/divergences/{{divergence}}/resolve", async (string divergence, HttpContext context) =>
        {
            var caller = callerOf(context);
            var target = divergence.Length > 64 ? divergence[..64] : divergence;
            var decision = caller.Authorize(ServerRole.Admin);
            if (!decision.Allowed)
            {
                Record(audit, caller.Actor, target, AuditEvent.Refused, new { status = 403, reason = decision.Reason ?? "the role is not enough" });
                return Error(403, "Resolving a divergence needs the admin role.");
            }

            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Error(400, "The body is not JSON."); }
            var resolution = (string?)body?["resolution"] switch
            {
                "archive" => TrackingResolution.Archive,
                "reset" => TrackingResolution.ResetCopy,
                _ => (TrackingResolution?)null,
            };
            if (resolution is null) return Error(400, "Say how: `resolution` is `archive` (keep the copy's later rows apart) or `reset` (delete them).");

            if (!long.TryParse(divergence, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) return Error(404, $"There is no divergence {divergence}.");
            var word = resolution == TrackingResolution.Archive ? "archive" : "reset";
            if (SyncPositionLogCopy.IsPositionLogDivergence(state, id))
            {
                var entry = await SyncPositionLogCopy.ResolveAsync(state, BuildTrackingState.ProgressKey, null, id, resolution.Value, caller.Actor.Id, context.RequestAborted);
                Record(audit, caller.Actor, target, entry.Outcome == SyncPositionLogResolveOutcome.Resolved ? AuditEvent.Ok : AuditEvent.Refused,
                    new { resolution = word, identity = entry.Divergence?.Identity, stream = entry.Divergence?.Stream, reason = entry.Message });
                return entry.Outcome switch
                {
                    SyncPositionLogResolveOutcome.Resolved => Results.Json(new { resolved = true, divergence = DivergenceOf(state, entry.Divergence!) }),
                    SyncPositionLogResolveOutcome.NotFound => Error(404, entry.Message!),
                    _ => Error(409, entry.Message!),
                };
            }

            var result = await BuildTrackingDocument.ResolveAsync(state, id, resolution.Value, caller.Actor.Id, context.RequestAborted);
            var detail = new { resolution = word, identity = result.Divergence?.Identity, stream = result.Divergence?.Stream, reason = result.Message };
            Record(audit, caller.Actor, target, result.Outcome == TrackingResolveOutcome.Resolved ? AuditEvent.Ok : AuditEvent.Refused, detail);
            return result.Outcome switch
            {
                TrackingResolveOutcome.Resolved => Results.Json(new { resolved = true, divergence = BuildTrackingDocument.Divergence(result.Divergence!) }),
                TrackingResolveOutcome.NotFound => Error(404, result.Message!),
                _ => Error(409, result.Message!),
            };
        });
    }

    /// <summary>The answer's divergence of a Sync copy: the same object the document lists for it (with the common point's time).</summary>
    private static JsonNode DivergenceOf(StateDatabase state, TrackingDivergence divergence) =>
        SyncPositionLogCopy.Document(state, BuildTrackingState.ProgressKey, _ => ("both", "target")).Divergences.First(d => (long)d!["id"]! == divergence.Id)!.DeepClone();

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static void Record(AuditLog audit, RunnerActor actor, string target, string outcome, object detail) =>
        audit.Record(new AuditEvent("person", actor.Id, ResolveVerb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: TargetKind, TargetId: target, Detail: JsonSerializer.Serialize(detail)));
}
