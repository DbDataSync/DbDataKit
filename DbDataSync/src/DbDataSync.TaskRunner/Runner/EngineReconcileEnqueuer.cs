using DbDataSync.Core.Config;
using DbDataSync.Planning;
using DbDataSync.State;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// Phase 125's scheduled reconcile sweep, queued by a drain on a runner (plan 28c E11, E11b): the same items the API's <c>ReconcileService.EnqueueScheduledAsync</c> queues (one <see cref="RunKind.ReconcileDeletes"/> item per
/// segment, the <c>KeyReconcile</c> reader and <c>StagingTable</c> cache, the writer the mapping resolves to, the resolved delete guard), into the runner's queue of the drain's owner. Segments that need a connection to divide
/// (an <see cref="AutoSegment"/> asks the source's reader for buckets, a <see cref="CustomSegment"/> runs a strategy) are divided by <see cref="IDrainSegments"/>, the engine's own way of opening one, as the API does.
/// </summary>
internal sealed class EngineReconcileEnqueuer(ConfigRepository config, ProtocolPlannerState state, IDrainSegments? segments = null) : IScheduledReconcileEnqueuer
{
    private const string ReaderKind = "KeyReconcile";
    private const string CacheKind = "StagingTable";

    public async Task<ScheduledReconcileResult> EnqueueScheduledAsync(string replication, string mapping, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        TableMappingConfig tableMapping;
        try
        {
            task = config.LoadReplicationTask(replication);
            tableMapping = config.LoadTableMapping(replication, mapping);
        }
        catch (FileNotFoundException)
        {
            return new ScheduledReconcileResult(ScheduledReconcileOutcome.NotFound);
        }

        if (tableMapping.Sources.Count != 1 || tableMapping.Targets.Count != 1)
            return Invalid($"Table mapping '{mapping}' has {tableMapping.Sources.Count} source(s) and {tableMapping.Targets.Count} target(s); reconcile-deletes supports 1:1 mappings.");

        // Empty means Full — the same convention DefaultSegmenting already has for a standalone reload's own configured segments.
        IReadOnlyList<BatchReloadSegment> requested = tableMapping.DefaultSegmenting.Count == 0 ? [new FullSegment()] : tableMapping.DefaultSegmenting;
        if (segments is null && requested.FirstOrDefault(s => s is AutoSegment or CustomSegment) is { } needsConnection)
            return Invalid($"The default segmenting of '{mapping}' has a {needsConnection.GetType().Name}, which is divided by asking the source or the target, and this drain has no way to open a connection to do it.");

        IReadOnlyList<BatchReloadSegment> expanded;
        try
        {
            expanded = segments is null ? requested : await segments.ExpandAsync(task, tableMapping, requested, ReaderKind, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            return Invalid(DbDataSync.Core.Sql.DriverFailures.Describe(ex));
        }

        var kinds = new WorkItemKinds(ReaderKind, CacheKind, PipelineResolution.ReconcileWriterKind(task, tableMapping));
        var guardJson = DeleteGuardOption.Serialize(PipelineResolution.Reconcile(task, tableMapping).DeleteGuard);
        foreach (var segment in expanded)
            state.EnqueueReconcile(replication, mapping, segment.Describe(), SegmentSerializer.Serialize(segment), kinds, guardJson);
        return ScheduledReconcileResult.Queued;
    }

    private static ScheduledReconcileResult Invalid(string reason) => new(ScheduledReconcileOutcome.Invalid, reason);
}
