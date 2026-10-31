using System.Diagnostics;
using System.Runtime.InteropServices;
using DbDataKit.Scripting;

namespace DbDataSync.Libraries;

/// <summary>
/// Restores a library package (and its full transitive closure — managed dependencies and any
/// <c>runtimes/&lt;rid&gt;/native/</c> assets) into <c>&lt;repo&gt;/libraries/&lt;id&gt;/lib/</c>, using
/// nothing but the <c>dotnet</c> muxer <see cref="System.Diagnostics.ProcessStartInfo"/> already runs
/// everywhere else in this solution — no NuGet client library in the host.
/// <para>
/// The mechanism is a throwaway SDK-style class-library project referencing every package in the
/// manifest, published (not merely restored) so the SDK's own build copies the full dependency closure —
/// managed DLLs, <c>.deps.json</c>, and any native <c>runtimes/</c> assets — into one flat output
/// directory, which is copied verbatim into <c>lib/</c>.
/// </para>
/// </summary>
public static class LibraryInstaller
{
    /// <summary>
    /// Installs (or reinstalls) one library: its packages are resolved into <c>libraries/&lt;id&gt;/lib/</c> (a feed, <see cref="LibraryResolution.DefaultSource"/> unless <paramref name="nugetSource"/> names one), their hashes go to
    /// <c>library.lock</c>, and <c>library.csx</c> is written. <paramref name="factoryType"/> null means neither the caller nor <see cref="KnownLibraries"/> could name one up front, so the resolved closure is scanned for a single public
    /// <see cref="System.Data.Common.DbProviderFactory"/> subclass (see <see cref="FactoryTypeReflector"/>) before giving up. The closure has to exist on disk before there is anything to scan.
    /// </summary>
    public static async Task<LibraryManifest> InstallAsync(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string? factoryType,
        string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var libDir = LibraryPaths.LibDir(libraryDir);
        Directory.CreateDirectory(libraryDir);
        LibraryPaths.EnsureStoreGitignored(repoRoot);

        // A fresh store each install: a version downgrade or a dropped transitive dependency must not leave an old package behind. The lock keeps the pins of what stays.
        if (Directory.Exists(libDir)) Directory.Delete(libDir, recursive: true);
        Directory.CreateDirectory(libDir);

        Closure closure;
        try
        {
            closure = await LibraryResolution.RestoreAsync(libraryDir, packages, nugetSource, cancellationToken);
            // DuckDB.NET.Data has managed bindings only; the engine comes from the repository's pin (duckdb.pin), hash-checked.
            if (DuckDbEngine.Needs(packages))
                await DuckDbEngine.EnsureAsync(libDir, cancellationToken);
        }
        catch
        {
            CleanUpFailedInstall(libraryDir);
            throw;
        }

        if (factoryType is null)
        {
            var discovery = FactoryTypeReflector.Discover(closure.Managed.Values.ToList());
            if (discovery.Status != FactoryTypeDiscoveryStatus.Found)
            {
                // Nothing usable under this id — clean up rather than leave a lib/ with no manifest,
                // the same "nothing installed" outcome any other failed install leaves behind.
                CleanUpFailedInstall(libraryDir);
                throw new InvalidOperationException(discovery.Status == FactoryTypeDiscoveryStatus.Ambiguous
                    ? $"'{id}' has more than one DbProviderFactory — pass factoryType explicitly."
                    : $"Could not find a DbProviderFactory in '{id}'. Pass factoryType explicitly.");
            }

            factoryType = discovery.FactoryType;
        }

        var manifest = new LibraryManifest(id, factoryType!, packages);
        manifest.Write(LibraryPaths.ManifestPath(libraryDir));
        return manifest;
    }

