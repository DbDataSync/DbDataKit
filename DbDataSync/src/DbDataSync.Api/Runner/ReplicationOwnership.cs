using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>What one move did, for the response and the log.</summary>
public sealed record OwnershipChange(string Replication, string From, string To, string Task, int PositionsHandedOver, DateTimeOffset? HandledUpTo, bool Paused, bool Waited);

/// <summary>One replication's owner as this API's file says it and as the runner says it.</summary>
public sealed record OwnerView(string Replication, string Owner, RunnerLink? Link, ClientTaskStatus? Runner, string? RunnerError);

/// <summary>
/// Moving one replication from this API's scheduler and workers to a runner's drain, and back (plan 28c E12, 7.3 of the plan), with the owner recorded on both sides: <c>runner-owners.json</c> here, <c>task_owner</c> on the runner. Every
/// step is ordered so that a failure leaves a replication that nobody schedules, never one that both do:
/// <list type="bullet">
/// <item><b>observe</b>: the runner evaluates the schedule against a copy and records what it would have queued; this API goes on and nothing runs on the runner.</item>
/// <item><b>cutover</b>: the owner is written as <c>runner</c> here first (the scheduler leaves the replication alone and the supervisor starts no worker, so nothing new is queued); the worker in flight and its queue are waited for; then the
/// positions of the mappings (and a pause) are handed to the runner as the first drain's <c>requests</c> and the owner is set there with the schedule position. If the wait runs out, work is left in the queue, a mapping is still loading or the
/// runner refuses, the owner here is written back and nothing on the runner changed.</item>
/// <item><b>rollback</b>: the runner is told to let go, its run in flight is waited for, its pause is copied here and the owner here is written back as <c>old</c>; the queue and the run history of this API were not touched. The positions
/// the drain advanced on the runner are not copied back (they are the runner's run progress: reading them is E13), so the next worker reads again from the positions of the cutover (at least once; a merge writer makes that a no-op).</item>
/// </list>
/// </summary>
public sealed class ReplicationOwnership(
    ConfigRepository config,
    TaskRunStore taskRuns,
    WorkQueueStore workQueue,
    ChangeWatermarkStore watermarks,
    DriverRegistry drivers,
    Func<string, bool> workerRunning,
    RunnerAccess runner,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

    public async Task<OwnerView> ViewAsync(string replication, CancellationToken ct)
    {
        var owner = runner.File.OwnerOf(replication);
        ClientTaskStatus? status = null;
        string? error = null;
        if (owner != TaskOwners.Old && runner.Open() is { } sync)
        {
            try { status = await sync.StatusAsync(RunnerAccess.TaskOf(replication), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is RunnerClientException or HttpRequestException or TaskCanceledException) { error = ex.Message; }
        }

        return new OwnerView(replication, owner, runner.Link, status, error);
    }

    private ReplicationTaskConfig Load(string replication)
    {
        try { return config.LoadReplicationTask(replication); }
        catch (FileNotFoundException) { throw new OwnershipException($"There is no replication '{replication}'."); }
    }

    /// <summary>The runner has to serve the task already: a version of the package active in the channel with the replication's task (publish the replication set and wait until it is active).</summary>
    private async Task<IRunnerSync> RequireActiveAsync(string replication, CancellationToken ct)
    {
        var sync = runner.Require();
        var link = runner.Link!;
        var state = await sync.ScheduleOfAsync(RunnerAccess.TaskOf(replication), ct).ConfigureAwait(false);
        if (state.Version is null)
            throw new OwnershipException($"The runner has no active version of the package '{link.Package}' with the task '{RunnerAccess.TaskOf(replication)}' in channel '{link.Channel}'. Publish the replication set (`dbdatasync package publish --to runner --package {link.Package} --channel {link.Channel}`) and wait until it is active; nothing was changed.");
        return sync;
    }

    private DateTimeOffset HandledUpTo(string replication)
    {
        var last = taskRuns.GetLastPrimaryEnqueueByMapping(replication);
        return last.Count == 0 ? clock.GetUtcNow() : last.Values.Max();
    }

    public async Task<OwnershipChange> ObserveAsync(string replication, string actor, CancellationToken ct)
    {
        Load(replication);
        var from = runner.File.OwnerOf(replication);
        if (from == TaskOwners.Observe) throw new OwnershipException($"'{replication}' is observed by the runner already.");
        if (from == TaskOwners.Runner) throw new OwnershipException($"'{replication}' is owned by the runner: roll it back first, then observe.");
        var sync = await RequireActiveAsync(replication, ct).ConfigureAwait(false);
        var (paused, _) = taskRuns.GetPauseState(replication);
        var handed = HandledUpTo(replication);
        await sync.SetOwnerAsync(RunnerAccess.TaskOf(replication), TaskOwners.Observe, $"observe by {actor}", new ClientSeed(handed, paused ? actor : null, paused ? clock.GetUtcNow() : null), ct).ConfigureAwait(false);
        runner.File.SetOwner(replication, TaskOwners.Observe);
        return new OwnershipChange(replication, from, TaskOwners.Observe, RunnerAccess.TaskOf(replication), 0, handed, paused, false);
    }

    public async Task<OwnershipChange> CutoverAsync(string replication, string actor, TimeSpan? wait, CancellationToken ct)
    {
        var task = Load(replication);
        var from = runner.File.OwnerOf(replication);
        if (from == TaskOwners.Runner) throw new OwnershipException($"'{replication}' is owned by the runner already.");
        var sync = await RequireActiveAsync(replication, ct).ConfigureAwait(false);
        var taskName = RunnerAccess.TaskOf(replication);
        var mappings = config.ListTableMappings(replication).ToList();
        var (paused, _) = taskRuns.GetPauseState(replication);

        // the owner here first: the scheduler and the supervisor leave the replication alone from now on, so no scheduled time is skipped meanwhile and nothing new is queued
        runner.File.SetOwner(replication, TaskOwners.Runner);
        try
        {
            var waited = await WaitForWorkerAsync(replication, wait ?? DefaultWait, ct).ConfigureAwait(false);
            if (workQueue.HasOutstandingWork(replication))
                throw new OwnershipException($"'{replication}' has work left in this API's queue and no worker to run it; the owner is as it was. Run it (or cancel it) first.");
            var seeds = Seeds(task, mappings);
            var handed = HandledUpTo(replication);

            // the first drain starts with the positions: the runner's schedule is not active yet, so nothing of its own can start one before them
            var inputs = new Dictionary<string, string> { ["requests"] = DrainRequestText.Serialize(seeds) };
            if (seeds.Count > 0 && await sync.RunNowAsync(taskName, inputs, ct).ConfigureAwait(false) is null)
                throw new OwnershipException($"A run of '{taskName}' is queued or running on the runner already; nothing was handed over and the owner is as it was.");
            await sync.SetOwnerAsync(taskName, TaskOwners.Runner, $"cutover by {actor}", new ClientSeed(handed, paused ? actor : null, paused ? clock.GetUtcNow() : null), ct).ConfigureAwait(false);
            return new OwnershipChange(replication, from, TaskOwners.Runner, taskName, seeds.Count, handed, paused, waited);
        }
        catch
        {
            runner.File.SetOwner(replication, from);
            throw;
        }
    }

    public async Task<OwnershipChange> RollbackAsync(string replication, string actor, TimeSpan? wait, CancellationToken ct)
    {
        Load(replication);
        var from = runner.File.OwnerOf(replication);
        if (from == TaskOwners.Old) throw new OwnershipException($"'{replication}' is scheduled by this API already.");
        var sync = runner.Require();
        var taskName = RunnerAccess.TaskOf(replication);
        var waited = false;
        var runnerPaused = false;
        if (from == TaskOwners.Runner)
        {
            await sync.SetOwnerAsync(taskName, TaskOwners.Old, $"rollback by {actor}", null, ct).ConfigureAwait(false);
            try
            {
                var deadline = clock.GetUtcNow() + (wait ?? DefaultWait);
                while (await sync.StatusAsync(taskName, ct).ConfigureAwait(false) is { } status && (status.Running > 0 || status.Queued > 0))
                {
                    waited = true;
                    if (clock.GetUtcNow() >= deadline)
                        throw new OwnershipException($"A drain of '{taskName}' is still running on the runner after the wait; it was given back to the runner and nothing changed here. Stop it on the runner or wait longer.");
                    await _delay(PollEvery, ct).ConfigureAwait(false);
                }

                runnerPaused = (await sync.ScheduleOfAsync(taskName, ct).ConfigureAwait(false)).Paused;
            }
            catch
            {
                await sync.SetOwnerAsync(taskName, TaskOwners.Runner, "rollback abandoned", null, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        else
        {
            await sync.SetOwnerAsync(taskName, TaskOwners.Old, $"rollback by {actor}", null, ct).ConfigureAwait(false);
        }

        if (from == TaskOwners.Runner && runnerPaused != taskRuns.GetPauseState(replication).Paused)
            taskRuns.SetPaused(replication, runnerPaused, runnerPaused ? "paused on the runner" : "resumed on the runner", actor);
        runner.File.SetOwner(replication, TaskOwners.Old);
        return new OwnershipChange(replication, from, TaskOwners.Old, taskName, 0, null, runnerPaused, waited);
    }

    private async Task<bool> WaitForWorkerAsync(string replication, TimeSpan wait, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + wait;
        var waited = false;
        while (workerRunning(replication))
        {
            waited = true;
            if (clock.GetUtcNow() >= deadline)
                throw new OwnershipException($"The worker of '{replication}' was still running after the wait; the owner is as it was.");
            await _delay(PollEvery, ct).ConfigureAwait(false);
        }

        return waited;
    }

    /// <summary>The positions this API holds for the mappings, as the requests that seed a drain that has none. A mapping that is held for a load (no position yet, or one in progress) is refused: its batch lives in this API's queue.</summary>
    private List<DrainRequest> Seeds(ReplicationTaskConfig task, IReadOnlyList<string> mappings)
    {
        var seeds = new List<DrainRequest>();
        foreach (var name in mappings)
        {
            var mapping = config.LoadTableMapping(task.Name, name);
            if (mapping.Sources.Count != 1) continue;
            var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            var dialect = (drivers.Get(config.LoadConnection(source.ConnectionName).DriverType) as IDialectProvider)?.Dialect
                ?? throw new OwnershipException($"The driver of the connection '{source.ConnectionName}' names no SQL dialect.");
            var key = WatermarkKey.Build(source, dialect);
            var state = watermarks.GetReadState(task.Name, name, key);
            if (state?.Hold == ReadHold.Loading)
                throw new OwnershipException($"The mapping '{name}' is loading (its initial load is a batch of this API's queue); let it finish before the move. The owner is as it was.");
            if (state?.Watermark is { Length: > 0 } position)
                seeds.Add(new DrainRequest("Position", name, SourceKey: key, Position: position, PositionTime: state.WatermarkTimeUtc, Intent: state.Intent.ToString(), Hold: state.Hold.ToString()));
        }

        return seeds;
    }
}
