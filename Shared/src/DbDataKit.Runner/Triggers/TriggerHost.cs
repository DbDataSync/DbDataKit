using System.Data.Common;
using DbDataKit.State;

namespace DbDataKit.Runner.Triggers;

/// <summary>What a trigger asks the scheduler to put on the queue (plan 28a DR9: a trigger writes a work item, it never runs a task).</summary>
/// <param name="Trigger">What set it off, as the payload names it: <see cref="TriggerEngine.AfterRunTrigger"/>, <see cref="TriggerEngine.WebhookTrigger"/> or <see cref="TriggerEngine.FileTrigger"/>.</param>
/// <param name="Part">The slice of the task the item is for, so two items of one event collide and two events do not (<c>after:&lt;run id&gt;</c>, <c>file:&lt;hash&gt;</c>); null keeps the scheduler's own rule (one item of a task at a time, unless it allows overlap).</param>
/// <param name="Depth">How many triggered runs came before this one in its chain.</param>
/// <param name="Source">What set it off, in words, for the payload and the run's story. Never a body or a secret.</param>
/// <param name="Inputs">Inputs for the task (none by default: a task's inputs are the ones it declares, and an event has none to give).</param>
/// <param name="RequestedBy">Who asked, when a person did.</param>
/// <param name="AlsoWrite">Runs in the transaction that inserts the item: the trigger's claim on the event (its cursor move, its ledger note), so the claim and the item stand or fall together. It throws <see cref="TriggerClaimLostException"/> when another runner claimed first.</param>
public sealed record TriggerStart(string Trigger, string? Part, int Depth, string? Source, IReadOnlyDictionary<string, string>? Inputs = null, string? RequestedBy = null,
    Action<DbConnection, DbTransaction, Guid>? AlsoWrite = null);

public enum TriggerEnqueueOutcome
{
    /// <summary>An item is on the queue.</summary>
    Queued,
    /// <summary>An item with the same task and part is queued or running already: the event is being handled.</summary>
    Collision,
    /// <summary>The task is not one this loop serves, or its projection row cannot be read.</summary>
    Unavailable,
}

public sealed record TriggerEnqueueResult(TriggerEnqueueOutcome Outcome, Guid? RunId = null, string? Problem = null);

/// <summary>Another runner claimed the event first (its cursor move or ledger note got there before ours): this one does nothing about it.</summary>
public sealed class TriggerClaimLostException() : Exception("Another runner claimed this event first.");

/// <summary>
/// What the trigger engine needs of the scheduler it is part of: the rows it serves, whether a task is paused, and the one way to queue work. <see cref="Scheduler.RunnerScheduler"/> implements it, so a triggered item is
/// queued exactly as a scheduled or a manual one is (same payload, same resource limit, same run lock, same lease).
/// </summary>
public interface ITriggerHost
{
    /// <summary>The projection rows of the served channels whose name and schedule and limits read (a broken row is the scheduler's to report).</summary>
    IReadOnlyList<TaskProjection> ServedRows();

    bool IsPaused(TaskRef task);

    TriggerEnqueueResult Enqueue(TaskRef task, TriggerStart start);

    /// <summary>Notes something the loop did not do and why (the scheduler's problem list and log).</summary>
    void Report(string subject, string message);
}
