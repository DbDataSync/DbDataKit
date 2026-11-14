using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.State.Tracking;

/// <summary>What a resolve of a divergence of Sync's entries came to (the HTTP-shaped outcome a host answers with).</summary>
public enum SyncPositionLogResolveOutcome
{
    Resolved,
    NotFound,
    AlreadyClosed,
    NotSyncStream,
    ClosedMeanwhile,
}

/// <summary>The result of <see cref="SyncPositionLogCopy.ResolveAsync"/>; <see cref="Message"/> says what is wrong for every outcome but <see cref="SyncPositionLogResolveOutcome.Resolved"/>.</summary>
public sealed record SyncPositionLogResolveResult(SyncPositionLogResolveOutcome Outcome, TrackingDivergence? Divergence, string? Message);

/// <summary>
/// Sync's copy of the entries a replication recorded on its target (plan 28c E17), as the two hosts that can hold it see it: the Sync server (its own state) and the runner (the state of a runner-owned replication, written
/// through the runner protocol by the drain's follower). The one stream <c>entry</c>, its identities (<c>syncposition:&lt;engine&gt;://&lt;server&gt;/&lt;database&gt;/&lt;replication&gt;@&lt;connection&gt;</c>), the document of the
/// <c>GET /api/v1/tracking</c> answer for such copies and the resolve of a divergence live here so that the two apply the same rules. The entries themselves (<c>dbdatasync.position_log</c> on the target) are Sync's
/// (<c>DbDataSync.Drivers.Abstractions.TargetPositionLog</c>); nothing here reads a target.
/// </summary>
public static class SyncPositionLogCopy
{
    public const string Name = "position";

    public const string IdentityPrefix = "syncposition:";

    public static TrackingStream Stream { get; } = new(
        Name,
        ["entry_seq", "entry_id", "replication", "mapping", "watermark_key", "watermark_before", "watermark_after", "watermark_time", "rows_staged", "run_id", "committed_at"],
        orderingKey: "entry_seq", rowKey: "entry_id");

    /// <summary>Whether an identity is a copy of Sync's entries.</summary>
    public static bool IsIdentity(string identity) => identity.StartsWith(IdentityPrefix, StringComparison.Ordinal);

    /// <summary>The replication an identity of this stream is for (the part between the last <c>/</c> and the <c>@</c>).</summary>
    public static string ReplicationOf(string identity)
    {
        var at = identity.LastIndexOf('@');
        var head = at < 0 ? identity : identity[..at];
        return head[(head.LastIndexOf('/') + 1)..];
    }

    public static string ConnectionOf(string identity) => identity[(identity.LastIndexOf('@') + 1)..];

