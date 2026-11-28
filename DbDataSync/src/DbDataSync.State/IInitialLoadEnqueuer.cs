namespace DbDataSync.State;

/// <summary>
/// The segment-expansion/enqueue core behind "start a bulk load for this mapping", reached through this interface
/// because <see cref="LocalRunnerState"/> lives in this project and an implementation needs <c>ConfigRepository</c>,
/// driver connections and script-backed segment expansion, which do not belong down here. (The Sync server's
/// <c>BulkLoadService</c> was its implementation until plan 33 SC23 deleted the server; on a Runner a bulk load is a
/// request of the replication's drain, and the tests give their own.)
/// <para>
/// See phase 134's <c>IRunnerState.RequestInitialLoad</c> — <see cref="LocalRunnerState"/>'s
/// implementation of it is this interface's only caller, and takes it as a <see cref="Lazy{T}"/> rather
/// than resolving it directly: this interface's only real implementation (<c>BulkLoadService</c>)
/// depends, transitively, on <c>StateHost</c> — the same <c>IHostedService</c> that depends on
/// <see cref="LocalRunnerState"/> to exist at all — so resolving it eagerly in that constructor closes a
/// DI cycle that hangs the host at startup. See <c>LocalRunnerState</c>'s own doc on the field for the
/// full chain.
/// </para>
/// </summary>
public interface IInitialLoadEnqueuer
{
    /// <summary>
    /// Segments this mapping's own <c>TableMappingConfig.DefaultSegmenting</c> (empty means Full — the
    /// same convention every other scheduled reload path already follows) and enqueues one
    /// <c>RunKind.BulkLoad</c> work item per segment under <paramref name="batchId"/>, which the caller
    /// has already minted and written a pending watermark row under.
    /// </summary>
    Task EnqueueForInitialLoadAsync(
        string replicationName, string mappingName, string batchId, CancellationToken cancellationToken);

    /// <summary>
    /// Enqueues one <c>RunKind.BulkLoad</c> work item per already-resolved segment under a fresh batch
    /// and returns its id. For a Primary pass that resolved its own segments (phase 203M) and must not
    /// have them expanded a second time.
    /// </summary>
    Task<string> EnqueueSegmentsAsync(
        string replicationName, string mappingName, IReadOnlyList<DbDataKit.Sync.Core.Config.BatchReloadSegment> segments,
        CancellationToken cancellationToken);
}
