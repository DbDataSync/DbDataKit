using System.Data.Common;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.Tracking;

/// <summary>What a position check found for one mapping of a replication.</summary>
/// <param name="Mapping">The mapping.</param>
/// <param name="Reason">What is wrong, or null when the target's newest unit and the state's position agree (or it is too soon to tell).</param>
public sealed record PositionFinding(string Mapping, string? Reason);

/// <summary>
/// Sync's follower for one replication (plan 28c E17): copies the units a replication recorded in <c>dbdatasync.unit</c> on its target into the <c>tracking</c> module of Sync's state, with the shared
/// <see cref="TrackingFollower"/> (checkpoint in the progress store, rows and checkpoint in one transaction, divergence marked and resolved by an administrator), and checks that the position the
/// state holds for each mapping is one the target recorded (<see cref="CheckPositionsAsync"/>): a target restored from a backup, or a state that was reset, is a <b>persisted</b> divergence, not
/// only a line in a run's log. It never throws into the caller: a target that is down is the error of the result.
/// </summary>
public sealed class SyncUnitFollower
{
    /// <summary>How long a unit must have been on the target before a difference with the state's position counts: the pass that wrote it moves the state's position a moment later (default taken: two minutes).</summary>
    public static readonly TimeSpan PositionGrace = TimeSpan.FromMinutes(2);

    private readonly StateDatabase _state;
    private readonly TargetUnitState _units;
    private readonly Func<CancellationToken, Task<DbConnection>> _open;
    private readonly string _replication;
    private readonly StateTrackingDivergences _divergences;
    private readonly StateTrackingStore _store;
    private readonly TimeProvider _time;

    public SyncUnitFollower(StateDatabase state, TargetUnitState units, Func<CancellationToken, Task<DbConnection>> open, string identity, string replication,
        TrackingFollowerOptions? options = null, TimeProvider? time = null)
    {
        _state = state;
        _units = units;
        _open = open;
        _replication = replication;
        Identity = identity;
        _time = time ?? TimeProvider.System;
        _store = new StateTrackingStore(state);
        _divergences = new StateTrackingDivergences(state);
        Follower = new TrackingFollower(identity, SyncUnitStream.Stream, new SyncUnitSource(units, open, identity, replication), _store, new ProgressStore(state, SyncUnitStream.ProgressKey), _divergences, options,
            new AuditLog(state, SyncState.ServiceId));
    }

    public string Identity { get; }

    public TrackingFollower Follower { get; }

    /// <summary>Copies what the target recorded since the checkpoint, until caught up, diverged, failed or <paramref name="budget"/> is spent.</summary>
    public Task<TrackingRunResult> CatchUpAsync(TimeSpan budget, CancellationToken cancellationToken = default) => Follower.CatchUpAsync(budget, cancellationToken);

    /// <summary>
    /// For every mapping and source the target holds units for, compares the target's newest unit with the position the state holds (<paramref name="watermarkOf"/>): when the state's position is an older unit of the
    /// target, the target is <b>ahead</b> of the state; when it is no unit the target recorded (or the state holds none), the sides <b>part</b> (a target restored from a backup, a state that was reset). The
    /// difference is marked as the stream's divergence (one open at a time) with the newest unit both hold as the common point, and stays until an administrator resolves it. A difference younger than
    /// <see cref="PositionGrace"/> is left alone.
    /// </summary>
    public async Task<IReadOnlyList<PositionFinding>> CheckPositionsAsync(Func<string, string, string?> watermarkOf, CancellationToken cancellationToken = default)
    {
        var findings = new List<PositionFinding>();
        await using var connection = await _open(cancellationToken).ConfigureAwait(false);
        if (!await _units.ExistsAsync(connection, cancellationToken).ConfigureAwait(false)) return findings;
        var now = _time.GetUtcNow();
        foreach (var latest in await _units.LatestPerMappingAsync(connection, _replication, cancellationToken).ConfigureAwait(false))
        {
            var statePosition = watermarkOf(latest.Mapping, latest.WatermarkKey);
            if (statePosition is not null && string.Equals(statePosition, latest.WatermarkAfter, StringComparison.Ordinal)) { findings.Add(new(latest.Mapping, null)); continue; }
            if (now - latest.CommittedAt < PositionGrace) { findings.Add(new(latest.Mapping, null)); continue; }

            TargetUnitRecord? held = statePosition is null ? null : await _units.FindByPositionAsync(connection, _replication, latest.Mapping, latest.WatermarkKey, statePosition, cancellationToken).ConfigureAwait(false);
            var stateText = statePosition is null ? "none" : $"'{statePosition}'";
            var reason = held is not null
                ? $"The target is ahead of the state for mapping '{latest.Mapping}': its newest unit {latest.Sequence} ended at '{latest.WatermarkAfter}', the state's position {stateText} is the target's unit {held.Sequence}."
                : statePosition is null
                    ? $"The state holds no position for mapping '{latest.Mapping}' but the target recorded its unit {latest.Sequence} ('{latest.WatermarkAfter}'): the state was reset or lost."
                    : $"The state's position {stateText} for mapping '{latest.Mapping}' is not one the target recorded (its newest unit is {latest.Sequence}, '{latest.WatermarkAfter}'): the target was restored from a backup, or replaced.";
            var common = held is null ? null : new TrackingPosition(held.Sequence, held.UnitId.ToString("D"));
            var copyHigh = (await _store.TailAsync(Identity, SyncUnitStream.Stream, 1, cancellationToken).ConfigureAwait(false)).LastOrDefault();
            _divergences.Mark(Identity, SyncUnitStream.Name, reason, common, new TrackingPosition(latest.Sequence, latest.UnitId.ToString("D")), copyHigh);
            findings.Add(new(latest.Mapping, reason));
        }

        return findings;
    }
}
