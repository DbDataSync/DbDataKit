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

    /// <summary>The new form of a top-level command, or null.</summary>
    public static string? ReplacementFor(string command) => TopLevel.GetValueOrDefault(command);

    /// <summary>The new form of a <c>config</c> subcommand that is a noun of its own now, or null.</summary>
    public static string? ConfigReplacementFor(string subcommand) => MovedOutOfConfig.Contains(subcommand) ? subcommand.ToLowerInvariant() : null;
}
