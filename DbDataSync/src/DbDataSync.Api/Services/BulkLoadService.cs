using DbDataSync.Core.Sql;
using DbDataSync.Api.Models;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Turns a bulk load request into work for the replication's drain on the runner (plan 28c E12; E22: the Runner is Sync's only scheduler): expand any <see cref="AutoSegment"/> against the real
/// source table, then send one request per resulting segment, with the batch as the drain's own record. An initial load a pass asks for is the drain's own business on the runner.
/// </summary>
public sealed class BulkLoadService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    CustomSegmentExpansion customSegments,
    DbDataSync.Api.Runner.RunnerDispatch runnerDispatch)
{
    private readonly SegmentExpander expander = new(customSegments);

    public async Task<TriggerResult> EnqueueAsync(
        string replicationName, string mappingName, BulkLoadRequest request, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return TriggerResult.NotFound();
        }

        if (request.Segments.Count == 0)
            return TriggerResult.Invalid("At least one segment is required — use a Full segment to reload the whole table.");

        if (mapping.Sources.Count != 1)
            return TriggerResult.Invalid(
                $"Table mapping '{mappingName}' has {mapping.Sources.Count} sources; bulk load supports 1:1 mappings in v1.");

        IReadOnlyList<BatchReloadSegment> segments;
        try
        {
            segments = await ExpandAsync(task, mapping, request.Segments, request.ReaderKind, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException
                                       or MetadataNotCachedException)
        {
            // A bad segment column, an undividable column type, an unreachable source, or (phase 167V:
            // ExpandAutoSegmentsAsync now resolves a segment column's type from the mapping's cache
            // rather than a live catalog call) a segment column the cache doesn't have are all the
            // request being wrong or unrunnable — not a server fault, and worth saying plainly rather
            // than enqueueing work that is guaranteed to fail once a worker claims it.
            return TriggerResult.Invalid(DriverFailures.Describe(ex));
        }

        // the reload is a request of the run of the drain (plan 28c E12), with its batch as the drain's own record
        var order = SegmentOrderResolution.Resolve(request.SegmentOrder, TargetConnection(task, mapping), task, mapping);
        var (rows, caveat) = await SegmentExpander.EstimateRowsAsync(task, mapping, connections.OpenAsync, cancellationToken);
        var batch = Guid.NewGuid().ToString("N");
        var requests = segments.Select(s => new DrainRequest(
            nameof(RunKind.BulkLoad), mappingName, s.Describe(), SegmentSerializer.Serialize(s), request.ReaderKind, request.CacheKind, request.WriterKind,
            OneAtATime: order == SegmentOrder.OneAtATime, BatchId: batch, BatchSegments: segments.Count, EstimatedRows: rows, EstimateCaveat: caveat)).ToList();
        return await runnerDispatch.SendAsync(replicationName, requests, runAll: false, cancellationToken);
    }

    /// <summary>The connection the mapping writes to, for the segment-order default it may carry; null when the mapping has no target or the connection cannot be read, in
    /// which case the levels above it decide.</summary>
    private ConnectionConfig? TargetConnection(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        try
        {
            return mapping.Targets.Count == 0
                ? null
                : configRepository.LoadConnection(EndpointResolution.ResolveTarget(task, mapping.Targets[0]).ConnectionName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <see cref="AutoSegment"/>s and <see cref="CustomSegment"/>s into concrete segments (<see cref="SegmentExpander"/>, shared with a drain on a runner). Done here, once, rather than in the
    /// worker: the bounds come from the source's real MIN/MAX, and each resulting range has to become its own queue row so segments can be scheduled, retried and observed independently.
    /// </summary>
    private Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested,
        string? readerKindOverride, CancellationToken cancellationToken) =>
        expander.ExpandAsync(task, mapping, requested, readerKindOverride, connections.OpenAsync, cancellationToken);
}
