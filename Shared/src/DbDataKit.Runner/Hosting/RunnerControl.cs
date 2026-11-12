using System.Text.Json;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>Who asked for a change to a task, for the audit: <c>person</c> (a user of a console, or the operator on the machine) and what it came through.</summary>
/// <param name="Id">The subject id of the person (a console user), or <c>local:&lt;os user&gt;</c> for the command line.</param>
/// <param name="Name">The person's display name, when known.</param>
/// <param name="ViaKind"><c>console</c>, <c>api</c> or <c>command</c>.</param>
/// <param name="ViaId">The client that carried the call, when there is one.</param>
public sealed record RunnerActor(string Id, string? Name, string ViaKind, string? ViaId = null)
{
    public static RunnerActor Local() => new("local:" + Environment.UserName, Environment.UserName, "command");

    /// <summary>The name a pause shows as <c>paused by</c>.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Id : Name!;
}

/// <summary>What <see cref="RunnerControl.RunNow"/> did: the run id of the item queued, or that one is queued or running already.</summary>
public sealed record RunNowResult(bool Queued, Guid? RunId);

/// <summary>What <see cref="RunnerControl.ResolveStep"/> did: whether the step was waiting for an answer, and the run queued afterwards when the operator asked for one (<see cref="RunId"/> is null when none was, or one is queued already).</summary>
public sealed record StepResolution(bool Resolved, Guid? RunId, bool RetryQueued);

/// <summary>What <see cref="RunnerControl.ReplayDeadLetter"/> did: <see cref="Replayed"/> false when the letter is not open (replayed or discarded already, or not the task's); otherwise the inbox item it became and whether the task was queued for it.</summary>
public sealed record DeadLetterReplay(bool Replayed, Guid? InboxItem, bool Queued, bool HadBody);

/// <summary>A task named in a call is not one this runner serves (not in the projection of a channel it serves, or its row cannot be read).</summary>
public sealed class UnknownTaskException(string message) : InvalidOperationException(message);

/// <summary>
/// The acts on a runner's tasks that a person asks for (plan 28a R10): start one now, pause it, resume it, and what the scheduler says of every task. The management API and the command line (<c>tasks run|pause|resume</c>) both
/// go through here, so both are audited the same way (<c>runner.task.run</c>, <c>runner.task.pause</c>, <c>runner.task.resume</c>; the target is <c>package/channel/task</c>). A control built for the command line has no loop
/// of its own: what it queues is run by the server that shares the state, as soon as one is running.
/// </summary>
public sealed class RunnerControl
{
    public const string RunVerb = "runner.task.run";
    public const string PauseVerb = "runner.task.pause";
    public const string ResumeVerb = "runner.task.resume";
    public const string OwnerVerb = "runner.task.owner";
    public const string RewindVerb = "runner.checkpoint.rewind";
    public const string ResolveStepVerb = "runner.step.resolve";
    public const string ReplayVerb = "runner.deadletter.replay";
    public const string DiscardVerb = "runner.deadletter.discard";
    public const string TargetKind = "task";

    private readonly RunnerScheduler _scheduler;
    private readonly AuditLog _audit;

    public RunnerControl(StateDatabase state, RunnerScheduler scheduler, string serviceId = RunnerNames.ServiceId)
    {
        State = state;
        _scheduler = scheduler;
        _audit = new AuditLog(state, serviceId);
    }

    public StateDatabase State { get; }

    /// <summary>A control with no running loop, for the command line: the scheduler is built to read and to queue, with an executor that refuses to run anything.</summary>
    public static RunnerControl ForCommands(StateDatabase state, IReadOnlyCollection<string> channels, string cacheDirectory) =>
        new(state, new RunnerScheduler(state, new NoExecutor(), new ArtifactCache(cacheDirectory, new PayloadArtifactSource(new PayloadStore(state))),
            new DefinitionVersionSource(new DefinitionStore(state), new DefinitionPublisher(state, RunnerPackages.Kinds(), new PayloadStore(state), "runner")),
            new RunnerSchedulerOptions { Channels = channels, Service = RunnerNames.EngineService }));

    public SchedulerStatus Status() => _scheduler.Status();

    /// <summary>The triggers of this runner's tasks (plan 28a R13): what a webhook arrival asks to queue, and what the status reports.</summary>
    public DbDataKit.Runner.Triggers.TriggerEngine Triggers => _scheduler.Triggers;

