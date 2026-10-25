using DbDataKit.Monitoring;
using DbDataSync.Core.Config;

namespace DbDataSync.Api.Services;

/// <summary>A replication's lag as the shared monitoring model (plan 13, M06): the worst of its mappings' lags, because a replication is as far behind as its furthest-behind table.</summary>
public static class ReplicationLagReading
{
    /// <summary>
    /// The worst mapping's lag, or null when none can say. An exact lag is preferred over an estimate for a mapping, and the estimate is used only where there is no exact one:
    /// the two are never compared with each other (see <see cref="ReaderLag"/>), so a mapping contributes one figure or none.
    /// </summary>
    public static LagReading? Worst(IEnumerable<ReaderLag> mappings, DateTimeOffset now)
    {
        TimeSpan? worst = null;
        foreach (var lag in mappings)
        {
            var behind = lag.ExactLag ?? lag.EstimatedLag;
            if (behind is { } b && (worst is null || b > worst)) worst = b;
        }
        return worst is { } w ? new LagReading(w, now) : null;
    }

    /// <summary>The reading for one replication, from the lag service's cached figures (it never asks a source).</summary>
    public static LagReading? For(ReaderLagService service, ConfigRepository config, string replicationName, TimeProvider? clock = null)
    {
        var task = config.LoadReplicationTask(replicationName);
        return Worst(config.ListTableMappings(replicationName).Select(m => service.Describe(task, m)), (clock ?? TimeProvider.System).GetUtcNow());
    }
}
