using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataKit.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Planning;
using DbDataSync.Scripting;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What the manifest's <c>inputs</c> say for the task <c>drain</c>: which replication of the definition to drain, how many passes at once, and where in the package the definition is (<c>repo_dir</c>, relative to the
/// package's root: the directory that holds <c>config/</c> and <c>libraries/</c>). <c>run_all</c> is the API's "Run now": a pass for every mapping at once, whatever the schedule, the holds and the gate say (plan 28c E11; the
/// default is to plan as the API's scheduler does).
/// </summary>
public sealed record DrainInput(string Replication, int DegreeOfParallelism = 4, string RepoDir = "repo", bool RunAll = false);

/// <summary>One mapping's position: where its incremental read got to (<c>ChangeWatermarks.Watermark</c> and its time).</summary>
public sealed class WatermarkProgress
{
    /// <summary>Opaque to everything but the reader: a hex LSN, a Change Tracking version in decimal, a time.</summary>
    [Checkpoint]
    public string? Position { get; set; }

    [Checkpoint]
    public DateTimeOffset? PositionTime { get; set; }

    /// <summary>What the next pass is asked to do (<c>ReadIntent</c> as text); unset is <c>Changes</c>.</summary>
    [Checkpoint]
    public string? Intent { get; set; }

    /// <summary>Why the next pass is not to run (<c>ReadHold</c> as text); unset is <c>None</c>.</summary>
    [Checkpoint]
    public string? Hold { get; set; }
}

/// <summary>The checkpoint <c>watermark</c> per mapping and source table (plan 28c section 5.1, reduced to two parts); the key is the engine's watermark key.</summary>
public sealed class DrainProgress : Progress
{
    public Keyed<WatermarkProgress> Watermark { get; } = new();
}

public static class DrainCounters
{
    /// <summary>Rows written to the targets: what the runner records as the rows the run moved.</summary>
    public static readonly Counter Rows = new("rows");

    public static readonly Counter RowsRead = new("rows_read");
}

/// <summary>
/// One pass over one mapping, claimed by the drain from its queue. The key of its unit is the mapping and a token of this claim: a drain that stays alive passes over a mapping again and again, an item given back and
/// claimed again is a new claim, and a unit that completed in an attempt chain is skipped by a retry, which would skip a pass that was never run.
/// </summary>
public sealed record PlannedPass(string Mapping, WorkItem Item, string Token = "")
{
    public string Key => $"{Mapping}#{(Token.Length > 0 ? Token : Item.RunId.ToString("N")[..8])}";
}

/// <summary>What a pass reports of the target it provisioned (plan 28c ED9): the mapping and the columns of the target as the engine found or created it.</summary>
public sealed record ProvisionedColumnsReport(string Replication, string Mapping, List<CachedColumn> Columns);

public static class DrainSteps
{
    /// <summary>
    /// The provisioning report of a mapping, a run output (the step's result in the run record) rather than a write into the definition: a published definition is immutable. The Sync back end reads it and, as
    /// an authoring change with its own commit, updates the mapping's column cache; until then the pass reports again on its next run (the report is recurring and loss-tolerant, <see cref="IRunnerConfig"/>).
    /// </summary>
    public static readonly ValueStep<ProvisionedColumnsReport, ProvisionedColumnsReport> ProvisionedColumns =
        Step.Value<ProvisionedColumnsReport, ProvisionedColumnsReport>("provisioned-columns", r => r.Mapping);
}

public static class DrainUnits
{
    /// <summary>A pass of a mapping (a scheduled pass, a reconcile sweep or a segment of a bulk load); the key is the mapping and the claim's token, so the passes of a drain that stays alive are each their own unit and a run's failed item still names its mapping.</summary>
    public static readonly Unit<PlannedPass> Mapping = Unit.Of<PlannedPass>("mapping", p => p.Key);
}

