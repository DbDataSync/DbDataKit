using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using DbDataKit.Scripting;

namespace DbDataKit.Connections.Libraries;

/// <param name="Version">Pinned exactly; a restore never floats.</param>
public sealed record PackageRef(string Id, string Version);

public sealed record LibraryManifest(string Id, string FactoryType, IReadOnlyList<PackageRef> Packages)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a manifest and validates every field (a hand-edited or planted file gets the same checks as input).</summary>
    public static LibraryManifest Parse(string json)
    {
        var m = JsonSerializer.Deserialize<LibraryManifest>(json, Json) ?? throw new LibraryValidationException("manifest", "it is empty");
        LibraryValidation.ValidateName(m.Id, "manifest id");
        LibraryValidation.ValidateFactoryType(m.FactoryType);
        if (m.Packages is null || m.Packages.Count == 0) throw new LibraryValidationException("manifest packages", "there are none");
        foreach (var p in m.Packages) { LibraryValidation.ValidatePackageId(p.Id); LibraryValidation.ValidateVersion(p.Version); }
        return m;
    }
}

/// <summary>On-disk layout under a repository root. Every path is resolved and must stay inside the area it belongs to.</summary>
public static class LibraryPaths
{
    public static string LibrariesDir(string repoRoot) => Path.GetFullPath(Path.Combine(repoRoot, "libraries"));

    /// <summary><c>&lt;root&gt;/libraries/&lt;id&gt;</c> after validating the id and checking the resolved path is a direct child of <c>libraries</c>.</summary>
    public static string LibraryDir(string repoRoot, string id)
    {
        LibraryValidation.ValidateName(id, "library id");
        string libraries = LibrariesDir(repoRoot);
        string dir = Path.GetFullPath(Path.Combine(libraries, id));
        AssertDirectChild(libraries, dir, "library id");
        return dir;
    }

    public static string LibDir(string libraryDir) => Path.Combine(libraryDir, "lib");
    public static string ManifestPath(string libraryDir) => Path.Combine(libraryDir, "library.json");

    /// <summary>Throws unless <paramref name="path"/> is strictly inside <paramref name="root"/> (not the root itself, not a sibling with the same prefix).</summary>
    public static void AssertInside(string root, string path, string field = "path")
    {
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!p.StartsWith(r, cmp) || p.Length == r.Length) throw new LibraryValidationException(field, "it resolves outside its directory");
    }

    private static void AssertDirectChild(string parent, string child, string field)
    {
        AssertInside(parent, child, field);
        if (!string.Equals(Path.GetDirectoryName(child), Path.TrimEndingDirectorySeparator(parent), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new LibraryValidationException(field, "it is not a direct child of its directory");
    }

    /// <summary>Deletes a library directory only after proving it is a direct child of <c>libraries</c>; never follows a reparse point out.</summary>
    public static void DeleteLibraryDir(string repoRoot, string id)
    {
        string dir = LibraryDir(repoRoot, id);
        if (!Directory.Exists(dir)) return;
        if (new DirectoryInfo(dir).LinkTarget is not null) { Directory.Delete(dir, false); return; } // remove the link, not what it points at
        Directory.Delete(dir, recursive: true);
    }
}

/// <summary>Everything the restore will do, decided and validated up front. Nothing here has touched the disk or the network.</summary>
public sealed record LibraryInstallPlan(
    string Id, IReadOnlyList<PackageRef> Packages, string FactoryType, string ProjectXml,
    string LibraryDir, string LibDir, string ManifestPath, string Source, string RuntimeIdentifier)
{
    /// <summary>The <c>dotnet</c> argument list for the restore, one element per argument (never joined into a shell string).</summary>
    public IReadOnlyList<string> PublishArguments(string projectPath, string outputDir) =>
        ["publish", projectPath, "-c", "Release", "-o", outputDir, "--nologo", "--source", Source];
}

public static class LibraryInstallPlanner
{
    /// <summary>Validates every input and builds the plan. Throws <see cref="LibraryValidationException"/> for any hostile or malformed value.</summary>
    public static LibraryInstallPlan Plan(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string factoryType,
        string? source = null, NuGetSourcePolicy? sources = null, string? runtimeIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        string libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        if (packages is null || packages.Count == 0) throw new LibraryValidationException("packages", "there are none");
        if (packages.Count > 20) throw new LibraryValidationException("packages", "there are more than 20");
        foreach (var p in packages) { LibraryValidation.ValidatePackageId(p.Id); LibraryValidation.ValidateVersion(p.Version); }
        LibraryValidation.ValidateFactoryType(factoryType);
        string feed = (sources ?? NuGetSourcePolicy.NuGetOrgOnly).Resolve(source);
        string rid = runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
        LibraryValidation.ValidateRuntimeIdentifier(rid);

        return new LibraryInstallPlan(id, packages, factoryType, ProjectFile.Build(packages, rid), libraryDir,
            LibraryPaths.LibDir(libraryDir), LibraryPaths.ManifestPath(libraryDir), feed, rid);
    }
}

/// <summary>The throwaway restore project. Built with <see cref="XElement"/>, so ids and versions are escaped as attribute values and cannot add elements.</summary>
public static class ProjectFile
{
    public static string Build(IReadOnlyList<PackageRef> packages, string runtimeIdentifier, string targetFramework = "net10.0")
    {
        var group = new XElement("PropertyGroup",
            new XElement("TargetFramework", targetFramework),
            new XElement("Nullable", "disable"),
            new XElement("RuntimeIdentifier", runtimeIdentifier),
            new XElement("SelfContained", "false"),
            new XElement("NuGetAudit", "true"));
        // the feed is passed with --source on the command line, never written into the project
        var items = new XElement("ItemGroup", packages.Select(p => new XElement("PackageReference", new XAttribute("Include", p.Id), new XAttribute("Version", p.Version))));
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), group, items);
        return new XDocument(project).ToString();
    }
}

