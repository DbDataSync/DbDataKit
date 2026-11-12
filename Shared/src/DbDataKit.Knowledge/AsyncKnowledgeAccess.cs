namespace DbDataKit.Knowledge;

/// <summary>
/// The asynchronous access check (plan 27a, open item "one async access check"): a source of access that has to ask a service per person (our own dashboards and reports, whose catalog answers <c>Find</c> asynchronously) cannot answer the synchronous
/// <see cref="IKnowledgeCaller.MayRead"/>. This interface sits beside it and does not change it: <see cref="Handles"/> says which subjects it answers, <see cref="MayReadAsync"/> answers them. <see cref="SyncKnowledgeAccess"/> adapts a synchronous caller,
/// so a host that has only the caller can still be put through the same path. Once the knowledge module has settled the two checks are to be consolidated into this one (the plan says so).
/// </summary>
public interface IAsyncKnowledgeAccess
{
    /// <summary>Whether this access answers the subject (a subject it does not handle is left to the synchronous caller).</summary>
    bool Handles(KnowledgeSubject subject);

    /// <summary>Whether the person may read the object the subject names; only asked for a subject <see cref="Handles"/> says yes to.</summary>
    ValueTask<bool> MayReadAsync(KnowledgeSubject subject, CancellationToken cancellationToken = default);
}

/// <summary>An asynchronous access over a synchronous caller: it handles every subject and answers as the caller does, without waiting for anything.</summary>
public sealed class SyncKnowledgeAccess(IKnowledgeCaller caller) : IAsyncKnowledgeAccess
{
    public bool Handles(KnowledgeSubject subject) => true;

    public ValueTask<bool> MayReadAsync(KnowledgeSubject subject, CancellationToken cancellationToken = default) => ValueTask.FromResult(caller.MayRead(subject));
}

/// <summary>Several asynchronous accesses as one: a subject is answered by the first that handles it; one nobody handles is not readable (fail closed).</summary>
public sealed class CompositeKnowledgeAccess(params IAsyncKnowledgeAccess[] accesses) : IAsyncKnowledgeAccess
{
    public bool Handles(KnowledgeSubject subject) => accesses.Any(a => a.Handles(subject));

    public ValueTask<bool> MayReadAsync(KnowledgeSubject subject, CancellationToken cancellationToken = default) =>
        accesses.FirstOrDefault(a => a.Handles(subject)) is { } access ? access.MayReadAsync(subject, cancellationToken) : ValueTask.FromResult(false);
}
