using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataSync.State;

namespace DbDataSync.Tracking;

/// <summary>What a resolve of a divergence came to: the HTTP-shaped outcome the API answers with.</summary>
public enum SyncResolveOutcome
{
    Resolved,
    NotFound,
    AlreadyClosed,
    NotSyncStream,
    ClosedMeanwhile,
}

/// <summary>The result of <see cref="SyncTrackingDocument.ResolveAsync"/>; <see cref="Message"/> says what is wrong for every outcome but <see cref="SyncResolveOutcome.Resolved"/>.</summary>
public sealed record SyncResolveResult(SyncResolveOutcome Outcome, TrackingDivergence? Divergence, string? Message);

/// <summary>
/// The document of <c>GET /api/v1/tracking</c> on a Sync server and the resolve of a divergence over its copy of the units (plan 28c E17, the Sync half of E18): the same shape as Build's and the runner's
/// (<c>DbDataKit.Tracking.Build.BuildTrackingDocument</c>), so the console's page reads all three, with the replication and its own mode and primary on each copy (the setting is per replication here).
/// The host decides who may call and records the audit entry; what is read and what is changed is here. The rules are those of Build's divergence: resolve is <c>archive</c> (the copy keeps its later rows apart)
/// or <c>reset</c> (it deletes them), the checkpoint goes back to the common point, and the next catch-up copies the target's units again from there.
/// </summary>
public static class SyncTrackingDocument
{
    /// <param name="settingsOf">The mode and primary of a replication, as words (<c>("both", "target")</c>); null for a replication that is gone.</param>
    public static JsonObject Build(StateDatabase database, Func<string, (string Mode, string? Primary)?> settingsOf)
    {
        var store = new StateTrackingStore(database);
        var progress = new ProgressStore(database, SyncUnitStream.ProgressKey);
        var divergences = new StateTrackingDivergences(database).List().Where(d => d.Stream == SyncUnitStream.Name).ToList();
        var identities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in divergences) identities.Add(d.Identity);
        foreach (var identity in store.Identities().Where(i => i.StartsWith("syncunit:", StringComparison.Ordinal))) identities.Add(identity);

        var copies = new JsonArray();
        foreach (var identity in identities)
        {
            var replication = SyncUnitStream.ReplicationOf(identity);
            var setting = settingsOf(replication);
            var checkpoint = progress.GetCheckpoint(TrackingFollower.CheckpointNameOf(SyncUnitStream.Name), identity);
            var open = divergences.FirstOrDefault(d => d.IsOpen && d.Identity == identity);
            copies.Add(new JsonObject
            {
                ["identity"] = identity,
                ["replication"] = replication,
                ["mode"] = setting?.Mode ?? "state",
                ["primary"] = setting?.Primary,
                ["streams"] = new JsonArray(new JsonObject
                {
                    ["stream"] = SyncUnitStream.Name,
                    ["rows"] = store.Count(identity, SyncUnitStream.Name),
                    ["checkpoint"] = checkpoint is not null && checkpoint.Parts.TryGetValue(TrackingFollower.OrderPartName, out var order) && checkpoint.Parts.TryGetValue(TrackingFollower.KeyPartName, out var key)
                        ? Position(store, identity, new TrackingPosition(order.AsInt, key.AsText)) : null,
                    ["diverged"] = open is not null,
                }),
            });
        }

        return new JsonObject
        {
            ["holder"] = "sync-server",
            ["mode"] = "state",
            ["primary"] = null,
            ["copies"] = copies,
            ["divergences"] = new JsonArray([.. divergences.OrderByDescending(d => d.Id).Select(d => (JsonNode)Divergence(store, d))]),
        };
    }

    /// <summary>A position with the time the unit was committed on the target, read from the copy's row (null when the copy no longer holds it live, for instance after an archive).</summary>
    private static JsonObject? Position(StateTrackingStore store, string identity, TrackingPosition? p)
    {
        if (p is null) return null;
        var row = store.ReadAfterAsync(identity, SyncUnitStream.Stream, new TrackingPosition(p.Order, ""), 1, CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault(r => r.Order == p.Order);
        var at = row is null ? null : SyncUnitStream.CommittedAt(row.Document);
        return new JsonObject { ["order"] = p.Order, ["key"] = p.Key, ["time"] = at?.ToString("O", CultureInfo.InvariantCulture) };
    }

    private static JsonObject Divergence(StateTrackingStore store, TrackingDivergence d) => new()
    {
        ["id"] = d.Id, ["identity"] = d.Identity, ["stream"] = d.Stream, ["status"] = d.Status, ["open"] = d.IsOpen, ["reason"] = d.Reason,
        ["common"] = Position(store, d.Identity, d.Common), ["primary_high"] = Position(store, d.Identity, d.PrimaryHigh), ["copy_high"] = Position(store, d.Identity, d.CopyHigh),
        ["diverged_at"] = d.DivergedAt, ["resolved_at"] = d.ResolvedAt, ["resolved_by"] = d.ResolvedBy,
    };

    /// <summary>Resolves the open divergence <paramref name="id"/> of the copy in <paramref name="database"/>.</summary>
    public static async Task<SyncResolveResult> ResolveAsync(StateDatabase database, long id, TrackingResolution resolution, string actor, CancellationToken cancellationToken)
    {
        var divergences = new StateTrackingDivergences(database);
        var divergence = divergences.List().FirstOrDefault(d => d.Id == id);
        if (divergence is null) return new(SyncResolveOutcome.NotFound, null, $"There is no divergence {id}.");
        if (!divergence.IsOpen) return new(SyncResolveOutcome.AlreadyClosed, divergence, $"The divergence {id} is already {divergence.Status}.");
        if (divergence.Stream != SyncUnitStream.Name || !divergence.Identity.StartsWith("syncunit:", StringComparison.Ordinal))
            return new(SyncResolveOutcome.NotSyncStream, divergence, $"The divergence {id} is of '{divergence.Stream}' for '{divergence.Identity}', which is not a copy of Sync's units, so this server does not resolve it.");

        var store = new StateTrackingStore(database);
        var follower = new TrackingFollower(divergence.Identity, SyncUnitStream.Stream, store, store, new ProgressStore(database, SyncUnitStream.ProgressKey), divergences, audit: new AuditLog(database, SyncState.ServiceId));
        var resolved = await follower.ResolveAsync(resolution, actor, cancellationToken).ConfigureAwait(false);
        return resolved
            ? new(SyncResolveOutcome.Resolved, divergences.List().First(d => d.Id == id), null)
            : new(SyncResolveOutcome.ClosedMeanwhile, divergence, $"The divergence {id} could not be resolved (it was closed meanwhile).");
    }
}
