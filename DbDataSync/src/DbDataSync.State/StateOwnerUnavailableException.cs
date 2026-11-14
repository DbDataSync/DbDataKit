namespace DbDataSync.State;

/// <summary>Raised by a state that cannot reach its owner any more (the runner's state over the protocol, plan 28c E22). The caller's job is to stop cleanly, not to retry.</summary>
public sealed class StateOwnerUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
