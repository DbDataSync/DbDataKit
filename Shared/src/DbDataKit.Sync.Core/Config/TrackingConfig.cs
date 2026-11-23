namespace DbDataSync.Core.Config;

/// <summary>
/// Where a replication's tracking state (its positions, the record of what each entry moved) lives (plan 28c E17, plan 29 section 4a.2): the optional
/// <c>tracking:</c> section of <c>task.yaml</c>, <c>mode: target|state|both</c> and, for <c>both</c>, <c>primary: target|state</c>. Absent means the default
/// (<see cref="TrackingResolution"/> says what that is and why). An optional key of an open format: a file from before loads, a misspelled key is refused by the strict loader.
/// </summary>
public sealed class TrackingConfig
{
    /// <summary><c>target</c> (the state table on the target, in the entry's transaction), <c>state</c> (the runner's state only) or <c>both</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>The side decisions are made from when <see cref="Mode"/> is <c>both</c>: <c>target</c> or <c>state</c>. Refused for the other modes.</summary>
    public string? Primary { get; set; }

    /// <summary>How much of the state table on the target is kept (plan 28c E17, <c>tracking.retention</c>). Absent: <see cref="TrackingRetention.DefaultEntries"/> entries and <see cref="TrackingRetention.DefaultDays"/> days. Refused with the mode that keeps no table on the target.</summary>
    public TrackingRetention? Retention { get; set; }
}

/// <summary>
/// The retention of <c>dbdatasync.position_log</c>: an entry is removed only when it is older than <see cref="Days"/> days <b>and</b> not among the newest <see cref="Entries"/> of its mapping (the newest entry of each source is
/// never removed: it is where the next pass resumes). Default taken (plan 28c E17, owner not asked): the last 1000 entries and anything newer than 7 days.
/// </summary>
public sealed class TrackingRetention
{
    public const int DefaultEntries = 1000;
    public const double DefaultDays = 7;

    /// <summary>How many of a mapping's newest entries are always kept. At least 1.</summary>
    public int? Entries { get; set; }

    /// <summary>How many days of entries are always kept. Zero or more.</summary>
    public double? Days { get; set; }

    public int EffectiveEntries() => Entries ?? DefaultEntries;

    public double EffectiveDays() => Days ?? DefaultDays;
}

/// <summary>What a replication's tracking comes to: the mode asked for (or the default), the one that applies and the reason when they differ.</summary>
public sealed record TrackingResolved(string RequestedMode, string? RequestedPrimary, string EffectiveMode, string? EffectivePrimary, bool Defaulted, string Reason);

/// <summary>
/// Resolves <see cref="TrackingConfig"/> against what the writers can do. The state table on the target needs an <c>IChangeWriter</c> that writes it in the entry's transaction
/// (E17): they exist for <see cref="TargetWriterEngines"/> (SQL Server and PostgreSQL); MySQL/MariaDB and Oracle are listed in <see cref="TargetsThatCanHoldState"/> but keep their state in the
/// runner's state only (<c>mode: state</c>): the owner decided on 2026-11-14 (S3) that their writers are not built. A Flight SQL or DuckDB target never holds the table. A file that asks for <c>target</c> or <c>both</c> on an engine that cannot is refused at save with a
/// message instead of being accepted and ignored; a file without the section keeps the runner's state and creates nothing on the target.
/// </summary>
public static class TrackingResolution
{
    /// <summary>The engines whose writers could carry a state table in the entry's transaction (E17); of these only <see cref="TargetWriterEngines"/> do (S3: MySQL, MariaDB and Oracle stay on <c>mode: state</c>).</summary>
    public static readonly IReadOnlySet<string> TargetsThatCanHoldState = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { DriverIds.MsSql, DriverIds.Postgres, DriverIds.MySql, DriverIds.Oracle };

    /// <summary>The engines whose state-table writers are built (<c>TargetPositionLog</c>): a subset of <see cref="TargetsThatCanHoldState"/>.</summary>
    public static readonly IReadOnlySet<string> TargetWriterEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { DriverIds.MsSql, DriverIds.Postgres };

    /// <summary>Whether any target-state writer exists (the status and the screen read it; which engines is <see cref="TargetWriterEngines"/>).</summary>
    public const bool TargetWritersBuilt = true;

    /// <summary>The retention that applies to a replication's state table: what the file says, or the default.</summary>
    public static TrackingRetention RetentionOf(TrackingConfig? tracking) => tracking?.Retention ?? new TrackingRetention();

    public static readonly string[] Modes = ["target", "state", "both"];
    public static readonly string[] Primaries = ["target", "state"];

