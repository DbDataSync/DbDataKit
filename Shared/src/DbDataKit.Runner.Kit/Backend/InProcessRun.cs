using DbDataKit.State;
using DbDataKit.State.Tracking;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// The run on the real stores, in this process (plan 28b section 12): progress through <see cref="ProgressStore"/> (the checkpoint moves in the transaction that finishes the run record), steps through
/// <see cref="StepStore"/>, units through <see cref="UnitStore"/>, the file ledger, inbox and dead letters through the progress store, payloads through <see cref="PayloadStore"/>, the run record and its log
/// through <see cref="RunStore"/>.
/// </summary>
internal sealed class InProcessRun : IRunBackend
{
    private readonly RunnerEnvironment _env;
    private readonly StateDatabase _database;
    private readonly ProgressStore _progress;
    private readonly StepStore _steps;
    private readonly UnitStore _units;
    private readonly RunStore _runs;
    private readonly PayloadStore _payloads;

    public InProcessRun(RunnerEnvironment env, RunIdentity identity)
    {
        _env = env;
        _database = env.Database;
        Identity = identity;
        var key = new ProgressKey(identity.Task.Package, identity.Task.Channel, identity.Task.Task);
        _progress = new ProgressStore(_database, key);
        _steps = new StepStore(_database, key, env.Steps);
        _units = new UnitStore(_database, key);
        _runs = new RunStore(_database, env.Service);
        _payloads = new PayloadStore(_database);
    }

    public RunIdentity Identity { get; }

    private int Attempt => Identity.Attempt;

    private string Chain => Identity.Chain;

    public Task StartAsync(CancellationToken ct)
    {
        // a runner's executor records the run (with its package version) before the task starts; a run that nobody recorded is recorded here
        if (_runs.Get(Identity.RunId) is null)
            _runs.Start(Identity.Task.ToString(), Identity.RunId, package: Identity.Task.Package, channel: Identity.Task.Channel, attempt: Identity.Attempt);
        return Task.CompletedTask;
    }

    // ---- progress

    public Task<Checkpoint?> ReadCheckpointAsync(string name, string itemKey, CancellationToken ct) => Task.FromResult(_progress.GetCheckpoint(name, itemKey));

    public Task<(ProgressValue Value, long Version)?> ReadStateAsync(string name, string itemKey, CancellationToken ct) => Task.FromResult(_progress.ReadState(name, itemKey));

    public Task<bool> CommitProgressAsync(IReadOnlyList<CheckpointWrite> checkpoints, IReadOnlyList<StateWrite> states, TaskEnd? end, CancellationToken ct)
    {
        var finish = end?.ToRunFinish();
        var ok = _progress.Commit(checkpoints, states, Identity.RunId, finish is null ? null : (connection, transaction) =>
            _runs.Finish(Identity.RunId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure, connection: connection, transaction: transaction));
        return Task.FromResult(ok);
    }

