using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Evaluates every enabled replication's schedule on a tick and enqueues due Primary work
/// (architecture/detailed-design.md §3.1). Due-ness is evaluated per table mapping for Continuous
/// replications — a replication can have hundreds of mappings, and one shared due-ness check would
/// mean one slow/behind mapping resets the clock for every other mapping's schedule. Periodic (cron)
/// due-ness stays replication-scoped (one occurrence fires for the whole replication, anchored to the
/// most recent of all its mappings' last Primary starts), then batch-enqueues every mapping at once —
/// see architecture/implementation/done/phase-008-work-queue-schema.md.
/// <para>
/// **Since phase 75, a tick can talk to a source system.** Due-ness is still decided entirely from
/// local config and state; what changed is that a mapping found due on a CDC or Change-Tracking
/// reader is then offered to <see cref="ChangePollingGate"/>, which asks the source database once per
/// tick whether anything is there. That is this service's first I/O against anything it does not
/// own, and it means a tick can now be slowed by a source that is far away or stopped by nothing at
/// all — the gate fails open per source group precisely so an unreachable database costs a wasted
/// dispatch rather than a suppressed schedule.
/// </para>
/// <para>
/// **Since plan 28c E11 the decisions are the planner's** (<see cref="ReplicationPlanner"/>, a library the engine's loop on a runner calls too); this service is what is the API's own: the five second tick, which
/// replications to look at (loaded, enabled and not paused), and starting the worker when a tick queued something.
/// </para>
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly ConfigRepository _configRepository;
    private readonly TaskRunStore _taskRunStore;
    private readonly ProcessSupervisor _supervisor;
    private readonly IReplicationPlanner _planner;
    private readonly ILogger<SchedulerService> _logger;
    private readonly DbDataSync.Api.Runner.RunnerOwnerFile? _owners;

    public SchedulerService(
        ConfigRepository configRepository,
        TaskRunStore taskRunStore,
        WorkQueueStore workQueueStore,
        ProcessSupervisor supervisor,
        ChangePollingGate gate,
        ChangeWatermarkStore watermarks,
        DriverRegistry driverRegistry,
        ReconcileService reconcileService,
        ILogger<SchedulerService> logger,
        DbDataSync.Api.Runner.RunnerOwnerFile? owners = null)
    {
        _configRepository = configRepository;
        _taskRunStore = taskRunStore;
        _supervisor = supervisor;
        _logger = logger;
        _owners = owners;
        _planner = new ReplicationPlanner(
            configRepository, new LocalPlannerState(taskRunStore, workQueueStore, watermarks), gate, driverRegistry, reconcileService, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await TickAsync(stoppingToken);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        foreach (var name in _configRepository.ListReplications())
        {
            // a replication the runner owns is planned by the runner's drain (plan 28c E12): this tick queues nothing for it, which is also the guard against both processing a mapping
            if (_owners?.OwnerOf(name) == DbDataKit.State.TaskOwners.Runner)
                continue;

            ReplicationTaskConfig task;
            List<string> mappingNames;
            try
            {
                task = _configRepository.LoadReplicationTask(name);
                mappingNames = _configRepository.ListTableMappings(name).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping replication '{Name}': failed to load config.", name);
                continue;
            }

            // Paused is read from state, Enabled from the config just loaded — both gates, one
            // definition. A pause stops the *next* thing being scheduled; anything already claimed by
            // a worker runs to completion, exactly as disabling has always behaved.
            if (!TaskScheduling.ShouldRun(task.Enabled, _taskRunStore.IsPaused(name)) || mappingNames.Count == 0)
                continue;

            var enqueuedAny = await _planner.PlanAsync(name, task, mappingNames, cancellationToken);

            // Idempotent and cheap to skip when nothing changed — only worth spawning a worker to
            // check an empty queue when this tick actually put something new into it.
            if (!enqueuedAny)
                continue;

            var result = _supervisor.EnsureWorkerRunning(name);
            if (result.Outcome == TriggerOutcome.FailedToStart)
                _logger.LogWarning("Scheduled worker for '{Name}' failed to start: {Reason}", name, result.Reason);
        }
    }
}
