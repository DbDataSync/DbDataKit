using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DbData.Update.Service;

/// <summary>
/// Side-by-side layout under a product root:
/// <code>
/// versions\&lt;ver&gt;\   current -> versions\&lt;ver&gt;   config\   updates\{staging,backup,update-plan.json,update-result.json}
/// </code>
/// All path arithmetic that involves externally supplied strings goes through this class.
/// </summary>
public sealed class InstallLayout
{
    private static readonly Regex VersionSegment = new(@"^[0-9A-Za-z][0-9A-Za-z.\-+]*$", RegexOptions.Compiled);

    public string Root { get; }
    public InstallLayout(string root) => Root = Path.GetFullPath(root);

    public string VersionsDir => Path.Combine(Root, "versions");
    public string CurrentLink => Path.Combine(Root, "current");
    public string ConfigDir => Path.Combine(Root, "config");
    public string UpdatesDir => Path.Combine(Root, "updates");
    public string StagingDir => Path.Combine(UpdatesDir, "staging");
    public string BackupDir => Path.Combine(UpdatesDir, "backup");
    public string PlanPath => Path.Combine(UpdatesDir, "update-plan.json");
    public string ResultPath => Path.Combine(UpdatesDir, "update-result.json");

    /// <summary>A version used as a directory name: must be a valid semver and contain no separators or dots-only segments.</summary>
    public static bool IsSafeVersion(string? v)
        => !string.IsNullOrEmpty(v) && v.Length <= 64 && VersionSegment.IsMatch(v) && !v.Contains("..") && ReleaseVersion.TryParse(v, out _);

    public string VersionDir(string version)
    {
        if (!IsSafeVersion(version)) throw new UpdatePlanException($"Unsafe version '{version}'.");
        return Path.Combine(VersionsDir, version);
    }

    /// <summary>Resolve a plan-supplied relative path; must stay inside <c>updates\</c>.</summary>
    public string ResolveUnderUpdates(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0')) throw new UpdatePlanException("Empty path in plan.");
        var n = relative.Replace('\\', '/');
        if (n.StartsWith('/') || (n.Length >= 2 && n[1] == ':') || n.Split('/').Contains("..")) throw new UpdatePlanException($"Path '{relative}' in plan is not a safe relative path.");
        var root = UpdatesDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, n));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UpdatePlanException($"Path '{relative}' escapes the updates directory.");
        return full;
    }

    public IReadOnlyList<string> InstalledVersions()
        => Directory.Exists(VersionsDir)
            ? Directory.EnumerateDirectories(VersionsDir).Select(d => Path.GetFileName(d)!).Where(IsSafeVersion).ToList()
            : [];
}

public sealed class UpdatePlanException(string message) : Exception(message);

/// <summary>What "current" points at. Real implementations: junction (Windows) / symlink (Unix); marker file as fallback and for tests.</summary>
public interface ICurrentPointer
{
    string? CurrentVersion();
    void Set(string version);
}

/// <summary>Portable fallback: a <c>current.txt</c> next to <c>versions\</c>. Services launched via a wrapper can read it.</summary>
public sealed class MarkerFileCurrentPointer(InstallLayout layout) : ICurrentPointer
{
    private string Path_ => System.IO.Path.Combine(layout.Root, "current.txt");

    public string? CurrentVersion() => File.Exists(Path_) ? File.ReadAllText(Path_).Trim() : null;

    public void Set(string version)
    {
        if (!Directory.Exists(layout.VersionDir(version))) throw new DirectoryNotFoundException($"Version {version} is not installed.");
        Directory.CreateDirectory(layout.Root);
        var tmp = Path_ + ".tmp";
        File.WriteAllText(tmp, version);
        File.Move(tmp, Path_, true);
    }
}

/// <summary>Directory junction on Windows (no privilege needed), symbolic link elsewhere.</summary>
public sealed class LinkCurrentPointer(InstallLayout layout) : ICurrentPointer
{
    public string? CurrentVersion()
    {
        var info = new DirectoryInfo(layout.CurrentLink);
        if (!info.Exists) return null;
        var target = info.LinkTarget;
        return target is null ? null : System.IO.Path.GetFileName(target.TrimEnd('\\', '/'));
    }

    public void Set(string version)
    {
        var dir = layout.VersionDir(version);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Version {version} is not installed.");
        var link = layout.CurrentLink;
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(link)) Directory.Delete(link, false); // removes the junction only
            RunMklinkJunction(link, dir);
        }
        else
        {
            var tmp = link + ".new";
            if (Directory.Exists(tmp) || File.Exists(tmp)) File.Delete(tmp);
            Directory.CreateSymbolicLink(tmp, dir);
            File.Move(tmp, link, true); // rename(2): atomic replace
        }
    }

    private static void RunMklinkJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("mklink /J failed: " + p.StandardError.ReadToEnd());
    }
}
