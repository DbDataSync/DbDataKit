using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;
using Xunit;

namespace DbDataSync.Drivers.Loader.Tests;

/// <summary>
/// Plan 28c E06c: the <c>driver-libraries</c> part is restored at publish time for a platform, and a definition view made of it loads a library (a descriptor driver's provider), a compiled plugin driver and the IKVM runtime of a JDBC
/// driver without a feed, without the SDK and without writing a byte under the view. The packages come from the machine's NuGet folder, which is a feed (as <see cref="DriverFixtures"/> does).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DriverLibrariesPartTests : IDisposable
{
    private const string LibraryId = "Microsoft.Data.Sqlite";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dbdatasync-part-test-{Guid.NewGuid():N}");
    private string Repo => Path.Combine(_root, "repo");
    private string Part => Path.Combine(_root, "part");
    private string Scratch => Path.Combine(_root, "scratch");

    private static string Feed =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    private static string Platform => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    public DriverLibrariesPartTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }

    private static string SqliteVersion()
    {
        var version = DriverFixtures.SqliteVersion();
        Assert.True(version is not null, "Microsoft.Data.Sqlite is not in the machine's NuGet package folder: restore a project that uses it first");
        return version!;
    }

    private void WriteLibrary(string id, string package, string version)
    {
        var dir = Path.Combine(Repo, "libraries", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LibraryPaths.ManifestFileName), $"#r \"nuget: {package}, {version}\"\n");
    }

    private void WriteCompiledDriver(string id, string version)
    {
        var dir = Path.Combine(Repo, "drivers", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, CompiledDriverManifest.FileName), DriverFixtures.WorkingDriver(version));
    }

    /// <summary>The part as a runner has it: every file read-only (in place, the IKVM image is too big to copy), so a write that is attempted fails on a platform that enforces it, and the snapshot proves it on one that does not.</summary>
    private static string ViewOf(string part)
    {
        foreach (var file in Directory.EnumerateFiles(part, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.ReadOnly);
        return part;
    }

    [Fact]
    public async Task APartHoldsTheClosureOfEveryLibraryAndDriver_AndAnIndexOfThem()
    {
        var version = SqliteVersion();
        WriteLibrary(LibraryId, "Microsoft.Data.Sqlite", version);
        WriteCompiledDriver("fixture-driver", version);

        var index = await DriverLibrariesPart.BuildAsync(Repo, Part, Platform, Feed);

        Assert.Equal(Platform, index.Platform);
        Assert.Equal(["library:" + LibraryId, "driver:fixture-driver"], index.Entries.Select(e => e.Kind + ":" + e.Id));
        Assert.All(index.Entries, e => Assert.Contains(e.Packages, p => p.StartsWith("Microsoft.Data.Sqlite ", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.Combine(Part, "libraries", LibraryId, "library.lock")));
        Assert.True(File.Exists(Path.Combine(Part, "drivers", "fixture-driver", "driver.lock")));
        Assert.True(Directory.Exists(Path.Combine(Part, "libraries", LibraryId, "lib")));
        var read = DriverLibrariesPart.ReadIndex(Part)!;
        Assert.Equal(index.Platform, read.Platform);
        Assert.Equal(index.Entries.Select(e => (e.Kind, e.Id, string.Join(";", e.Packages), e.Files)), read.Entries.Select(e => (e.Kind, e.Id, string.Join(";", e.Packages), e.Files)));
        // the repository is not touched
        Assert.False(Directory.Exists(Path.Combine(Repo, "libraries", LibraryId, "lib")));
    }

    [Fact]
    public async Task APartForTheOtherPlatformHoldsThePackagesOfThatPlatform()
    {
        WriteLibrary(LibraryId, "Microsoft.Data.Sqlite", SqliteVersion());
        var other = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";

        await DriverLibrariesPart.BuildAsync(Repo, Part, other, Feed);

        var natives = Directory.EnumerateFiles(Path.Combine(Part, "libraries", LibraryId, "lib"), "*e_sqlite3*", SearchOption.AllDirectories).Select(f => f.Replace('\\', '/')).ToList();
        Assert.Contains(natives, f => f.Contains("/runtimes/" + other + "/native/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlatformThatIsNotPublishedIsRefusedByName_AndAPartIsNeverMergedIntoAnOldOne()
    {
        WriteLibrary(LibraryId, "Microsoft.Data.Sqlite", SqliteVersion());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => DriverLibrariesPart.BuildAsync(Repo, Part, "osx-arm64", Feed));
        Assert.Contains("osx-arm64", refused.Message);

        await DriverLibrariesPart.BuildAsync(Repo, Part, Platform, Feed);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => DriverLibrariesPart.BuildAsync(Repo, Part, Platform, Feed));
        Assert.Contains("not empty", again.Message);
    }

    [Fact]
    public async Task APackageWhoseBytesDifferFromThePinInTheRepositoryIsRefusedNamingIt()
    {
        var version = SqliteVersion();
        WriteLibrary(LibraryId, "Microsoft.Data.Sqlite", version);
        await DriverLibrariesPart.BuildAsync(Repo, Part, Platform, Feed);
        var pins = File.ReadAllText(Path.Combine(Part, "libraries", LibraryId, "library.lock"));
        var tampered = System.Text.RegularExpressions.Regex.Replace(pins, "(\"microsoft.data.sqlite/[^\"]+\"\\s*:\\s*\")[A-Za-z0-9+/]", m => m.Groups[1].Value + (m.Value[^1] == 'A' ? "B" : "A"));
        Assert.NotEqual(pins, tampered);
        File.WriteAllText(Path.Combine(Repo, "libraries", LibraryId, "library.lock"), tampered);
        Directory.Delete(Part, recursive: true);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DriverLibrariesPart.BuildAsync(Repo, Part, Platform, Feed));

        Assert.Contains("Microsoft.Data.Sqlite", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LibraryId, refused.Message);
    }

    [Fact]
    public async Task ADescriptorDriversLibraryAndACompiledDriverRunFromAReadOnlyView_AndNothingUnderItIsWritten()
    {
        var version = SqliteVersion();
        WriteLibrary(LibraryId, "Microsoft.Data.Sqlite", version);
        WriteCompiledDriver("fixture-driver", version);
        var descriptor = Path.Combine(Repo, "drivers", "sqlite.descriptor");
        Directory.CreateDirectory(descriptor);
        File.WriteAllText(Path.Combine(descriptor, DriverLoader.DescriptorFileName), $$"""
            id: sqlite.descriptor
            displayName: SQLite (descriptor)
            library: {{LibraryId}}
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              rowLimit: limitOffset
              catalog: default
            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """);
        await DriverLibrariesPart.BuildAsync(Repo, Part, Platform, Feed);
        File.Copy(Path.Combine(descriptor, DriverLoader.DescriptorFileName), Path.Combine(Part, "drivers", "sqlite.descriptor", DriverLoader.DescriptorFileName).Also(p => Directory.CreateDirectory(p)), overwrite: true);
        var view = ViewOf(Part);
        var before = DriverLibrariesPart.Snapshot(view);

        var libraries = new LibraryRegistry(view).LoadAll();
        var drivers = new DriverRegistry();
        var errors = new List<string>();
        DriverLoader.LoadDescriptorDrivers(view, libraries, drivers, (m, ex) => errors.Add(m + ": " + ex.Message));
        DriverLoader.LoadCompiledDrivers(view, drivers, (m, ex) => errors.Add(m + ": " + ex.Message), cacheRoot: Path.Combine(Scratch, "drivers"));

        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.Empty(libraries.NotRestored);
        Assert.Contains(LibraryId, libraries.Installed.Keys);
        Assert.True(drivers.TryGet("sqlite.descriptor", out _));
        // the provider the descriptor names is the library's, loaded from the view
        await using (var connection = libraries.GetFactory(LibraryId).CreateConnection()!)
        {
            connection.ConnectionString = "Data Source=:memory:";
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        Assert.True(drivers.TryGet("fixture.loadertest", out var compiled));
        await using var compiledConnection = compiled!.CreateConnection(
            new ConnectionConfig { Name = "c", DriverType = "fixture.loadertest", AuthMode = AuthMode.None, ConnectionString = "Data Source=:memory:" }, credential: null);
        var roundTrip = (Task<long>)compiled.GetType().GetMethod("RoundTripAsync")!.Invoke(compiled, [compiledConnection, CancellationToken.None])!;
        Assert.Equal(1L, await roundTrip);

        Assert.Equal(before, DriverLibrariesPart.Snapshot(view));
        Assert.True(Directory.Exists(Path.Combine(Scratch, "drivers", "fixture-driver", "compiled")));
    }

    [Fact]
    public async Task TheIkvmRuntimeOfAJdbcDriverIsRestoredIntoThePartAndLoadsFromAReadOnlyView()
    {
        var ikvm = KnownLibraries.TryGetById("ikvm")!;
        WriteLibrary("ikvm", ikvm.PackageId, ikvm.PinnedVersion);

        // IKVM's closure needs packages the machine's folder may not have: the public feed, as `library install` uses
        var index = await DriverLibrariesPart.BuildAsync(Repo, Part, Platform);

        Assert.Contains(index.Entries.Single().Packages, p => p.StartsWith("IKVM ", StringComparison.Ordinal));
        Assert.Contains(index.Entries.Single().Packages, p => p.StartsWith("IKVM.Image.Runtime." + Platform, StringComparison.OrdinalIgnoreCase));
        var view = ViewOf(Part);
        var before = DriverLibrariesPart.Snapshot(view);

        var libraries = new LibraryRegistry(view).LoadAll();

        Assert.Empty(libraries.NotRestored);
        Assert.Contains("ikvm", libraries.Installed.Keys);
        Assert.Equal(before, DriverLibrariesPart.Snapshot(view));
    }
}

internal static class PathExtensions
{
    /// <summary>Runs <paramref name="action"/> on the folder of a path and returns the path (test helper).</summary>
    public static string Also(this string path, Func<string, DirectoryInfo> action) { action(Path.GetDirectoryName(path)!); return path; }
}
