using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Planning;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// The engine's loop on a runner (plan 28c E11): what <see cref="RunExecutor.ExecuteWorkerAsync"/>'s producer does on the API's worker, as the body of a drain. Each cycle begins with the planner (the same
/// <see cref="IReplicationPlanner"/> the API's scheduler calls) deciding what is due and queueing it in the runner's queue of this replication, then claims the items one at a time (a claim only when a slot is free, so
/// nothing is claimed ahead of the passes that will run it), and runs each as a unit of the run.
/// <para>
/// **How it ends is the replication's mode, as on the worker** (QE11: the idle timeout is kept). A periodic drain plans once, runs what is queued and ends when the queue gives it nothing for a few sweeps in a row. A
/// continuous drain stays alive between passes and ends once it has gone a whole <see cref="SchedulingConfig.IdleTimeout"/> without a primary pass reading anything, so a live load is not restarted several times a minute;
/// that process churn is why the idle timeout exists. A cancel (the run's token) is a stop at an item's boundary: nothing more is claimed, passes in flight finish, and what was claimed and not run goes back.
/// </para>
/// </summary>
internal sealed class DrainLoop(
    string replication,
    ConfigRepository config,
    IReplicationPlanner planner,
    ProtocolPlannerState plannerState,
    ProtocolRunnerState state,
    IDrainQueue queue,
    bool runAll,
    int parallelism,
    ILogger log,
    Action<string>? status = null,
    TimeSpan? cycleInterval = null,
    TimeSpan? sweepInterval = null,
    IDrainOutputs? outputs = null,
    Func<DbDataKit.Runner.Kit.RunnerWorkItem, Task>? requestItem = null)
{
    /// <summary>Sweeps that find nothing to claim, with nothing of ours in flight, before a periodic drain ends (the worker's <c>EmptyPollsBeforeExit</c>).</summary>
    public const int EmptySweepsBeforeExit = 5;

    private static readonly TimeSpan DefaultCycle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultSweep = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<long, string> _inHand = new();
    private readonly SemaphoreSlim _slots = new(Math.Max(1, parallelism), Math.Max(1, parallelism));
    private long _lastProductiveTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long _claimedInSweep;

    /// <summary>The cycles this drain made, for the run's status line and for a test.</summary>
    public int Cycles { get; private set; }

    /// <summary>A pass read rows: the drain is not idle (only a primary pass counts, as on the worker: a bulk load or a verification says nothing about whether changes are still arriving).</summary>
    public void NotePass(WorkItem item, PassOutcome outcome)
    {
        if (outcome.RowsRead > 0 && item.RunKind == RunKind.Primary)
            Interlocked.Exchange(ref _lastProductiveTicks, DateTimeOffset.UtcNow.UtcTicks);
        // what an after-change reconcile strategy reacts to: the rows a mapping's successful primary passes read
        if (item.RunKind == RunKind.Primary && outcome.Status == RunStatus.Succeeded)
        {
            plannerState.NoteRowsRead(item.MappingName, outcome.RowsRead);
            plannerState.NotePassSucceeded(item.MappingName);   // what reconcile.afterEveryPasses counts
        }
    }

    /// <summary>An item is over, however it ended: its slot is free for the next claim and it is not the drain's to give back.</summary>
    public void PassFinished(PlannedPass pass)
    {
        if (_inHand.TryRemove(pass.Item.Id, out _))
            _slots.Release();
    }

    private TimeSpan IdleFor() => DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref _lastProductiveTicks), TimeSpan.Zero);

    /// <param name="runUnits">Runs the claimed items as units of the run, <c>parallelism</c> at a time, and returns when the sequence ended and every item is done.</param>
    public async Task RunAsync(Func<IAsyncEnumerable<PlannedPass>, int, CancellationToken, Task> runUnits, CancellationToken ct)
    {
        var task = config.LoadReplicationTask(replication);
        state.UpsertTask(task.Name, task.Enabled);
        var continuous = task.Scheduling.Mode == ScheduleMode.Continuous;
        var emptySweeps = 0;
        var first = true;
        var pausedSaid = false;

        while (!ct.IsCancellationRequested)
        {
            Cycles++;
            task = config.LoadReplicationTask(replication);
            var mappings = config.ListTableMappings(replication).Order(StringComparer.Ordinal).ToList();

            // A pause on the runner stops the next start of a drain, and a drain that is up sees it here: it plans nothing new (what is queued still runs, as on the worker, whose scheduler is what a pause stops), and
            // "Run now" still queues its passes (an operator asked). Enabled is the definition's own (the manifest's schedule being active is the runner's).
            if (task.Enabled && mappings.Count > 0)
            {
                await plannerState.RefreshAsync(ct).ConfigureAwait(false);
                if (runAll && first)
                {
                    foreach (var mapping in mappings)
                        plannerState.EnqueuePrimary(replication, mapping);
                    log.LogInformation("Run all: a pass is queued for each of the {Count} mapping(s) of '{Replication}'.", mappings.Count, replication);
                }
                else if (TaskScheduling.ShouldRun(task.Enabled, plannerState.Paused))
                {
                    await planner.PlanAsync(replication, task, mappings, ct).ConfigureAwait(false);
                    outputs?.ChangeChecks(replication, plannerState.TakeChecks());
                }
                else if (!pausedSaid)
                {
                    pausedSaid = true;
                    log.LogInformation("'{Replication}' is paused on the runner: this drain plans nothing new until it is resumed.", replication);
                }

                if (!plannerState.Paused) pausedSaid = false;
            }

            first = false;
            status?.Invoke($"cycle {Cycles} of '{replication}'");

            // sweeps until a sweep claims nothing: items that were not claimable (the next segment of a mapping, waiting on the one running) are claimable when the sweep before has finished
            long claimed;
            do
            {
                Interlocked.Exchange(ref _claimedInSweep, 0);
                await runUnits(ClaimAsync(ct), parallelism, ct).ConfigureAwait(false);
                claimed = Interlocked.Read(ref _claimedInSweep);
            }
            while (claimed > 0 && !ct.IsCancellationRequested);

            if (ct.IsCancellationRequested)
                break;

            if (!continuous)
            {
                emptySweeps = claimed == 0 ? emptySweeps + 1 : 0;
                // A periodic drain ends when its queue stays empty: nothing of ours is in flight (the sweep above ran every claimed item to its end), and nothing was claimable for a few sweeps.
                var outstanding = await plannerState.RefreshAsync(ct).ConfigureAwait(false);
                if (!outstanding || emptySweeps >= EmptySweepsBeforeExit)
                    break;
                await Task.Delay(sweepInterval ?? DefaultSweep, ct).ConfigureAwait(false);
                continue;
            }

            // A pause ends a continuous drain once nothing of ours is left (plan 28c E12c): it plans nothing new while paused, so it would only wait out its idle timeout. This is how a rollback stops it, and a pause that is lifted
            // starts the next drain by the runner's own schedule.
            if (plannerState.Paused && !await plannerState.RefreshAsync(ct).ConfigureAwait(false))
            {
                log.LogInformation("'{Replication}' is paused on the runner and its queue is empty: this drain ends.", replication);
                break;
            }

            // Continuous: an empty queue is the normal state between passes, not a reason to go. The drain leaves only once it has gone a whole idle timeout without a primary pass reading anything.
            if (IdleFor() >= task.Scheduling.IdleTimeout)
            {
                log.LogInformation("'{Replication}' has been idle for {Idle}: this drain ends (idle timeout {Timeout}).", replication, IdleFor(), task.Scheduling.IdleTimeout);
                break;
            }

            await WaitForNextCycleAsync(task.Scheduling, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits out the time to the next cycle: the schedule's interval, at most five seconds (the scheduler's tick), and stops early the moment there is something to claim (a person pressing Run now, a bulk load
    /// queued while this drain is up). A continuous drain polls the queue once a second for that, as the worker does.
    /// </summary>
    private async Task WaitForNextCycleAsync(SchedulingConfig scheduling, CancellationToken ct)
    {
        var interval = cycleInterval ?? TimeSpan.FromSeconds(Math.Max(1, Math.Min(scheduling.FrequencySeconds ?? 5, DefaultCycle.TotalSeconds)));
        var sweep = sweepInterval ?? DefaultSweep;
        var deadline = DateTimeOffset.UtcNow + interval;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < sweep ? remaining : sweep, ct).ConfigureAwait(false);
            if (await plannerState.RefreshAsync(ct).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>The claims of one sweep, one at a time and only when a slot is free; ends when the queue has nothing for this run, or the run is told to stop.</summary>
    private async IAsyncEnumerable<PlannedPass> ClaimAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            var item = await queue.ClaimAsync(null, CancellationToken.None).ConfigureAwait(false);
            if (item is null)
            {
                _slots.Release();
                yield break;
            }

            if (item.Kind == DrainRequestText.ItemKind)
            {
                // a request of the API that came while this drain was up (plan 28c E12c): applied here, in the drain that owns the replication's progress, and not run as a pass
                _slots.Release();
                if (requestItem is not null) await requestItem(item).ConfigureAwait(false);
                else await queue.ReleaseAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            var claimed = state.Claimed(replication, item);
            _inHand[claimed.Id] = claimed.MappingName;
            Interlocked.Increment(ref _claimedInSweep);
            yield return new PlannedPass(claimed.MappingName, claimed, Guid.NewGuid().ToString("N")[..8]);
        }
    }

    /// <summary>What was claimed and never finished (the run was told to stop with items buffered, a pass threw before it could say how) goes back to the queue, as Pending.</summary>
    public async Task ReleaseUnfinishedAsync()
    {
        foreach (var (id, mapping) in _inHand.ToArray())
        {
            try
            {
                if (await queue.ReleaseAsync(id, CancellationToken.None).ConfigureAwait(false))
                    log.LogInformation("The claim on '{Mapping}' (item {Id}) is given back to the queue: the drain stopped before it ran.", mapping, id);
            }
            catch (Exception ex)
            {
                // the run's end gives back what is left
                log.LogWarning(ex, "Could not give back the claim on item {Id}.", id);
            }

            _inHand.TryRemove(id, out _);
        }
    }
}
