using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DbDataKit.Apps.Bundling;

/// <summary>Where Bun was found (<see cref="Path"/>, and <see cref="Source"/> says which rule found it), or why it was not (<see cref="Problem"/>, a message that names the pin and the script that fetches it).</summary>
public sealed record BunLocation(string? Path, string? Source, string? Problem)
{
    public bool Found => Path is not null;
}

/// <summary>
/// Finds the Bun a build runs (plan 26 AP23): the one the repository pins in <c>bun.pin</c> and <c>scripts/fetch-bun.sh</c> downloads, hash-checked, into <c>.bun/&lt;rid&gt;/</c>. The rules, in order: a path named by the caller
/// (<c>--bun</c>), the <see cref="EnvironmentVariable"/> variable, <c>.bun/&lt;rid&gt;/</c> in a folder above the start folders (checked against the program hash in the <c>bun.pin</c> beside it, so a changed byte is refused),
/// then <c>bun</c> on the PATH (not checked: the pin is the way to a Bun that is). Nothing is downloaded here, and no package install script ever runs (the bundler passes <c>--ignore-scripts</c>).
/// </summary>
public static class BunLocator
{
    public const string EnvironmentVariable = "DBDATAKIT_BUN";

    /// <summary>The .NET runtime identifier the pin names this platform by (<c>linux-x64</c>, <c>win-x64</c>, <c>osx-arm64</c>...).</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", _ => "x64" };
        return $"{os}-{arch}";
    }

    /// <param name="explicitPath">A Bun the caller named; it must exist.</param>
    /// <param name="environment">Reads an environment variable (tests replace it).</param>
    /// <param name="startFolders">Where to look upwards for <c>.bun/&lt;rid&gt;</c> and <c>bun.pin</c>: the working folder and the program's folder by default.</param>
    /// <param name="path">The PATH value (tests replace it).</param>
    /// <param name="rid">The platform (tests replace it).</param>
    public static BunLocation Locate(string? explicitPath = null, Func<string, string?>? environment = null, IEnumerable<string>? startFolders = null, string? path = null, string? rid = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        rid ??= CurrentRid();
        var starts = (startFolders ?? [Directory.GetCurrentDirectory(), AppContext.BaseDirectory]).Select(System.IO.Path.GetFullPath).Distinct().ToList();
        var pinFile = starts.Select(FindPin).FirstOrDefault(p => p is not null);

        if (explicitPath is { Length: > 0 })
            return File.Exists(explicitPath) ? new(System.IO.Path.GetFullPath(explicitPath), "--bun", null) : new(null, null, $"The Bun named with --bun, {explicitPath}, does not exist.");
        if (environment(EnvironmentVariable) is { Length: > 0 } named)
            return File.Exists(named) ? new(System.IO.Path.GetFullPath(named), EnvironmentVariable, null) : new(null, null, $"The Bun named by {EnvironmentVariable}, {named}, does not exist.");

        var binary = rid.StartsWith("win", StringComparison.Ordinal) ? "bun.exe" : "bun";
        foreach (var start in starts)
            for (var folder = start; folder is not null; folder = System.IO.Path.GetDirectoryName(folder))
            {
                var candidate = System.IO.Path.Combine(folder, ".bun", rid, binary);
                if (!File.Exists(candidate)) continue;
                var pin = System.IO.Path.Combine(folder, "bun.pin");
                if (File.Exists(pin) && PinValue(pin, "BIN_SHA256_" + rid.Replace('-', '_')) is { } expected && !string.Equals(Sha256(candidate), expected, StringComparison.OrdinalIgnoreCase))
                    return new(null, null, $"{candidate} does not match the program hash in {pin}, so it is not run. Fetch it again with scripts/fetch-bun.sh.");
                return new(System.IO.Path.GetFullPath(candidate), pin is not null && File.Exists(pin) ? ".bun (checked against bun.pin)" : ".bun", null);
            }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var folder in (path ?? environment("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(folder, binary);
            if (File.Exists(candidate)) return new(candidate, "PATH (not checked against bun.pin)", null);
        }

        var version = pinFile is null ? null : PinValue(pinFile, "VERSION");
        return new(null, null, $"Bun is not installed. Building an app uses the Bun pinned in bun.pin{(version is null ? "" : $" (version {version})")}, downloaded and checked against the SHA-256 recorded there by scripts/fetch-bun.sh: "
            + $"run scripts/fetch-bun.sh from a checkout of the repository (it installs .bun/{rid}/{binary}), or name a Bun with --bun <path> or the {EnvironmentVariable} variable.");
    }

    private static string? FindPin(string start)
    {
        for (var folder = start; folder is not null; folder = System.IO.Path.GetDirectoryName(folder))
            if (File.Exists(System.IO.Path.Combine(folder, "bun.pin"))) return System.IO.Path.Combine(folder, "bun.pin");
        return null;
    }

    private static string? PinValue(string pinFile, string key) =>
        File.ReadLines(pinFile).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal)) is { } line ? line[(key.Length + 1)..] : null;

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

}
