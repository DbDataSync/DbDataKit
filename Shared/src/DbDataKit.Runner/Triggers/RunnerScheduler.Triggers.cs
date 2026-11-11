using DbDataKit.Runner.Triggers;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner.Scheduler;

/// <summary>The scheduler's side of triggers (plan 28a R13): it is the host the <see cref="TriggerEngine"/> queues through, so every trigger writes the same kind of work item as the schedule does (DR9).</summary>
public sealed partial class RunnerScheduler : ITriggerHost
{
    private readonly TriggerEngine _triggers;

    /// <summary>The triggers of this loop's tasks: after-run, webhook, file arrival. A tick runs them; a webhook receiver calls them on arrival.</summary>
    public TriggerEngine Triggers => _triggers;

    IReadOnlyList<TaskProjection> ITriggerHost.ServedRows() => [.. Rows().Where(r => ParseOrProblem(r, out _))];

    bool ITriggerHost.IsPaused(TaskRef task) => _pauses.IsPaused(OwnerOf(task), task.Task);

    TriggerEnqueueResult ITriggerHost.Enqueue(TaskRef task, TriggerStart start)
    {
        Served served;
        try { served = Find(task); }
        catch (InvalidOperationException ex) { return new TriggerEnqueueResult(TriggerEnqueueOutcome.Unavailable, Problem: ex.Message); }

        return TryEnqueue(served, start.Trigger, start.Inputs, start.RequestedBy, start.Part, start.Depth, start.Source, start.AlsoWrite) is { } runId
            ? new TriggerEnqueueResult(TriggerEnqueueOutcome.Queued, runId)
            : new TriggerEnqueueResult(TriggerEnqueueOutcome.Collision);
    }

    void ITriggerHost.Report(string subject, string message) => Report(subject, message);
}
