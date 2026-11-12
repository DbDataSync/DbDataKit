using DbDataKit.Config.Yaml;
using DbDataKit.Scripting;

namespace DbDataKit.Runner.Packaging;

/// <summary>What a project folder is, as far as building it goes: C# source files built here with Roslyn, or a .NET project that needs the SDK.</summary>
public enum IntegrationLanguage
{
    /// <summary><c>src/**/*.csx</c>: C# files with <c>#r "nuget: Id, 1.2.3"</c> lines, built by <see cref="CsxPackageBuilder"/> with no SDK.</summary>
    Csx,

    /// <summary>A <c>.csproj</c> at the root or in <c>src/</c>: built with <c>dotnet publish</c>, which needs the .NET SDK (<see cref="DotnetSdk"/>).</summary>
    CompiledProject,
}

/// <summary>One finding of a build or a test, in the shape the Workspace's kinds answer: <see cref="Severity"/> is <c>error</c>, <c>warning</c> or <c>info</c>, <see cref="File"/> is relative to the project with <c>/</c> between the parts.</summary>
public sealed record BuildDiagnostic(string Severity, string Code, string Message, string? File = null, int? Line = null)
{
    public bool IsError => Severity == "error";

    public static BuildDiagnostic Error(string code, string message, string? file = null, int? line = null) => new("error", code, message, file, line);

    public static BuildDiagnostic From(ConfigDiagnostic d, string file) =>
        new(d.Severity == ConfigSeverity.Error ? "error" : d.Severity == ConfigSeverity.Warning ? "warning" : "info", "DBD-INT-MANIFEST", d.Message, file, d.Line > 0 ? d.Line : null);

    public override string ToString() => File is null ? Message : Line is { } line ? $"{File}:{line}: {Message}" : $"{File}: {Message}";
}

/// <summary>The layout of an integration project (docs/integrations.md): <c>package.yaml</c> (the <c>package/1</c> manifest, hand-written), <c>src/</c> the code, <c>tests/*.csx</c> the tests, <c>files/</c> anything else the package carries, <c>package.lock</c> the pins of the packages.</summary>
public static class IntegrationProject
{
    public const string ManifestFile = "package.yaml";
    public const string LockFile = "package.lock";
    public const string SourceFolder = "src";
    public const string TestFolder = "tests";
    public const string ExtraFilesFolder = "files";

    /// <summary>Which way the project builds, or null when it holds neither C# files nor a project file.</summary>
    public static IntegrationLanguage? Detect(string root)
    {
        if (ProjectFile(root) is not null) return IntegrationLanguage.CompiledProject;
        return CsxFiles(root).Any() ? IntegrationLanguage.Csx : null;
    }

    /// <summary>The <c>.csproj</c> of a compiled project: in the root, else in <c>src/</c>; null when there is none.</summary>
    public static string? ProjectFile(string root)
    {
        foreach (var folder in new[] { root, Path.Combine(root, SourceFolder) })
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).FirstOrDefault() is { } found) return found;
        return null;
    }

    /// <summary>The code files, in a fixed order (the order is part of what makes a build reproducible).</summary>
    public static IEnumerable<string> CsxFiles(string root)
    {
        var folder = Path.Combine(root, SourceFolder);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.csx", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];
    }

    public static IEnumerable<string> TestFiles(string root)
    {
        var folder = Path.Combine(root, TestFolder);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.csx", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];
    }

    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>Whether the .NET SDK is on this machine, which a compiled project needs and a csx project does not.</summary>
public static class DotnetSdk
{
    public const string Missing = "This integration is a compiled .NET project, and building it needs the .NET SDK, which is not installed on the machine that builds (only the runtime is). Install the .NET SDK there and build again, "
        + "or write the integration as C# files under src/ (csx), which this server builds without the SDK.";

    /// <summary>The <c>dotnet</c> program when it is on <c>PATH</c> (or in <c>DOTNET_ROOT</c>) <b>and</b> has an SDK beside it; null otherwise (a machine with only the runtime has the host program but no <c>sdk/</c> folder).</summary>
    public static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) folders.Add(root);
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var folder in folders)
            try
            {
                var candidate = Path.Combine(folder, name);
                if (!File.Exists(candidate)) continue;
                // `dotnet` is usually a link into the real install: the sdk/ folder is beside the real file
                var real = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                var sdk = Path.Combine(Path.GetDirectoryName(real)!, "sdk");
                if (Directory.Exists(sdk) && Directory.EnumerateDirectories(sdk).Any()) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* a bad entry of PATH */ }
        return null;
    }
}
