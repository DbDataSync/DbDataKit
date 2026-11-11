using System.Collections.Concurrent;
using DbDataKit.State;

namespace DbDataKit.Runner.Scheduler;

/// <summary>A published version of a package: its manifest and the files to materialize (path, hash, size and where the bytes are).</summary>
public sealed record PackageVersionContent(PackageManifest Manifest, IReadOnlyList<ArtifactFile> Files);

/// <summary>A runtime package version a definition resolved to: the runtime's package (<c>name-platform</c>, as the registry stores it), its definition version number, its own version (<c>2.1.0</c>), and its manifest and files.</summary>
public sealed record ResolvedRuntime(string Package, string Version, string RuntimeVersion, PackageVersionContent Content);

/// <summary>A definition's runtime reference cannot be met in the channel; the message names the runtime, the range and what the channel has.</summary>
public sealed class RuntimeUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Where the scheduler gets a package version from: the manifest (for the task's launch, limits and overlap rule) and the files for the artifact cache. The real source is plan 30's definition store
/// (<see cref="DefinitionVersionSource"/>); <see cref="InMemoryVersionSource"/> serves tests.
/// </summary>
public interface IVersionSource
{
    /// <summary>The version's content, or null when the source has no such version. Throws for a version that exists and cannot be read (a manifest that does not validate): the scheduler fails the run naming the message.</summary>
    Task<PackageVersionContent?> GetAsync(string package, string version, string channel, CancellationToken ct = default);

    /// <summary>
    /// The active version of the runtime package <paramref name="reference"/> names, built for <paramref name="platform"/> (the runner's own), in <paramref name="channel"/>, when it is in the reference's range (plan 28c E04). Throws
    /// <see cref="RuntimeUnavailableException"/> naming the runtime, the range, the platform, the channel and what the channel has otherwise. A source with no runtimes refuses every reference.
    /// </summary>
    Task<ResolvedRuntime> ResolveRuntimeAsync(RuntimeReference reference, string channel, string forPackage, string platform, CancellationToken ct = default) =>
        throw new RuntimeUnavailableException($"'{forPackage}' needs the runtime '{reference.Name}' in the range '{reference.Range}', and this runner has no runtime packages.");
}

/// <summary>Versions held in memory.</summary>
public sealed class InMemoryVersionSource : IVersionSource
{
    private readonly ConcurrentDictionary<(string Package, string Version), PackageVersionContent> _versions = new();

    /// <summary>Counts the lookups, per package and version (tests).</summary>
    public ConcurrentDictionary<(string Package, string Version), int> Lookups { get; } = new();

    private readonly ConcurrentDictionary<(string Package, string Channel), string> _activeRuntimes = new();

    /// <summary>Makes <paramref name="version"/> of the runtime package <paramref name="package"/> (its registry name, <see cref="PackageManifest.PackageName"/>) the active one of <paramref name="channel"/> (tests).</summary>
    public InMemoryVersionSource ActivateRuntime(string package, string version, string channel) { _activeRuntimes[(package, channel)] = version; return this; }

    public Task<ResolvedRuntime> ResolveRuntimeAsync(RuntimeReference reference, string channel, string forPackage, string platform, CancellationToken ct = default)
    {
        var package = RuntimePlatforms.PackageName(reference.Name, platform);
        _activeRuntimes.TryGetValue((package, channel), out var version);
        PackageVersionContent? content = version is null ? null : _versions.GetValueOrDefault((package, version));
        if (version is null)
        {
            var others = RuntimePlatforms.Supported.Where(p => p != platform && _activeRuntimes.ContainsKey((RuntimePlatforms.PackageName(reference.Name, p), channel)))
                .Select(p => (p, _activeRuntimes[(RuntimePlatforms.PackageName(reference.Name, p), channel)])).ToList();
            var unplatformed = _activeRuntimes.TryGetValue((reference.Name, channel), out var legacy) ? _versions.GetValueOrDefault((reference.Name, legacy))?.Manifest : null;
            RuntimeResolution.ThrowNone(reference, channel, forPackage, platform, [.. others.Select(o => $"{o.p} (version {_versions.GetValueOrDefault((RuntimePlatforms.PackageName(reference.Name, o.p), o.Item2))?.Manifest.Version ?? "?"})")], unplatformed is { IsRuntime: true } && unplatformed.Platform is null);
        }

        return Task.FromResult(RuntimeResolution.Check(reference, channel, forPackage, platform, package, version, content?.Manifest, content));
    }

    public InMemoryVersionSource Add(string package, string version, PackageManifest manifest, IReadOnlyList<ArtifactFile>? files = null)
    {
        _versions[(package, version)] = new PackageVersionContent(manifest, files ?? []);
        return this;
    }

    public Task<PackageVersionContent?> GetAsync(string package, string version, string channel, CancellationToken ct = default)
    {
        Lookups.AddOrUpdate((package, version), 1, (_, n) => n + 1);
        return Task.FromResult(_versions.TryGetValue((package, version), out var content) ? content : null);
    }
}

/// <summary>
/// The versions of kind <c>integration</c> in the definition store (plan 30, R15): a package is a definition named after it, a version is its number as text (<c>"7"</c>), the manifest is the version's content and the files are its
/// artifacts (their payload ids are what the artifact cache's source opens). The channel is not needed to find a version: a version belongs to the definition, the channel says which one serves.
/// </summary>
public sealed class DefinitionVersionSource(DefinitionStore store, DefinitionPublisher publisher) : IVersionSource
{
    /// <summary>The definition kind of a runner package.</summary>
    public const string Kind = "integration";

    public Task<ResolvedRuntime> ResolveRuntimeAsync(RuntimeReference reference, string channel, string forPackage, string platform, CancellationToken ct = default) =>
        Task.FromResult(RuntimeResolution.Resolve(store, publisher, reference, channel, forPackage, platform));

    public Task<PackageVersionContent?> GetAsync(string package, string version, string channel, CancellationToken ct = default)
    {
        if (!int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            throw new ArtifactException($"'{version}' is not a version number of the package '{package}'; a definition's versions are whole numbers.");
        var definition = store.Get(Kind, package, number);
        if (definition is null) return Task.FromResult<PackageVersionContent?>(null);
        // a version in a format this runner does not read is refused naming the package, the version and both numbers (24a UC01)
        if (DefinitionFormat.Refusal(Kind, package, number, definition.Format, publisher.FormatsOf(Kind), "this runner") is { } unreadable) throw new ArtifactException(unreadable);

        var manifest = PackageManifestReader.Load(definition.Content, $"{Kind}/{package}@{number}").ThrowIfInvalid();
        IReadOnlyList<ArtifactFile> files = [.. publisher.Artifacts(Kind, package, number).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))];
        return Task.FromResult<PackageVersionContent?>(new PackageVersionContent(manifest, files));
    }
}

