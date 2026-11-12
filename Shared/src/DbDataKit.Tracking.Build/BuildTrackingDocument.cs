using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tracking;

namespace DbDataKit.Tracking.Build;

/// <summary>What a resolve of a divergence came to: the HTTP-shaped outcome both hosts (the Build server's API and the runner's) answer with, so the two apply the same rules.</summary>
public enum TrackingResolveOutcome
{
    Resolved,
    NotFound,
    AlreadyClosed,
    NotBuildStream,
    ClosedMeanwhile,
}

/// <summary>The result of <see cref="BuildTrackingDocument.ResolveAsync"/>; <see cref="Message"/> says what is wrong for every outcome but <see cref="TrackingResolveOutcome.Resolved"/>.</summary>
public sealed record TrackingResolveResult(TrackingResolveOutcome Outcome, TrackingDivergence? Divergence, string? Message);

/// <summary>
/// The document of <c>GET /api/v1/tracking</c> and the resolve of a divergence over a <c>build_tracking</c> copy (plan 28c E18), shared by the two servers that hold such a copy: the Build server and the runner (owner decision 2026-11-11: the runner's copy is
/// resolved with the same archive and reset actions, under the same rules). The host decides who may call and records the audit; what is read and what is changed is here.
/// </summary>
public static class BuildTrackingDocument
{
    /// <summary>The document: per identity and stream the checkpoint, rows and whether it is diverged, and every divergence (open or resolved) with its common point. <paramref name="mode"/> and <paramref name="primary"/> are the host's setting, as words; <paramref name="holder"/> says whose copy it is.</summary>
    public static JsonObject Build(StateDatabase database, string mode, string? primary, string holder)
    {
        var store = new BuildTrackingStore(database);
        var progress = new ProgressStore(database, BuildTrackingState.ProgressKey);
        // a divergence of Sync's units (a runner-owned replication's copy) is not Build's: the runner's answer lists it beside Build's (SyncUnitCopy)
        var divergences = new StateTrackingDivergences(database).List().Where(d => !SyncUnitCopy.IsIdentity(d.Identity)).ToList();
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
            ["holder"] = holder,
            ["mode"] = mode,
            ["primary"] = primary,
            ["copies"] = copies,
            ["divergences"] = new JsonArray([.. divergences.OrderByDescending(d => d.Id).Select(d => (JsonNode)Divergence(d))]),
        };
    }

    private static JsonObject? Position(TrackingPosition? p) => p is null ? null : new JsonObject { ["order"] = p.Order, ["key"] = p.Key, ["time"] = BuildStreams.TimeText(BuildStreams.TimeOf(p.Order)) };

    public static JsonObject Divergence(TrackingDivergence d) => new()
    {
        ["id"] = d.Id, ["identity"] = d.Identity, ["stream"] = d.Stream, ["status"] = d.Status, ["open"] = d.IsOpen, ["reason"] = d.Reason,
        ["common"] = Position(d.Common), ["primary_high"] = Position(d.PrimaryHigh), ["copy_high"] = Position(d.CopyHigh),
        ["diverged_at"] = d.DivergedAt, ["resolved_at"] = d.ResolvedAt, ["resolved_by"] = d.ResolvedBy,
    };

    /// <summary>Resolves the open divergence <paramref name="id"/> of the copy in <paramref name="database"/>: <c>archive</c> keeps the copy's later rows apart, <c>reset</c> deletes them, and the checkpoint goes back to the common point.</summary>
    public static async Task<TrackingResolveResult> ResolveAsync(StateDatabase database, long id, TrackingResolution resolution, string actor, CancellationToken ct)
    {
        var divergences = new StateTrackingDivergences(database);
        var divergence = divergences.List().FirstOrDefault(d => d.Id == id);
        if (divergence is null) return new(TrackingResolveOutcome.NotFound, null, $"There is no divergence {id}.");
        if (!divergence.IsOpen) return new(TrackingResolveOutcome.AlreadyClosed, divergence, $"The divergence {id} is already {divergence.Status}.");
        if (BuildStreams.All.All(s => s.Name != divergence.Stream)) return new(TrackingResolveOutcome.NotBuildStream, divergence, $"The divergence {id} is of '{divergence.Stream}', which is not a stream of Build's tracking tables, so this server does not resolve it.");

        var store = new BuildTrackingStore(database);
        var follower = new TrackingFollower(divergence.Identity, BuildStreams.Get(divergence.Stream).Stream, store, store, new ProgressStore(database, BuildTrackingState.ProgressKey), divergences);
        var resolved = await follower.ResolveAsync(resolution, actor, ct).ConfigureAwait(false);
        return resolved
            ? new(TrackingResolveOutcome.Resolved, divergences.List().First(d => d.Id == id), null)
            : new(TrackingResolveOutcome.ClosedMeanwhile, divergence, $"The divergence {id} could not be resolved (it was closed meanwhile).");
    }
}
