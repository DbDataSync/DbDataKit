using DbDataSync.Core.Config;
using DbDataSync.Planning;
using DbDataSync.State;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// Phase 125's scheduled reconcile sweep, queued by a drain on a runner (plan 28c E11): the same items the API's <c>ReconcileService.EnqueueScheduledAsync</c> queues (one <see cref="RunKind.ReconcileDeletes"/> item per
/// segment, the <c>KeyReconcile</c> reader and <c>StagingTable</c> cache, the writer the mapping resolves to, the resolved delete guard), into the runner's queue of the drain's owner.
/// <para>
/// **Segments that need a connection to divide are not expanded here.** An <see cref="AutoSegment"/> asks the source's reader for buckets and a <see cref="CustomSegment"/> runs a query against both sides; the API does
/// that with its connection factory. A mapping whose default segmenting has either is refused by name (a warning in the drain's log each time the sweep is due), and the sweep is E12's (a reconcile request becomes
/// <c>RunNow</c> with the segments as a payload). A mapping with no default segmenting (one Full segment) or with explicit list and range segments is queued.
/// </para>
/// </summary>
internal sealed class EngineReconcileEnqueuer(ConfigRepository config, ProtocolPlannerState state) : IScheduledReconcileEnqueuer
{
    private const string ReaderKind = "KeyReconcile";
    private const string CacheKind = "StagingTable";

    public Task<ScheduledReconcileResult> EnqueueScheduledAsync(string replication, string mapping, CancellationToken cancellationToken)
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
            return Task.FromResult(new ScheduledReconcileResult(ScheduledReconcileOutcome.NotFound));
        }

        if (tableMapping.Sources.Count != 1 || tableMapping.Targets.Count != 1)
            return Task.FromResult(Invalid($"Table mapping '{mapping}' has {tableMapping.Sources.Count} source(s) and {tableMapping.Targets.Count} target(s); reconcile-deletes supports 1:1 mappings."));

        // Empty means Full — the same convention DefaultSegmenting already has for a standalone reload's own configured segments.
        IReadOnlyList<BatchReloadSegment> segments = tableMapping.DefaultSegmenting.Count == 0 ? [new FullSegment()] : tableMapping.DefaultSegmenting;
        if (segments.FirstOrDefault(s => s is AutoSegment or CustomSegment) is { } needsConnection)
            return Task.FromResult(Invalid(
                $"The default segmenting of '{mapping}' has a {needsConnection.GetType().Name} that is divided by asking the source or the target, which a drain on the runner does not do yet (plan 28c E12: a reconcile request becomes a run with its segments given). " +
                "Use explicit list or range segments, or leave the segmenting empty for one Full segment, or sweep through the API."));

        var kinds = new WorkItemKinds(ReaderKind, CacheKind, PipelineResolution.ReconcileWriterKind(task, tableMapping));
        var guardJson = DeleteGuardOption.Serialize(PipelineResolution.Reconcile(task, tableMapping).DeleteGuard);
        foreach (var segment in segments)
            state.EnqueueReconcile(replication, mapping, segment.Describe(), SegmentSerializer.Serialize(segment), kinds, guardJson);
        return Task.FromResult(ScheduledReconcileResult.Queued);
    }

    private static ScheduledReconcileResult Invalid(string reason) => new(ScheduledReconcileOutcome.Invalid, reason);
}