/// <summary>
/// How a <c>launch.runtime</c> reference becomes a runtime package version (plan 28c E04): the active version in the channel of the runtime built for the runner's own platform (a runtime is published once per platform, stored as
/// <c>&lt;name&gt;-&lt;platform&gt;</c>), which has to be a runtime package and inside the range. The refusal names the runtime, the range, the platform and the channel, and the platforms the channel has the runtime for.
/// </summary>
public static class RuntimeResolution
{
    /// <summary>The active version of the runtime for <paramref name="platform"/> in the channel, read from the definition store.</summary>
    public static ResolvedRuntime Resolve(DefinitionStore store, DefinitionPublisher publisher, RuntimeReference reference, string channel, string forPackage, string platform)
    {
        var package = RuntimePlatforms.PackageName(reference.Name, platform);
        var active = publisher.Head(DefinitionVersionSource.Kind, package, channel)?.Active;
        PackageVersionContent? content = null;
        if (active is { } number && store.Get(DefinitionVersionSource.Kind, package, number) is { } definition)
        {
            var manifest = PackageManifestReader.Load(definition.Content, $"{DefinitionVersionSource.Kind}/{package}@{number}").ThrowIfInvalid();
            IReadOnlyList<ArtifactFile> files = [.. publisher.Artifacts(DefinitionVersionSource.Kind, package, number).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))];
            content = new PackageVersionContent(manifest, files);
        }

        if (content is null)
        {
            // the platforms the channel does have the runtime for, and a runtime published under the bare name before runtimes carried a platform
            var others = new List<string>();
            foreach (var other in RuntimePlatforms.Supported.Where(p => p != platform))
                if (publisher.Head(DefinitionVersionSource.Kind, RuntimePlatforms.PackageName(reference.Name, other), channel)?.Active is { } otherActive
                    && store.Get(DefinitionVersionSource.Kind, RuntimePlatforms.PackageName(reference.Name, other), otherActive)?.Content is { } text
                    && PackageManifestReader.Load(text, "manifest") is { Manifest: { } otherManifest })
                    others.Add($"{other} (version {otherManifest.Version ?? "?"})");
            // (read as text: such a manifest no longer validates, so the reader gives no model for it)
            var unplatformed = publisher.Head(DefinitionVersionSource.Kind, reference.Name, channel)?.Active is { } bare
                && store.Get(DefinitionVersionSource.Kind, reference.Name, bare)?.Content is { } bareText
                && System.Text.RegularExpressions.Regex.IsMatch(bareText, @"^runtime:\s*true\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                && !System.Text.RegularExpressions.Regex.IsMatch(bareText, @"^platform:", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            ThrowNone(reference, channel, forPackage, platform, others, unplatformed);
        }

        return Check(reference, channel, forPackage, platform, package, active?.ToString(System.Globalization.CultureInfo.InvariantCulture), content?.Manifest, content);
    }

    /// <summary>The refusal when the channel has no active runtime of this name for this platform.</summary>
    public static void ThrowNone(RuntimeReference reference, string channel, string forPackage, string platform, IReadOnlyList<string> otherPlatforms, bool unplatformedRuntime)
    {
        var needs = $"'{forPackage}' needs the runtime '{reference.Name}' in the range '{reference.Range}' for the platform {platform}";
        if (unplatformedRuntime)
            throw new RuntimeUnavailableException($"{needs}, and channel '{channel}' has the package '{reference.Name}' active, a runtime package with no `platform:` (it was published before runtime packages carried one); add `platform: <rid>` to its package.yaml and publish it again once per platform ({string.Join(", ", RuntimePlatforms.Supported)}), then activate the one for {platform}.");
        var have = otherPlatforms.Count > 0 ? $"it has it for {string.Join(", ", otherPlatforms)} only" : "it has no active version of it for any platform";
        throw new RuntimeUnavailableException($"{needs}, and channel '{channel}' has no active version for {platform}: {have}; publish and activate the runtime built for {platform} there first.");
    }

    /// <summary>The decision, given what the channel has (null: no active version).</summary>
    public static ResolvedRuntime Check(RuntimeReference reference, string channel, string forPackage, string platform, string package, string? version, PackageManifest? manifest, PackageVersionContent? content)
    {
        var needs = $"'{forPackage}' needs the runtime '{reference.Name}' in the range '{reference.Range}' for the platform {platform}";
        if (version is null || manifest is null || content is null)
            throw new RuntimeUnavailableException($"{needs}, and channel '{channel}' has no active version of '{reference.Name}' for {platform}; publish and activate the runtime there first.");
        if (!manifest.IsRuntime)
            throw new RuntimeUnavailableException($"{needs}, and '{package}' in channel '{channel}' is not a runtime package (it has no `runtime: true`).");
        if (manifest.Platform != platform)
            throw new RuntimeUnavailableException($"{needs}, and '{package}' in channel '{channel}' says it is built for {manifest.Platform ?? "no platform"}; publish the runtime built for {platform} under its own platform.");
        if (!VersionRange.TryParse(reference.Range, out var range, out var problem))
            throw new RuntimeUnavailableException($"{needs}, which is not a range: {problem}");
        var have = manifest.Version ?? "?";
        if (!range.Contains(have))
            throw new RuntimeUnavailableException($"{needs}, and channel '{channel}' has version {have} of '{reference.Name}' for {platform} active, which is outside the range; activate a runtime in the range or publish the definition for {have}.");
        return new ResolvedRuntime(package, version, have, content);
    }
}
