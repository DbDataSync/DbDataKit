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
    public static async Task<string> CreateAsync(string repoRoot, string driverId)
    {
        var source = Directory.CreateTempSubdirectory("dbdatasync-runner-package-").FullName;
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
        await BuiltInDriverLibraries.EnsureInstalledAsync(new LibraryRegistry(repo).LoadAll(), repo, driverId, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "package.yaml"), PackageManifestWriter.Write(RunnerRig.EngineManifest()));
        return source;
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
