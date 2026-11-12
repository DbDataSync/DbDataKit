using System.Data.Common;
using DbDataKit.State.Tracking;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Tracking;

/// <summary>
/// <c>dbdatasync.unit</c> on a replication's target as the primary side of a follower (plan 28c E17): the units after a position, the newest one, the tail the two sides are compared by. One instance is for one
/// replication on one target; the identity a follower passes must be the one it was made for. A target without the table (never used, or restored from before it was made) answers as a primary with no rows, which is
/// what makes the follower mark the copy diverged when it holds units.
/// </summary>
public sealed class SyncUnitSource(TargetUnitState state, Func<CancellationToken, Task<DbConnection>> open, string identity, string replication) : ITrackingSource
{
    private void Check(string asked, TrackingStream stream)
    {
        if (!string.Equals(asked, identity, StringComparison.Ordinal)) throw new InvalidOperationException($"This source is for '{identity}', not '{asked}'.");
        if (stream.Name != SyncUnitStream.Name) throw new InvalidOperationException($"Sync's tracking has one stream, '{SyncUnitStream.Name}', not '{stream.Name}'.");
    }

    private async Task<T?> WithTableAsync<T>(Func<DbConnection, Task<T>> read, CancellationToken cancellationToken)
    {
        await using var connection = await open(cancellationToken).ConfigureAwait(false);
        if (!await state.ExistsAsync(connection, cancellationToken).ConfigureAwait(false)) return default;
        return await read(connection).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackingRow>> ReadAfterAsync(string identity, TrackingStream stream, TrackingPosition? after, int limit, CancellationToken cancellationToken)
    {
        Check(identity, stream);
        var units = await WithTableAsync(c => state.ReadAfterAsync(c, replication, after?.Order ?? 0, limit, cancellationToken), cancellationToken).ConfigureAwait(false);
        return units is null ? [] : [.. units.Select(SyncUnitStream.ToRow)];
    }

    public async Task<TrackingPosition?> HighWaterMarkAsync(string identity, TrackingStream stream, CancellationToken cancellationToken)
    {
        Check(identity, stream);
        var unit = await WithTableAsync(c => state.HighAsync(c, replication, cancellationToken), cancellationToken).ConfigureAwait(false);
        return unit is null ? null : new TrackingPosition(unit.Sequence, unit.UnitId.ToString("D"));
    }

    public async Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken cancellationToken)
    {
        Check(identity, stream);
        var units = await WithTableAsync(c => state.TailAsync(c, replication, count, cancellationToken), cancellationToken).ConfigureAwait(false);
        return units is null ? [] : [.. units.Select(u => new TrackingPosition(u.Sequence, u.UnitId.ToString("D")))];
    }
}
