namespace DbDataSync.Cli;

/// <summary>
/// Phase 157: a repository folder another account made (the Sync server's service did, before plan 33 SC23 deleted it) is one libgit2 refuses to open. Disabling libgit2's ownership check is process-wide
/// (it wraps <c>git_libgit2_opts_set_owner_validation</c>, not a per-path allowlist), which is fine for this short-lived command line: it warns and turns the check off on Windows.
/// </summary>
internal static class GitOwnershipValidation
{
    internal const string WarningMessage =
        "Warning: disabling libgit2's repository-ownership check for this command. Installing " +
        "DbDataSync as a Windows service transfers ownership of the data directory to the service " +
        "account, which otherwise blocks every other command run against the same directory.";

    /// <summary>No-op outside Windows (where a service install got ownership right with <c>chown -R</c>, phase 135); on Windows it warns and disables the check for this process's lifetime.</summary>
    public static void DisableIfInteractive(TextWriter? warningWriter = null)
    {
        if (!OperatingSystem.IsWindows())
            return;

        (warningWriter ?? Console.Error).WriteLine(WarningMessage);
        LibGit2Sharp.GlobalSettings.SetOwnerValidation(false);
    }
}
