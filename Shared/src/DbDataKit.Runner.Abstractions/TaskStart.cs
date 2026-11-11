namespace DbDataKit.Runner;

/// <summary>The protocol version this model belongs to (plan 28a DR4).</summary>
public static class RunnerProtocol
{
    public const string V1 = "dbdatakit.runner.v1";
}

/// <summary>
/// What a task is given when it starts (plan 28 section 4.1). <see cref="ConnectionHandles"/> maps a connection slot of the manifest to the
/// name of a handle: only names, never a secret or a connection string; the runner resolves a handle when the task opens it.
/// <see cref="ProgressSnapshot"/> says where the progress read at start can be fetched (the task reads it once, DR5); null when the task declares none.
/// </summary>
public sealed record TaskStart(
    TaskRef Task,
    Guid RunId,
    int Attempt,
    string PackageVersion,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string> ConnectionHandles,
    string? ProgressSnapshot,
    TimeSpan CancellationGrace,
    string ProtocolVersion = RunnerProtocol.V1)
{
    /// <summary>What is wrong with the start message, one line each; empty when it can be sent.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (RunId == Guid.Empty) problems.Add("The run id is empty.");
        if (Attempt < 1) problems.Add($"The attempt number is {Attempt}; the first attempt is 1.");
        if (string.IsNullOrWhiteSpace(PackageVersion)) problems.Add("The package version is empty.");
        if (CancellationGrace < TimeSpan.Zero) problems.Add("The cancellation grace is negative.");
        if (string.IsNullOrWhiteSpace(ProtocolVersion)) problems.Add("The protocol version is empty.");
        foreach (var (slot, handle) in ConnectionHandles)
        {
            if (string.IsNullOrWhiteSpace(slot)) problems.Add("A connection slot has no name.");
            if (string.IsNullOrWhiteSpace(handle) || handle.Any(c => char.IsWhiteSpace(c) || c is '=' or ';' or '@' or ':'))
                problems.Add($"The handle for connection slot '{slot}' is not a plain name; a handle names a connection and never holds a connection string or a secret.");
        }
        return problems;
    }
}
