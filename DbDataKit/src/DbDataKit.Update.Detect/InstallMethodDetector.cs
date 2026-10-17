using System.Text.RegularExpressions;

namespace DbDataKit.Update.Detect;

/// <summary>Everything detection needs, as plain data so it can be tested without a machine.</summary>
public sealed record InstallEnvironment(
    string ProcessPath,
    string BaseDirectory,
    IReadOnlyDictionary<string, string?> Env,
    bool HasDockerEnvFile = false)
{
    public static InstallEnvironment FromCurrent()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in new[] { "DOTNET_RUNNING_IN_CONTAINER", "KUBERNETES_SERVICE_HOST", "SCOOP", "SCOOP_GLOBAL", "DOTNET_TOOLS_PATH" })
            env[k] = Environment.GetEnvironmentVariable(k);
        return new InstallEnvironment(Environment.ProcessPath ?? "", AppContext.BaseDirectory, env, File.Exists("/.dockerenv"));
    }
}

public sealed record InstallDetection(InstallMethod Method, string Evidence);

/// <summary>Pure functions: decide how a CLI was installed from its path and environment.</summary>
public static partial class InstallMethodDetector
{
    [GeneratedRegex(@"/versions/\d+\.\d+\.\d+[^/]*/")]
    private static partial Regex SideBySide();

    /// <summary>Normalise to lowercase forward-slash form with a trailing slash so containment checks are separator-agnostic.</summary>
    public static string Norm(string? p) => string.IsNullOrWhiteSpace(p) ? "" : p.Replace('\\', '/').ToLowerInvariant().TrimEnd('/') + "/";

    private static string? Get(InstallEnvironment e, string key) => e.Env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public static InstallDetection Detect(InstallEnvironment e)
    {
        // Containers first: in-place update prompts must never appear there, whatever the path looks like.
        if (string.Equals(Get(e, "DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)) return new(InstallMethod.Container, "DOTNET_RUNNING_IN_CONTAINER=true");
        if (Get(e, "KUBERNETES_SERVICE_HOST") is not null) return new(InstallMethod.Container, "KUBERNETES_SERVICE_HOST is set");
        if (e.HasDockerEnvFile) return new(InstallMethod.Container, "/.dockerenv exists");

        var proc = Norm(e.ProcessPath);
        var bas = Norm(e.BaseDirectory);
        var paths = new[] { proc, bas };

        foreach (var root in new[] { Get(e, "SCOOP"), Get(e, "SCOOP_GLOBAL") })
        {
            if (root is null) continue;
            var r = Norm(root);
            if (paths.Any(p => p.StartsWith(r + "apps/", StringComparison.Ordinal) || p.StartsWith(r + "shims/", StringComparison.Ordinal)))
                return new(InstallMethod.Scoop, $"path is under SCOOP root {root}");
        }
        if (paths.Any(p => p.Contains("/scoop/apps/") || p.Contains("/scoop/shims/"))) return new(InstallMethod.Scoop, "path contains \\scoop\\apps or \\scoop\\shims");

        if (paths.Any(p => p.Contains("/microsoft/winget/packages/") || p.Contains("/microsoft/winget/links/"))) return new(InstallMethod.Winget, "path is under the WinGet packages/links folder");

        var toolsPath = Get(e, "DOTNET_TOOLS_PATH");
        if (toolsPath is not null && paths.Any(p => p.StartsWith(Norm(toolsPath), StringComparison.Ordinal))) return new(InstallMethod.DotnetTool, "path is under DOTNET_TOOLS_PATH");
        if (paths.Any(p => p.Contains("/.dotnet/tools/"))) return new(InstallMethod.DotnetTool, "path is under ~/.dotnet/tools");
        if (bas.Contains("/.store/") && bas.Contains("/tools/")) return new(InstallMethod.DotnetTool, "base directory is inside a dotnet tool store");

        if (paths.Any(p => SideBySide().IsMatch(p)))
            return new(InstallMethod.ServiceSwap, "path is inside a versions/<ver>/ side-by-side layout");

        return string.IsNullOrEmpty(proc) && string.IsNullOrEmpty(bas)
            ? new(InstallMethod.Unknown, "no path information")
            : new(InstallMethod.Archive, "no package-manager, tool-store or container marker found");
    }
}
