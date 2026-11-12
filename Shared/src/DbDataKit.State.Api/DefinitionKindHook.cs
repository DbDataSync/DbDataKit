using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.State.Api;

/// <summary>A refusal a kind's hook makes: the HTTP status and the sentence the caller reads. The API audits it as a refused call and answers with it.</summary>
public sealed class DefinitionsRefusalException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>A body to answer with instead of the plain error (the posture's "needs confirmation" list).</summary>
    public JsonObject? Body { get; init; }
}

/// <summary>What a hook sees of a publish after the API parsed it, checked the roles and the owner rules, and before it verifies the signature and stores the version.</summary>
public sealed record PublishHookContext(string Kind, string Name, string Channel, string Manifest, IReadOnlyList<ArtifactRef> Files, DefinitionsCaller Caller)
{
    /// <summary>The request body as sent (a kind reads the fields it adds to it, such as the Gateway's <c>adopt</c>); never null from the API.</summary>
    public JsonObject Body { get; init; } = [];
}

/// <summary>What a hook sees of a life-cycle call (<c>activate</c>, <c>switch</c>, <c>rollback</c>, <c>retire</c>) the API authorised: the parsed body, the version a <c>switch</c> names, and <c>expected_version</c>.</summary>
public sealed record LifecycleHookContext(string Kind, string Name, string Channel, string Act, int? ExpectedVersion, int? Version, JsonObject Body, DefinitionsCaller Caller);

/// <summary>What a hook sees of a binding call the API authorised: the slots (slot to what it is bound to; a single slot for an unbind) and the parsed body.</summary>
public sealed record BindingHookContext(string Kind, string Name, string Channel, IReadOnlyDictionary<string, string> Slots, int? ExpectedRevision, JsonObject Body, DefinitionsCaller Caller);

/// <summary>
/// What one kind adds to the shared publish API (plan 30 PB02): the rules that are not the registry's validation and not the roles, because they act on what a host does with the kind. The runner's <c>integration</c> kind carries
/// the channel it may serve, the trigger-loop check, the task projection it rebuilds, the progress migration and the holds of an activation, and the posture of a binding. Every member has a default that adds nothing, so a kind
/// without a hook (every serving kind) is published exactly as before. A hook says no by throwing <see cref="DefinitionsRefusalException"/>.
/// </summary>
public interface IDefinitionKindHook
{
    /// <summary>The kind the hook is for.</summary>
    string Kind { get; }

    /// <summary>Whether a caller with only the publisher role may read the kind's definitions (the shared API's default: yes; the runner's: no, a publisher uploads and publishes and does not browse).</summary>
    bool PublisherMayRead => true;

    /// <summary>Whether signed versions are required for this kind, when the host keeps its own setting (the runner's <c>require_signatures</c>); null leaves it to the group's signature policy.</summary>
    bool? SignaturesRequired => null;

    /// <summary>The sentence for a version refused because it is unsigned where signatures are required, when the host words it itself; null for the API's own sentence.</summary>
    string? UnsignedRefusal(string name, SignatureVerdict verdict) => null;

    /// <summary>The HTTP status of a refusal of the registry's rules (an unbound slot, a dependency not active, a format); null for the API's own (409, and 400 for an invalid or a missing file).</summary>
    int? StatusOf(PublishRefusal reason) => null;

    /// <summary>First check of a publish, a life-cycle call and a binding: the channel the host serves (a refusal says why not).</summary>
    void CheckChannel(string channel) { }

    /// <summary>Before a version is signature-checked and stored: names, the trigger loop. Throws to refuse.</summary>
    void BeforePublish(PublishHookContext context) { }

    /// <summary>The same as <see cref="BeforePublish"/> for a check that waits on something (the Gateway asks a data source a test query); the API calls this one, which by default calls <see cref="BeforePublish"/>.</summary>
    Task BeforePublishAsync(PublishHookContext context, CancellationToken ct)
    {
        BeforePublish(context);
        return Task.CompletedTask;
    }

    /// <summary>Who owns the definition when the group's settings hold no owner for it (the Gateway's models and data sources carry <c>managed_by</c> in what they publish, and the per-kind routes that predate the API stamp it); null when the host keeps none.</summary>
    string? OwnerOf(string name) => null;

    /// <summary>Before a definition is described or one of its versions is read: a host with rules of its own about who reads which one (the Gateway's owner rules) throws to refuse.</summary>
    void BeforeRead(string name, DefinitionsCaller caller) { }

    /// <summary>After a version is stored: what the host does with it (a served version replaces the channel's task projection), and what it adds to the answer.</summary>
    void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response) { }

    /// <summary>Does a life-cycle call itself (it audits itself) and answers; null leaves it to the API's own handling.</summary>
    Task<DefinitionsResult?> ActAsync(LifecycleHookContext context, CancellationToken ct) => Task.FromResult<DefinitionsResult?>(null);

    /// <summary>Before slots are bound: the names, and what a binding would change (the posture). Throws to refuse.</summary>
    void BeforeBind(BindingHookContext context) { }

    /// <summary>After slots are bound.</summary>
    void AfterBind(BindingHookContext context) { }

    /// <summary>Before a slot is emptied (a slot a served or staged version declares is not). Throws to refuse.</summary>
    void BeforeUnbind(BindingHookContext context) { }

    /// <summary>After a slot was emptied (<paramref name="removed"/> says whether it was bound); may replace the answer, null keeps the API's.</summary>
    DefinitionsResult? AfterUnbind(BindingHookContext context, bool removed) => null;

    /// <summary>Replaces the list of the kind's definitions (a host with its own shape); null for the API's.</summary>
    JsonObject? List() => null;

    /// <summary>Replaces the description of one definition; null for the API's.</summary>
    JsonObject? Describe(string name) => null;
}
