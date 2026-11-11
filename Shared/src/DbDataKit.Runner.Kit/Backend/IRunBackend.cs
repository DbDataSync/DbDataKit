using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>Who a run is: its record, its attempt, the attempt chain it belongs to (a retry reuses the chain) and the task.</summary>
internal sealed record RunIdentity(Guid RunId, int Attempt, string Chain, TaskRef Task);

/// <summary>An inbox item as handed out: its body is a payload (<see cref="Body"/>), null for an empty one.</summary>
internal sealed record InboxTaken(Guid Id, string Source, bool SignatureOk, Guid? Body);

/// <summary>The write side of a streaming step: the stream the body fills, the call that commits the payload once the body returned normally, and the payload's id (read after the stream is disposed: a remote upload only learns it then).</summary>
internal sealed record StepStream(Stream Stream, Func<Guid> PayloadId, Action Complete);

/// <summary>
/// What a run needs from wherever the runner's state lives (plan 28b section 9). The kit's logic (recovery, units, typed progress, counts) sits above this interface and is the same for every implementation; the
/// in-process one (<see cref="InProcessRun"/>) calls the stores, and the protocol client (<see cref="GrpcRunBackend"/>, slice R03b) sends the messages of the same names.
/// </summary>
internal interface IRunBackend
{
    RunIdentity Identity { get; }

    /// <summary>Records that the run began.</summary>
    Task StartAsync(CancellationToken ct);

    // ---- progress (Start, Advance)
    Task<Checkpoint?> ReadCheckpointAsync(string name, string itemKey, CancellationToken ct);

    Task<(ProgressValue Value, long Version)?> ReadStateAsync(string name, string itemKey, CancellationToken ct);

    /// <summary>
    /// Writes the changed progress in one transaction, and with it the run's outcome when <paramref name="end"/> is given. False when a version moved (nothing is written, the run is not finished).
    /// </summary>
    Task<bool> CommitProgressAsync(IReadOnlyList<CheckpointWrite> checkpoints, IReadOnlyList<StateWrite> states, TaskEnd? end, CancellationToken ct);

    /// <summary>Records how the run ended, without progress.</summary>
    Task FinishAsync(TaskEnd end, CancellationToken ct);

    // ---- steps (BeginStep, CompleteStep)
    Task<StepBegin> BeginStepAsync(string name, string key, CancellationToken ct);

    Task<StepBegin.Run> RetakeStepAsync(string name, string key, CancellationToken ct);

    /// <summary>
    /// For an in-doubt streaming step: when the dead attempt left a complete payload owned by the step, takes the step over and completes it with that payload (plan 28b K14) and returns the payload's id; null when there
    /// is none (nothing is changed).
    /// </summary>
    Task<Guid?> AdoptStepPayloadAsync(string name, string key, CancellationToken ct);

    Task CompleteValueStepAsync(string name, string key, string resultJson, IReadOnlyDictionary<string, long>? counts, CancellationToken ct);

    Task CompleteStreamStepAsync(string name, string key, Guid payload, IReadOnlyDictionary<string, long>? counts, CancellationToken ct);

    Task FailStepAsync(string name, string key, string error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct);

    Task<StepStream> OpenStepStreamAsync(StepBegin.Run run, string stepName, string key, string? mediaType, CancellationToken ct);

    // ---- units (BeginUnit, EndUnit)
    Task<UnitBegin> BeginUnitAsync(string name, string key, CancellationToken ct);

    Task CompleteUnitAsync(string name, string key, IReadOnlyDictionary<string, long>? counts, CancellationToken ct);

    Task FailUnitAsync(string name, string key, string error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct);

    Task SkipUnitAsync(string name, string key, CancellationToken ct);

    // ---- files, inbox, dead letters (ClaimFile, TakeInbox, Reject)
    /// <summary>True when a file with this hash was already processed on the connection; otherwise the file is noted as seen.</summary>
    Task<bool> ClaimFileAsync(string connection, string path, long size, DateTimeOffset modified, string hash, CancellationToken ct);

    Task MarkFileProcessedAsync(string connection, string hash, string outcome, CancellationToken ct);

    Task<InboxTaken?> TakeInboxAsync(CancellationToken ct);

    /// <summary>The job finished with an item (the protocol's CompleteInbox). The in-process run has no completed state for an item, so it records nothing.</summary>
    Task CompleteInboxAsync(Guid id, CancellationToken ct);

    Task ReleaseInboxAsync(Guid id, CancellationToken ct);

    Task AddDeadLetterAsync(string source, string error, byte[] body, Guid id, CancellationToken ct);

    // ---- payloads, log
    PayloadRef GetPayload(Guid id);

    void Log(string level, string message, string? item = null);

    // ---- engine work (EnqueueWork, ClaimWork, StartWork, FinishWork, ReleaseWork, IsCancelRequested, WorkState; plan 28c E11): the owner's queue, for the run's own owner only
    Task<Guid> EnqueueWorkAsync(string kind, string subject, string part, string resource, string lane, string? payload, CancellationToken ct);

    Task<RunnerWorkItem?> ClaimWorkAsync(string? lane, CancellationToken ct);

    Task<bool> StartWorkAsync(long id, CancellationToken ct);

    Task<bool> FinishWorkAsync(long id, WorkFinish outcome, CancellationToken ct);

    Task<bool> ReleaseWorkAsync(long id, CancellationToken ct);

    Task<bool> IsWorkCancelRequestedAsync(Guid itemRunId, CancellationToken ct);

    Task<WorkSnapshot> WorkStateAsync(IReadOnlyList<string> kinds, CancellationToken ct);

    // ---- one-way (the protocol batches them): counts, the status line, lineage
    void Count(string name, long n);

    void Status(string text);

    void Lineage(IReadOnlyList<string> reads, IReadOnlyList<string> writes);
}
