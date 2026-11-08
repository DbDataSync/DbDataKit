using System.Collections.Concurrent;
using DbDataKit.State;

namespace DbDataKit.Runner.Scheduler;

/// <summary>A published version of a package: its manifest and the files to materialize (path, hash, size and where the bytes are).</summary>
public sealed record PackageVersionContent(PackageManifest Manifest, IReadOnlyList<ArtifactFile> Files);

/// <summary>A runtime package version a definition resolved to: the runtime's name, its definition version number, its own version (<c>2.1.0</c>), and its manifest and files.</summary>
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
    /// The active version of the runtime package <paramref name="reference"/> names in <paramref name="channel"/>, when it is in the reference's range (plan 28c E04). Throws <see cref="RuntimeUnavailableException"/> naming the runtime,
    /// the range, the channel and what the channel has otherwise. A source with no runtimes refuses every reference.
    /// </summary>
    Task<ResolvedRuntime> ResolveRuntimeAsync(RuntimeReference reference, string channel, string forPackage, CancellationToken ct = default) =>
        throw new RuntimeUnavailableException($"'{forPackage}' needs the runtime '{reference.Name}' in the range '{reference.Range}', and this runner has no runtime packages.");
}

/// <summary>Versions held in memory.</summary>
public sealed class InMemoryVersionSource : IVersionSource
{
    private readonly ConcurrentDictionary<(string Package, string Version), PackageVersionContent> _versions = new();

    /// <summary>Counts the lookups, per package and version (tests).</summary>
    public ConcurrentDictionary<(string Package, string Version), int> Lookups { get; } = new();

    private readonly ConcurrentDictionary<(string Package, string Channel), string> _activeRuntimes = new();

    /// <summary>Makes <paramref name="version"/> of the runtime <paramref name="package"/> the active one of <paramref name="channel"/> (tests).</summary>
    public InMemoryVersionSource ActivateRuntime(string package, string version, string channel) { _activeRuntimes[(package, channel)] = version; return this; }

    public Task<ResolvedRuntime> ResolveRuntimeAsync(RuntimeReference reference, string channel, string forPackage, CancellationToken ct = default)
    {
        _activeRuntimes.TryGetValue((reference.Name, channel), out var version);
        PackageVersionContent? content = version is null ? null : _versions.GetValueOrDefault((reference.Name, version));
        return Task.FromResult(RuntimeResolution.Check(reference, channel, forPackage, version, content?.Manifest, content));
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

    public Task<ResolvedRuntime> ResolveRuntimeAsync(RuntimeReference reference, string channel, string forPackage, CancellationToken ct = default) =>
        Task.FromResult(RuntimeResolution.Resolve(store, publisher, reference, channel, forPackage));

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
/// How a <c>launch.runtime</c> reference becomes a runtime package version (plan 28c E04): the runtime's active version in the channel, which has to be a runtime package and inside the range. The refusal names the runtime, the
/// range and the channel, the same kind of message <c>RunnerContracts</c> gives for a contract the runner lacks.
/// </summary>
public static class RuntimeResolution
{
    /// <summary>The active version of the runtime in the channel, read from the definition store.</summary>
    public static ResolvedRuntime Resolve(DefinitionStore store, DefinitionPublisher publisher, RuntimeReference reference, string channel, string forPackage)
    {
        var active = publisher.Head(DefinitionVersionSource.Kind, reference.Name, channel)?.Active;
        PackageVersionContent? content = null;
        if (active is { } number && store.Get(DefinitionVersionSource.Kind, reference.Name, number) is { } definition)
        {
            var manifest = PackageManifestReader.Load(definition.Content, $"{DefinitionVersionSource.Kind}/{reference.Name}@{number}").ThrowIfInvalid();
            IReadOnlyList<ArtifactFile> files = [.. publisher.Artifacts(DefinitionVersionSource.Kind, reference.Name, number).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))];
            content = new PackageVersionContent(manifest, files);
        }

        return Check(reference, channel, forPackage, active?.ToString(System.Globalization.CultureInfo.InvariantCulture), content?.Manifest, content);
    }

    /// <summary>The decision, given what the channel has (null: no active version).</summary>
    public static ResolvedRuntime Check(RuntimeReference reference, string channel, string forPackage, string? version, PackageManifest? manifest, PackageVersionContent? content)
    {
        var needs = $"'{forPackage}' needs the runtime '{reference.Name}' in the range '{reference.Range}'";
        if (version is null || manifest is null || content is null)
            throw new RuntimeUnavailableException($"{needs}, and channel '{channel}' has no active version of '{reference.Name}'; publish and activate the runtime there first.");
        if (!manifest.IsRuntime)
            throw new RuntimeUnavailableException($"{needs}, and '{reference.Name}' in channel '{channel}' is not a runtime package (it has no `runtime: true`).");
        if (!VersionRange.TryParse(reference.Range, out var range, out var problem))
            throw new RuntimeUnavailableException($"{needs}, which is not a range: {problem}");
        var have = manifest.Version ?? "?";
        if (!range.Contains(have))
            throw new RuntimeUnavailableException($"{needs}, and channel '{channel}' has version {have} of '{reference.Name}' active, which is outside the range; activate a runtime in the range or publish the definition for {have}.");
        return new ResolvedRuntime(reference.Name, version, have, content);
    }
}
