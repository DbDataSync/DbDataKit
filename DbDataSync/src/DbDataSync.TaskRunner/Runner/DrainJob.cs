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
/// default is to plan as the API's scheduler does). <c>requests</c> is what an operator asked of the replication through the API (a bulk load, a reconcile sweep, a verification, a resync: <see cref="DrainRequest"/>, as a JSON
/// list), queued before the first cycle (plan 28c E12).
/// </summary>
public sealed record DrainInput(string Replication, int DegreeOfParallelism = 4, string RepoDir = "repo", bool RunAll = false, string Requests = "");

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

    /// <summary>A position captured for an initial load that has not finished (<c>pending_position</c> of the declared checkpoint, <see cref="WatermarkCheckpoint"/>): it becomes the position when the batch of <see cref="PendingBatch"/> completes.</summary>
    [Checkpoint]
    public string? PendingPosition { get; set; }

    [Checkpoint]
    public DateTimeOffset? PendingPositionTime { get; set; }

    [Checkpoint]
    public string? PendingBatch { get; set; }
}

/// <summary>The bulk load batch of an initial load as the run's progress (plan 28c E11b): what <c>BulkLoadBatchStore</c> keeps for the API's own path, counted from the segments' outcomes as they end.</summary>
public sealed class BatchProgress
{
    [Checkpoint]
    public string? Mapping { get; set; }

    /// <summary>The checkpoint item of the mapping (<see cref="WatermarkCheckpoint.ItemKey"/>: the mapping and its source's watermark key): where the position waiting on this batch is.</summary>
    [Checkpoint]
    public string? SourceKey { get; set; }

    [Checkpoint]
    public long? Segments { get; set; }

    [Checkpoint]
    public long? Succeeded { get; set; }

    [Checkpoint]
    public long? Failed { get; set; }

    [Checkpoint]
    public long? RowsRead { get; set; }

    [Checkpoint]
    public long? RowsWritten { get; set; }

    [Checkpoint]
    public long? EstimatedRows { get; set; }

    [Checkpoint]
    public string? EstimateCaveat { get; set; }

    [Checkpoint]
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>The rows a mapping's successful primary passes have read, and how many of them a reconcile sweep already accounts for: the input of an after-change reconcile strategy.</summary>
public sealed class ReadsProgress
{
    [Checkpoint]
    public long? Total { get; set; }

    [Checkpoint]
    public long? AtSweep { get; set; }
}

/// <summary>The checkpoint <c>watermark</c> per mapping and source table (plan 28c section 5.1); the item key is <see cref="WatermarkCheckpoint.ItemKey"/>: the mapping and the engine's watermark key of its source.</summary>
public sealed class DrainProgress : Progress
{
    public Keyed<WatermarkProgress> Watermark { get; } = new();

    /// <summary>The batches of initial loads, per batch id (plan 28c E11b).</summary>
    public Keyed<BatchProgress> Batch { get; } = new();

    /// <summary>The rows read by a mapping's primary passes, per mapping (plan 28c E11b).</summary>
    public Keyed<ReadsProgress> Reads { get; } = new();

    /// <summary>The requests of the API that were applied, per request id (plan 28c E12c): a retried run reads its input again and finds them applied.</summary>
    public Keyed<AppliedRequestProgress> Requests { get; } = new();
}

/// <summary>One request of the API the drain applied; the key is the request's id.</summary>
public sealed class AppliedRequestProgress
{
    [Checkpoint]
    public DateTimeOffset? AppliedAt { get; set; }
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

    /// <summary>The checks the polling gate made in one cycle (plan 28c E11b): the gate's history, written by the drain as an output of the run, keyed by replication and the time of the first check.</summary>
    public static readonly ValueStep<ChangeCheckBatch, ChangeCheckBatch> ChangeChecks =
        Step.Value<ChangeCheckBatch, ChangeCheckBatch>(ChangeCheckText.StepName, b => $"{b.Replication}/{b.Checks[0].CheckedAtUtc.UtcTicks}");