    /// <summary>Checks the words of a section and the combination, naming the key that is wrong. Does not look at what is built.</summary>
    public static void ValidateShape(TrackingConfig? tracking, string replicationName)
    {
        if (tracking is null) return;
        var mode = tracking.Mode?.Trim().ToLowerInvariant();
        var primary = tracking.Primary?.Trim().ToLowerInvariant();
        if (mode is null) throw new ConfigValidationException($"Replication '{replicationName}': `tracking` needs a `mode` (target, state or both).");
        if (!Modes.Contains(mode)) throw new ConfigValidationException($"Replication '{replicationName}': tracking mode '{tracking.Mode}' is not one of target, state, both.");
        if (primary is not null && !Primaries.Contains(primary)) throw new ConfigValidationException($"Replication '{replicationName}': tracking primary '{tracking.Primary}' is not one of target, state.");
        if (mode == "both" && primary is null) throw new ConfigValidationException($"Replication '{replicationName}': tracking mode 'both' keeps two copies, so it must say which one is the primary (target or state).");
        if (tracking.Retention is { } retention)
        {
            if (mode == "state") throw new ConfigValidationException($"Replication '{replicationName}': `tracking.retention` prunes the state table on the target, and mode 'state' keeps none; remove it or use mode target or both.");
            if (retention.Entries is < 1) throw new ConfigValidationException($"Replication '{replicationName}': `tracking.retention.entries` is {retention.Entries}; it keeps at least 1 entry per mapping.");
            if (retention.Days is < 0) throw new ConfigValidationException($"Replication '{replicationName}': `tracking.retention.days` is {retention.Days}; it cannot be negative.");
        }

        if (mode != "both" && primary is not null) throw new ConfigValidationException($"Replication '{replicationName}': tracking mode '{mode}' keeps one side only, so it takes no primary (it was given '{tracking.Primary}').");
    }

    /// <summary>
    /// The save-time check: the shape, and that the mode can be honored by the replication's target engine when it is known (<paramref name="targetEngine"/>: the driver of the
    /// replication's target connection, null when the file names none at its level or the connection is not there yet; a mapping's own target is checked when the pass runs).
    /// </summary>
    public static void Validate(TrackingConfig? tracking, string replicationName, string? targetEngine = null)
    {
        ValidateShape(tracking, replicationName);
        if (tracking is null || string.Equals(tracking.Mode?.Trim(), "state", StringComparison.OrdinalIgnoreCase)) return;
        if (targetEngine is not null && !TargetWriterEngines.Contains(targetEngine))
            throw new ConfigValidationException($"Replication '{replicationName}': tracking mode '{tracking.Mode}' keeps the state on the target, and {EngineSentence(targetEngine)}; use `mode: state` or leave `tracking` out.");
    }

    private static string EngineSentence(string engine) =>
        TargetsThatCanHoldState.Contains(engine)
            ? $"a {engine} target keeps Sync's state on the Runner only: MySQL, MariaDB and Oracle use `mode: state`, and no writer keeps the position log on them (owner decision of 2026-11-14, plan 28c E17 S3), so a restored target of this engine is not flagged as diverged"
            : $"a {engine} target cannot hold a state table in the entry's transaction (a Flight SQL or DuckDB target)";

    /// <summary>The answer for the screen and the API: what applies, whether it is the default, and why.</summary>
    public static TrackingResolved Resolve(TrackingConfig? tracking, string? targetEngine)
    {
        // Owner decision (E17): no `tracking:` section keeps the runner's state exactly as before and creates nothing on the target; the target is used only when the file asks.
        if (tracking is null) return new("state", null, "state", null, true, "No tracking section: the state is kept in the runner's state, as before. Set `tracking: { mode: target }` or `both` to keep it on the target as well.");
        var wanted = tracking.Mode?.Trim().ToLowerInvariant() ?? "state";
        var wantedPrimary = tracking.Primary?.Trim().ToLowerInvariant();
        const bool defaulted = false;

        if (wanted == "state") return new("state", null, "state", null, defaulted, "The state is kept in the runner's state only, as asked.");
        if (targetEngine is null || !TargetWriterEngines.Contains(targetEngine))
        {
            var why = targetEngine is null
                ? "The target is not known yet, so the state is kept in the runner's state only."
                : $"The target ({targetEngine}): {EngineSentence(targetEngine)}, so the state is kept in the runner's state only.";
            return new(wanted, wantedPrimary, "state", null, defaulted, $"Mode '{wanted}' keeps the state on the target. " + why);
        }

        return new(wanted, wantedPrimary, wanted, wanted == "both" ? wantedPrimary : null, defaulted,
            wanted == "both"
                ? $"The state table is on the target, written in the entry's transaction; the runner's state follows it. The {wantedPrimary} decides where a pass resumes."
                : "The state table is on the target, written in the entry's transaction.");
    }
}