    /// <summary>Queues a run of the task apart from its schedule (also when it is paused). With <paramref name="dryRun"/> it is a dry run: the job reads and nothing it keeps changes, the kit refuses writes and lists what it would have sent (plan 27a IX16), and the run does not start the tasks that follow it. Throws <see cref="UnknownTaskException"/> for a task this runner does not serve.</summary>
    public RunNowResult RunNow(TaskRef task, IReadOnlyDictionary<string, string>? inputs, RunnerActor actor, bool dryRun = false)
    {
        var runId = Translate(() => _scheduler.RunNow(task, inputs, actor.Display, dryRun));
        Record(RunVerb, task, actor, new { run_id = runId, queued = runId is not null, dry_run = dryRun, inputs = inputs?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [] });
        return new RunNowResult(runId is not null, runId);
    }

    public void Pause(TaskRef task, RunnerActor actor)
    {
        Translate(() => { _scheduler.Pause(task, actor.Display); return 0; });
        Record(PauseVerb, task, actor, null);
    }

    public void Resume(TaskRef task, RunnerActor actor)
    {
        Translate(() => { _scheduler.Resume(task); return 0; });
        Record(ResumeVerb, task, actor, null);
    }

    /// <summary>
    /// Sets who schedules the task (plan 28c E09: <c>old</c>, <c>observe</c> or <c>runner</c>), with the position the product's scheduler had reached (<paramref name="seed"/>), and writes who, from what, to what and why to the audit
    /// (<c>runner.task.owner</c>). The task need not have a version yet. Throws <see cref="UnknownTaskException"/> for a channel or package this runner does not serve.
    /// </summary>
    public void SetOwner(TaskRef task, string owner, string? reason, ScheduleSeed? seed, RunnerActor actor)
    {
        var before = Translate(() => _scheduler.OwnerOfTask(task).Owner);
        Translate(() => { _scheduler.SetOwner(task, owner, actor.Display, reason, seed); return 0; });
        Record(OwnerVerb, task, actor, new { from = before, to = owner, reason, handled_up_to = seed?.HandledUpTo.UtcDateTime.ToString("O"), paused_by = seed?.PausedBy });
    }

    /// <summary>
    /// Rewinds a checkpoint of the task to what it was before the change <paramref name="historyId"/> of its history (plan 29a Q20, plan 28a R25): a new version under the actor's name, written to the audit as <c>runner.checkpoint.rewind</c>
    /// (what it was, what it became, the change it went back past). False, and a failure on record, when the checkpoint moved meanwhile (a run advanced it) or the change is not that checkpoint's. Throws
    /// <see cref="InvalidOperationException"/> when going back would leave it with no part.
    /// </summary>
    public bool RewindCheckpoint(TaskRef task, string name, string itemKey, long historyId, RunnerActor actor)
    {
        var store = new ProgressStore(State, new ProgressKey(task.Package, task.Channel, task.Task));
        var before = store.GetCheckpoint(name, itemKey);
        var rewound = store.Rewind(name, historyId, actor.Display, itemKey);
        var after = rewound ? store.GetCheckpoint(name, itemKey) : null;
        static object? Parts(Checkpoint? c) => c?.Parts.ToDictionary(p => p.Key, p => p.Value.Canonical());
        Record(RewindVerb, task, actor, new { checkpoint = name, key = itemKey, history_id = historyId, from_version = before?.Version, to_version = after?.Version, from = Parts(before), to = Parts(after) },
            rewound ? AuditEvent.Ok : AuditEvent.Failed);
        return rewound;
    }

    /// <summary>
    /// The operator's answer for a step an earlier attempt left in doubt (plan 28a R25): it <paramref name="happened"/> (then <paramref name="resultJson"/> is its result, JSON, <c>null</c> for no value) or it did not; the step is then
    /// complete or failed, so the next attempt of the chain reuses the result or runs the step. With <paramref name="retry"/> a run of the task is queued as well. Audited as <c>runner.step.resolve</c> (the names of what was answered, never the result).
    /// Throws <see cref="ArgumentException"/> when the result is not allowed (too large, not JSON).
    /// </summary>
    public StepResolution ResolveStep(TaskRef task, string chain, string name, string stepKey, bool happened, string? resultJson, bool retry, RunnerActor actor)
    {
        var steps = new StepStore(State, new ProgressKey(task.Package, task.Channel, task.Task));
        bool resolved;
        try { resolved = steps.Get(chain, name, stepKey) is { State: StepState.InDoubt or StepState.Started } && steps.Resolve(chain, name, stepKey, happened, resultJson, null, actor.Display); }
        catch (Exception ex) when (ex is ArgumentException or StepResultTooLargeException or JsonException)
        {
            Record(ResolveStepVerb, task, actor, new { chain, step = name, key = stepKey, happened, reason = ex.Message }, AuditEvent.Failed);
            throw new ArgumentException(ex.Message, ex);
        }

        Guid? runId = null;
        if (resolved && retry)
        {
            var started = Translate(() => _scheduler.RunNow(task, new Dictionary<string, string>(), actor.Display));
            runId = started;
        }

        Record(ResolveStepVerb, task, actor, new { chain, step = name, key = stepKey, happened, retry, run_id = runId }, resolved ? AuditEvent.Ok : AuditEvent.Failed);
        return new StepResolution(resolved, runId, runId is not null);
    }

