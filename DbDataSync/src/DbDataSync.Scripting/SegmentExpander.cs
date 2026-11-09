using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>Opens a named connection of the configuration and the driver that speaks to it: what dividing a segment and estimating a table need (the API's connection factory, or the engine's own).</summary>
public delegate Task<(DbConnection Connection, IDriver Driver)> OpenSegmentConnection(string connectionName, CancellationToken cancellationToken);

/// <summary>
/// Turns a segment list into the concrete segments a bulk load queues (plan 28c E11b, extracted from <c>BulkLoadService</c>): a <see cref="CustomSegment"/> is replaced by what its strategy selects, an
/// <see cref="AutoSegment"/> is divided into ranges from the source's real minimum and maximum, and a list, range or full segment passes through. Done once, before the segments become queue rows, because every
/// segment is its own unit of work. The API's bulk load service and a drain on a runner both call it, each with its own way of opening a connection, so both divide a table the same way.
/// </summary>
public sealed class SegmentExpander(CustomSegmentExpansion custom)
{
    /// <summary>
    /// <paramref name="readerKindOverride"/> is the request's own optional override where there is a request, and null (resolve the mapping's configured Bulk Load reader) where there is none. Throws
    /// <see cref="InvalidOperationException"/> for a strategy or reader that cannot divide what it was asked to, and the driver's own exceptions for a source that cannot be reached.
    /// </summary>
    public async Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested, string? readerKindOverride,
        OpenSegmentConnection open, CancellationToken cancellationToken, Action<string>? log = null)
    {
        var needsAuto = requested.OfType<AutoSegment>().Any();
        var needsCustom = requested.OfType<CustomSegment>().Any();
        if (!needsAuto && !needsCustom)
            return requested;

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var readerKind = PipelineResolution.BulkLoadReaderKind(readerKindOverride, task, mapping);

        var (connection, driver) = await open(source.ConnectionName, cancellationToken);
        await using (connection)
        {
            IReadOnlyList<BatchReloadSegment> segments = requested;

            if (needsCustom)
            {
                // The target only when something might read it — a strategy that segments from a control table needs the connection, and one that generates months from the calendar should not make anyone open one.
                var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
                var (targetConnection, _) = await open(target.ConnectionName, cancellationToken);
                await using (targetConnection)
                {
                    segments = await custom.ExpandAsync(task, mapping, segments, new SegmentingConnections(connection, targetConnection), cancellationToken, log);
                }
            }

            if (!segments.OfType<AutoSegment>().Any())
                return segments;

            var reader = driver.Readers.FirstOrDefault(r => r.Kind == readerKind)
                ?? throw new InvalidOperationException($"Source driver does not support reader kind '{readerKind}'.");

            if (reader is not ISegmentExpandingReader expanding)
                throw new InvalidOperationException(
                    $"Reader '{readerKind}' cannot divide a column into buckets, so an Auto segment can't be used with " +
                    "it. Pick a reader that supports segmentation, or specify explicit list/range segments.");

            return await expanding.ExpandAutoSegmentsAsync(
                connection, source, segments, mapping.SourceColumns, mapping.Name, mapping.ColumnMappings,
                mapping.Relationships,
                mapping.RelationshipColumns.ToDictionary(kv => kv.Key, IReadOnlyList<CachedColumn> (kv) => kv.Value, StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }
    }

    /// <summary>
    /// A whole-table row count from the source engine's catalog statistics — a best-effort denominator for a batch's progress, not a fact a decision hangs on, so any failure yields <c>(null, null)</c>. Deliberately the whole
    /// table even when the mapping has a row filter (catalog statistics cannot answer a predicate); the caveat says so. A query source or a driver with no catalog has no estimate.
    /// </summary>
    public static async Task<(long? Rows, string? Caveat)> EstimateRowsAsync(ReplicationTaskConfig task, TableMappingConfig mapping, OpenSegmentConnection open, CancellationToken cancellationToken)
    {
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        if (string.IsNullOrWhiteSpace(source.Table))
            return (null, null);

        try
        {
            var (connection, driver) = await open(source.ConnectionName, cancellationToken);
            await using (connection)
            {
                if (driver is not ITableRowEstimator estimator)
                    return (null, null);

                var rows = await estimator.EstimateRowCountAsync(connection, source, cancellationToken);
                return (rows, rows is not null && !string.IsNullOrWhiteSpace(source.Filter) ? "ignores row filter" : null);
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return (null, null);
        }
    }
}
