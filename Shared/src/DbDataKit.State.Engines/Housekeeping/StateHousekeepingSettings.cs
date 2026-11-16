namespace DbDataKit.State;

/// <summary>
/// How often a server keeps its own state (plan 35 I1): the <c>housekeeping:</c> part of the <c>state:</c> section every product shares, read by <see cref="StateSettingsReader"/> and run by
/// <see cref="DbDataKit.State.Engines.Housekeeping.StateHousekeeping"/> in the server's own process.
/// <code>
/// state:
///   housekeeping:
///     retention_every_minutes: 60   # how often the retention rules run (an hour by default)
///     backup_directory: backups     # a folder for snapshots of this server's state; relative to the server's directory; none: no snapshot
///     backup_every_hours: 24        # how often a snapshot is written (a day by default)
///     backup_keep: 7                # how many snapshots stay: the newest
/// </code>
/// </summary>
public sealed record StateHousekeepingSettings
{
    public static StateHousekeepingSettings Defaults { get; } = new();

    /// <summary>How often the retention rules of the state's modules run (<see cref="RetentionJob"/>).</summary>
    public TimeSpan RetentionEvery { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The folder snapshots go to, as written (relative to the server's directory when not rooted); null: no snapshot is written.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>How often a snapshot is written.</summary>
    public TimeSpan BackupEvery { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How many snapshots stay in the folder: the newest.</summary>
    public int BackupKeep { get; init; } = 7;

    /// <summary>The backup folder as a full path for a server whose directory is <paramref name="serviceDirectory"/>; null when none is set.</summary>
    public string? BackupDirectoryIn(string serviceDirectory) => BackupDirectory is { Length: > 0 } folder ? Path.GetFullPath(folder, serviceDirectory) : null;
}
