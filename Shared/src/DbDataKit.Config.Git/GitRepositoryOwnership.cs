using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace DbDataKit.Config.Git;

/// <summary>
/// Opens a server's own configuration repository when another account owns its folder.
/// <para>
/// Git, and libgit2 with it, refuses a repository whose folder is owned by someone other than the account that opens it ("repository path '...' is not
/// owned by current user"). A configuration folder made by an elevated administrator on a Windows client edition is owned by that administrator, not by the
/// Administrators group, so the service's own account cannot open it although the folder's access list gives that account write access. When the account
/// can write the folder (and its <c>.git</c>), that one folder is allowed the way git allows one (<c>safe.directory</c>); the ownership check stays on for
/// every other path. When it cannot write it, opening fails with a message that names the folder, its owner and how to fix it.
/// </para>
/// Design: docs/install.md#the-gateways-configuration-repository
/// </summary>
public sealed partial class GitRepositoryOwnership
{
    /// <summary>The text libgit2 puts in the error when it refuses a repository it finds owned by another account.</summary>
    public const string NotOwnedText = "is not owned by current user";

    private readonly string _allowListHome;

    /// <param name="allowListHome">A folder of the server's own (in its data folder) where the git settings that allow the folder are written when they are needed.</param>
    public GitRepositoryOwnership(string allowListHome) => _allowListHome = allowListHome;

    /// <summary>Whether this account can create and remove a file in the folder.</summary>
    public Func<string, bool> CanWrite { get; init; } = CanWriteFolder;

    /// <summary>The account that owns the folder, for the message.</summary>
    public Func<string, string> OwnerOf { get; init; } = OwnerOfFolder;

    /// <summary>The account this process runs as, for the message.</summary>
    public string Account { get; init; } = CurrentAccount();

    /// <summary>Allows one repository folder despite its owner. The default writes it to git's settings for this process (<see cref="GitSafeDirectory"/>).</summary>
    public Action<IReadOnlyCollection<string>>? Allow { get; init; }