/// <summary>
/// One drain of one replication, as a task of the runner (plan 28c E03, decision ED4: the unit of a run is what runs in one process today). The engine is <see cref="RunExecutor"/> unchanged; what differs from the
/// worker the API spawns is who plans the passes (this job: one primary pass per mapping, each a unit of the run), where state lives (<see cref="ProtocolRunnerState"/>: the position is the run's checkpoint, saved in the
/// protocol call that follows the target's commit), where the definition is read from (the package's view) and where the engine may write (the run's scratch, never the view).
/// </summary>
[RunnerTask("drain")]
[RunnerDeclarations(typeof(DrainUnits), typeof(DrainCounters), typeof(DrainSteps))]
public sealed class DrainJob : RunnerJob<DrainInput, DrainProgress, NoSlots>
{
    private readonly SemaphoreSlim _save = new(1, 1);

    protected override async Task RunAsync(CancellationToken ct)
    {
        var package = Environment.GetEnvironmentVariable(RunnerProtocolNames.PackageDirectoryVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.PackageDirectoryVariable} is not set: the runner tells a task where its package's view is.");
        var scratch = Environment.GetEnvironmentVariable(RunnerProtocolNames.ScratchVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.ScratchVariable} is not set: the runner tells a task where it may write.");
        var repoRoot = Path.GetFullPath(Path.Combine(package, Input.RepoDir));

        // The definition is read, never written (ED8): logins come from the environment the runner set (DBDATA_SECRET_..., ED11), so no secret store file or key is created; the configuration is the read-only
        // repository (no git service, no directory created in the view); the engine's own caches go to the scratch directory.
        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));

        var checkpoints = new Checkpoints(this);
        var queue = new KitDrainQueue(Work);
        var state = new ProtocolRunnerState(checkpoints, Run.Log, queue);
        var executor = new RunExecutor(
            config, engine.Drivers, secrets, engine.Libraries, repoRoot, state, new RunOutputConfig(this), engine.Scripts, Path.Combine(scratch, "state.db"));

        var plannerState = new ProtocolPlannerState(queue, checkpoints);
        var gate = new ChangePollingGate(
            new ChangeSourceResolver(config, engine.Drivers),
            new ConnectionChangeCounterSource((connection, token) => executor.OpenPlanningConnectionAsync(connection, token)),
            plannerState, Run.Log);
        var planner = new ReplicationPlanner(config, plannerState, gate, engine.Drivers, new EngineReconcileEnqueuer(config, plannerState), Run.Log);

        var drain = new DrainLoop(Input.Replication, config, planner, plannerState, state, queue, Input.RunAll, Math.Max(1, Input.DegreeOfParallelism), Run.Log, Status);
        try
        {
            await drain.RunAsync(
                (items, parallelism, token) => ForEachAsync(DrainUnits.Mapping, items, parallelism, (pass, _) => RunPassAsync(executor, state, drain, pass), token),
                ct).ConfigureAwait(false);
        }
        finally
        {
            // what the drain claimed and did not finish goes back to the queue now, whether it was cancelled, failed or ended: the run's end gives back the rest, and a unit of the next drain finds the item as Pending
            await drain.ReleaseUnfinishedAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One claimed item, run as the engine runs any: the pass is not given the run's token. A runner's stop is a stop at the boundary of an item: the drain claims nothing more and lets the passes in flight finish (the
    /// runner gives the run its cancellation grace for that; what is still running when it ends is given back to the queue, and a pass that stopped half way has moved no position past its last commit). An operator's
    /// cancel of one item reaches that pass through the queue's cancel mark, as it does on the API's worker.
    /// </summary>
    private async Task RunPassAsync(RunExecutor executor, ProtocolRunnerState state, DrainLoop drain, PlannedPass pass)
    {
        try
        {
            await RunClaimedAsync(executor, state, drain, pass).ConfigureAwait(false);
        }
        finally
        {
            drain.PassFinished(pass);
        }
    }

    private async Task RunClaimedAsync(RunExecutor executor, ProtocolRunnerState state, DrainLoop drain, PlannedPass pass)
    {
        try
        {
            await executor.RunPlannedItemAsync(pass.Item, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WorkClaimLostException ex)
        {
            // another run holds the item now: this one has nothing to record for it
            state.ReleaseLock(pass.Item.TaskName, pass.Item.RunKind, pass.Item.MappingName);
            Run.Log.LogWarning("{Message}", ex.Message);
            return;
        }

        if (state.WasReleased(pass.Item.Id) && state.TakeOutcome(pass.Item.RunId) is null)
        {
            // the mapping's lock was held by another pass of this drain: the item is back in the queue, for the next claim, and not before the pass that holds the lock has had a moment
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            return;
        }

        var outcome = state.TakeOutcome(pass.Item.RunId)
            ?? throw new TaskFailureException(TaskFailureKind.Permanent, $"The pass over '{pass.Mapping}' ended without saying how.", pass.Mapping);
        Count(DrainCounters.RowsRead, outcome.RowsRead);
        Count(DrainCounters.Rows, outcome.RowsWritten);
        drain.NotePass(pass.Item, outcome);
        if (outcome.Status != RunStatus.Succeeded)
        {
            var kind = outcome.Status == RunStatus.Cancelled ? TaskFailureKind.Cancelled
                : outcome.FailureKind is RunFailureKinds.MappingStillLoading or RunFailureKinds.ConcurrentLoadInProgress ? TaskFailureKind.Temporary
                : TaskFailureKind.Permanent;
            throw new TaskFailureException(kind, $"The pass over '{pass.Mapping}' ended {outcome.Status}: {outcome.Summary}", pass.Mapping);
        }
    }

    /// <summary>The position of a mapping as the run's checkpoint: read from the progress the run started with, written and saved at once, one save at a time (a save writes everything changed, and two at once would conflict with each other).</summary>
    private sealed class Checkpoints(DrainJob job) : IDrainCheckpoints
    {
        public (string Position, DateTimeOffset? Time)? Read(string key)
        {
            var item = job.Progress.Watermark[key];
            return item.Position is { } position ? (position, item.PositionTime) : null;
        }

        public void Write(string key, string position, DateTimeOffset? time) => Save(key, item =>
        {
            item.Position = position;
            item.PositionTime = time;
        });

        public (ReadIntent? Intent, ReadHold? Hold) ReadIntentAndHold(string key)
        {
            var item = job.Progress.Watermark[key];
            return (Enum.TryParse<ReadIntent>(item.Intent, out var intent) ? intent : null, Enum.TryParse<ReadHold>(item.Hold, out var hold) ? hold : null);
        }

        /// <summary>Changes is what a checkpoint with a position and no intent already means, so it is not stored while none is.</summary>
        public void WriteIntent(string key, ReadIntent intent)
        {
            if (job.Progress.Watermark[key].Intent is null && intent == ReadIntent.Changes)
                return;
            Save(key, item => item.Intent = intent.ToString());
        }

        /// <summary>None is the default, so it is not stored while no hold is.</summary>
        public void WriteHold(string key, ReadHold hold)
        {
            if (job.Progress.Watermark[key].Hold is null && hold == ReadHold.None)
                return;
            Save(key, item => item.Hold = hold.ToString());
        }

        private void Save(string key, Action<WatermarkProgress> change)
        {
            job._save.Wait();
            try
            {
                change(job.Progress.Watermark[key]);
                job.SaveProgressAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                job._save.Release();
            }
        }
    }

    /// <summary>The one config write a pass can make, its provisioning report, is a step of the run (ED9): the result is stored with the run, the definition is not touched.</summary>
    private sealed class RunOutputConfig(DrainJob job) : IRunnerConfig
    {
        public void ReportProvisionedTargetColumns(string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns)
        {
            var report = new ProvisionedColumnsReport(replicationName, mappingName, [.. columns]);
            job.Step(DrainSteps.ProvisionedColumns, report).Repeatable().RunAsync(_ => Task.FromResult(report), CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
