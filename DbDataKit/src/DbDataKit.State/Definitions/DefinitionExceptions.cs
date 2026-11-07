namespace DbDataKit.State;

/// <summary>Why a publish or a promotion was refused.</summary>
public enum PublishRefusal
{
    UnknownKind,
    WrongTarget,
    Invalid,
    UnboundSlot,
    DependencyNotActive,
    ApprovalRequired,
    Artifact,
}

/// <summary>A publish or promotion that plan 30's rules refuse; the message names the thing (the kind and name, the slot and channel, the dependency).</summary>
public sealed class PublishRefusedException(PublishRefusal reason, string message) : DefinitionRejectedException(message)
{
    public PublishRefusal Reason { get; } = reason;
}

/// <summary>An <c>expected_version</c> that no longer holds: someone changed the definition after the caller looked. Nothing was changed; read again and decide.</summary>
public sealed class DefinitionConflictException(string message, int expected, int actual) : Exception(message)
{
    public int Expected { get; } = expected;

    public int Actual { get; } = actual;
}