    /// <summary>One pass as the run's own output (plan 28c E13): Sync's row of the run history for a replication the runner owns (<see cref="PassRecord"/>), keyed by the pass's run id. The API reads it through the runner's steps endpoint.</summary>
    public static readonly ValueStep<PassRecord, PassRecord> Pass =
        Step.Value<PassRecord, PassRecord>(PassRecordText.StepName, r => r.RunId.ToString("N"));

    /// <summary>The file of a verification result (plan 28c E06d): the parquet the check wrote into the run's scratch, streamed into a payload of the run (under the run's retention) before the scratch is deleted; the record of <see cref="Verification"/> names it.</summary>
    public static readonly StreamStep<VerificationResultRecord> VerificationFile =
        Step.Stream<VerificationResultRecord>(VerificationResultPayload.FileStepName, r => $"{r.MappingName}/{r.CheckName}/{r.CompletedAtUtc.UtcTicks}", "application/vnd.apache.parquet");

    /// <summary>A verification result (plan 28c E11b): the record the verification wrote, an output of the run keyed by mapping, check and time; the Sync back end reads it from the run for a runner-owned replication. Its path is the payload of <see cref="VerificationFile"/>.</summary>
    public static readonly ValueStep<VerificationResultRecord, VerificationResultRecord> Verification =
        Step.Value<VerificationResultRecord, VerificationResultRecord>(VerificationResultPayload.RecordStepName, r => $"{r.MappingName}/{r.CheckName}/{r.CompletedAtUtc.UtcTicks}");
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

