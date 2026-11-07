using DbDataKit.Runner.Api;
using System.Diagnostics;
using System.Reflection;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataRunner.Tests;

/// <summary>
/// The sample package of the runner's tests (<c>DbDataKit.Runner.TestPackage</c>: the jobs <c>ok</c>, <c>fails</c>, <c>waits</c> and others that the executor's tests use), published once for the whole run to a directory like a real
/// package (its <c>deps.json</c> and a copy of the kit), and a <c>package/1</c> manifest for it written with the manifest's own writer.
/// </summary>
internal static class SamplePackage
{
    public const string Name = "sample";

    private static readonly Lazy<string> Directory_ = new(Publish);
    private static readonly List<string> Made = [];

    static SamplePackage() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

    /// <summary>What the tests made in the temporary folder is removed when the run ends (the published package is some 20 MB).</summary>
    private static void Cleanup()
    {
        lock (Made)
            foreach (var directory in Made)
            {
                try { System.IO.Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
            }
    }

    private static string Temp(string prefix)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory(prefix).FullName;
        lock (Made) Made.Add(directory);
        return directory;
    }

    /// <summary>The published package's directory.</summary>
    public static string Directory => Directory_.Value;

    public static PackageTask Task(string name, TimeSpan? every = null, TaskLimits? limits = null) =>
        new(name, new TaskEntry("T"), every is { } interval ? Schedule.Interval(interval) : new Schedule(), [], [], [], ProgressDeclaration.Empty, limits ?? TaskLimits.None, [], []);

    public static PackageManifest Manifest(params PackageTask[] tasks) =>
        new(Name, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "DbDataKit.Runner.TestPackage.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), tasks);

    /// <summary>Writes the manifest as a file in a new temporary directory and returns its path.</summary>
    public static string WriteManifest(PackageManifest manifest, string? directory = null)
    {
        directory ??= Temp("dbdatarunner-manifest-");
        var path = Path.Combine(directory, "package.yaml");
        File.WriteAllText(path, PackageManifestWriter.Write(manifest));
        return path;
    }

    private static string Publish()
    {
        // a directory a person published beforehand (a run as an ordinary user cannot write the project's obj directory or read the root's package cache)
        if (Environment.GetEnvironmentVariable("DBDATA_TEST_PUBLISHED_PACKAGE") is { Length: > 0 } published) return published;

        var project = FindProject();
        var output = Temp("dbdatarunner-publish-");
        // the test project built the package already (a project reference), in the configuration the tests run in (DBDATA_TEST_CONFIGURATION names it when the layout is not the usual one); an artifacts path (DBDATA_TEST_ARTIFACTS_PATH) is where it was built when the build was given one
        var here = new DirectoryInfo(AppContext.BaseDirectory);   // <project>/bin/<configuration>/<framework>
        var configuration = Environment.GetEnvironmentVariable("DBDATA_TEST_CONFIGURATION") is { Length: > 0 } named ? named : here.Parent?.Parent?.Name == "bin" ? here.Parent.Name : "Debug";
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "publish", project, "--no-build", "-c", configuration, "-o", output, "--nologo", "-v", "q" }) info.ArgumentList.Add(argument);
        if (Environment.GetEnvironmentVariable("DBDATA_TEST_ARTIFACTS_PATH") is { Length: > 0 } artifacts)
        {
            info.ArgumentList.Add("--artifacts-path");
            info.ArgumentList.Add(artifacts);
        }

        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Publishing the test package failed ({process.ExitCode}):\n{text.Result}\n{errors.Result}");
        return output;
    }

    private static string FindProject()
    {
        var root = typeof(SamplePackage).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(m => m.Key == "RepoRoot")?.Value ?? "";
        var candidate = Path.Combine(root, "DbDataKit", "tests", "DbDataKit.Runner.TestPackage", "DbDataKit.Runner.TestPackage.csproj");
        return File.Exists(candidate) ? candidate : throw new InvalidOperationException($"The test package's project was not found at {candidate}.");
    }
}
