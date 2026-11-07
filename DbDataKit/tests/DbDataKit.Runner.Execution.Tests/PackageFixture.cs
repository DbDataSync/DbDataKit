using System.Diagnostics;
using System.Security.Cryptography;

namespace DbDataKit.Runner.Execution.Tests;

/// <summary>A directory of files as an artifact source: what a published version is, for the tests (the files are listed by path and hash, and opened by path).</summary>
internal sealed class DirectorySource(string directory) : IArtifactSource
{
    public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default)
    {
        var files = new List<ArtifactFile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            using var stream = File.OpenRead(path);
            files.Add(new ArtifactFile(relative, Convert.ToHexStringLower(SHA256.HashData(stream)), new FileInfo(path).Length));
        }

        return Task.FromResult<IReadOnlyList<ArtifactFile>>(files);
    }

    public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(File.OpenRead(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar))));
}

/// <summary>
/// The sample package, published once for the whole run of the tests (<c>dotnet publish</c> of <c>DbDataKit.Runner.TestPackage</c> into a temporary directory, so it has its <c>deps.json</c> and a copy of the kit
/// like a real package) and materialized through the artifact cache into a read-only view that every test's tasks run from. Executable packages are small directories of scripts.
/// </summary>
internal static class PackageFixture
{
    private static readonly Lazy<Task<MaterializedVersion>> Sample = new(() => MaterializeAsync(Publish()));
    private static readonly Lazy<Task<MaterializedVersion>> Scripts = new(() => MaterializeAsync(WriteScripts()));

    public static Task<MaterializedVersion> SamplePackage() => Sample.Value;

    public static Task<MaterializedVersion> ScriptPackage() => Scripts.Value;

    private static async Task<MaterializedVersion> MaterializeAsync(string directory)
    {
        var root = Directory.CreateTempSubdirectory("dbdatakit-exec-cache-").FullName;
        var cache = new ArtifactCache(Path.Combine(root, "cache"), new DirectorySource(directory));
        return await cache.MaterializeAsync("test-package@1");
    }

    private static string WriteScripts()
    {
        var directory = Directory.CreateTempSubdirectory("dbdatakit-exec-scripts-").FullName;
        File.WriteAllText(Path.Combine(directory, "exit0.sh"), "echo 'a script that speaks no protocol'\nexit 0\n");
        File.WriteAllText(Path.Combine(directory, "exit3.sh"), "echo 'failing quietly' >&2\nexit 3\n");
        File.WriteAllText(Path.Combine(directory, "args.sh"), "echo \"args: $1 $2\"\necho \"task: $DBDATAKIT_TASK_NAME\"\nexit 0\n");
        return directory;
    }

    private static string Publish()
    {
        // a directory a person published beforehand (a run as an ordinary user cannot write the project's obj directory or read the root's package cache)
        if (Environment.GetEnvironmentVariable("DBDATA_TEST_PUBLISHED_PACKAGE") is { Length: > 0 } published) return published;

        var project = FindProject();
        var output = Directory.CreateTempSubdirectory("dbdatakit-exec-publish-").FullName;
        // the test project built the package already (a project reference), in the configuration the tests run in
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "publish", project, "--no-build", "-c", configuration, "-o", output, "--nologo", "-v", "q" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Publishing the test package failed ({process.ExitCode}):\n{text.Result}\n{errors.Result}");
        return output;
    }

    /// <summary>
    /// The task host as built in its own project's output, with its own dependencies beside it. (The test project's copy of it would run without the assemblies that the ASP.NET shared framework supplies to the
    /// test project and that a runner-only host does not have; a runner installs the task host from its own build.)
    /// </summary>
    public static string TaskHostPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DbDataKit.Runner.TaskHost", "bin", configuration, framework, "dbdatakit-taskhost.dll");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("The task host's build output was not found above " + AppContext.BaseDirectory);
    }

    private static string FindProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "DbDataKit.Runner.TestPackage", "DbDataKit.Runner.TestPackage.csproj");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("The test package's project was not found above " + AppContext.BaseDirectory);
    }
}
