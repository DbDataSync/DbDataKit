using DbDataKit.Runner;
using DbDataSync.Libraries;
using DbDataSync.State;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// A directory like the one a replication set is published from, for the suites that run a replication on a runner (plan 28c E03, E12e): the engine as the executable package's files, <c>repo/config</c> with the definition and
/// the client library of the driver beside it, installed the way an install does it, for the engine to load into its default context.
/// </summary>
internal static class RunnerPackageSource
{
    public static Task<string> CreateAsync(string repoRoot, string driverId) => CreateAsync(repoRoot, [driverId], []);

    /// <summary>
    /// The same for a replication between two engines (plan 28c E06d): the client library of each driver is installed beside the definition. SQL Server's and PostgreSQL's are seeded the way the product seeds them; MySQL's
    /// (which MariaDB uses too) and Oracle's are installed the way <c>config library install</c> does it, by the library id the driver names.
    /// </summary>
    public static Task<string> CreateAsync(string repoRoot, params DbDataSync.Drivers.Abstractions.IDriver[] drivers) =>
        CreateAsync(repoRoot, drivers.Select(d => d.DriverType).Distinct().ToList(), drivers.Select(d => d.RequiredLibraryId).OfType<string>().Distinct().ToList());

    private static async Task<string> CreateAsync(string repoRoot, IReadOnlyList<string> driverIds, IReadOnlyList<string> libraryIds)
    {
        var source = Directory.CreateTempSubdirectory("dbdatasync-runner-package-").FullName;
        try
        {
            await FillAsync(source, repoRoot, driverIds, libraryIds);
            return source;
        }
        catch
        {
            // the caller never gets the directory to delete: a package that could not be made (a full disk) is not left behind
            try { GitTempDirectory.DeleteRecursively(source); }
            catch (Exception) { /* untidy, not wrong */ }
            throw;
        }
    }

    private static async Task FillAsync(string source, string repoRoot, IReadOnlyList<string> driverIds, IReadOnlyList<string> libraryIds)
    {
        var engine = EngineBuildOutput();
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source, "engine"), engine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopyDirectory(engine, Path.Combine(source, "engine"));
        }

        var repo = Directory.CreateDirectory(Path.Combine(source, "repo")).FullName;
        CopyDirectory(Path.Combine(repoRoot, "config"), Path.Combine(repo, "config"));
        var registry = new LibraryRegistry(repo).LoadAll();
        foreach (var driverId in driverIds) await BuiltInDriverLibraries.EnsureInstalledAsync(registry, repo, driverId, CancellationToken.None);
        foreach (var libraryId in libraryIds.Where(id => !registry.Installed.ContainsKey(id)))
        {
            var entry = KnownLibraries.TryGetById(libraryId)!;
            await LibraryInstaller.InstallOrDeferAsync(repo, libraryId, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType, cancellationToken: CancellationToken.None);
            registry.RegisterInstalled(libraryId);
        }

        await File.WriteAllTextAsync(Path.Combine(source, "package.yaml"), PackageManifestWriter.Write(RunnerRig.EngineManifest()));
    }

    /// <summary>The engine as built: the executable's own output directory, with its runtime configuration beside it.</summary>
    public static string EngineBuildOutput()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DbDataSync.TaskRunner", "bin", configuration, framework);
            if (File.Exists(Path.Combine(candidate, "DbDataSync.TaskRunner.runtimeconfig.json"))) return candidate;
        }

        throw new InvalidOperationException("The engine's build output was not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
