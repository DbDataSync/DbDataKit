using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// Enumerates <c>&lt;repo&gt;/drivers/*</c> and registers a driver for each — the one shared routine
/// both composition roots (<c>DbDataSyncHost.cs</c> and <c>TaskRunner/Program.cs</c>) call, so a
/// driver is available identically to the API and the worker without either duplicating the load logic
/// ("two composition roots", plan doc §*Blockers to clear*). A directory holding <c>driver.yaml</c> is
/// a descriptor (109d, <see cref="LoadDescriptorDrivers"/>); one holding <c>driver.csx</c> is a
/// compiled plugin (109e, <see cref="LoadCompiledDrivers"/>) — the two never coexist in one directory,
/// but nothing stops one repo having some of each.
/// </summary>
public static class DriverLoader
{
    public const string DescriptorFileName = "driver.yaml";

    /// <summary>
    /// A failed descriptor is logged (via <paramref name="onError"/>, defaulting to stderr) and
    /// skipped rather than failing host startup — one operator's typo in a driver they added should
    /// not take down every other replication.
    /// </summary>
    public static void LoadDescriptorDrivers(
        string repoRoot, LibraryRegistry libraryRegistry, DriverRegistry driverRegistry,
        Action<string, Exception>? onError = null)
    {
        onError ??= (message, ex) => Console.Error.WriteLine($"{message}: {ex.Message}");

        var driversRoot = Path.Combine(repoRoot, "drivers");
        if (!Directory.Exists(driversRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(driversRoot))
        {
            var yamlPath = Path.Combine(dir, DescriptorFileName);
            if (!File.Exists(yamlPath))
                continue;

            try
            {
                var descriptor = DriverDescriptorReader.Read(yamlPath);
                driverRegistry.Register(DriverDescriptorReader.BuildDriver(descriptor, libraryRegistry, repoRoot));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
                or YamlDotNet.Core.YamlException)
            {
                onError($"Failed to load driver descriptor '{yamlPath}'", ex);
            }
        }
    }

    /// <summary>
    /// A failed driver script — a missing package, a compile error, a contract-version mismatch, a script that does not end with an <see cref="IDriver"/> — is logged and skipped rather than failing host startup, for
    /// the same reason a failed descriptor is: one driver's problem should not take every other driver down with it. A <c>driver.json</c> (the format before <c>driver.csx</c>) is logged by name and skipped too.
    /// </summary>
    public static void LoadCompiledDrivers(string repoRoot, DriverRegistry driverRegistry, Action<string, Exception>? onError = null)
    {
        onError ??= (message, ex) => Console.Error.WriteLine($"{message}: {ex.Message}");

        var driversRoot = Path.Combine(repoRoot, "drivers");
        if (!Directory.Exists(driversRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(driversRoot))
        {
            var scriptPath = Path.Combine(dir, CompiledDriverManifest.FileName);
            var oldPath = Path.Combine(dir, CompiledDriverManifest.RefusedFileName);
            if (!File.Exists(scriptPath))
            {
                if (File.Exists(oldPath))
                    onError($"Skipped driver '{oldPath}'", new NotSupportedException(
                        $"'{CompiledDriverManifest.RefusedFileName}' is the old compiled-driver format and is no longer read. A compiled driver is now a {CompiledDriverManifest.FileName} script of #r \"nuget:\" lines ending with `new MyDriver()`: " +
                        $"run `dbdatasync driver install {Path.GetFileName(dir)} --kind compiled --package <id> --version <v> --driver-type <Type>` to write it."));
                continue;
            }

            try
            {
                var driver = DriverScriptHost.Load(dir);
                if (!DriverContract.IsSupported(driver.ContractVersion))
                {
                    throw new NotSupportedException(
                        $"Driver '{Path.GetFileName(dir)}' targets contract version {driver.ContractVersion}, which this build of " +
                        $"DbDataSync (supporting {DriverContract.MinSupportedVersion}-{DriverContract.CurrentVersion}) cannot load. " +
                        "Use a version of the driver that targets this DbDataSync version.");
                }
                driverRegistry.Register(driver);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or InvalidDataException or FileLoadException or BadImageFormatException)
            {
                onError($"Failed to load compiled driver '{scriptPath}'", ex);
            }
        }
    }
}
