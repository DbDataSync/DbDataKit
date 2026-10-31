namespace DbDataSync.Cli;

/// <summary>The commands that were renamed in plan 13 (C06), and what they are called now, so the old form says so instead of "unknown".</summary>
public static class RetiredCommands
{
    private static readonly Dictionary<string, string> TopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["serve"] = "server run",
        ["setup"] = "server setup",
    };

    private static readonly HashSet<string> MovedOutOfConfig = new(StringComparer.OrdinalIgnoreCase) { "cert", "secret", "library", "driver" };

    /// <summary>
    /// A command that is gone, with what to do instead (plan 13, X05): users and invitations are the dbdatakit console's, so Sync no longer prints an invitation.
    /// </summary>
    public static string? RemovedBecause(string command) => command.Equals("invite", StringComparison.OrdinalIgnoreCase)
        ? "'dbdatasync invite' is gone: Sync has no sign-in of its own to invite anyone to. Users and invitations are the dbdatakit console's; run `dbdatakit user invite` there, and add this server to the console (`dbdatasync server pair`)."
        : null;

    /// <summary>The new form of a top-level command, or null.</summary>
    public static string? ReplacementFor(string command) => TopLevel.GetValueOrDefault(command);

    /// <summary>The new form of a <c>config</c> subcommand that is a noun of its own now, or null.</summary>
    public static string? ConfigReplacementFor(string subcommand) => MovedOutOfConfig.Contains(subcommand) ? subcommand.ToLowerInvariant() : null;
}