    /// <summary>True when the exception is libgit2 refusing a repository owned by another account.</summary>
    public static bool IsNotOwned(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is LibGit2SharpException && e.Message.Contains(NotOwnedText, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The repository service for <paramref name="repositoryRoot"/>, after checking that libgit2 opens it. <paramref name="open"/> replaces the default
    /// (make the service, then open the repository once) in tests.
    /// </summary>
    public GitCommitService Open(string repositoryRoot, Func<string, GitCommitService>? open = null)
    {
        open ??= OpenAndCheck;
        try
        {
            return open(repositoryRoot);
        }
        catch (Exception error) when (IsNotOwned(error))
        {
            var folders = new List<string> { repositoryRoot };
            var gitFolder = Path.Combine(repositoryRoot, ".git");
            if (Directory.Exists(gitFolder)) folders.Add(gitFolder);
            if (folders.FirstOrDefault(f => !CanWrite(f)) is { } locked)
                throw new GitRepositoryOwnershipException(CannotWriteMessage(repositoryRoot, locked), error);

            var paths = new List<string> { SafeDirectoryPath(repositoryRoot) };
            if (RefusedPath(error) is { } refused && !paths.Contains(refused, StringComparer.Ordinal)) paths.Add(refused);
            (Allow ?? (p => GitSafeDirectory.Allow(p, _allowListHome)))(paths);
            try
            {
                return open(repositoryRoot);
            }
            catch (Exception again) when (IsNotOwned(again))
            {
                throw new GitRepositoryOwnershipException(
                    $"The configuration repository '{repositoryRoot}' is owned by {OwnerOf(repositoryRoot)}, and git still refuses it to {Account} after it was allowed " +
                    $"in '{Path.Combine(_allowListHome, GitSafeDirectory.FileName)}'. {Fix(repositoryRoot)}", again);
            }
        }
    }

    /// <summary>The message when this account cannot write a folder of the repository: the folder, its owner, the account and the fix.</summary>
    public string CannotWriteMessage(string repositoryRoot, string folder) =>
        $"The configuration repository '{repositoryRoot}' cannot be opened: '{folder}' is owned by {OwnerOf(folder)}, and {Account}, the account this runs as, " +
        $"cannot write to it, so git refuses the repository. {Fix(repositoryRoot)}";

    private string Fix(string repositoryRoot) => OperatingSystem.IsWindows()
        ? $"As an administrator, make {Account} the owner of the folder (icacls \"{repositoryRoot}\" /setowner \"{Account}\" /T /C) or give it Modify access to the folder, then start it again."
        : $"Make {Account} the owner of the folder (sudo chown -R {Account} '{repositoryRoot}') or give it write access, then start it again.";

    /// <summary>The folder as libgit2 compares it with a <c>safe.directory</c> entry: absolute, forward slashes, no trailing slash.</summary>
    public static string SafeDirectoryPath(string folder) => Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');

    /// <summary>The path libgit2 named in its refusal, which is the form it compares the allow list with.</summary>
    public static string? RefusedPath(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (RefusedPathPattern().Match(e.Message) is { Success: true } m) return m.Groups[1].Value.TrimEnd('/');
        return null;
    }

    [GeneratedRegex("repository path '(.+)' is not owned by current user")]
    private static partial Regex RefusedPathPattern();

    private static GitCommitService OpenAndCheck(string repositoryRoot)
    {
        var service = new GitCommitService(repositoryRoot);
        // the service makes the repository but opens it only when it is used: open it once here, so a refusal is found at start
        using (new Repository(repositoryRoot)) { }
        return service;
    }

    private static bool CanWriteFolder(string folder)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(folder, $".write-check-{Guid.NewGuid():N}"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string OwnerOfFolder(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsOwner(folder);
            using var stat = Process.Start(new ProcessStartInfo("stat", ["-c", "%U", folder]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (stat is null) return "another account";
            var owner = stat.StandardOutput.ReadToEnd().Trim();
            stat.WaitForExit();
            return stat.ExitCode == 0 && owner.Length > 0 ? owner : "another account";
        }
        catch (Exception)
        {
            return "another account";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsOwner(string folder)
    {
        var sid = new DirectoryInfo(folder).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        if (sid is null) return "another account";
        try { return sid.Translate(typeof(System.Security.Principal.NTAccount)).Value; }
        catch (System.Security.Principal.IdentityNotMappedException) { return sid.Value; }
    }

    private static string CurrentAccount()
    {
        try { return OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().Name : Environment.UserName; }
        catch (Exception) { return Environment.UserName; }
    }
}

/// <summary>A configuration repository that git refuses for its owner and that this account cannot take over; the message says what to do.</summary>
public sealed class GitRepositoryOwnershipException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Git's <c>safe.directory</c> for this process: libgit2 reads it only from the global and system settings files, so the global settings are a file of
/// the server's own that lists the allowed folders and includes the file that was global before, which keeps every other setting as it was.
/// Only the folders named are allowed; the ownership check stays on for every other repository.
/// </summary>
public static class GitSafeDirectory
{
    /// <summary>The name libgit2 looks for in the global search path.</summary>
    public const string FileName = ".gitconfig";

    private static readonly object Gate = new();
    private static readonly SortedSet<string> Allowed = new(StringComparer.Ordinal);
    private static string? _home;
    private static string? _previous;

    /// <summary>Adds <paramref name="folders"/> (in the form <see cref="GitRepositoryOwnership.SafeDirectoryPath"/> gives) to the allowed folders, writing the settings into <paramref name="home"/>.</summary>
    public static void Allow(IReadOnlyCollection<string> folders, string home)
    {
        lock (Gate)
        {
            home = Path.GetFullPath(home);
            if (_home is null)
            {
                _previous = GlobalSettings.GetConfigSearchPaths(ConfigurationLevel.Global)
                    .Where(p => !string.IsNullOrEmpty(p) && !SamePath(p, home))
                    .Select(p => Path.Combine(p, FileName))
                    .FirstOrDefault(File.Exists);
            }
            else if (!SamePath(_home, home))
            {
                // one home per process: a second one would drop the folders the first allowed
                home = _home;
            }

            foreach (var folder in folders) Allowed.Add(folder);
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, FileName), Text(_previous, Allowed));
            // set every time, so the folders stay allowed even if something else in the process moved the global search path since
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, home);
            _home = home;
        }
    }

    /// <summary>
    /// The settings file: the earlier global file included first, then one <c>safe.directory</c> line per folder, so an empty entry in the earlier file
    /// cannot clear them. Lines end in <c>\n</c> on every system; git reads either ending.
    /// </summary>
    public static string Text(string? previous, IEnumerable<string> folders)
    {
        var text = new StringBuilder();
        text.Append("# Written by the server at start: git refused its configuration repository for its owner, and this account can write it.\n");
        if (previous is not null)
        {
            text.Append("[include]\n");
            text.Append($"\tpath = {Quote(previous.Replace('\\', '/'))}\n");
        }
        text.Append("[safe]\n");
        foreach (var folder in folders) text.Append($"\tdirectory = {Quote(folder)}\n");
        return text.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
