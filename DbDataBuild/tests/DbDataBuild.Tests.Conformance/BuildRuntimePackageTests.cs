using System.Diagnostics;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 28c, E05: the Build runtime package. What needs no database engine, so it runs on Windows as well (the `runtime-package` job of dbdatabuild-ci.yml): the manifest `scripts/runtime-manifest.sh` prints is the one the tests
/// and the runner read, the size check fails a file above 400 MiB naming it, a definition runs from the read-only view with the engine unpacked into the runtime's directory under the runner's cache (once, whatever starts it
/// next, however many start together), and the refresh against a database runs in <c>ApplyConformanceTests</c> through the same runner and the same package.
/// </summary>
[Trait("Group", "runtime-package")]
public class BuildRuntimePackageTests
{
    private static string Scripts => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts"));

    private static string? Bash()
    {
        // on Windows `bash` on the PATH can be the launcher of WSL, which has no distribution on a CI machine: Git's own is the one the scripts are written for
        if (OperatingSystem.IsWindows() && Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Git", "bin", "bash.exe") is var git && File.Exists(git)) return git;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "bash.exe" : "bash"))) return Path.Combine(directory, OperatingSystem.IsWindows() ? "bash.exe" : "bash");
        return null;
    }

    private static (int Exit, string Out, string Err) Script(string name, params string[] args)
    {
        var info = new ProcessStartInfo(Bash()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(Scripts, name).Replace('\\', '/'));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, text.Result.Replace("\r\n", "\n"), errors.Result);
    }

    [SkippableTheory]
    [InlineData("linux-x64", false)]
    [InlineData("win-x64", false)]
    [InlineData("linux-x64", true)]
    public void The_manifest_the_publish_script_prints_is_the_one_the_runner_reads_and_serves(string rid, bool frameworkDependent)
    {
        Skip.If(Bash() is null, "bash is not installed");
        var printed = Script("runtime-manifest.sh", [rid, "1.2.3-beta.4", .. frameworkDependent ? new[] { "--framework-dependent" } : []]);
        Assert.True(printed.Exit == 0, printed.Err);

        // the same text the tests' writer makes (a suffix of the release is cut off: a range reads numbers), which the reader, the validator and this runner's contracts accept
        var expected = BuildRuntimePackage.Manifest(rid, "1.2.3", frameworkDependent);
        Assert.Equal(PackageManifestWriter.Write(expected), printed.Out);
        var load = PackageManifestReader.Load(printed.Out, "package.yaml");
        Assert.True(load.Ok, string.Join('\n', load.Diagnostics));
        var manifest = load.Manifest!;
        Assert.Empty(PackageManifestValidator.Validate(manifest, "package.yaml"));
        Assert.Null(RunnerContracts.Check(manifest));
        Assert.True(manifest.IsRuntime);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal(rid, manifest.Platform);                                    // one package per platform: the same name and version, stored as dbdatabuild-runtime-<rid>
        Assert.Equal("dbdatabuild-runtime-" + rid, manifest.PackageName);
        Assert.Empty(manifest.Tasks);
        Assert.Equal(LaunchKind.Executable, manifest.Launch.Kind);

        // a definition that names the runtime by the range of its minor line accepts it
        Assert.True(VersionRange.TryParse(BuildOnRunner.VersionRangeOf("1.2.3"), out var range, out _));
        Assert.True(range.Contains("1.2.9"));
        Assert.False(range.Contains("1.3.0"));
    }

    [SkippableFact]
    public void A_platform_that_is_not_published_is_refused_by_name_and_a_version_that_is_not_numbers_too()
    {
        Skip.If(Bash() is null, "bash is not installed");
        var mac = Script("runtime-manifest.sh", "osx-arm64", "1.0.0");
        Assert.Equal(2, mac.Exit);
        Assert.Contains("win-x64 and linux-x64 only", mac.Err);
        Assert.Contains("osx-arm64", mac.Err);
        var text = Script("runtime-manifest.sh", "linux-x64", "next");
        Assert.Equal(2, text.Exit);
        Assert.Contains("'next'", text.Err);
    }