    public Task FinishAsync(TaskEnd end, CancellationToken ct)
    {
        var finish = end.ToRunFinish();
        _runs.Finish(Identity.RunId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
        return Task.CompletedTask;
    }

    // ---- steps

    public Task<StepBegin> BeginStepAsync(string name, string key, CancellationToken ct) => Task.FromResult(_steps.Begin(Chain, name, key, Attempt));

    public Task<StepBegin.Run> RetakeStepAsync(string name, string key, CancellationToken ct) => Task.FromResult(_steps.Retake(Chain, name, key, Attempt));

    public Task<Guid?> AdoptStepPayloadAsync(string name, string key, CancellationToken ct)
    {
        var step = _steps.Get(Chain, name, key) ?? throw new InvalidOperationException($"There is no step '{name}' with key '{key}' in this chain.");
        if (_steps.FindOwnedCompletePayload(step.Id) is not { } payload) return Task.FromResult<Guid?>(null);
        _steps.Adopt(Chain, name, key, Attempt, payload.Id);
        return Task.FromResult<Guid?>(payload.Id);
    }

    public Task CompleteValueStepAsync(string name, string key, string resultJson, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        _steps.CompleteValue(Chain, name, key, Attempt, resultJson, counts);
        return Task.CompletedTask;
    }

    public Task CompleteStreamStepAsync(string name, string key, Guid payload, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        _steps.CompleteStream(Chain, name, key, Attempt, payload, counts);
        return Task.CompletedTask;
    }

    public Task FailStepAsync(string name, string key, string error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        _steps.Fail(Chain, name, key, Attempt, error, counts);
        return Task.CompletedTask;
    }

    public Task<StepStream> OpenStepStreamAsync(StepBegin.Run run, string stepName, string key, string? mediaType, CancellationToken ct)
    {
        var writer = _steps.OpenStream(run, stepName, mediaType);
        return Task.FromResult(new StepStream(writer, () => writer.Id, writer.Complete));
    }

    // ---- units

    public Task<UnitBegin> BeginUnitAsync(string name, string key, CancellationToken ct) => Task.FromResult(_units.Begin(Chain, name, key, Attempt));

    public Task CompleteUnitAsync(string name, string key, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        _units.Complete(Chain, name, key, Attempt, counts);
        return Task.CompletedTask;
    }

    public Task FailUnitAsync(string name, string key, string error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        _units.Fail(Chain, name, key, Attempt, error, counts);
        return Task.CompletedTask;
    }

    public Task SkipUnitAsync(string name, string key, CancellationToken ct)
    {
        _units.MarkSkipped(Chain, name, key, Attempt);
        return Task.CompletedTask;
    }

    // ---- files, inbox, dead letters

    public Task<bool> ClaimFileAsync(string connection, string path, long size, DateTimeOffset modified, string hash, CancellationToken ct)
    {
        if (_progress.FindFile(connection, hash) is { Processed: true }) return Task.FromResult(true);
        _progress.NoteFile(connection, path, size, modified, hash);
        return Task.FromResult(false);
    }

    public Task MarkFileProcessedAsync(string connection, string hash, string outcome, CancellationToken ct)
    {
        _progress.MarkFileProcessed(connection, hash, Identity.RunId, outcome);
        return Task.CompletedTask;
    }

    public Task<InboxTaken?> TakeInboxAsync(CancellationToken ct)
    {
        var item = _progress.TakeNext(Identity.RunId);
        return Task.FromResult(item is null ? null : new InboxTaken(item.Id, item.Source, item.SignatureOk, item.BodyPayload));
    }

    public Task CompleteInboxAsync(Guid id, CancellationToken ct) => Task.CompletedTask;

    public Task ReleaseInboxAsync(Guid id, CancellationToken ct)
    {
        _progress.ReleaseInboxItem(id, Identity.RunId);
        return Task.CompletedTask;
    }

    public Task AddDeadLetterAsync(string source, string error, byte[] body, Guid id, CancellationToken ct)
    {
        Guid payload;
        using (var writer = _payloads.OpenWrite(new PayloadOptions("application/json", ProgressModule.Name, id.ToString("D"), Label: "a rejected record")))
        {
            payload = writer.Id;
            writer.Write(body);
            writer.Complete();
        }

        _progress.AddDeadLetter(source, error, id, payload);
        return Task.CompletedTask;
    }

    // ---- payloads, log

    public PayloadRef GetPayload(Guid id)
    {
        var info = _payloads.Get(id) ?? throw new InvalidOperationException($"The payload {id} does not exist.");
        return new PayloadRef(info.Id, info.Size, info.Sha256, info.MediaType, OpenPayload);
    }

    private Stream OpenPayload(Guid id) => _payloads.OpenRead(id);

    public void Log(string level, string message, string? item = null) => _runs.Log(Identity.RunId, level, message, item);

    // ---- engine work: the queue is the runner's (the scheduling module and the claim bound to a run's token live in the protocol host), so an in-process run has none

    private static NotSupportedException NoWork() => new("An engine's work queue is the runner's: a run in this process (the in-process kit) has none. Run the job through a runner, or take the queue's calls out of the test.");

    public Task<Guid> EnqueueWorkAsync(string kind, string subject, string part, string resource, string lane, string? payload, CancellationToken ct) => throw NoWork();

    public Task<RunnerWorkItem?> ClaimWorkAsync(string? lane, CancellationToken ct) => throw NoWork();

    public Task<bool> StartWorkAsync(long id, CancellationToken ct) => throw NoWork();

    public Task<bool> FinishWorkAsync(long id, WorkFinish outcome, CancellationToken ct) => throw NoWork();

    public Task<bool> ReleaseWorkAsync(long id, CancellationToken ct) => throw NoWork();

    public Task<bool> IsWorkCancelRequestedAsync(Guid itemRunId, CancellationToken ct) => throw NoWork();

    public Task<WorkSnapshot> WorkStateAsync(IReadOnlyList<string> kinds, CancellationToken ct) => throw NoWork();

    // ---- Build's tracking copy: the runner's state (the protocol host holds the module and the checks bound to a run's token), so an in-process run has none

    private static NotSupportedException NoTracking() => new("Build's tracking copy is the runner's (the build_tracking module behind the run's token): a run in this process (the in-process kit) has none. Run the job through a runner.");

    public Task<TrackingCopyState> TrackingStateAsync(string identity, string stream, CancellationToken ct) => throw NoTracking();

    public Task<bool> TrackingWriteAsync(string identity, string stream, IReadOnlyList<TrackingRow> rows, bool advance, long expectedVersion, CancellationToken ct) => throw NoTracking();

    public Task<IReadOnlyList<TrackingPosition>> TrackingTailAsync(string identity, string stream, int count, CancellationToken ct) => throw NoTracking();

    public Task<IReadOnlyList<TrackingRow>> TrackingReadAsync(string identity, string stream, TrackingPosition? after, int limit, CancellationToken ct) => throw NoTracking();

    public Task<IReadOnlyList<(string Key, string Hash)>> TrackingUnsettledAsync(string identity, string stream, int limit, CancellationToken ct) => throw NoTracking();

    public Task<TrackingDivergence> TrackingDivergenceAsync(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh, CancellationToken ct) => throw NoTracking();

    public Task<int> TrackingSnapshotAsync(string identity, string table, IReadOnlyList<TrackingRow> rows, CancellationToken ct) => throw NoTracking();

    // counts, the status line and lineage have no place in the state database yet (the lineage slice gives them one); the protocol server keeps them in memory for the runner
    public void Count(string name, long n)
    {
    }

    public void Status(string text)
    {
    }

    public void Lineage(IReadOnlyList<string> reads, IReadOnlyList<string> writes)
    {
    }
}