    /// <summary>When the entry was committed on the target, read back from a row's document (null when the document does not say).</summary>
    public static DateTimeOffset? CommittedAt(string document)
    {
        try
        {
            using var parsed = JsonDocument.Parse(document);
            return parsed.RootElement.TryGetProperty("committed_at", out var value) && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The copies and divergences of Sync's entries in <paramref name="database"/>: per identity the checkpoint, rows and whether it is diverged, and every divergence of the stream with its common point. The checkpoints are
    /// read from <paramref name="progressKey"/> (the host's own key). <paramref name="settingsOf"/> gives the mode and primary of a replication as words, or null for one that is gone.
    /// </summary>
    public static (JsonArray Copies, JsonArray Divergences) Document(StateDatabase database, ProgressKey progressKey, Func<string, (string Mode, string? Primary)?> settingsOf)
    {
        var store = new StateTrackingStore(database);
        var progress = new ProgressStore(database, progressKey);
        var divergences = new StateTrackingDivergences(database).List().Where(d => d.Stream == Name && IsIdentity(d.Identity)).ToList();
        var identities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in divergences) identities.Add(d.Identity);
        foreach (var identity in store.Identities().Where(IsIdentity)) identities.Add(identity);

        var copies = new JsonArray();
        foreach (var identity in identities)
        {
            var replication = ReplicationOf(identity);
            var setting = settingsOf(replication);
            var checkpoint = progress.GetCheckpoint(TrackingFollower.CheckpointNameOf(Name), identity);
            var open = divergences.FirstOrDefault(d => d.IsOpen && d.Identity == identity);
            copies.Add(new JsonObject
            {
                ["identity"] = identity,
                ["replication"] = replication,
                ["mode"] = setting?.Mode ?? "state",
                ["primary"] = setting?.Primary,
                ["streams"] = new JsonArray(new JsonObject
                {
                    ["stream"] = Name,
                    ["rows"] = store.Count(identity, Name),
                    ["checkpoint"] = checkpoint is not null && checkpoint.Parts.TryGetValue(TrackingFollower.OrderPartName, out var order) && checkpoint.Parts.TryGetValue(TrackingFollower.KeyPartName, out var key)
                        ? Position(store, identity, new TrackingPosition(order.AsInt, key.AsText)) : null,
                    ["diverged"] = open is not null,
                }),
            });
        }

        return (copies, new JsonArray([.. divergences.OrderByDescending(d => d.Id).Select(d => (JsonNode)Divergence(store, d))]));
    }

    /// <summary>A position with the time the entry was committed on the target, read from the copy's row (null when the copy no longer holds it live, for instance after an archive).</summary>
    private static JsonObject? Position(StateTrackingStore store, string identity, TrackingPosition? p)
    {
        if (p is null) return null;
        var row = store.ReadAfterAsync(identity, Stream, new TrackingPosition(p.Order, ""), 1, CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault(r => r.Order == p.Order);
        var at = row is null ? null : CommittedAt(row.Document);
        return new JsonObject { ["order"] = p.Order, ["key"] = p.Key, ["time"] = at?.ToString("O", CultureInfo.InvariantCulture) };
    }

    private static JsonObject Divergence(StateTrackingStore store, TrackingDivergence d) => new()
    {
        ["id"] = d.Id, ["identity"] = d.Identity, ["stream"] = d.Stream, ["status"] = d.Status, ["open"] = d.IsOpen, ["reason"] = d.Reason,
        ["common"] = Position(store, d.Identity, d.Common), ["primary_high"] = Position(store, d.Identity, d.PrimaryHigh), ["copy_high"] = Position(store, d.Identity, d.CopyHigh),
        ["diverged_at"] = d.DivergedAt, ["resolved_at"] = d.ResolvedAt, ["resolved_by"] = d.ResolvedBy,
    };

    /// <summary>Whether divergence <paramref name="id"/> of <paramref name="database"/> is one of Sync's entries (what a host that holds copies of several kinds asks before choosing the resolve).</summary>
    public static bool IsPositionLogDivergence(StateDatabase database, long id) =>
        new StateTrackingDivergences(database).List().FirstOrDefault(d => d.Id == id) is { } d && d.Stream == Name && IsIdentity(d.Identity);

    /// <summary>Resolves the open divergence <paramref name="id"/> of the copy in <paramref name="database"/>; the checkpoint is the one under <paramref name="progressKey"/>.</summary>
    public static async Task<SyncPositionLogResolveResult> ResolveAsync(StateDatabase database, ProgressKey progressKey, AuditLog? audit, long id, TrackingResolution resolution, string actor, CancellationToken cancellationToken)
    {
        var divergences = new StateTrackingDivergences(database);
        var divergence = divergences.List().FirstOrDefault(d => d.Id == id);
        if (divergence is null) return new(SyncPositionLogResolveOutcome.NotFound, null, $"There is no divergence {id}.");
        if (!divergence.IsOpen) return new(SyncPositionLogResolveOutcome.AlreadyClosed, divergence, $"The divergence {id} is already {divergence.Status}.");
        if (divergence.Stream != Name || !IsIdentity(divergence.Identity))
            return new(SyncPositionLogResolveOutcome.NotSyncStream, divergence, $"The divergence {id} is of '{divergence.Stream}' for '{divergence.Identity}', which is not a copy of Sync's entries, so this server does not resolve it.");

        var store = new StateTrackingStore(database);
        var follower = new TrackingFollower(divergence.Identity, Stream, store, store, new ProgressStore(database, progressKey), divergences, audit: audit);
        var resolved = await follower.ResolveAsync(resolution, actor, cancellationToken).ConfigureAwait(false);
        return resolved
            ? new(SyncPositionLogResolveOutcome.Resolved, divergences.List().First(d => d.Id == id), null)
            : new(SyncPositionLogResolveOutcome.ClosedMeanwhile, divergence, $"The divergence {id} could not be resolved (it was closed meanwhile).");
    }
}
