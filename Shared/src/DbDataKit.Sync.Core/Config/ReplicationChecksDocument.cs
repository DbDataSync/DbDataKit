using DbDataKit.Quality;

namespace DbDataSync.Core.Config;

/// <summary>The checks of a replication as a screen edits them: <see cref="Checks"/> as forms, <see cref="Text"/> the section as the file has it, <see cref="Version"/> what a save quotes (plan 33 UE07).</summary>
public sealed record ReplicationChecksDocument(IReadOnlyList<CheckForm> Checks, string Text, string Version)
{
    /// <summary>Reads the checks of a task file's <paramref name="text"/>; a fault in the section is a <see cref="ConfigFileException"/>.</summary>
    public static ReplicationChecksDocument Of(string text, string file) =>
        new([.. ReplicationChecks.Read(text, file).Select(CheckForms.Of)], ReplicationChecks.Section(text), ReplicationChecks.Version(text));
}

/// <summary>A save quoted a version of the checks that is not the file's any more; <see cref="Current"/> is what the file has now.</summary>
public sealed class ReplicationChecksConflictException(ReplicationChecksDocument current)
    : InvalidOperationException("The checks of this replication were changed after you read them.")
{
    public ReplicationChecksDocument Current { get; } = current;
}
