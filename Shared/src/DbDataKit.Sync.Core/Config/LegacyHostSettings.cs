namespace DbDataSync.Core.Config;

/// <summary>
/// The host settings files of earlier releases, which are refused and never read (plan 13, E04; clean break, D10). Sync's settings are
/// <c>server.yaml</c> in the repository root, like every DbData server's. An <c>appsettings.json</c> (the repository root, its <c>config</c> folder or
/// beside the executable) and the <c>dbdatasync.config.yaml</c> of earlier releases are not converted: the server stops with a message that names the
/// file, and the settings are written again as <c>server.yaml</c>. The migrations that used to rewrite old key names and move the per-user default
/// folder are gone, so an old install is set up again.
/// </summary>
public static class LegacyHostSettings
{
    /// <summary>The settings file of releases before plan 13.</summary>
    public const string ConfigFileName = "dbdatasync.config.yaml";

    /// <summary>The name pattern of ASP.NET Core's settings files (<c>appsettings.json</c>, <c>appsettings.Development.json</c>).</summary>
    public const string AppSettingsPattern = "appsettings*.json";

    /// <summary>
    /// The message for the first old file found, or null when there is none.
    /// </summary>
    /// <param name="repoRoot">The repository root the server was asked to use.</param>
    /// <param name="baseDirectory">Where the executable is (an <c>appsettings.json</c> beside it counts); null skips that place.</param>
    /// <param name="legacyDefaultRoot">When the root is the platform default, the per-user folder earlier releases used (an old install may still be there); looked at only while the root has no <c>server.yaml</c> yet. Null skips it.</param>
    public static string? Refusal(string repoRoot, string? baseDirectory, string? legacyDefaultRoot = null)
    {
        var target = Path.Combine(repoRoot, "server.yaml");

        if (Find(repoRoot, ConfigFileName) is { } config)
            return $"'{config}' is the settings file of an earlier release and is no longer read. Set the server up again: move that file away, then run `dbdatasync server setup`, " +
                   $"or write the settings by hand in the DbDataSync: section of '{target}' (docs/configuration.md lists each key; the key names changed in phase 164, so the old file cannot be copied as it is).";

        foreach (var directory in new[] { repoRoot, Path.Combine(repoRoot, "config"), baseDirectory })
            if (directory is not null && Find(directory, AppSettingsPattern) is { } appsettings)
                return $"'{appsettings}' is the settings file of an earlier release and is no longer read. Write its DbDataSync: settings in '{target}' " +
                       "(docs/configuration.md lists each key; Logging and AllowedHosts are not settings of this server), then delete the old file.";

        if (legacyDefaultRoot is not null && !File.Exists(target) && Find(legacyDefaultRoot, ConfigFileName) is { } stranded)
            return $"'{stranded}' is the settings file of an earlier release's install, kept in the per-user folder those releases used, and is no longer read. " +
                   $"Set the server up again at '{repoRoot}': run `dbdatasync server setup`, or name another folder with --repo; the old folder is not moved or read.";

        return null;
    }

    private static string? Find(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal).FirstOrDefault() : null;
}
