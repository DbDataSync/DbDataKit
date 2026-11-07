using System.Collections.Concurrent;
using DbDataKit.State;

namespace DbDataKit.Runner.Scheduler;

/// <summary>A published version of a package: its manifest and the files to materialize (path, hash, size and where the bytes are).</summary>
public sealed record PackageVersionContent(PackageManifest Manifest, IReadOnlyList<ArtifactFile> Files);

/// <summary>
/// Where the scheduler gets a package version from: the manifest (for the task's launch, limits and overlap rule) and the files for the artifact cache. The real source is plan 30's definition store
/// (<see cref="DefinitionVersionSource"/>); <see cref="InMemoryVersionSource"/> serves tests.
/// </summary>
public interface IVersionSource
{
    /// <summary>The version's content, or null when the source has no such version. Throws for a version that exists and cannot be read (a manifest that does not validate): the scheduler fails the run naming the message.</summary>
    Task<PackageVersionContent?> GetAsync(string package, string version, string channel, CancellationToken ct = default);
}

/// <summary>Versions held in memory.</summary>
public sealed class InMemoryVersionSource : IVersionSource
{
    private readonly ConcurrentDictionary<(string Package, string Version), PackageVersionContent> _versions = new();

    /// <summary>Counts the lookups, per package and version (tests).</summary>
    public ConcurrentDictionary<(string Package, string Version), int> Lookups { get; } = new();

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

    public Task<PackageVersionContent?> GetAsync(string package, string version, string channel, CancellationToken ct = default)
    {
        if (!int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            throw new ArtifactException($"'{version}' is not a version number of the package '{package}'; a definition's versions are whole numbers.");
        var definition = store.Get(Kind, package, number);
        if (definition is null) return Task.FromResult<PackageVersionContent?>(null);

        var manifest = PackageManifestReader.Load(definition.Content, $"{Kind}/{package}@{number}").ThrowIfInvalid();
        IReadOnlyList<ArtifactFile> files = [.. publisher.Artifacts(Kind, package, number).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))];
        return Task.FromResult<PackageVersionContent?>(new PackageVersionContent(manifest, files));
    }
}