    /// <summary>
    /// Sends a dead letter again (plan 28a R25): its body becomes an inbox item of the same task (the letter's id is the item's id, so it is one item however often this is called), the letter is marked replayed and a run of the task is
    /// queued to take it. A letter that is not open is not replayed. Audited as <c>runner.deadletter.replay</c>.
    /// </summary>
    public DeadLetterReplay ReplayDeadLetter(TaskRef task, Guid id, RunnerActor actor)
    {
        var progress = new ProgressStore(State, new ProgressKey(task.Package, task.Channel, task.Task));
        if (progress.DeadLetters(DeadLetter.Open).FirstOrDefault(l => l.Id == id) is not { } letter)
        {
            Record(ReplayVerb, task, actor, new { dead_letter = id, reason = "not open" }, AuditEvent.Failed);
            return new DeadLetterReplay(false, null, false, false);
        }

        if (progress.InboxRecords(1000).All(i => i.Id != id))
        {
            Guid? body = null;
            if (letter.BodyPayload is { } original)
            {
                var payloads = new PayloadStore(State);
                using var read = payloads.OpenRead(original);
                using var writer = payloads.OpenWrite(new PayloadOptions("application/octet-stream", ProgressModule.Name, id.ToString("D"), "dead-letter-replay"));
                read.CopyTo(writer);
                writer.Complete();
                body = writer.Id;
            }

            progress.Receive(letter.Source, signatureOk: true, id, body);
        }

        progress.MarkDeadLetterReplayed(id);
        var queued = Triggers.TriggerWebhook(task);
        Record(ReplayVerb, task, actor, new { dead_letter = id, source = letter.Source, attempts = letter.Attempts, queued }, AuditEvent.Ok);
        return new DeadLetterReplay(true, id, queued, letter.BodyPayload is not null);
    }

    /// <summary>An operator decides a dead letter is not worth sending (plan 28a R25). False when it is not open. Audited as <c>runner.deadletter.discard</c>.</summary>
    public bool DiscardDeadLetter(TaskRef task, Guid id, RunnerActor actor)
    {
        var discarded = new ProgressStore(State, new ProgressKey(task.Package, task.Channel, task.Task)).DiscardDeadLetter(id);
        Record(DiscardVerb, task, actor, new { dead_letter = id }, discarded ? AuditEvent.Ok : AuditEvent.Failed);
        return discarded;
    }

    /// <summary>Who schedules the task, and who set that and why (null: nobody set it, the runner schedules it).</summary>
    public (string Owner, TaskOwner? Record) Owner(TaskRef task) => Translate(() => _scheduler.OwnerOfTask(task));

    /// <summary>The task's position, pause, owner and in-flight items: what a product reads to take the task back.</summary>
    public TaskScheduleState ScheduleState(TaskRef task) => Translate(() => _scheduler.ScheduleStateOf(task));

    /// <summary>The decisions recorded for an observed task, both the runner's and the product's own, oldest first.</summary>
    public IReadOnlyList<TaskDecision> Decisions(TaskRef task, DateTimeOffset? since = null) => Translate(() => _scheduler.DecisionsOf(task, since));

    /// <summary>The product's own scheduler reports a run it queued for an observed task, for the comparison with what this runner would have done. Not audited: it is data about the observation, not a change of the runner.</summary>
    public void RecordOldDecision(TaskRef task, DateTimeOffset at, int missed, bool paused) => Translate(() => { _scheduler.RecordOldDecision(task, at, missed, paused); return 0; });

    /// <summary>Notes that an actor was refused (the role was not enough): the call changed nothing.</summary>
    public void Refused(string verb, TaskRef task, RunnerActor actor, string reason) =>
        Record(verb, task, actor, new { reason }, AuditEvent.Refused);

    private static T Translate<T>(Func<T> act)
    {
        try { return act(); }
        catch (InvalidOperationException ex) { throw new UnknownTaskException(ex.Message); }
    }

    private void Record(string verb, TaskRef task, RunnerActor actor, object? detail, string outcome = AuditEvent.Ok) =>
        _audit.Record(new AuditEvent("person", actor.Id, verb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: TargetKind, TargetId: task.ToString(),
            Detail: detail is null ? null : JsonSerializer.Serialize(detail)));

    private sealed class NoExecutor : ITaskRunner
    {
        public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("This control only queues and reads; the running server executes tasks.");
    }
}
