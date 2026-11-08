using System.Diagnostics;
using DbDataKit.Runner;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// The manifest of the Sync runtime package (plan 28c E06): <c>scripts/runtime-manifest.sh</c> prints what <see cref="PackageManifestWriter"/> writes, the reader, the validator and the runner's contract check accept it,
/// and a platform that is not published or a version that is not numbers is refused by name. No engine needed; the script needs bash (not run on Windows, where CI's Linux job covers it).
/// </summary>
public sealed class SyncRuntimePackageTests
{
    private static string Script
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "scripts", "runtime-manifest.sh");
                if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "DbDataSync.slnf"))) return candidate;
            }

            throw new InvalidOperationException("scripts/runtime-manifest.sh was not found above " + AppContext.BaseDirectory);
        }
    }

    private static string? Bash()
    {
        // on Windows `bash` on the PATH can be the launcher of WSL, which has no distribution on a CI machine: Git's own is the one the scripts are written for
        if (OperatingSystem.IsWindows() && Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Git", "bin", "bash.exe") is var git && File.Exists(git)) return git;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "bash.exe" : "bash"))) return Path.Combine(directory, OperatingSystem.IsWindows() ? "bash.exe" : "bash");
        return null;
    }

    private static (int Exit, string Output, string Errors) Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Bash()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Script.Replace('\\', '/'));
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result.Replace("\r\n", "\n"), errors.Result);
    }

    public static PackageManifest Expected(string rid, string version) =>
        new("dbdatasync-runtime", $"The dbdatasync engine {version} for {rid}, run by definition packages that name it",
            new PackageLaunch(LaunchKind.Executable, Command: "dotnet", Args: ["{runtime}/bin/DbDataSync.TaskRunner.dll", "--runner"]),
            new BuiltAgainst("10.0", "1", "dbdatakit.runner.v1"), [], IsRuntime: true, Version: version, Platform: rid);

    [SkippableTheory]
    [InlineData("linux-x64", "1.2.3", "1.2.3")]
    [InlineData("win-x64", "2026.11.08.1234-alpha.5", "2026.11.81234")]
    public void TheScriptsManifest_IsWhatTheWriterWrites_AndPassesTheReaderTheValidatorAndTheContractCheck(string rid, string given, string version)
    {
        Skip.If(Bash() is null, "bash is not installed");
        var (exit, output, errors) = Run(rid, given);

        Assert.True(exit == 0, errors);
        Assert.Equal(PackageManifestWriter.Write(Expected(rid, version)), output);
        var load = PackageManifestReader.Load(output, "package.yaml");
        Assert.True(load.Ok, string.Join('\n', load.Diagnostics));
        var manifest = load.Manifest!;
        Assert.Empty(PackageManifestValidator.Validate(manifest, "package.yaml"));
        Assert.Null(RunnerContracts.Check(manifest));
        Assert.True(manifest.IsRuntime);
        Assert.Equal(version, manifest.Version);
        Assert.Empty(manifest.Tasks);
    }

    [SkippableTheory]
    [InlineData("osx-arm64", "1.2.3", "win-x64 and linux-x64 only")]
    [InlineData("linux-x64", "one.two", "not a version of three numbers")]
    public void APlatformThatIsNotPublished_AndAVersionThatIsNotNumbers_AreRefusedByName(string rid, string version, string message)
    {
        Skip.If(Bash() is null, "bash is not installed");
        var (exit, _, errors) = Run(rid, version);

        Assert.NotEqual(0, exit);
        Assert.Contains(message, errors);
        Assert.Contains(version == "1.2.3" ? rid : version, errors);
    }
}
