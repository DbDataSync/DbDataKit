using System.Runtime.InteropServices;

namespace DbDataKit.Scripting;

/// <summary>What the machine is, as the resolver needs it: the runtime identifiers to look for under <c>runtimes/</c>, and the assemblies the host's shared framework already provides.</summary>
public static class Platform
{
    /// <summary>
    /// Runtime identifiers to look for under runtimes/, most specific first. NuGet's runtime graph (runtime.json) is the full answer; this hand-written chain covers the targets the suite ships on and is the part to extend for others.
    /// </summary>
    public static IReadOnlyList<string> RidChain(string os, string arch, bool musl = false) => os switch
    {
        "windows" => [$"win-{arch}", "win", "any"],
        "macos" => [$"osx-{arch}", "osx", "unix", "any"],
        // packages ship musl natives in a separate folder
        _ => musl ? [$"linux-musl-{arch}", $"linux-{arch}", "linux", "unix", "any"] : [$"linux-{arch}", "linux", "unix", "any"],
    };

    /// <summary>The chain for the machine this process runs on.</summary>
    public static IReadOnlyList<string> RidChain()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        return RidChain(os, arch, RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal));
    }

    /// <summary>
    /// Implementation assemblies of the shared frameworks the process runs on (Microsoft.NETCore.App, and Microsoft.AspNetCore.App in the web host), by simple name. The app's own assemblies are deliberately not in this set:
    /// those are the host's business, and a script cannot reach them by accident.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FrameworkAssemblies()
    {
        var marker = $"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}Microsoft.";
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (path.Contains(marker, StringComparison.Ordinal))
                result[Path.GetFileNameWithoutExtension(path)] = path;
        return result;
    }

    /// <summary>
    /// Packages that exist only to carry framework references. They are downloaded when something depends on them (nothing here knows better) but contribute no assets: the framework the host runs on provides what they stand for.
    /// </summary>
    public static readonly IReadOnlySet<string> FrameworkOwnedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "NETStandard.Library", "Microsoft.NETCore.Platforms", "Microsoft.NETCore.Targets", "Microsoft.NETCore.App", "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App", "Microsoft.AspNetCore.App.Ref",
        "Microsoft.NETFramework.ReferenceAssemblies", "Microsoft.NETFramework.ReferenceAssemblies.net461",
        // the polyfills a netstandard2.0 package asks for; every one of them is inbox on the runtimes this host runs on, so downloading them would only fetch assemblies that are then ignored (and fail offline)
        "System.Memory", "System.Buffers", "System.Numerics.Vectors", "System.Runtime.CompilerServices.Unsafe", "System.Threading.Tasks.Extensions", "System.ValueTuple", "Microsoft.Bcl.AsyncInterfaces",
    };
}
