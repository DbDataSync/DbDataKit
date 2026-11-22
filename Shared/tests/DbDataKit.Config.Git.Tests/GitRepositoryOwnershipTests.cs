using System.Diagnostics;
using DbDataKit.Config.Git;
using LibGit2Sharp;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

public class GitRepositoryOwnershipTests
{
    private static LibGit2SharpException NotOwned(string path) => new($"repository path '{path.Replace('\\', '/')}' is not owned by current user");

    [Fact]
    public void A_repository_another_account_owns_fails_naming_the_folder_its_owner_the_account_and_the_fix()
    {
        using var repo = new Repo();
        var opens = 0;
        var ownership = new GitRepositoryOwnership
        {
            OwnerOf = _ => @"CONTOSO\ada",
            Account = @"NT SERVICE\DbDataGateway",
        };

        var error = Assert.Throws<GitRepositoryOwnershipException>(() => ownership.Open(repo.Root, root => { opens++; throw NotOwned(root); }));

        Assert.Equal(1, opens);                                               // nothing is allowed and nothing is tried again
        Assert.Contains($"'{repo.Root}'", error.Message);
        Assert.Contains("is owned by CONTOSO\\ada", error.Message);
        Assert.Contains(@"NT SERVICE\DbDataGateway, the account this runs as, does not own it", error.Message);
        Assert.Contains(OperatingSystem.IsWindows() ? "/setowner \"NT SERVICE\\DbDataGateway\"" : "chown -R", error.Message);
        Assert.IsType<LibGit2SharpException>(error.InnerException);
    }

    [Fact]
    public void Another_error_of_git_is_not_taken_for_an_owner()
    {
        using var repo = new Repo();
        var ownership = new GitRepositoryOwnership();

        var error = Assert.Throws<LibGit2SharpException>(() => ownership.Open(repo.Root, _ => throw new LibGit2SharpException("the index is locked")));

        Assert.Equal("the index is locked", error.Message);
    }

    [Fact]
    public void The_path_git_refused_is_read_from_its_message()
    {
        Assert.Equal("C:/ProgramData/DbDataKit/gateway/config",
            GitRepositoryOwnership.RefusedPath(new LibGit2SharpException("repository path 'C:/ProgramData/DbDataKit/gateway/config' is not owned by current user")));
        Assert.Null(GitRepositoryOwnership.RefusedPath(new LibGit2SharpException("the index is locked")));
        Assert.True(GitRepositoryOwnership.IsNotOwned(new InvalidOperationException("wrapped", NotOwned("/srv/config"))));
    }
}

/// <summary>The tests that change libgit2's settings for the whole process: they run alone, never beside another test of the assembly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Libgit2GlobalSettings
{
    public const string Name = "libgit2 global settings";
}

[Collection(Libgit2GlobalSettings.Name)]
public class GitRepositoryOwnershipLibgit2Tests
{
    /// <summary>
    /// libgit2 itself: a repository whose folder another user owns is refused, and the open fails in words; nothing is allowed, so a second open is
    /// refused too. Needs a second owner, which only root (or passwordless sudo, as on the hosted Linux runner) can give; Windows needs the restore
    /// privilege for it and is checked by the Windows install test. The global, XDG, system and ProgramData git settings point at an empty folder while
    /// it runs, so an allow list of the machine (a <c>safe.directory</c> in the runner image's git settings) cannot open the folder.
    /// </summary>
    [Fact]
    public void Libgit2_refuses_a_folder_another_user_owns_and_the_open_says_so()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("giving a folder to another owner needs the restore privilege on Windows");
        if (!CanChown()) Assert.Skip("needs root or passwordless sudo to give a folder to another user");

        using var repo = new Repo();
        var empty = Directory.CreateTempSubdirectory("dbdata-git-empty-").FullName;
        // the levels libgit2 keeps a search path for (the repository's own and the worktree's have none)
        ConfigurationLevel[] levels = [ConfigurationLevel.Global, ConfigurationLevel.Xdg, ConfigurationLevel.System, ConfigurationLevel.ProgramData];
        var saved = levels.ToDictionary(l => l, l => GlobalSettings.GetConfigSearchPaths(l).ToArray());
        try
        {
            foreach (var level in levels) GlobalSettings.SetConfigSearchPaths(level, empty);
            GiveAway(repo.Root);

            var error = Assert.Throws<GitRepositoryOwnershipException>(() => new GitRepositoryOwnership().Open(repo.Root));

            Assert.Contains($"'{repo.Root}'", error.Message);
            Assert.Contains("is owned by", error.Message);
            Assert.True(GitRepositoryOwnership.IsNotOwned(Assert.ThrowsAny<LibGit2SharpException>(() => new Repository(repo.Root))));
        }
        finally
        {
            foreach (var (level, paths) in saved) GlobalSettings.SetConfigSearchPaths(level, paths);
            TakeBack(repo.Root);
        }
    }

    private static bool CanChown() => Environment.UserName == "root" || Run("sudo", "-n", "true");

    private static void GiveAway(string root)
    {
        Assert.True(Run(Sudo("chmod", "-R", "a+rwX", root)));
        Assert.True(Run(Sudo("chown", "65534", root, Path.Combine(root, ".git"))));
    }

    private static void TakeBack(string root) => Run(Sudo("chown", "-R", Environment.UserName, root));

    private static string[] Sudo(params string[] command) => Environment.UserName == "root" ? command : ["sudo", "-n", .. command];

    private static bool Run(params string[] command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command[0], command[1..]) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false });
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
