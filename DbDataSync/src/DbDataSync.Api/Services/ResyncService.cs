using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// The operator's way to ask for a full reload of a mapping — most often the recovery for a run that
/// failed because the source discarded the history it needed (see <c>PositionExpiredException</c>), but
/// no longer only that.
/// <para>
/// **Sets an intent rather than performing a reload.** Phase 100's <see cref="ReadIntent"/> and
/// <see cref="ReadHold"/> replace what this used to do by hand — clear the stored watermark, then force
/// a <c>BatchReload</c> run — with the same act said out loud: <see cref="ReadIntent.InitialLoad"/> and
/// clearing the hold, together, so recovering from one without the other (which would leave the mapping
/// still stuck) is not a state this can land in. The *next* <c>Primary</c> pass resolves
/// <see cref="ReadIntent.InitialLoad"/> itself and performs the full load — there is no separate reload
/// path to construct here any more, which is the whole reason an intent rather than a special-cased
/// reload existed to give (see phase 101's amended §1).
/// </para>
/// <para>
/// **The gate is wider than it used to be.** Requiring <c>RunFailureKinds.PositionExpired</c> was too
/// narrow once an intent exists to ask for directly: "reload this mapping" is a thing an operator can
/// legitimately want at any time, not only while recovering from that one specific hold. Any run
/// identifies which mapping to reload; nothing about its outcome gates whether that request is honoured.
/// </para>
/// </summary>
public sealed class ResyncService(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    TaskRunStore taskRunStore,
    DbDataSync.Api.Runner.RunnerDispatch runnerDispatch,
    DbDataSync.Api.Runner.RunnerRunSource runnerRuns)
{
    public async Task<TriggerResult> ResyncAsync(Guid runId, CancellationToken cancellationToken)
    {
        // a run of this server's history from before the Runner ran everything, or a pass (or drain) of the runner's history (plan 28c E22 precondition P2)
        TaskRunRecord? run;
        try { run = taskRunStore.GetRun(runId) ?? (runnerRuns.AnyOwned ? await runnerRuns.FindAsync(runId, cancellationToken) : null); }
        catch (Exception ex) when (ex is DbDataSync.Api.Runner.RunnerUnavailableException or DbDataKit.Runner.Client.RunnerClientException)
        {
            return TriggerResult.FailedToStart(ex.Message);
        }

        if (run is null || string.IsNullOrEmpty(run.MappingName))
            return TriggerResult.NotFound();

        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        SourceTableRef source;
        ConnectionConfig sourceConnectionConfig;
        try
        {
            task = configRepository.LoadReplicationTask(run.TaskName);
            mapping = configRepository.LoadTableMapping(run.TaskName, run.MappingName);

            if (mapping.Sources.Count != 1)
                return TriggerResult.Invalid(
                    $"Table mapping '{run.MappingName}' has {mapping.Sources.Count} sources; resync supports 1:1 mappings.");

            // Loaded for its driver, not to connect: the watermark key is spelled by the source
            // engine's dialect, so setting the intent means knowing which engine wrote the row. Reading
            // the connection's *configuration* rather than opening it keeps a resync possible for a
            // source that is unreachable — which is a plausible way to have arrived here in the first
            // place.
            source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            sourceConnectionConfig = configRepository.LoadConnection(source.ConnectionName);
        }
        catch (FileNotFoundException)
        {
            return TriggerResult.NotFound();
        }

        var sourceDialect = (driverRegistry.Get(sourceConnectionConfig.DriverType) as IDialectProvider)?.Dialect
            ?? throw new InvalidOperationException(
                $"The '{sourceConnectionConfig.DriverType}' driver does not name a SQL dialect.");

        var watermarkKey = WatermarkKey.Build(source, sourceDialect);

        // the position is the drain's progress on the runner: the resync is a request of its next run (plan 28c E12, E22)
        return await runnerDispatch.SendAsync(run.TaskName, [new DrainRequest("Resync", run.MappingName, SourceKey: watermarkKey)], runAll: false, cancellationToken);
    }
}
