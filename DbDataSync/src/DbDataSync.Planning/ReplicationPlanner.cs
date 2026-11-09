using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.Planning;

/// <summary>
/// Evaluates a replication's schedule and queues what is due (plan 28c E11): the logic of <c>SchedulerService</c>'s ticks, extracted so the API's scheduler and the engine's loop on a runner decide the same way.
/// Due-ness is evaluated per table mapping for Continuous replications — a replication can have hundreds of mappings, and one shared due-ness check would mean one slow or behind mapping resets the clock for every
/// other mapping's schedule. Periodic (cron) due-ness stays replication-scoped (one occurrence fires for the whole replication, anchored to the most recent of all its mappings' last Primary queue times), then
/// batch-enqueues every mapping at once — see architecture/implementation/done/phase-008-work-queue-schema.md.
/// <para>
/// **A tick can talk to a source system** (phase 75): a mapping found due on a CDC or Change-Tracking reader is offered to <see cref="ChangePollingGate"/>, which asks the source database once per tick whether
/// anything is there, and fails open per source group, so an unreachable database costs a wasted dispatch rather than a suppressed schedule.
/// </para>
/// <para>
/// **Where its inputs and its queue are** is the <see cref="IPlannerState"/>: Sync's own state database for the API, the runner's queue of the drain's own owner for the engine. The planner never starts a worker or
/// a process; that is the caller's (the API's supervisor; a drain is already running).
/// </para>
/// </summary>
public sealed class ReplicationPlanner(
    ConfigRepository configRepository,
    IPlannerState state,
    ChangePollingGate gate,
    DriverRegistry driverRegistry,
    IScheduledReconcileEnqueuer reconciles,
    ILogger logger) : IReplicationPlanner
{
    public async Task<bool> PlanAsync(string name, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken)
    {
        var enqueuedAny = task.Scheduling.Mode == ScheduleMode.Periodic
            ? await TickPeriodicAsync(name, task, mappingNames, cancellationToken)
            : await TickContinuousAsync(name, task, mappingNames, cancellationToken);

        // Phase 125: a mapping's delete-reconciliation cadence/after-change trigger, independent of the replication's own Scheduling.Mode — a sweep has its own SchedulingConfig (ReconcileConfig.Every) and is
        // evaluated the same way regardless of whether the containing replication is Continuous or Periodic.
        var reconcileEnqueuedAny = await TickReconcileAsync(name, task, mappingNames, cancellationToken);
        return enqueuedAny || reconcileEnqueuedAny;
    }

    public async Task<bool> TickContinuousAsync(
        string name, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken)
    {
        var lastStarts = state.GetLastPrimaryEnqueueByMapping(name);
        var now = DateTimeOffset.UtcNow;
        var due = new List<string>();

        foreach (var mappingName in mappingNames)
        {
            var lastStart = lastStarts.TryGetValue(mappingName, out var value) ? value : (DateTimeOffset?)null;
            if (SchedulingEvaluator.IsDue(task.Scheduling, lastStart, now))
                due.Add(mappingName);
        }

        return await EnqueueAsync(name, task, due, cancellationToken);
    }


    public async Task<bool> TickPeriodicAsync(
        string name, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken)
    {
        var lastStarts = state.GetLastPrimaryEnqueueByMapping(name);
        var lastAny = lastStarts.Count > 0 ? lastStarts.Values.Max() : (DateTimeOffset?)null;

        if (!SchedulingEvaluator.IsDue(task.Scheduling, lastAny, DateTimeOffset.UtcNow))
            return false;

        return await EnqueueAsync(name, task, mappingNames, cancellationToken);
    }


    /// <summary>
    /// Enqueues the mappings this tick found due, less any the gate can show have nothing waiting and
    /// less any a <see cref="ReadHold"/> stops (see <see cref="FilterHeld"/>).
    /// <para>
    /// One place for both schedule modes, because the gate's question is about a mapping's source and
    /// not about why it came to be due. A cron occurrence that fires over a source nothing has
    /// written to since the last one is the same wasted pass as a continuous interval doing it, and
    /// the periodic path is if anything the one where a hundred mappings arrive at once.
    /// </para>
    /// <para>
    /// The gate never adds, only removes, and a due-ness clock unchanged by it: a mapping the gate
    /// skips is not enqueued and so does not record a Primary enqueue, which leaves it due again next
    /// tick. That is the intent — being caught up is not progress to be timed from. A held mapping is
    /// filtered out before the gate ever sees it, for the same reason — no round-trip is spent asking a
    /// source about a mapping this tick was never going to dispatch.
    /// </para>
    /// </summary>
    private async Task<bool> EnqueueAsync(
        string name, ReplicationTaskConfig task, IReadOnlyList<string> due, CancellationToken cancellationToken)
    {
        if (due.Count == 0)
            return false;

        var runnable = FilterHeld(task, due);
        if (runnable.Count == 0)
            return false;

        var admitted = await gate.AdmitAsync(task, runnable, cancellationToken);

        foreach (var mappingName in admitted)
            state.EnqueuePrimary(name, mappingName);

        return admitted.Count > 0;
    }


    /// <summary>
    /// Phase 125: evaluates every mapping's <see cref="ReconcileConfig"/> for due-ness — its own
    /// <see cref="ReconcileConfig.Every"/> cadence, or its <see cref="ReconcileConfig.AfterChange"/>
    /// strategy against rows a Primary pass has read since the mapping's last sweep — and enqueues a
    /// scheduled sweep for whichever mappings qualify.
    /// <para>
    /// **Sharing one enqueue path with the after-change trigger**, per the plan's own Q3: both ask the
    /// same question ("is a sweep due for this mapping right now") and both go through
    /// <see cref="IScheduledReconcileEnqueuer.EnqueueScheduledAsync"/> — cadence-due and after-change-due are just
    /// two different reasons to arrive at the same call.
    /// </para>
    /// <para>
    /// After-change is floored by the cadence (never firing more often than <see cref="ReconcileConfig.Every"/>
    /// allows) — enforced structurally rather than by a separate check, because
    /// <see cref="ConfigValidation.ValidateReconcile"/> already requires <c>Every</c> to be set whenever
    /// <c>AfterChange</c> is not <see cref="NoAfterChangeStrategy"/>, so the same <c>IsDue</c> check that
    /// gates the cadence trigger gates the after-change one too.
    /// </para>
    /// </summary>
    public async Task<bool> TickReconcileAsync(
        string name, ReplicationTaskConfig task, IReadOnlyList<string> mappingNames, CancellationToken cancellationToken)
    {
        var candidates = new List<(string MappingName, ReconcileConfig Config)>();
        foreach (var mappingName in mappingNames)
        {
            TableMappingConfig mapping;
            try
            {
                mapping = configRepository.LoadTableMapping(name, mappingName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping reconcile due-ness for '{Task}'/'{Mapping}': failed to load config.", name, mappingName);
                continue;
            }

            var reconcile = PipelineResolution.Reconcile(task, mapping);
            if (reconcile.Enabled)
                candidates.Add((mappingName, reconcile));
        }

        if (candidates.Count == 0)
            return false;

        var lastReconcile = state.GetLastReconcileEnqueueByMapping(name);
        var now = DateTimeOffset.UtcNow;

        // "Since forever" (DateTimeOffset.MinValue) for a mapping that has never been swept — the same
        // "never run before, due immediately" posture SchedulingEvaluator.IsDue takes for a null last-run.
        var sinceByMapping = candidates.ToDictionary(
            c => c.MappingName,
            c => lastReconcile.TryGetValue(c.MappingName, out var last) ? last : DateTimeOffset.MinValue);
        // read only when a mapping has an after-change strategy to feed with it: a cadence-only sweep never looks at it, and a state that cannot say (a drain on a runner keeps no history of rows read, yet) is not asked for nothing
        var rowsReadSince = candidates.Any(c => c.Config.AfterChange is not NoAfterChangeStrategy)
            ? state.GetRowsReadSincePerMapping(name, sinceByMapping)
            : new Dictionary<string, long>();

        var enqueuedAny = false;
        foreach (var (mappingName, reconcile) in candidates)
        {
            var lastEnqueue = lastReconcile.TryGetValue(mappingName, out var last) ? (DateTimeOffset?)last : null;
            var due = reconcile.Every is not null && SchedulingEvaluator.IsDue(reconcile.Every, lastEnqueue, now);

            // NoAfterChangeStrategy: Every is the *only* trigger — a sweep fires on its own schedule
            // regardless of whether anything changed. Any other strategy: Every stops being an
            // unconditional trigger and becomes purely the after-change floor (never firing more often
            // than it allows) — a sweep now needs *both* enough time elapsed *and* something to react
            // to. Without that split, an after-change strategy would never differ observably from
            // NoAfterChangeStrategy: `due` alone already fires every time Every's interval elapses, so
            // OR-ing in a check that can only be true when `due` already is would be a no-op.
            var cadenceDue = reconcile.AfterChange is NoAfterChangeStrategy && due;
            var afterChangeDue = reconcile.AfterChange is not NoAfterChangeStrategy
                && (reconcile.Every is null || due)
                && AfterChangeEvaluator.ShouldReconcile(reconcile.AfterChange, rowsReadSince.GetValueOrDefault(mappingName));

            if (!cadenceDue && !afterChangeDue)
                continue;

            if (state.HasPendingReconcile(name, mappingName))
                continue; // Already in flight — no second sweep queued behind it.

            var result = await reconciles.EnqueueScheduledAsync(name, mappingName, cancellationToken);
            if (result.Outcome == ScheduledReconcileOutcome.Queued)
                enqueuedAny = true;
            else if (result.Outcome is ScheduledReconcileOutcome.Invalid or ScheduledReconcileOutcome.NotFound)
                logger.LogWarning(
                    "Scheduled reconcile sweep for '{Task}'/'{Mapping}' could not be enqueued: {Reason}",
                    name, mappingName, result.Reason);
        }

        return enqueuedAny;
    }


    /// <summary>
    /// Removes every mapping this tick found due that a <see cref="ReadHold"/> stops from being
    /// dispatched as a scheduled <c>Primary</c> pass — the fix phase 101 exists for: without it, a
    /// mapping whose position expired keeps being enqueued, fails again, and gets notified about again,
    /// every tick, forever.
    /// <para>
    /// **Here, at the scheduler — the highest of the three places this could live** (the alternatives
    /// being the work-queue enqueue itself, or the runner claiming an item). Chosen over the lower two
    /// because it is the one place that stops a held mapping from ever becoming a <c>WorkQueue</c> row
    /// or a <c>TaskRuns</c> history entry nobody was ever going to let run — the same reasoning
    /// <see cref="ChangePollingGate"/> already applies one line below this filter, for a quiet source
    /// instead of a held one. A manual "Run Now" (the supervisor's <c>TriggerReplication</c>) is
    /// deliberately left alone: an operator explicitly asking is not the automatic, unattended
    /// resubmission this phase exists to stop, and letting it through is one more way to notice a
    /// mapping is still held.
    /// </para>
    /// <para>
    /// **Does not touch a BulkLoad.** This filter only ever runs over mappings due for a scheduled
    /// Primary pass — a BulkLoad is enqueued by <c>BulkLoadService</c>, an entirely separate path this
    /// method never sees. That is deliberate: a BulkLoad does not use the cursor a hold exists to
    /// protect, and refusing to let an operator recover a held mapping by bulk loading it would be a
    /// second, worse kind of stuck.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> FilterHeld(ReplicationTaskConfig task, IReadOnlyList<string> due) =>
        [.. due.Where(mappingName => ResolveHold(task, mappingName) == ReadHold.None)];

    /// <summary>Best-effort: anything that stops this from resolving (missing config, an unresolvable
    /// dialect, more than one source) is not this filter's failure to report — the mapping goes through
    /// unheld, exactly as it always would have before this filter existed, and whatever is actually
    /// wrong with it surfaces from the pass itself.</summary>
    private ReadHold ResolveHold(ReplicationTaskConfig task, string mappingName)
    {
        try
        {
            var mapping = configRepository.LoadTableMapping(task.Name, mappingName);
            if (mapping.Sources.Count != 1)
                return ReadHold.None;

            var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            var connection = configRepository.LoadConnection(source.ConnectionName);
            if (driverRegistry.Get(connection.DriverType) is not IDialectProvider dialectProvider)
                return ReadHold.None;

            var watermarkKey = WatermarkKey.Build(source, dialectProvider.Dialect);
            return state.GetReadState(task.Name, mappingName, watermarkKey)?.Hold ?? ReadHold.None;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not resolve the read hold for '{Task}'/'{Mapping}'; dispatching it unheld.",
                task.Name, mappingName);
            return ReadHold.None;
        }
    }
}
