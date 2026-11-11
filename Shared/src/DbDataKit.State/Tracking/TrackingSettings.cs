using System.Globalization;

namespace DbDataKit.State.Tracking;

/// <summary>Where a connection's tracking state lives (plan 29 section 4a.2).</summary>
public enum TrackingMode
{
    /// <summary>On the target (or the project's tracking connection): today's behaviour, no copy.</summary>
    Target,

    /// <summary>In the unified state database only.</summary>
    State,

    /// <summary>On both: one is the primary, the other is kept up to date by a follower.</summary>
    Both,
}

/// <summary>One of the two sides.</summary>
public enum TrackingSide
{
    Target,
    State,
}

/// <summary>
/// The setting of one connection: a <see cref="Mode"/> and, for <see cref="TrackingMode.Both"/>, the <see cref="Primary"/> side that decisions are made from. Immutable and always valid: <c>Both</c> names a
/// primary, <c>Target</c> and <c>State</c> name none (their one side is the primary by being the only one). The primary of <c>Target</c> and <c>State</c> is read with <see cref="PrimarySide"/>.
/// </summary>
public sealed record TrackingSettings
{
    private TrackingSettings(TrackingMode mode, TrackingSide? primary)
    {
        Mode = mode;
        Primary = primary;
    }

    public TrackingMode Mode { get; }

    /// <summary>The primary of <c>Both</c>; null for the other modes.</summary>
    public TrackingSide? Primary { get; }

    /// <summary>The side decisions are made from, whatever the mode.</summary>
    public TrackingSide PrimarySide => Mode switch
    {
        TrackingMode.Target => TrackingSide.Target,
        TrackingMode.State => TrackingSide.State,
        _ => Primary!.Value,
    };

    /// <summary>Plan 29 section 4a.2's default: both, the target primary.</summary>
    public static TrackingSettings Default { get; } = new(TrackingMode.Both, TrackingSide.Target);

    public static TrackingSettings TargetOnly { get; } = new(TrackingMode.Target, null);

    public static TrackingSettings StateOnly { get; } = new(TrackingMode.State, null);

    public static TrackingSettings Both(TrackingSide primary) => Create(TrackingMode.Both, primary);

    /// <summary>Makes a setting, refusing a combination that means nothing, with a message that names the mode.</summary>
    public static TrackingSettings Create(TrackingMode mode, TrackingSide? primary)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException($"'{mode}' is not a tracking mode (target, state or both).", nameof(mode));
        if (primary is { } side && !Enum.IsDefined(side)) throw new ArgumentException($"'{side}' is not a tracking side (target or state).", nameof(primary));
        return mode switch
        {
            TrackingMode.Both when primary is null => throw new ArgumentException("Tracking mode 'both' keeps two copies, so it must say which one is the primary (target or state).", nameof(primary)),
            TrackingMode.Target when primary is not null => throw new ArgumentException($"Tracking mode 'target' keeps one side only, so it takes no primary (it was given '{primary}').", nameof(primary)),
            TrackingMode.State when primary is not null => throw new ArgumentException($"Tracking mode 'state' keeps one side only, so it takes no primary (it was given '{primary}').", nameof(primary)),
            _ => new TrackingSettings(mode, mode == TrackingMode.Both ? primary : null),
        };
    }

    /// <summary>Reads the two words of a settings file (<c>mode: both</c>, <c>primary: target</c>), naming the one that is wrong.</summary>
    public static TrackingSettings Parse(string? mode, string? primary)
    {
        if (!Enum.TryParse<TrackingMode>(mode, ignoreCase: true, out var parsedMode) || mode is null || int.TryParse(mode, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"Tracking mode '{mode}' is not one of target, state, both.", nameof(mode));
        TrackingSide? parsedPrimary = null;
        if (primary is { Length: > 0 })
        {
            if (!Enum.TryParse<TrackingSide>(primary, ignoreCase: true, out var side) || int.TryParse(primary, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException($"Tracking primary '{primary}' is not one of target, state.", nameof(primary));
            parsedPrimary = side;
        }

        return Create(parsedMode, parsedPrimary);
    }

    /// <summary>
    /// Switches the primary of a <c>Both</c> setting, as a recorded operation: first every follower (one per stream) runs until the copy, which is about to become the primary, is caught up within
    /// <paramref name="timeBudget"/>; if any cannot (a side down, slow, or a stream diverged), nothing changes and the message says which. Then the new setting is returned (the caller stores it) and the
    /// switch is written to the audit when one was given: the refusal too.
    /// </summary>
    /// <param name="followers">The followers of this connection's streams, each copying from the current primary to the other side.</param>
    public async Task<TrackingSwitchResult> SwitchPrimaryAsync(TrackingSide newPrimary, IReadOnlyCollection<TrackingFollower> followers, TimeSpan timeBudget, string actor, string identity, AuditLog? audit = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(followers);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        async Task<TrackingSwitchResult> Refuse(string message)
        {
            if (audit is not null) await RecordAsync(audit, actor, identity, "refused", Mode, Primary, newPrimary, message).ConfigureAwait(false);
            return new TrackingSwitchResult(false, this, message);
        }

        if (Mode != TrackingMode.Both)
            return await Refuse($"The primary can only be switched when tracking keeps both sides; this connection's mode is '{Mode.ToString().ToLowerInvariant()}'. Change the mode first.").ConfigureAwait(false);
        if (Primary == newPrimary)
            return await Refuse($"The {newPrimary.ToString().ToLowerInvariant()} is already the primary.").ConfigureAwait(false);
        if (followers.Any(f => f.Identity != identity))
            return await Refuse($"A follower given belongs to another connection than '{identity}'; a switch is for one connection's identity.").ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + timeBudget;
        foreach (var follower in followers)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var result = await follower.CatchUpAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (result.Diverged)
                return await Refuse($"The copy of '{follower.Stream.Name}' has diverged from the primary ({result.Divergence?.Reason}); resolve that before switching.").ConfigureAwait(false);
            if (result.Error is not null)
                return await Refuse($"The copy of '{follower.Stream.Name}' could not be brought up to date: {result.Error}").ConfigureAwait(false);
            if (!result.CaughtUp)
                return await Refuse($"The copy of '{follower.Stream.Name}' did not catch up within {timeBudget.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds; try again.").ConfigureAwait(false);
        }

        var switched = Both(newPrimary);
        if (audit is not null) await RecordAsync(audit, actor, identity, "ok", Mode, Primary, newPrimary, null).ConfigureAwait(false);
        return new TrackingSwitchResult(true, switched, null);
    }

    private static Task RecordAsync(AuditLog audit, string actor, string identity, string outcome, TrackingMode mode, TrackingSide? from, TrackingSide to, string? message)
    {
        var detail = $"{{\"mode\":\"{mode.ToString().ToLowerInvariant()}\",\"from\":\"{from?.ToString().ToLowerInvariant()}\",\"to\":\"{to.ToString().ToLowerInvariant()}\"" +
                     (message is null ? "}" : $",\"reason\":\"{TrackingFollower.Escape(message)}\"}}");
        audit.Record(new AuditEvent("person", actor, "tracking.switch_primary", outcome, TargetKind: "connection", TargetId: identity, Detail: detail));
        return Task.CompletedTask;
    }
}

/// <summary>The outcome of a switch: the setting to store (the new one when <see cref="Switched"/>, the unchanged one otherwise) and why a refusal was one.</summary>
public sealed record TrackingSwitchResult(bool Switched, TrackingSettings Settings, string? Message);
