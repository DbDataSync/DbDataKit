using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Loader.Tests;

/// <summary>
/// Compiled drivers for the tests, as the scripts an operator would write (<c>drivers/&lt;id&gt;/driver.csx</c>): a minimal <c>IDriver</c> over <c>Microsoft.Data.Sqlite</c> (it needs no server), the same with a contract version
/// the host cannot support, and a script that ends with something that is not a driver. The package comes from the machine's NuGet folder, which is a feed.
/// </summary>
public static class DriverFixtures
{
    public const string SqlitePackage = "Microsoft.Data.Sqlite";

    private static string GlobalPackages =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>The newest stable version of the package in the machine's folder, or null.</summary>
    public static string? SqliteVersion()
    {
        var folder = Path.Combine(GlobalPackages, SqlitePackage.ToLowerInvariant());
        return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(v => v is not null && !v.Contains('-')).Order().LastOrDefault() : null;
    }

    public static string WorkingDriver(string version) => $$"""
        // A minimal driver: the fixture the loader tests load as a real compiled plugin.
        #r "nuget: {{SqlitePackage}}, {{version}}"
        using Microsoft.Data.Sqlite;

        public sealed class FixtureDriver : IDriver
        {
            public string DriverType => "fixture.loadertest";
            public IReadOnlyList<IChangeReader> Readers { get; } = [];
            public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
            public IReadOnlyList<IChangeWriter> Writers { get; } = [];

            public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
                new SqliteConnection(connection.ConnectionString ?? "Data Source=:memory:");

            public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<string>>([]);

            public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<TableMetadata>>([]);

            public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<ColumnMetadata>>([]);

            public async Task<long> RoundTripAsync(DbConnection connection, CancellationToken cancellationToken)
            {
                await connection.OpenAsync(cancellationToken);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            }
        }

        new FixtureDriver()
        """;

    public static string BadContractDriver() => """
        public sealed class FixtureDriverBadContract : IDriver
        {
            public string DriverType => "fixture.badcontract";
            public int ContractVersion => 999;
            public IReadOnlyList<IChangeReader> Readers { get; } = [];
            public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
            public IReadOnlyList<IChangeWriter> Writers { get; } = [];
            public DbConnection CreateConnection(ConnectionConfig connection, string? credential) => throw new NotSupportedException();
            public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, string database, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        new FixtureDriverBadContract()
        """;

    /// <summary>Writes <c>drivers/&lt;id&gt;/driver.csx</c> and restores what it names from the machine's NuGet folder, the way <c>driver install</c> and <c>driver sync</c> leave a driver on disk.</summary>
    public static async Task InstallAsync(string repoRoot, string id, string script)
    {
        var driverDir = Path.Combine(repoRoot, "drivers", id);
        Directory.CreateDirectory(driverDir);
        var path = Path.Combine(driverDir, CompiledDriverManifest.FileName);
        await File.WriteAllTextAsync(path, script);
        var manifest = CompiledDriverManifest.Read(path);
        await LibraryResolution.RestoreAsync(driverDir, manifest.Packages, GlobalPackages, lockFileName: CompiledDriverManifest.LockFileName);
        Directory.CreateDirectory(LibraryPaths.LibDir(driverDir));
    }
}
