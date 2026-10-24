namespace DbDataKit.Scheduling;

/// <summary>Where an item of work is in its life.</summary>
public enum WorkItemStatus
{
    Pending,
    Claimed,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>What one claim hands a worker.</summary>
/// <param name="Id">The row.</param>
/// <param name="Service">The service whose queue it is on.</param>
/// <param name="Owner">Who the work belongs to.</param>
/// <param name="Kind">What sort of work.</param>
/// <param name="Subject">The thing worked on.</param>
/// <param name="Part">A slice of the subject, or <see cref="WorkQueueStore.NoPart"/>.</param>
/// <param name="Resource">What it must not share with other running work, or empty.</param>
/// <param name="Lane">The pool that runs it, or empty.</param>
/// <param name="RunId">The run this item is for; the caller's run record uses the same id.</param>
/// <param name="Status">Where it is now.</param>
/// <param name="Priority">Higher is claimed first.</param>
/// <param name="PayloadJson">What the product needs to run it; opaque here.</param>
public sealed record WorkItem(
    long Id, string Service, string Owner, string Kind, string Subject, string Part, string Resource, string Lane,
    Guid RunId, WorkItemStatus Status, int Priority, string? PayloadJson);

/// <summary>An enqueue that must not join other work found some already in flight for the same owner, kind, subject and part.</summary>
public sealed class WorkQueueCollisionException(string owner, string kind, string subject, string part)
    : InvalidOperationException($"'{kind}' for '{owner}' / '{subject}'{(part.Length == 0 ? "" : $" / '{part}'")} is already queued or running.")
{
    public string Owner { get; } = owner;
    public string Kind { get; } = kind;
    public string Subject { get; } = subject;
    public string Part { get; } = part;
}
