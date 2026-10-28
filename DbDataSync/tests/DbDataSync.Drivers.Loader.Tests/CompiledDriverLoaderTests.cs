using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using Xunit;

namespace DbDataSync.Drivers.Loader.Tests;

/// <summary>
/// Loads a real compiled <see cref="IDriver"/> — a <c>drivers/&lt;id&gt;/driver.csx</c>, installed exactly the way <c>dbdatasync driver install</c> leaves it on disk — through <see cref="DriverLoader.LoadCompiledDrivers"/>, and proves
/// the isolation property that makes the mechanism safe: the script's <c>IDriver</c> is the *same* <c>Type</c> as the host's, not a second incompatible one.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompiledDriverLoaderTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-loader-test-{Guid.NewGuid():N}");

    public CompiledDriverLoaderTests() => Directory.CreateDirectory(_repoRoot);

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static string Version()
    {
        var version = DriverFixtures.SqliteVersion();
        Assert.True(version is not null, "Microsoft.Data.Sqlite is not in the machine's NuGet package folder: restore a project that uses it first");
        return version!;
    }

    [Fact]
    public async Task LoadCompiledDrivers_RegistersTheDriver_AndARoundTripSucceeds()
    {
        await DriverFixtures.InstallAsync(_repoRoot, "fixture-driver", DriverFixtures.WorkingDriver(Version()));
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        Assert.Empty(errors);
        Assert.True(registry.TryGet("fixture.loadertest", out var driver));
        Assert.NotNull(driver);

        await using var connection = driver!.CreateConnection(
            new ConnectionConfig
            {
                Name = "fixture", DriverType = "fixture.loadertest", AuthMode = AuthMode.None,
                ConnectionString = "Data Source=:memory:",
            },
            credential: null);

        var roundTrip = driver.GetType().GetMethod("RoundTripAsync")!;
        var task = (Task<long>)roundTrip.Invoke(driver, [connection, CancellationToken.None])!;
        Assert.Equal(1L, await task);
    }

    [Fact]
    public async Task LoadCompiledDrivers_IsolationHolds_TheDriversIDriverIsTheHostsType()
    {
        await DriverFixtures.InstallAsync(_repoRoot, "fixture-driver-identity", DriverFixtures.WorkingDriver(Version()));
        var registry = new DriverRegistry();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry);

        Assert.True(registry.TryGet("fixture.loadertest", out var driver));
        // The script is compiled against the host's DbDataSync.Drivers.Abstractions and its load context hands that name back to the default context; a private copy would make this a second IDriver.
        Assert.IsAssignableFrom<IDriver>(driver);
    }

    [Fact]
    public async Task LoadCompiledDrivers_AContractVersionTheHostCannotSupport_IsRefused_AndSkipped()
    {
        await DriverFixtures.InstallAsync(_repoRoot, "fixture-bad-contract", DriverFixtures.BadContractDriver());
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        Assert.False(registry.TryGet("fixture.badcontract", out _));
        var error = Assert.Single(errors);
        Assert.Contains("contract version 999", error.Exception.Message);
        Assert.Contains(DriverContract.CurrentVersion.ToString(), error.Exception.Message);
    }

    [Fact]
    public void LoadCompiledDrivers_ANotRestoredDriver_IsSkippedRatherThanThrowing_AndSaysHowToRestoreIt()
    {
        var driverDir = Path.Combine(_repoRoot, "drivers", "fixture-missing-lib");
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, CompiledDriverManifest.FileName), "#r \"nuget: DoesNotExist, 1.0.0\"\nnew object()\n");
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        var error = Assert.Single(errors);
        Assert.Contains("driver sync fixture-missing-lib", error.Exception.Message);
        Assert.Empty(registry.All);
    }

    [Fact]
    public async Task LoadCompiledDrivers_AScriptThatDoesNotEndWithADriver_IsSkippedRatherThanThrowing()
    {
        await DriverFixtures.InstallAsync(_repoRoot, "fixture-wrong-type", "var notADriver = 42;\nnotADriver\n");
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        var error = Assert.Single(errors);
        Assert.Contains("not an IDriver", error.Exception.Message);
    }

    [Fact]
    public async Task LoadCompiledDrivers_AScriptThatDoesNotCompile_IsSkipped_WithTheLineOfTheError()
    {
        await DriverFixtures.InstallAsync(_repoRoot, "fixture-broken", "// a comment\n\nvar x = missing + 1;\nnew object()\n");
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        var error = Assert.Single(errors);
        Assert.Contains("(3,", error.Exception.Message);
    }

    [Fact]
    public void LoadCompiledDrivers_AnOldDriverJson_IsSkipped_WithAMessageNamingTheFile()
    {
        var driverDir = Path.Combine(_repoRoot, "drivers", "old-style");
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, "driver.json"), "{}");
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        var error = Assert.Single(errors);
        Assert.Contains("driver.json", error.Message);
        Assert.Contains("driver.csx", error.Exception.Message);
    }
}
