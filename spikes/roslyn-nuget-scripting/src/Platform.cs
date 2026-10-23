using System.Runtime.InteropServices;

namespace RoslynNuGetScripting;

static class Platform
{
    /// <summary>
    /// Runtime identifiers to look for under runtimes/, most specific first. NuGet's runtime graph (runtime.json) is the full
    /// answer; this hand-written chain covers the targets the suite ships on and is the part to extend for others.
    /// </summary>
    public static string[] RidChain()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        if (OperatingSystem.IsWindows()) return [$"win-{arch}", "win", "any"];
        if (OperatingSystem.IsMacOS()) return [$"osx-{arch}", "osx", "unix", "any"];
        // The runtime's own RID says "linux-musl-x64" on Alpine; packages ship musl natives in a separate folder.
        return RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal)
            ? [$"linux-musl-{arch}", $"linux-{arch}", "linux", "unix", "any"]
            : [$"linux-{arch}", "linux", "unix", "any"];
    }

    /// <summary>File names a [DllImport("name")] may mean on this OS, in the order the runtime tries them.</summary>
    public static IEnumerable<string> NativeNameCandidates(string name)
    {
        yield return name;
        var (prefix, suffix) = OperatingSystem.IsWindows() ? ("", ".dll") : OperatingSystem.IsMacOS() ? ("lib", ".dylib") : ("lib", ".so");
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) yield return name + suffix;
        yield return prefix + name + suffix;
    }

    /// <summary>
    /// Implementation assemblies of the shared frameworks the process runs on (Microsoft.NETCore.App, and Microsoft.AspNetCore.App
    /// in the web host), by simple name. The app's own assemblies are deliberately not in this set: those are the host's business.
    /// </summary>
    public static Dictionary<string, string> FrameworkAssemblies()
    {
        var marker = $"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}Microsoft.";
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (path.Contains(marker, StringComparison.Ordinal))
                result[Path.GetFileNameWithoutExtension(path)] = path;
        return result;
    }
}