    // a library that was not there before is removed; one that was keeps its script (and loses only the store that failed to refresh)
    private static void CleanUpFailedInstall(string libraryDir)
    {
        if (!File.Exists(LibraryPaths.ManifestPath(libraryDir))) { try { Directory.Delete(libraryDir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>Re-runs the restore for an already-written manifest — a fresh deployment, or after
    /// hand-editing a package version in <c>library.csx</c>. Also what completes the
    /// "pending restore" state: <see cref="InstallAsync"/> restores into <c>lib/</c> unconditionally,
    /// whether or not one was already there, so a manifest written with no <c>lib/</c> at all (because
    /// no SDK was available at install time) restores for the first time exactly the same way a
    /// re-sync of a normal library does.</summary>
    public static async Task SyncAsync(string repoRoot, string id, CancellationToken cancellationToken = default)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var manifest = LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir));
        await InstallAsync(repoRoot, id, manifest.Packages, manifest.FactoryType.Length > 0 ? manifest.FactoryType : null, cancellationToken: cancellationToken);
    }

    /// <summary>The outcome of <see cref="InstallOrDeferAsync"/>.</summary>
    public enum LibraryInstallOutcome
    {
        /// <summary>Resolved from a feed into the library's store.</summary>
        Installed,
        /// <summary>The exact package and pinned version of a catalog entry was found in the image's catalog cache and copied from there: no network.</summary>
        InstalledFromCache,
    }

    public sealed record LibraryInstallResult(LibraryManifest Manifest, LibraryInstallOutcome Outcome);

    /// <summary>
    /// The runtime-only image's entry point: a catalog id at its <see cref="KnownLibraries"/>-pinned version is copied from <paramref name="cacheRoot"/> when the image carries it (no network), anything else is
    /// <see cref="InstallAsync"/>. Nothing needs the .NET SDK any more, so there is no deferred "pending restore": an install either completes or fails with the reason.
    /// </summary>
    public static async Task<LibraryInstallResult> InstallOrDeferAsync(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string? factoryType,
        string? cacheRoot = null, string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        var cache = cacheRoot ?? DefaultCacheRoot;
        var catalogMatch = packages.Count == 1 && nugetSource is null
            ? KnownLibraries.All.FirstOrDefault(e =>
                string.Equals(e.PackageId, packages[0].Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.PinnedVersion, packages[0].Version, StringComparison.OrdinalIgnoreCase))
            : null;
        var cacheLibraryDir = catalogMatch is null ? null : LibraryPaths.LibraryDir(cache, catalogMatch.Id);

        if (cacheLibraryDir is not null && Directory.Exists(LibraryPaths.LibDir(cacheLibraryDir)))
        {
            var manifest = CopyFromCache(cacheLibraryDir, repoRoot, id, packages, factoryType ?? catalogMatch!.FactoryType);
            return new(manifest, LibraryInstallOutcome.InstalledFromCache);
        }

        var installed = await InstallAsync(repoRoot, id, packages, factoryType, nugetSource, cancellationToken);
        return new(installed, LibraryInstallOutcome.Installed);
    }

    /// <summary>Where <c>internal build-catalog-cache</c> writes and <see cref="InstallOrDeferAsync"/>
    /// reads by default — the runtime-only image's own copy of itself, so neither side needs new
    /// configuration to agree on it. Overridable (both directions) for tests.</summary>
    public const string DefaultCacheRoot = "/app/library-cache";

    private static LibraryManifest CopyFromCache(
        string cacheLibraryDir, string repoRoot, string id, IReadOnlyList<PackageRef> packages, string factoryType)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var libDir = LibraryPaths.LibDir(libraryDir);
        LibraryPaths.EnsureStoreGitignored(repoRoot);
        if (Directory.Exists(libDir))
            Directory.Delete(libDir, recursive: true);
        Directory.CreateDirectory(libDir);
        CopyAll(LibraryPaths.LibDir(cacheLibraryDir), libDir);
        var cachedLock = LibraryResolution.LockPath(cacheLibraryDir);
        if (File.Exists(cachedLock)) File.Copy(cachedLock, LibraryResolution.LockPath(libraryDir), overwrite: true);

        var manifest = new LibraryManifest(id, factoryType, packages);
        manifest.Write(LibraryPaths.ManifestPath(libraryDir));
        return manifest;
    }

    private static void CopyAll(string sourceDir, string destDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }
}
