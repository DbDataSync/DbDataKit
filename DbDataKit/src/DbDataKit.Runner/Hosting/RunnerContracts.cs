using DbDataKit.Runner;

namespace DbDataKit.Runner;

/// <summary>
/// The contracts this runner serves (plan 30 section 6.1, plan 28a DR4): compiled code depends on the .NET runtime, the kit and the runner protocol it was built against, so a publish whose manifest names others is refused,
/// naming both, and asks for a republish. Majors are compared: the runtime and the kit change their contract with a major version.
/// </summary>
public static class RunnerContracts
{
    /// <summary>The kit contract's major version this runner serves.</summary>
    public const int KitMajor = 1;

    public static string Dotnet { get; } = $"{Environment.Version.Major}.{Environment.Version.Minor}";

    public static string Protocol => RunnerProtocol.V1;

    /// <summary>What is wrong with the contracts a package was built against, in words naming both sides; null when this runner can serve it.</summary>
    public static string? Check(BuiltAgainst built)
    {
        if (built.Protocol is { } protocol && protocol != Protocol)
            return $"the package was built against the runner protocol '{protocol}' and this runner speaks '{Protocol}'; build it against a kit that speaks '{Protocol}' and publish it again.";
        if (built.Dotnet is { } dotnet && Major(dotnet) is { } major && major != Environment.Version.Major)
            return $"the package was built against .NET {dotnet} and this runner runs .NET {Dotnet}; build it for .NET {Dotnet} and publish it again.";
        if (built.Kit is { } kit && Major(kit) is { } kitMajor && kitMajor != KitMajor)
            return $"the package was built against kit contract {kit} and this runner serves kit contract {KitMajor}; build it against kit {KitMajor} and publish it again.";
        return null;
    }

    private static int? Major(string version)
    {
        var first = version.Split('.')[0];
        return int.TryParse(first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) ? major : null;
    }
}