    [SkippableFact]
    public void A_file_above_the_size_limit_fails_the_report_naming_the_file_and_its_size()
    {
        Skip.If(Bash() is null, "bash is not installed");
        var directory = Directory.CreateTempSubdirectory("ddb-sizes-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            File.WriteAllText(Path.Combine(directory, "bin", "small.txt"), "hello");
            using (var big = File.Create(Path.Combine(directory, "bin", "dbdatabuild"))) big.SetLength(401L * 1024 * 1024);   // sparse where the file system allows

            var report = Script("runtime-sizes.sh", directory.Replace('\\', '/'));
            Assert.Equal(1, report.Exit);
            Assert.Contains("401.0 MiB", report.Out);
            Assert.Contains("bin/dbdatabuild", report.Out);
            Assert.Contains("above 400 MiB", report.Err);
            Assert.Contains("dbdatabuild is 401.0 MiB", report.Err);

            File.Delete(Path.Combine(directory, "bin", "dbdatabuild"));
            var small = Script("runtime-sizes.sh", directory.Replace('\\', '/'));
            Assert.Equal(0, small.Exit);
            Assert.Contains("small.txt", small.Out);
            Assert.Contains("total", small.Out);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>The published tool's own start, without a runner: it unpacks into the directory it is given, the first start fills it and the others reuse it, two that start together leave one.</summary>
    [Fact]
    public async Task The_engine_unpacks_once_into_the_directory_it_is_given_however_many_start_together()
    {
        var tool = Path.Combine(BuildRuntimePackage.Directory(), "files", "bin", BuildRuntimePackage.ToolFile);
        var extract = Directory.CreateTempSubdirectory("ddb-extract-").FullName;
        try
        {
            async Task<(int Exit, string Out)> Start()
            {
                var info = new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extract;
                using var process = Process.Start(info)!;
                var text = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, await text + await errors);
            }

            var together = await Task.WhenAll(Start(), Start(), Start());
            Assert.All(together, r => Assert.True(r.Exit == 0, r.Out));
            Assert.Single(Directory.GetDirectories(Path.Combine(extract, "dbdatabuild")));   // <base>/<program>/<one directory per bundle>
            var first = BuildOnRunner.Snapshot(extract);
            Assert.Contains("duckdb", first, StringComparison.OrdinalIgnoreCase);

            var later = await Start();
            Assert.True(later.Exit == 0, later.Out);
            Assert.Equal(first, BuildOnRunner.Snapshot(extract));
        }
        finally { try { Directory.Delete(extract, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } }
    }

    /// <summary>
    /// A definition package (a project with no database behind it and no compiled plan: the refresh is refused with DDB-446 by the engine, which read the view) run by the runtime package on a real runner: the task is the runtime's executable with the definition as the working directory, the
    /// view of both packages is read-only, the engine unpacks into the runtime's directory under the runner's cache, and a second run reuses it.
    /// </summary>
    [Fact]
    public async Task A_definition_runs_from_read_only_views_and_the_engine_unpacks_into_the_runtimes_directory_under_the_cache_once()
    {
        var project = Directory.CreateTempSubdirectory("ddb-runtime-project-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(project, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\n");
            await using var runner = await BuildOnRunner.StartAsync(project, "sqlserver", null);

            var first = await runner.RunNowAsync("nope");
            runner.Expect(RunOutcome.Failed, first);
            Assert.True(first.Run.Failure?.Message.Contains("DDB-446") == true, runner.Why(first));   // the engine started, read the project the view holds (it has no rendered/ files) and refused
            var log = runner.Why(first);
            Assert.Contains("as an executable", log);

            // the engine's natives are in the directory of this runtime version under the runner's cache, and nowhere in a view
            var unpacked = BuildOnRunner.Snapshot(runner.ExtractDirectory);
            Assert.Contains("duckdb", unpacked, StringComparison.OrdinalIgnoreCase);
            var cache = Path.Combine(runner.DataDirectory, "cache");
            Assert.StartsWith(Path.Combine(cache, "runtimes") + Path.DirectorySeparatorChar, runner.ExtractDirectory);
            await runner.WaitForNoViewsAsync();
            var objects = BuildOnRunner.Snapshot(Path.Combine(cache, "objects"));

            var second = await runner.RunNowAsync("nope");
            runner.Expect(RunOutcome.Failed, second);
            Assert.Equal(unpacked, BuildOnRunner.Snapshot(runner.ExtractDirectory));
            Assert.Equal(objects, BuildOnRunner.Snapshot(Path.Combine(cache, "objects")));
        }
        finally { try { Directory.Delete(project, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ } }
    }
}
