using DbDataSync.Drivers.Loader.Tests;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A <see cref="TestApiFactory"/> whose repo root already has the compiled
/// <c>driver.csx</c> fixture (<see cref="DriverFixtures"/>) installed under
/// <c>drivers/&lt;id&gt;/</c> before the host ever starts — exactly the layout
/// <c>dbdatasync driver install --kind compiled</c> would leave. What
/// <see cref="LibrariesAndDriversEndpointTests"/> proves is that <c>GET /api/drivers</c> reports this
/// as <c>source: "compiled"</c> (phase 118), distinct from a built-in or a <c>driver.yaml</c>
/// descriptor.
/// </summary>
public sealed class CompiledDriverApiFactory : TestApiFactory
{
    public const string DriverId = "fixture.loadertest";

    public CompiledDriverApiFactory()
    {
        var version = DriverFixtures.SqliteVersion();
        if (version is null) return;       // the tests that use this factory are skipped without the package; see LibrariesAndDriversEndpointTests
        DriverFixtures.InstallAsync(RepoRoot, DriverId, DriverFixtures.WorkingDriver(version)).GetAwaiter().GetResult();
    }
}
