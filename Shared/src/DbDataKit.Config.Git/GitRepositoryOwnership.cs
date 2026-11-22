using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace DbDataKit.Config.Git;

/// <summary>
/// Opens a server's own configuration repository, and says in words why git refuses it when another account owns its folder.
/// <para>
/// Git, and libgit2 with it, refuses a repository whose folder is owned by someone other than the account that opens it ("repository path '...' is not
/// owned by current user"). The service makes its configuration repository itself at its first start, so the folder is the service account's; a folder
/// another account made (an administrator's command before the service first started, a copy from another machine) stops the start with a message that
/// names the folder, its owner, the account and the fix. The ownership check is never turned off and no folder is allowed despite its owner.
/// </para>
/// Design: docs/install.md#a-service-owns-its-data
/// </summary>
public sealed partial class GitRepositoryOwnership
{
    /// <summary>The text libgit2 puts in the error when it refuses a repository it finds owned by another account.</summary>
    public const string NotOwnedText = "is not owned by current user";

    /// <summary>The account that owns the folder, for the message.</summary>
    public Func<string, string> OwnerOf { get; init; } = OwnerOfFolder;

    /// <summary>The account this process runs as, for the message.</summary>
    public string Account { get; init; } = CurrentAccount();

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
    /// <exception cref="GitRepositoryOwnershipException">Git refused the repository for its owner.</exception>
    public GitCommitService Open(string repositoryRoot, Func<string, GitCommitService>? open = null)
    {
        open ??= OpenAndCheck;
        try
        {
            return open(repositoryRoot);
        }
        catch (Exception error) when (IsNotOwned(error))
        {
            // libgit2 names the folder it refused: the repository's own or its .git
            var refused = RefusedPath(error) is { } named && Directory.Exists(named) ? Path.GetFullPath(named) : repositoryRoot;
            throw new GitRepositoryOwnershipException(NotOwnedMessage(repositoryRoot, refused), error);
        }
    }

    /// <summary>The message when git refuses the repository for the owner of <paramref name="folder"/>: the folder, its owner, the account and the fix.</summary>
    public string NotOwnedMessage(string repositoryRoot, string folder) =>
        $"The configuration repository '{repositoryRoot}' cannot be opened: '{folder}' is owned by {OwnerOf(folder)}, and {Account}, the account this runs as, " +
        $"does not own it, so git refuses the repository. {Fix(repositoryRoot)}";

    private string Fix(string repositoryRoot) => OperatingSystem.IsWindows()
        ? $"As an administrator, make {Account} the owner of the folder and what is in it (icacls \"{repositoryRoot}\" /setowner \"{Account}\" /T /C), then start it again."
        : $"Make {Account} the owner of the folder and what is in it (sudo chown -R {Account} '{repositoryRoot}'), then start it again.";

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

/// <summary>A configuration repository that git refuses for its owner; the message says what to do.</summary>
public sealed class GitRepositoryOwnershipException(string message, Exception inner) : Exception(message, inner);
