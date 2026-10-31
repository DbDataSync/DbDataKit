using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>The extracted packages of a library are not the repository's: only <c>library.csx</c> and <c>library.lock</c> are, so a git repository ignores the store.</summary>
public sealed class LibraryStoreGitignoreTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"dbdatasync-libignore-{Guid.NewGuid():N}");

    public LibraryStoreGitignoreTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void A_git_repository_gets_the_rule_once_and_keeps_its_own_lines()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "secrets/");

        LibraryPaths.EnsureStoreGitignored(_repo);
        LibraryPaths.EnsureStoreGitignored(_repo);

        Assert.Equal(["secrets/", "libraries/*/lib/"], File.ReadAllLines(Path.Combine(_repo, ".gitignore")));
    }

    [Fact]
    public void A_folder_that_is_not_a_git_repository_is_left_alone()
    {
        LibraryPaths.EnsureStoreGitignored(_repo);

        Assert.False(File.Exists(Path.Combine(_repo, ".gitignore")));
    }
}
