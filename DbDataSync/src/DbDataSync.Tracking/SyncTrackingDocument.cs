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
        var (copies, divergences) = SyncUnitCopy.Document(database, SyncUnitStream.ProgressKey, settingsOf);
        return new JsonObject
        {
            ["holder"] = "sync-server",
            ["mode"] = "state",
            ["primary"] = null,
            ["copies"] = copies,
            ["divergences"] = divergences,
        };
    }

    /// <summary>Resolves the open divergence <paramref name="id"/> of the copy in <paramref name="database"/>.</summary>
    public static async Task<SyncResolveResult> ResolveAsync(StateDatabase database, long id, TrackingResolution resolution, string actor, CancellationToken cancellationToken)
    {
        var result = await SyncUnitCopy.ResolveAsync(database, SyncUnitStream.ProgressKey, new AuditLog(database, SyncState.ServiceId), id, resolution, actor, cancellationToken).ConfigureAwait(false);
        return new SyncResolveResult((SyncResolveOutcome)(int)result.Outcome, result.Divergence, result.Message);
    }
}
