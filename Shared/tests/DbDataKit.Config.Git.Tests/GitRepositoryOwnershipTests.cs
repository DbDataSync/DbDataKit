using System.Diagnostics;
using DbDataKit.Config.Git;
using LibGit2Sharp;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

public class GitRepositoryOwnershipTests
{
    private static LibGit2SharpException NotOwned(string path) => new($"repository path '{path.Replace('\\', '/')}' is not owned by current user");

    [Fact]
    public void A_repository_another_account_owns_and_this_one_can_write_is_opened_with_only_its_folder_allowed()
    {
        using var repo = new Repo();
        var allowed = new List<string>();
        var opens = 0;
        var ownership = new GitRepositoryOwnership(Path.Combine(repo.Root, "home"))
        {
            CanWrite = _ => true,
            OwnerOf = _ => @"CONTOSO\ada",
            Account = @"NT SERVICE\dbdatagateway",
            Allow = allowed.AddRange,
        };

        var service = ownership.Open(repo.Root, root =>
        {
            // refused until its folder is allowed, as libgit2 does for a folder another account owns
            opens++;
            if (!allowed.Contains(GitRepositoryOwnership.SafeDirectoryPath(root))) throw NotOwned(root);
            return new GitCommitService(root);
        });

        Assert.Equal(repo.Root, service.RepositoryRoot);
        Assert.Equal(2, opens);
        Assert.Equal([GitRepositoryOwnership.SafeDirectoryPath(repo.Root)], allowed.Distinct());   // that folder and nothing else
    }

    [Fact]
    public void A_repository_this_account_cannot_write_fails_naming_the_folder_its_owner_the_account_and_the_fix()
    {
        using var repo = new Repo();
        var allowed = new List<string>();
        var gitFolder = Path.Combine(repo.Root, ".git");
        var ownership = new GitRepositoryOwnership(Path.Combine(repo.Root, "home"))
        {
            CanWrite = folder => folder != gitFolder,
            OwnerOf = _ => @"CONTOSO\ada",
            Account = @"NT SERVICE\dbdatagateway",
            Allow = allowed.AddRange,
        };

        var error = Assert.Throws<GitRepositoryOwnershipException>(() => ownership.Open(repo.Root, root => throw NotOwned(root)));

        Assert.Contains($"'{repo.Root}'", error.Message);
        Assert.Contains($"'{gitFolder}' is owned by CONTOSO\\ada", error.Message);
        Assert.Contains(@"NT SERVICE\dbdatagateway, the account this runs as, cannot write to it", error.Message);
        Assert.Contains(OperatingSystem.IsWindows() ? "/setowner \"NT SERVICE\\dbdatagateway\"" : "chown -R", error.Message);
        Assert.IsType<LibGit2SharpException>(error.InnerException);
        Assert.Empty(allowed);                                                // nothing is allowed for a folder it cannot write
    }

    [Fact]
    public void Another_error_of_git_is_not_taken_for_an_owner_and_allows_nothing()
    {
        using var repo = new Repo();
        var allowed = new List<string>();
        var ownership = new GitRepositoryOwnership(Path.Combine(repo.Root, "home")) { CanWrite = _ => true, Allow = allowed.AddRange };

        var error = Assert.Throws<LibGit2SharpException>(() => ownership.Open(repo.Root, _ => throw new LibGit2SharpException("the index is locked")));

        Assert.Equal("the index is locked", error.Message);
        Assert.Empty(allowed);
    }

    [Fact]
    public void The_path_git_refused_is_read_from_its_message_and_allowed_beside_the_folder()
    {
        Assert.Equal("C:/ProgramData/DbDataKit/gateway/config",
            GitRepositoryOwnership.RefusedPath(new LibGit2SharpException("repository path 'C:/ProgramData/DbDataKit/gateway/config' is not owned by current user")));
        Assert.Null(GitRepositoryOwnership.RefusedPath(new LibGit2SharpException("the index is locked")));
        Assert.True(GitRepositoryOwnership.IsNotOwned(new InvalidOperationException("wrapped", NotOwned("/srv/config"))));
    }

    [Fact]
    public void The_settings_file_includes_the_earlier_global_file_first_and_lists_each_allowed_folder()
    {
        var text = GitSafeDirectory.Text(@"C:\Users\ada\.gitconfig", ["C:/ProgramData/DbDataKit/gateway/config"]);

        var include = text.IndexOf("[include]\n\tpath = \"C:/Users/ada/.gitconfig\"", StringComparison.Ordinal);
        var safe = text.IndexOf("[safe]\n\tdirectory = \"C:/ProgramData/DbDataKit/gateway/config\"", StringComparison.Ordinal);
        Assert.True(include >= 0 && safe > include, text);                    // an empty entry in the earlier file cannot clear the folder
        Assert.DoesNotContain("directory = \"*\"", text);
        Assert.DoesNotContain("[include]", GitSafeDirectory.Text(null, ["/srv/config"]));
    }

    /// <summary>
    /// libgit2 itself: a repository whose folder another user owns is refused, opened once allowed, and a second such repository stays refused. Needs a
    /// second owner, which only root (or passwordless sudo, as on the hosted Linux runner) can give; Windows needs the restore privilege for it and is
    /// checked on a workstation.
    /// </summary>
    [Fact]
    public void Libgit2_opens_the_allowed_folder_another_user_owns_and_still_refuses_any_other()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("giving a folder to another owner needs the restore privilege on Windows");
        if (!CanChown()) Assert.Skip("needs root or passwordless sudo to give a folder to another user");

        using var allowedRepo = new Repo();
        using var otherRepo = new Repo();
        var home = Directory.CreateTempSubdirectory("dbdata-git-home-").FullName;
        try
        {
            foreach (var root in new[] { allowedRepo.Root, otherRepo.Root }) GiveAway(root);
            Assert.True(GitRepositoryOwnership.IsNotOwned(Assert.ThrowsAny<LibGit2SharpException>(() => new Repository(allowedRepo.Root))));

            var service = new GitRepositoryOwnership(home).Open(allowedRepo.Root);

            Assert.Empty(service.GetHistory(""));
            using (new Repository(allowedRepo.Root)) { }
            Assert.True(GitRepositoryOwnership.IsNotOwned(Assert.ThrowsAny<LibGit2SharpException>(() => new Repository(otherRepo.Root))));
        }
        finally
        {
            foreach (var root in new[] { allowedRepo.Root, otherRepo.Root }) TakeBack(root);
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
