using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// The runtime-only image's entry point, <see cref="LibraryInstaller.InstallOrDeferAsync"/>: a catalog package at its pinned version is copied from the image's cache (no network); anything else is
/// resolved from a feed. Nothing needs the .NET SDK any more, so there is no deferred state: an install completes or fails with the reason.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibraryInstallOrDeferTests : IAsyncLifetime
{
    private static readonly LibraryCatalogEntry MySqlConnector = KnownLibraries.TryGetById("mysql-connector")!;

    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-installordefer-repo-{Guid.NewGuid():N}");
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-installordefer-cache-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_cacheRoot);
        // A real one-entry cache — the pinned catalog package, restored for real once, reused by
        // every "cache hit" test below rather than restoring it again per test.
        await LibraryInstaller.InstallAsync(
            _cacheRoot, MySqlConnector.Id, [new PackageRef(MySqlConnector.PackageId, MySqlConnector.PinnedVersion)],
            MySqlConnector.FactoryType);
    }

    public Task DisposeAsync()
    {
        foreach (var dir in new[] { _repoRoot, _cacheRoot })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WithNoCacheEntry_ResolvesFromAFeed_ExactlyLikeInstallAsync()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector", cacheRoot: Path.Combine(_cacheRoot, "none"));

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.Installed, result.Outcome);
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, "MySqlConnector");
        Assert.Contains("MySqlConnector.dll", Directory.EnumerateFiles(LibraryPaths.LibDir(libraryDir), "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.True(File.Exists(LibraryResolution.LockPath(libraryDir)));
    }

    [Fact]
    public async Task APinnedCatalogVersion_IsCopiedFromTheCache()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, MySqlConnector.Id, [new PackageRef(MySqlConnector.PackageId, MySqlConnector.PinnedVersion)],
            factoryType: null, cacheRoot: _cacheRoot);

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.InstalledFromCache, result.Outcome);
        Assert.Equal(MySqlConnector.FactoryType, result.Manifest.FactoryType);
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, MySqlConnector.Id);
        Assert.Contains("MySqlConnector.dll", Directory.EnumerateFiles(LibraryPaths.LibDir(libraryDir), "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.True(File.Exists(LibraryResolution.LockPath(libraryDir)));

        // A real factory out of the copy, not just files that happen to be there.
        var registry = new LibraryRegistry(_repoRoot).LoadAll();
        Assert.Empty(registry.NotRestored);
        Assert.NotNull(registry.GetFactory(MySqlConnector.Id));
    }

    [Fact]
    public async Task AnUnpinnedVersionOfACatalogPackage_IsResolvedFromAFeed()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, MySqlConnector.Id, [new PackageRef(MySqlConnector.PackageId, "2.3.7")],
            MySqlConnector.FactoryType, cacheRoot: _cacheRoot);

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.Installed, result.Outcome);
        Assert.Equal("2.3.7", Assert.Single(result.Manifest.Packages).Version);
    }

    [Fact]
    public async Task APackageNoFeedHas_FailsWithTheReason_AndLeavesNothingBehind()
    {
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, "SomeVendor.Driver", [new PackageRef("SomeVendor.Driver", "1.0.0")],
            "SomeVendor.Driver.SomeVendorFactory, SomeVendor.Driver", cacheRoot: _cacheRoot));

        Assert.Contains("SomeVendor.Driver", ex.Message);
        Assert.False(Directory.Exists(LibraryPaths.LibraryDir(_repoRoot, "SomeVendor.Driver")));
    }

    [Fact]
    public void AnOldLibraryJson_StopsTheLoad_WithAMessageNamingTheFile()
    {
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, "old-style");
        Directory.CreateDirectory(libraryDir);
        File.WriteAllText(Path.Combine(libraryDir, "library.json"), """{"id":"old-style","factoryType":"X.Y, X","packages":[{"id":"X","version":"1.0.0"}]}""");

        var ex = Assert.Throws<InvalidDataException>(() => new LibraryRegistry(_repoRoot).LoadAll());

        Assert.Contains("library.json", ex.Message);
        Assert.Contains("library.csx", ex.Message);
    }

    [Fact]
    public void ALibraryCsx_HoldsOnlyPackageLines_AndRoundTrips()
    {
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, "round-trip");
        var manifest = new LibraryManifest("round-trip", "A.B.Factory, A.B", [new PackageRef("A.B", "1.2.3"), new PackageRef("A.B.Native", "4.5.6")]);
        manifest.Write(LibraryPaths.ManifestPath(libraryDir));

        var read = LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir));
        Assert.Equal(("round-trip", "A.B.Factory, A.B"), (read.Id, read.FactoryType));
        Assert.Equal(manifest.Packages, read.Packages);

        File.AppendAllText(LibraryPaths.ManifestPath(libraryDir), "System.Console.WriteLine(1);" + Environment.NewLine);
        var ex = Assert.Throws<InvalidDataException>(() => LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir)));
        Assert.Contains("more than #r", ex.Message);
    }
}
