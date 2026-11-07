using DbDataKit.Monitoring;
using DbDataKit.Scheduling;

namespace DbDataKit.Runner;

/// <summary>How a task ended. Four states; each has exactly one <see cref="RunOutcome"/> (<see cref="TaskEndMapping.OutcomeOf"/>).</summary>
public enum TaskEndState
{
    Succeeded,
    /// <summary>It finished and some items failed (QR4: the existing <see cref="RunOutcome.CompletedWithFailures"/>).</summary>
    CompletedWithFailures,
    Failed,
    /// <summary>Stopped on purpose.</summary>
    Stopped,
}

/// <summary>An output of a task: a payload in the payload store (by id) or a short text, never both.</summary>
public sealed record TaskOutput(string? PayloadId, string? Text)
{
    public static TaskOutput Payload(string id) => new(id, null);
    public static TaskOutput FromText(string text) => new(null, text);
    public bool IsValid => (PayloadId is not null) != (Text is not null);
}

/// <summary>
/// How a task ended (the protocol's end message, or what the runner makes of a process that gave none). <see cref="Processed"/> and
/// <see cref="FailedItems"/> are counts of items; <see cref="FirstFailedItem"/> names the first one that failed.
/// </summary>
public sealed record TaskEnd(
    TaskEndState State,
    long Processed = 0,
    long FailedItems = 0,
    string? FirstFailedItem = null,
    TaskFailure? Failure = null,
    IReadOnlyDictionary<string, TaskOutput>? Outputs = null)
{
    public IReadOnlyDictionary<string, TaskOutput> OutputsOrEmpty => Outputs ?? new Dictionary<string, TaskOutput>();

    /// <summary>What is inconsistent in the end message; empty when the run store and the queue can take it.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!Enum.IsDefined(State)) p.Add($"The end state {(int)State} is not one the runner knows.");
        if (Processed < 0 || FailedItems < 0) p.Add("A count is negative.");
        switch (State)
        {
            case TaskEndState.Succeeded:
                if (Failure is not null) p.Add("A task that succeeded has no failure.");
                if (FailedItems > 0) p.Add("A task that succeeded has no failed items; it completed with failures.");
                break;
            case TaskEndState.CompletedWithFailures:
                if (FailedItems < 1) p.Add("A task that completed with failures says how many items failed (at least one).");
                break;
            case TaskEndState.Failed:
                if (Failure is null) p.Add("A task that failed says why (a failure).");
                else if (Failure.Kind == TaskFailureKind.Cancelled) p.Add("A cancel is a stopped task, not a failed one.");
                break;
            case TaskEndState.Stopped:
                break;
        }
        foreach (var (name, output) in OutputsOrEmpty)
            if (output is null || !output.IsValid) p.Add($"The output '{name}' is either a payload id or a text, not both and not neither.");
        return p;
    }
}

/// <summary>What the run store's <c>RunStore.Finish</c> takes besides the run id.</summary>
public sealed record RunFinish(RunOutcome Outcome, long? Rows, string? FailedItem, RunFailure? Failure);

/// <summary>
/// The pure mappings of plan 28a R02, with no I/O. A state with no mapping throws, and the tests enumerate every enum value, so adding a value
/// without a mapping fails a test and never a run.
/// </summary>
public static class TaskEndMapping
{
    /// <summary>The one outcome a state has.</summary>
    public static RunOutcome OutcomeOf(TaskEndState state) => state switch
    {
        TaskEndState.Succeeded => RunOutcome.Succeeded,
        TaskEndState.CompletedWithFailures => RunOutcome.CompletedWithFailures,
        TaskEndState.Failed => RunOutcome.Failed,
        TaskEndState.Stopped => RunOutcome.Stopped,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "An end state has exactly one run outcome."),
    };

    /// <summary>The queue item's final status: done for a task that finished (even with failed items), failed, or cancelled.</summary>
    public static WorkItemStatus WorkItemStatusOf(TaskEndState state) => state switch
    {
        TaskEndState.Succeeded or TaskEndState.CompletedWithFailures => WorkItemStatus.Done,
        TaskEndState.Failed => WorkItemStatus.Failed,
        TaskEndState.Stopped => WorkItemStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "An end state has exactly one work item status."),
    };

    public static RunOutcome ToRunOutcome(this TaskEnd end) => OutcomeOf(end.State);

    public static WorkItemStatus ToWorkItemStatus(this TaskEnd end) => WorkItemStatusOf(end.State);

    /// <summary>The arguments for <c>RunStore.Finish</c>. The failed item is the first failed item, or the failure's own item.</summary>
    public static RunFinish ToRunFinish(this TaskEnd end) =>
        new(OutcomeOf(end.State), end.Processed, end.FirstFailedItem ?? end.Failure?.Item, end.Failure?.ToRunFailure());

    /// <summary>
    /// DR8: a retry applies to a failed task whose failure is retryable, while <paramref name="attempt"/> (1 for the first run) is below
    /// <paramref name="maxAttempts"/> (the manifest's <c>attempts</c>, which counts the first run; null is one).
    /// </summary>
    public static bool ShouldRetry(TaskEnd end, int attempt, int? maxAttempts) =>
        end.State == TaskEndState.Failed && end.Failure is { Retryable: true } && attempt < (maxAttempts ?? 1);

    // ---- what the runner makes of a process that gave no end message

    /// <summary>The process ended without an end message. Exit code zero is still a violation: a task says how it ended.</summary>
    public static TaskEnd ProcessExited(int exitCode, long processed = 0) =>
        Failed(exitCode == 0
            ? new TaskFailure(TaskFailureKind.ProtocolViolation, "The process exited with code 0 without sending the end message.")
            : new TaskFailure(TaskFailureKind.ProcessExited, $"The process exited with code {exitCode} without sending the end message."), processed);

    public static TaskEnd TimedOut(TimeSpan limit, long processed = 0) =>
        Failed(new TaskFailure(TaskFailureKind.TimedOut, $"The task ran past its time limit of {limit} and was stopped."), processed);

    /// <summary>Stopped on request. Not a failure: the state is Stopped and the failure records why, so it is never retried.</summary>
    public static TaskEnd Cancelled(string? reason = null, long processed = 0) =>
        new(TaskEndState.Stopped, processed, Failure: new TaskFailure(TaskFailureKind.Cancelled, reason ?? "The task was stopped on request."));

    public static TaskEnd LimitExceeded(string limit, long processed = 0) =>
        Failed(new TaskFailure(TaskFailureKind.LimitExceeded, $"The task went over its {limit} limit and was stopped."), processed);

    public static TaskEnd ProtocolViolation(string message, long processed = 0) =>
        Failed(new TaskFailure(TaskFailureKind.ProtocolViolation, message), processed);

    private static TaskEnd Failed(TaskFailure failure, long processed) => new(TaskEndState.Failed, processed, Failure: failure);
}