/// <summary>Runs the restore for a plan: produces a flat output directory holding the package closure.</summary>
public interface ILibraryRestoreExecutor
{
    Task RestoreAsync(LibraryInstallPlan plan, string projectDir, string outputDir, CancellationToken ct);
}

/// <summary>Runs <c>dotnet publish</c> with an argument list. The only place that starts a process.</summary>
public sealed class DotnetPublishExecutor(string dotnet = "dotnet") : ILibraryRestoreExecutor
{
    public async Task RestoreAsync(LibraryInstallPlan plan, string projectDir, string outputDir, CancellationToken ct)
    {
        string project = Path.Combine(projectDir, "library.csproj");
        await File.WriteAllTextAsync(project, plan.ProjectXml, ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in plan.PublishArguments(project, outputDir)) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"`dotnet publish` failed (exit code {process.ExitCode}) restoring {string.Join(", ", plan.Packages.Select(p => p.Id + " " + p.Version))}.");
    }
}

/// <summary>Executes a plan: restore into a temp directory, then replace <c>lib/</c> and write the manifest, with every path checked.</summary>
public static class LibraryInstaller
{
    public static async Task<LibraryManifest> InstallAsync(LibraryInstallPlan plan, ILibraryRestoreExecutor executor, CancellationToken ct = default)
    {
        string temp = Path.Combine(Path.GetTempPath(), "dbdata-library-" + Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(temp, "out");
        Directory.CreateDirectory(temp);
        try
        {
            await executor.RestoreAsync(plan, temp, outDir, ct).ConfigureAwait(false);
            if (!Directory.Exists(outDir)) throw new InvalidOperationException("The restore produced no output.");

            LibraryPaths.AssertInside(plan.LibraryDir, plan.LibDir, "lib directory");
            if (Directory.Exists(plan.LibDir)) Directory.Delete(plan.LibDir, recursive: true); // a fresh lib/: no stale DLL from a previous version
            Directory.CreateDirectory(plan.LibDir);
            CopyAll(outDir, plan.LibDir);

            var manifest = new LibraryManifest(plan.Id, plan.FactoryType, plan.Packages);
            await File.WriteAllTextAsync(plan.ManifestPath, manifest.ToJson(), ct).ConfigureAwait(false);
            return manifest;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CopyAll(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, file)));
            LibraryPaths.AssertInside(destination, target, "output file");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
