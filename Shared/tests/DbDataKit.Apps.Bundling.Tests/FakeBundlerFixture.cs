[assembly: AssemblyFixture(typeof(DbDataKit.Apps.Bundling.Tests.FakeBundlerFixture))]

namespace DbDataKit.Apps.Bundling.Tests;

/// <summary>
/// A fake bundler, a shell script that prints its environment and then fails, written once for the whole assembly before any test runs.
/// <para>
/// It cannot be written by the test that runs it: tests of this assembly run in parallel and several start processes, and a process started
/// on another thread holds a copy of every open descriptor of the test process from its fork until its exec. If that copy is the write
/// descriptor of the script, executing the script in that window fails with ETXTBSY ("Text file busy"), which the bundler reports as
/// "could not be started". Writing to a temporary name and renaming does not help, because the copied descriptor refers to the same file.
/// An assembly fixture is made before the first test starts, when no other thread forks, so the script is closed before anything can copy
/// its descriptor.
/// </para>
/// </summary>
public sealed class FakeBundlerFixture : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "fake-bundler-" + Guid.NewGuid().ToString("N")[..8]);

    public FakeBundlerFixture()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(folder);
        PrintEnvironmentAndFail = Path.Combine(folder, "fake-bun");
        File.WriteAllText(PrintEnvironmentAndFail, "#!/bin/sh\nenv\nexit 1\n");
        File.SetUnixFileMode(PrintEnvironmentAndFail, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The script's path; null on Windows, where a shell script is not a program.</summary>
    public string? PrintEnvironmentAndFail { get; }

    public void Dispose() { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } }
}
