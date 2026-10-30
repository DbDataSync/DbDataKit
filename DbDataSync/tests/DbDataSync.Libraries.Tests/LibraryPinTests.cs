using DbDataKit.Scripting;
using DbDataSync.Tests.Shared;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// S12: a library's package hashes are pinned in <c>libraries/&lt;id&gt;/library.lock</c>, committed beside <c>library.csx</c> (the pattern a script's <c>scripts/&lt;name&gt;.lock</c> follows). Run against a local folder feed: no network.
/// </summary>
public sealed class LibraryPinTests : IDisposable
{
    private readonly LocalPackageFeed _feed = new();
    private readonly string _libraryDir = Path.Combine(Path.GetTempPath(), "dbdatasync-libpin-" + Guid.NewGuid().ToString("N")[..10], "libraries", "demo");

    public LibraryPinTests() => Directory.CreateDirectory(_libraryDir);

    public void Dispose()
    {
        _feed.Dispose();
        try { Directory.Delete(Path.GetFullPath(Path.Combine(_libraryDir, "..", "..")), true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly IReadOnlyList<PackageRef> Packages = [new PackageRef("Demo.Pkg", "1.0.0")];

    [Fact]
    public async Task TheFirstResolution_WritesTheLockFile_AndALaterOneFromTheStoreAloneWorksOffline()
    {
        _feed.Add("Demo.Pkg", "1.0.0", "original");
        var lockPath = LibraryResolution.LockPath(_libraryDir);
        Assert.False(File.Exists(lockPath));

        await LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory);

        var text = File.ReadAllText(lockPath);
        Assert.Contains("\"schema\": \"scripting-lock/1\"", text);
        Assert.Contains("demo.pkg/1.0.0", text);

        // the source is gone: the hashes match what is stored, so the library loads without it
        Directory.Delete(_feed.Directory, true);
        var closure = await LibraryResolution.LoadAsync(_libraryDir, Packages);
        Assert.Equal("Demo.Pkg", Assert.Single(closure.Packages).Id);
    }

    [Fact]
    public async Task APackageWhoseBytesDiffer_IsRefused_NamingThePackageTheVersionAndTheLockFile()
    {
        _feed.Add("Demo.Pkg", "1.0.0", "original");
        await LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory);
        var before = File.ReadAllText(LibraryResolution.LockPath(_libraryDir));

        // another machine (an empty store) fetches the same id and version, and the feed now serves other bytes
        _feed.Add("Demo.Pkg", "1.0.0", "replaced on the feed");
        Directory.Delete(LibraryPaths.LibDir(_libraryDir), true);

        var error = await Assert.ThrowsAsync<PackageIntegrityException>(() => LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory));

        Assert.Contains("Demo.Pkg 1.0.0", error.Message);
        Assert.Contains("demo/library.lock", error.Message);
        Assert.Equal(before, File.ReadAllText(LibraryResolution.LockPath(_libraryDir)));
        Assert.False(Directory.Exists(LibraryPaths.LibDir(_libraryDir)) && Directory.EnumerateFiles(LibraryPaths.LibDir(_libraryDir), "*.nupkg", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task AStoredPackageThatIsNotThePinnedOne_IsRefusedWhenTheLibraryLoads()
    {
        _feed.Add("Demo.Pkg", "1.0.0", "original");
        await LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory);
        var pins = new LockFile();
        pins.Pin("Demo.Pkg", "1.0.0", "not-the-hash");
        pins.Write(LibraryResolution.LockPath(_libraryDir));

        var error = await Assert.ThrowsAsync<PackageIntegrityException>(() => LibraryResolution.LoadAsync(_libraryDir, Packages));

        Assert.Contains("Demo.Pkg 1.0.0", error.Message);
        Assert.Contains("demo/library.lock", error.Message);
    }

    [Fact]
    public async Task ALibraryWithoutALockFile_KeepsWorking_AndTheNextResolutionCreatesIt()
    {
        _feed.Add("Demo.Pkg", "1.0.0", "original");
        await LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory);
        File.Delete(LibraryResolution.LockPath(_libraryDir));

        var closure = await LibraryResolution.LoadAsync(_libraryDir, Packages);       // a repository from before the lock file: the stored package is trusted
        Assert.Single(closure.Packages);
        Assert.False(File.Exists(LibraryResolution.LockPath(_libraryDir)));

        await LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory);
        Assert.Contains("demo.pkg/1.0.0", File.ReadAllText(LibraryResolution.LockPath(_libraryDir)));
    }

    [Fact]
    public async Task ALockFileWithAnUnknownKey_StopsTheLoad_NamingTheFileAndTheKey()
    {
        _feed.Add("Demo.Pkg", "1.0.0", "original");
        await LibraryResolution.RestoreAsync(_libraryDir, Packages, _feed.Directory);
        File.WriteAllText(LibraryResolution.LockPath(_libraryDir), """{"schema":"scripting-lock/1","packges":{}}""");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LibraryResolution.LoadAsync(_libraryDir, Packages));

        Assert.Contains("demo/library.lock", error.Message);
        Assert.Contains("$.packges", error.Message);
    }
}
