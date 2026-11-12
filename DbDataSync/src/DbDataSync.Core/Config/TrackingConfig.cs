namespace DbDataSync.Core.Config;

/// <summary>
/// Where a replication's tracking state (its positions, the record of what each unit moved) lives (plan 28c E17, plan 29 section 4a.2): the optional
/// <c>tracking:</c> section of <c>task.yaml</c>, <c>mode: target|state|both</c> and, for <c>both</c>, <c>primary: target|state</c>. Absent means the default
/// (<see cref="TrackingResolution"/> says what that is today and why). An optional key of an open format: a file from before loads, a misspelled key is refused by the strict loader.
/// </summary>
public sealed class TrackingConfig
{
    /// <summary><c>target</c> (the state table on the target, in the unit's transaction), <c>state</c> (the runner's state only) or <c>both</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>The side decisions are made from when <see cref="Mode"/> is <c>both</c>: <c>target</c> or <c>state</c>. Refused for the other modes.</summary>
    public string? Primary { get; set; }
}

/// <summary>What a replication's tracking comes to: the mode asked for (or the default), the one that applies and the reason when they differ.</summary>
public sealed record TrackingResolved(string RequestedMode, string? RequestedPrimary, string EffectiveMode, string? EffectivePrimary, bool Defaulted, string Reason);

/// <summary>
/// Resolves <see cref="TrackingConfig"/> against what the writers can do. The state table on the target needs an <c>IChangeWriter</c> that writes it in the unit's transaction; none does yet
/// (E17's writers are not built), so every replication runs in <c>state</c> mode, the default says so, and a file that asks for <c>target</c> or <c>both</c> is refused at save with a message
/// instead of being accepted and ignored. <see cref="TargetsThatCanHoldState"/> is the list of engines the writers will be built for; a Flight SQL or DuckDB target is never on it.
/// </summary>
public static class TrackingResolution
{
    /// <summary>The engines whose writers can carry a state table in the unit's transaction once built (E17).</summary>
    public static readonly IReadOnlySet<string> TargetsThatCanHoldState = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { DriverIds.MsSql, DriverIds.Postgres, DriverIds.MySql, DriverIds.Oracle };

    /// <summary>Whether the target-state writers exist. False until E17's writers are built; the one switch the validation and the status read.</summary>
    public const bool TargetWritersBuilt = false;

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
        if (mode != "both" && primary is not null) throw new ConfigValidationException($"Replication '{replicationName}': tracking mode '{mode}' keeps one side only, so it takes no primary (it was given '{tracking.Primary}').");
    }

    /// <summary>The save-time check: the shape, and that the mode can be honored (today only <c>state</c>).</summary>
    public static void Validate(TrackingConfig? tracking, string replicationName)
    {
        ValidateShape(tracking, replicationName);
        if (tracking is null || TargetWritersBuilt) return;
        if (!string.Equals(tracking.Mode?.Trim(), "state", StringComparison.OrdinalIgnoreCase))
            throw new ConfigValidationException($"Replication '{replicationName}': tracking mode '{tracking.Mode}' keeps the state on the target, and no writer writes it there yet (plan 28c E17); use `mode: state` or leave `tracking` out.");
    }

    /// <summary>The answer for the screen and the API: what applies, whether it is the default, and why.</summary>
    public static TrackingResolved Resolve(TrackingConfig? tracking, string? targetEngine)
    {
        var wanted = tracking?.Mode?.Trim().ToLowerInvariant() ?? "both";
        var wantedPrimary = tracking is null ? "target" : tracking.Primary?.Trim().ToLowerInvariant();
        var defaulted = tracking is null;
        var engineCan = targetEngine is not null && TargetsThatCanHoldState.Contains(targetEngine);

        if (wanted == "state") return new("state", null, "state", null, defaulted, "The state is kept in the runner's state only, as asked.");
        if (!TargetWritersBuilt)
            return new(wanted, wantedPrimary, "state", null, defaulted, defaulted
                ? "The default is both with the target primary, but no writer keeps the state on the target yet (plan 28c E17), so the state is kept in the runner's state only."
                : $"Mode '{wanted}' keeps the state on the target, and no writer does that yet (plan 28c E17), so the state is kept in the runner's state only.");
        if (!engineCan)
            return new(wanted, wantedPrimary, "state", null, defaulted, $"The target{(targetEngine is null ? "" : $" ({targetEngine})")} cannot hold a state table in the unit's transaction (a Flight SQL or DuckDB target), so the state is kept in the runner's state only.");
        return new(wanted, wantedPrimary, wanted, wanted == "both" ? wantedPrimary : null, defaulted, "The state table is on the target, written in the unit's transaction.");
    }
}