        // The definition is read, never written (ED8): logins come from the environment the runner set (DBDATAKIT_SECRET_..., ED11), so no secret store file or key is created; the configuration is the read-only
        // repository (no git service, no directory created in the view); the engine's own caches go to the scratch directory.
        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));

        var checkpoints = new Checkpoints(this);
        var queue = new KitDrainQueue(Work);
        RunExecutor? made = null;
        var segments = new EngineSegments(new SegmentExpander(new CustomSegmentExpansion(new SegmentingStrategyRunner(engine.Scripts))), (name, token) => made!.OpenPlanningConnectionWithDriverAsync(name, token));
        var outputs = new RunOutputs(this);
        var bulkLoads = new DrainBulkLoads(config, queue, checkpoints, segments, Run.Log);
        var state = new ProtocolRunnerState(checkpoints, Run.Log, queue, bulkLoads, outputs);
        var executor = made = new RunExecutor(
            config, engine.Drivers, secrets, engine.Libraries, repoRoot, state, new RunOutputConfig(this), engine.Scripts, Path.Combine(scratch, "state.db"));

        var plannerState = new ProtocolPlannerState(queue, checkpoints, checkpoints);
        var gate = new ChangePollingGate(
            new ChangeSourceResolver(config, engine.Drivers),
            new ConnectionChangeCounterSource((connection, token) => executor.OpenPlanningConnectionAsync(connection, token)),
            plannerState, Run.Log);
        var planner = new ReplicationPlanner(config, plannerState, gate, engine.Drivers, new EngineReconcileEnqueuer(config, plannerState, segments), Run.Log);

        await DrainRequests.ApplyAsync(Input.Requests, Input.Replication, queue, checkpoints, checkpoints, Run.Log, ct).ConfigureAwait(false);

        var drain = new DrainLoop(Input.Replication, config, planner, plannerState, state, queue, Input.RunAll, Math.Max(1, Input.DegreeOfParallelism), Run.Log, Status, outputs: outputs,
            requestItem: item => DrainRequests.ApplyItemAsync(item, Input.Replication, queue, checkpoints, checkpoints, Run.Log, ct));
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
        var startedAt = DateTimeOffset.UtcNow;
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
        RecordPass(pass, outcome, startedAt);
        if (outcome.Status != RunStatus.Succeeded)
        {
            var kind = outcome.Status == RunStatus.Cancelled ? TaskFailureKind.Cancelled
                : TaskFailureKind.Permanent;
            // the run's own classification is only Permanent or Cancelled: what Sync classified (PositionExpired, MetadataNotCached, ...) is said in the message, which is what the console's row of the run shows
            var classified = outcome.FailureKind is { Length: > 0 } sync ? $" ({sync})" : "";
            throw new TaskFailureException(kind, $"The pass over '{pass.Mapping}' ended {outcome.Status}{classified}: {outcome.Summary}", pass.Mapping);
        }
    }

    /// <summary>
    /// The pass as a record of the run (plan 28c E13), written whatever the outcome, before a failed pass fails the run: the pass's mapping, kind, rows, timings, the failure kind the engine classified and the summary (a pass that
    /// lost the initial-load race ends <c>Succeeded</c> and says <c>Not run: ...</c>). The summary of a pass that did not fail is also a line of the run's log, which the engine does not write itself on this path.
    /// </summary>
    private void RecordPass(PlannedPass pass, PassOutcome outcome, DateTimeOffset startedAt)
    {
        if (outcome.Status == RunStatus.Succeeded && !string.IsNullOrEmpty(outcome.Summary))
            Run.Log.LogInformation("Pass over '{Mapping}' ({Kind}): {Summary}", pass.Mapping, pass.Item.RunKind, outcome.Summary);
        var record = new PassRecord(
            pass.Item.RunId, Run.Id, Input.Replication, pass.Item.RunKind.ToString(), pass.Mapping,
            pass.Item.SegmentLabel == WorkQueueStore.NoSegment ? null : pass.Item.SegmentLabel, outcome.Status.ToString(), startedAt, DateTimeOffset.UtcNow,
            outcome.RowsRead, outcome.RowsWritten, outcome.Summary, outcome.FailureKind, outcome.Detail, pass.Item.BulkLoadBatchId);
        Step(DrainSteps.Pass, record).Repeatable().RunAsync(_ => Task.FromResult(record), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The position of a mapping as the run's checkpoint: read from the progress the run started with, written and saved at once, one save at a time (a save writes everything changed, and two at once would conflict with each other).</summary>
    private sealed class Checkpoints(DrainJob job) : IDrainCheckpoints, IDrainLedger
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

        private void Save(string key, Action<WatermarkProgress> change) => Locked(() => change(job.Progress.Watermark[key]));

        private void Locked(Action change)
        {
            job._save.Wait();
            try
            {
                change();
                job.SaveProgressAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                job._save.Release();
            }
        }

        public void SetPendingLoad(string key, string position, DateTimeOffset? time, string batchId) => Save(key, item =>
        {
            item.Hold = nameof(ReadHold.Loading);
            item.PendingPosition = position;
            item.PendingPositionTime = time;
            item.PendingBatch = batchId;
        });

        public bool PromotePendingLoad(string key)
        {
            var promoted = false;
            Save(key, item =>
            {
                if (item.Hold != nameof(ReadHold.Loading) || item.PendingPosition is null)
                    return;
                item.Position = item.PendingPosition;
                item.PositionTime = item.PendingPositionTime;
                item.Intent = nameof(ReadIntent.Changes);
                item.Hold = nameof(ReadHold.None);
                item.PendingPosition = null;
                item.PendingPositionTime = null;
                item.PendingBatch = null;
                promoted = true;
            });
            return promoted;
        }

        public BatchRecord? ReadBatch(string batchId)
        {
            var item = job.Progress.Batch[batchId];
            return item.Segments is not { } segments
                ? null
                : new BatchRecord(item.Mapping ?? "", item.SourceKey ?? "", (int)segments, (int)(item.Succeeded ?? 0), (int)(item.Failed ?? 0), item.RowsRead ?? 0, item.RowsWritten ?? 0, item.EstimatedRows, item.EstimateCaveat, item.CreatedAt ?? DateTimeOffset.UtcNow);
        }

        public void WriteBatch(string batchId, BatchRecord batch) => Locked(() =>
        {
            var item = job.Progress.Batch[batchId];
            item.Mapping = batch.Mapping;
            item.SourceKey = batch.SourceKey;
            item.Segments = batch.Segments;
            item.Succeeded = batch.Succeeded;
            item.Failed = batch.Failed;
            item.RowsRead = batch.RowsRead;
            item.RowsWritten = batch.RowsWritten;
            item.EstimatedRows = batch.EstimatedRows;
            item.EstimateCaveat = batch.EstimateCaveat;
            item.CreatedAt = batch.CreatedAt;
        });

        public bool TryMarkApplied(string requestId)
        {
            var marked = false;
            Locked(() =>
            {
                var item = job.Progress.Requests[requestId];
                if (item.AppliedAt is not null) return;
                item.AppliedAt = DateTimeOffset.UtcNow;
                marked = true;
            });
            return marked;
        }

        public bool WasApplied(string requestId) => job.Progress.Requests[requestId].AppliedAt is not null;

        public void AddRowsRead(string mapping, long rows) => Locked(() => job.Progress.Reads[mapping].Total = (job.Progress.Reads[mapping].Total ?? 0) + rows);

        public long RowsReadSinceSweep(string mapping)
        {
            var item = job.Progress.Reads[mapping];
            return (item.Total ?? 0) - (item.AtSweep ?? 0);
        }

        public void MarkSweep(string mapping) => Locked(() => job.Progress.Reads[mapping].AtSweep = job.Progress.Reads[mapping].Total ?? 0);
    }

    /// <summary>What a pass reports that belongs to the run, not to state: a verification result is a step of the run (its value stored with the run, repeatable: a result reported again is the same result).</summary>
    private sealed class RunOutputs(DrainJob job) : IDrainOutputs
    {
        public void VerificationResult(VerificationResultRecord result)
        {
            // the file is in the run's scratch, which is deleted when the run ends: it goes into a payload of the run first, and the record names the payload (a retry of the attempt chain answers from the stored step)
            var file = job.Step(DrainSteps.VerificationFile, result).Repeatable().StreamAsync(async (stream, ct) =>
            {
                await using var source = File.OpenRead(result.ResultPath);
                await source.CopyToAsync(stream, ct).ConfigureAwait(false);
            }, CancellationToken.None).GetAwaiter().GetResult();
            var recorded = result with { ResultPath = VerificationResultPayload.PathFor(file.Id) };
            job.Step(DrainSteps.Verification, recorded).Repeatable().RunAsync(_ => Task.FromResult(recorded), CancellationToken.None).GetAwaiter().GetResult();
            try { File.Delete(result.ResultPath); }
            catch (IOException) { /* the scratch is deleted with the run */ }
        }

        public void ChangeChecks(string replication, IReadOnlyList<ChangeCheckEntry> checks)
        {
            if (checks.Count == 0) return;
            var batch = new ChangeCheckBatch(replication, [.. checks]);
            job.Step(DrainSteps.ChangeChecks, batch).Repeatable().RunAsync(_ => Task.FromResult(batch), CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary><see cref="SegmentExpander"/> over the engine's own way of opening a connection.</summary>
    private sealed class EngineSegments(SegmentExpander expander, OpenSegmentConnection open) : IDrainSegments
    {
        public Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested, string? readerKind, CancellationToken cancellationToken) =>
            expander.ExpandAsync(task, mapping, requested, readerKind, open, cancellationToken);

        public Task<(long? Rows, string? Caveat)> EstimateAsync(ReplicationTaskConfig task, TableMappingConfig mapping, CancellationToken cancellationToken) =>
            SegmentExpander.EstimateRowsAsync(task, mapping, open, cancellationToken);
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
