namespace DbDataKit.Update;

public enum UpdateDecisionKind { UpToDate, UpdateAvailable, Mandatory, Blocked }

public enum BlockReason { None, WrongProduct, ChannelNotAllowed, Revoked, Downgrade, BelowMinimumSafe, UpdaterTooOld }

public sealed record UpdateDecision(UpdateDecisionKind Kind, BlockReason Reason, ReleaseManifest? Manifest, string Message, bool IsDowngrade = false)
{
    public bool CanApply => Kind is UpdateDecisionKind.UpdateAvailable or UpdateDecisionKind.Mandatory;
}

public sealed record UpdateCheckOptions
{
    /// <summary>Least stable channel this install follows. Stable installs never see beta/snapshot.</summary>
    public Channel Channel { get; init; } = Channel.Stable;
    /// <summary>Only an interactive administrator should ever set this.</summary>
    public bool AllowDowngrade { get; init; }
    public string? UpdaterVersion { get; init; }
    /// <summary>Highest minimumSafeVersion this install has ever seen in a verified manifest; blocks replay of older manifests.</summary>
    public string? KnownMinimumSafeVersion { get; init; }
}

public sealed class UpdateChecker(string product, IContentFetcher fetcher, TrustStore trust)
{
    /// <summary>Pure policy over an already verified manifest.</summary>
    public static UpdateDecision Evaluate(ReleaseManifest m, ReleaseVersion installed, UpdateCheckOptions options, string product)
    {
        UpdateDecision Block(BlockReason r, string msg) => new(UpdateDecisionKind.Blocked, r, m, msg);
        var candidate = m.ParsedVersion;

        if (!string.Equals(m.Product, product, StringComparison.OrdinalIgnoreCase)) return Block(BlockReason.WrongProduct, $"Manifest is for '{m.Product}'.");
        if (m.Revoked) return Block(BlockReason.Revoked, $"Release {m.Version} has been revoked.");
        if (m.ParsedChannel > options.Channel) return Block(BlockReason.ChannelNotAllowed, $"Release is on channel '{m.Channel}', install follows '{options.Channel}'.");
        if (options.KnownMinimumSafeVersion is not null && ReleaseVersion.TryParse(options.KnownMinimumSafeVersion, out var known) && candidate < known)
            return Block(BlockReason.BelowMinimumSafe, $"{m.Version} is below the minimum safe version {known}.");
        if (m.MinUpdater is not null && options.UpdaterVersion is not null
            && TryParseLoose(m.MinUpdater, out var minU) && TryParseLoose(options.UpdaterVersion, out var haveU) && haveU < minU)
            return Block(BlockReason.UpdaterTooOld, $"Release requires updater {m.MinUpdater}; this is {options.UpdaterVersion}.");

        var cmp = candidate.CompareTo(installed);
        if (cmp == 0) return new(UpdateDecisionKind.UpToDate, BlockReason.None, m, "Up to date.");
        if (cmp < 0)
            return options.AllowDowngrade
                ? new(UpdateDecisionKind.UpdateAvailable, BlockReason.None, m, $"Downgrade to {m.Version} explicitly allowed.", true)
                : Block(BlockReason.Downgrade, $"{m.Version} is older than installed {installed}; downgrade refused.");

        var mandatory = (m.MinimumSafeVersion is not null && installed < ReleaseVersion.Parse(m.MinimumSafeVersion))
                        || m.Supersedes.Any(s => ReleaseVersion.Parse(s).Equals(installed));
        return mandatory
            ? new(UpdateDecisionKind.Mandatory, BlockReason.None, m, $"{installed} is unsafe or superseded; update to {m.Version} required.")
            : new(UpdateDecisionKind.UpdateAvailable, BlockReason.None, m, $"Update available: {m.Version}.");
    }

    /// <summary>Accepts "1.0" or "1" as well as full semver.</summary>
    public static bool TryParseLoose(string text, out ReleaseVersion version)
    {
        var core = text.Split('-', '+')[0];
        var n = core.Count(c => c == '.');
        var padded = n switch { 0 => text + ".0.0", 1 => text.Split('-', '+')[0] + ".0", _ => text };
        return ReleaseVersion.TryParse(padded, out version!);
    }

    /// <summary>
    /// List -> pick the highest allowed version -> fetch and verify its manifest -> evaluate.
    /// Verification failures throw <see cref="UpdateVerificationException"/> (fail closed; tampering is not "no update").
    /// </summary>
    public async Task<UpdateDecision> CheckAsync(IReleaseSource source, ReleaseVersion installed, UpdateCheckOptions options, CancellationToken ct = default)
    {
        var refs = await source.ListAsync(ct).ConfigureAwait(false);
        var signed = refs.Where(r => !r.IsUnsignedHint)
            .Select(r => (Ref: r, Ver: ReleaseVersion.TryParse(r.Version, out var v) ? v : null))
            .Where(x => x.Ver is null ? x.Ref.Version.Length == 0 : x.Ver.Channel <= options.Channel)
            .OrderByDescending(x => x.Ver, Comparer<ReleaseVersion?>.Create((a, b) => a is null ? (b is null ? 0 : 1) : a.CompareTo(b)))
            .ToList();
        if (signed.Count == 0) return new(UpdateDecisionKind.UpToDate, BlockReason.None, null, "No signed releases found.");
        var pick = signed[0];
        var verified = await FetchVerifiedAsync(pick.Ref, ct).ConfigureAwait(false);
        if (pick.Ver is not null && !verified.Manifest.ParsedVersion.Equals(pick.Ver))
            throw new UpdateVerificationException(VerificationFailure.VersionMismatch, $"Source advertised {pick.Ver} but the signed manifest says {verified.Manifest.Version}.");
        return Evaluate(verified.Manifest, installed, options, product);
    }

    public async Task<VerifiedManifest> FetchVerifiedAsync(ReleaseRef r, CancellationToken ct = default)
    {
        if (r.ManifestLocation is null || r.SignatureLocation is null) throw new InvalidOperationException("Release has no signed manifest.");
        var bytes = await fetcher.ReadAllBytesAsync(r.ManifestLocation, ReleaseVerifier.MaxManifestBytes, ct).ConfigureAwait(false);
        var sig = System.Text.Encoding.UTF8.GetString(await fetcher.ReadAllBytesAsync(r.SignatureLocation, 4096, ct).ConfigureAwait(false));
        return ReleaseVerifier.VerifyManifest(bytes, sig, trust, product);
    }
}

public enum PolicyAction { None, Notify, AwaitApproval, ApplyNow }

public static class UpdatePolicy
{
    /// <summary>Maps mode + decision (+ window) to what the host may do. Nothing applies unless the decision allows it.</summary>
    public static PolicyAction Decide(UpdateMode mode, UpdateDecision decision, DateTimeOffset localNow, UpdateWindow? window = null)
    {
        if (mode == UpdateMode.Disabled || !decision.CanApply) return PolicyAction.None;
        return mode switch
        {
            UpdateMode.Notify => PolicyAction.Notify,
            UpdateMode.Approve => PolicyAction.AwaitApproval,
            UpdateMode.Auto => window is null || window.Contains(localNow) ? PolicyAction.ApplyNow : PolicyAction.Notify,
            _ => PolicyAction.None,
        };
    }
}
