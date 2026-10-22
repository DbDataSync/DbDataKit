using DbDataKit.Config.Git;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

public sealed class Repo : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("dbdata-git-").FullName;
    public GitCommitService Service { get; }
    public Repo() => Service = new GitCommitService(Root);

    public string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string? Commit(string message, GitAuthor? author = null, params string[] paths) =>
        Service.CommitChanges(paths.Select(p => Path.Combine(Root, p)).ToArray(), message, author ?? new GitAuthor("Ada", "ada@console-a"));

    public void Dispose()
    {
        // libgit2 writes its objects read-only, which a plain delete refuses on Windows
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public class GitCommitServiceTests
{
    [Fact]
    public void A_service_makes_the_repository_and_heals_it_when_the_git_folder_goes()
    {
        using var repo = new Repo();
        Assert.True(GitCommitService.IsRepositoryAt(repo.Root));
        Assert.Equal(repo.Root, repo.Service.RepositoryRoot);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo.Root, ".git"), "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path.Combine(repo.Root, ".git"), true);
        Assert.Empty(repo.Service.GetHistory("a"));                       // the next use makes it again
        Assert.True(GitCommitService.IsRepositoryAt(repo.Root));
    }

    [Fact]
    public void A_folder_inside_another_repository_is_not_taken_for_one_of_its_own()
    {
        using var outer = new Repo();
        var inner = Path.Combine(outer.Root, "nested", "config");
        Directory.CreateDirectory(inner);
        Assert.False(GitCommitService.IsRepositoryAt(inner));
        _ = new GitCommitService(inner);
        Assert.True(GitCommitService.IsRepositoryAt(inner));              // it made its own
    }

    [Fact]
    public void A_commit_is_attributed_to_the_author_it_is_given_and_records_what_was_staged()
    {
        using var repo = new Repo();
        repo.Write("connections/erp.yaml", "name: erp\n");
        var sha = repo.Commit("add erp", new GitAuthor("Grace Hopper", "grace@ops-console"), "connections/erp.yaml");

        Assert.NotNull(sha);
        var commit = Assert.Single(repo.Service.GetHistory("connections"));
        Assert.Equal(sha, commit.Sha);
        Assert.Equal("add erp", commit.Message);
        Assert.Equal("Grace Hopper", commit.AuthorName);
        Assert.Equal("grace@ops-console", commit.AuthorEmail);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - commit.WhenUtc).TotalMinutes) < 5);
    }

    [Fact]
    public void Saving_the_same_content_again_records_nothing()
    {
        using var repo = new Repo();
        repo.Write("a/x.yaml", "one\n");
        Assert.NotNull(repo.Commit("first", null, "a/x.yaml"));
        Assert.Null(repo.Commit("again", null, "a/x.yaml"));
        Assert.Single(repo.Service.GetHistory("a"));
    }

    [Fact]
    public void History_is_scoped_to_a_path_newest_first_and_limited()
    {
        using var repo = new Repo();
        repo.Write("a/one.yaml", "1\n"); repo.Commit("a1", null, "a/one.yaml");
        repo.Write("b/one.yaml", "1\n"); repo.Commit("b1", null, "b/one.yaml");
        repo.Write("a/one.yaml", "2\n"); repo.Commit("a2", null, "a/one.yaml");
        repo.Write("a/two.yaml", "1\n"); repo.Commit("a3", null, "a/two.yaml");

        Assert.Equal(["a3", "a2", "a1"], repo.Service.GetHistory("a").Select(c => c.Message).ToArray());
        Assert.Equal(["b1"], repo.Service.GetHistory("b").Select(c => c.Message).ToArray());
        Assert.Equal(["a3", "a2"], repo.Service.GetHistory("a", limit: 2).Select(c => c.Message).ToArray());
        Assert.Equal(["a2", "a1"], repo.Service.GetHistory("a/one.yaml").Select(c => c.Message).ToArray());        // one file
        Assert.Equal(4, repo.Service.GetHistory("").Count);                                                         // no prefix: the whole repository
        Assert.Empty(repo.Service.GetHistory("ab"));                                                                // "a" is a folder, not a string prefix
    }

    [Fact]
    public void A_commits_diff_has_both_versions_and_the_first_commit_reads_as_everything_added()
    {
        using var repo = new Repo();
        repo.Write("a/x.yaml", "old\n");
        var first = repo.Commit("first", null, "a/x.yaml")!;
        repo.Write("a/x.yaml", "new\n");
        var second = repo.Commit("second", null, "a/x.yaml")!;

        var added = repo.Service.GetCommitDiff("a", first);
        var change = Assert.Single(added.Changes);
        Assert.Equal((ConfigChangeKind.Added, null, "old\n"), (change.Kind, change.Before, change.After));

        var modified = Assert.Single(repo.Service.GetCommitDiff("a", second).Changes);
        Assert.Equal((ConfigChangeKind.Modified, "old\n", "new\n", "a/x.yaml"), (modified.Kind, modified.Before, modified.After, modified.Path));
    }

    [Fact]
    public void A_diff_shows_only_the_files_under_the_path_asked_for_and_an_abbreviated_sha_works()
    {
        using var repo = new Repo();
        repo.Write("a/x.yaml", "1\n"); repo.Write("b/y.yaml", "1\n");
        var sha = repo.Commit("both", null, "a/x.yaml", "b/y.yaml")!;

        Assert.Equal(["a/x.yaml"], repo.Service.GetCommitDiff("a", sha).Changes.Select(c => c.Path).ToArray());
        Assert.Equal(2, repo.Service.GetCommitDiff("", sha).Changes.Count);
        Assert.Equal(sha, repo.Service.GetCommitDiff("a", sha[..10]).Sha);
        Assert.Equal("both", repo.Service.GetCommitDiff("a", sha).Message);
    }

    [Fact]
    public void A_sha_that_is_not_a_commit_is_named_at_once()
    {
        using var repo = new Repo();
        repo.Write("a/x.yaml", "1\n"); repo.Commit("one", null, "a/x.yaml");
        var ex = Assert.Throws<GitCommitNotFoundException>(() => repo.Service.GetCommitDiff("a", "0123456789abcdef0123456789abcdef01234567"));
        Assert.Contains("0123456789abcdef", ex.Message);
        Assert.Throws<ArgumentException>(() => repo.Service.GetCommitDiff("a", " "));
    }

    [Fact]
    public void A_restore_preview_is_the_change_from_now_to_then_and_the_files_then_can_be_read()
    {
        using var repo = new Repo();
        repo.Write("a/x.yaml", "v1\n");
        var first = repo.Commit("v1", null, "a/x.yaml")!;
        repo.Write("a/x.yaml", "v2\n"); repo.Write("a/added-later.yaml", "z\n");
        repo.Commit("v2", null, "a/x.yaml", "a/added-later.yaml");

        var preview = repo.Service.GetRestoreDiff("a", first);
        var byPath = preview.Changes.ToDictionary(c => c.Path);
        Assert.Equal((ConfigChangeKind.Modified, "v2\n", "v1\n"), (byPath["a/x.yaml"].Kind, byPath["a/x.yaml"].Before, byPath["a/x.yaml"].After));
        Assert.Equal(ConfigChangeKind.Deleted, byPath["a/added-later.yaml"].Kind);                                  // restoring would remove it

        var then = repo.Service.GetTextFilesAt("a", first);
        Assert.Equal(["a/x.yaml"], then.Keys.ToArray());
        Assert.Equal("v1\n", then["a/x.yaml"]);
        Assert.Equal("v1\n", repo.Service.GetTextFilesAt("a/x.yaml", first)["a/x.yaml"]);                           // a single file works too
    }

    [Fact]
    public void A_diff_over_its_budget_still_lists_every_file_but_drops_the_content_and_says_so()
    {
        using var repo = new Repo();
        repo.Write("a/big.yaml", new string('x', 5000)); repo.Write("a/small.yaml", "tiny\n");
        var sha = repo.Commit("two", null, "a/big.yaml", "a/small.yaml")!;

        var diff = repo.Service.GetCommitDiff("a", sha, maxContentBytes: 1000);
        Assert.True(diff.Truncated);
        Assert.Equal(["a/big.yaml", "a/small.yaml"], diff.Changes.Select(c => c.Path).ToArray());
        var big = diff.Changes.Single(c => c.Path == "a/big.yaml");
        Assert.Null(big.After);
        Assert.Equal("tiny\n", diff.Changes.Single(c => c.Path == "a/small.yaml").After);
        Assert.False(repo.Service.GetCommitDiff("a", sha).Truncated);
    }

    [Fact]
    public void A_renamed_file_is_reported_as_renamed_and_a_deleted_one_as_deleted()
    {
        using var repo = new Repo();
        repo.Write("a/old-name.yaml", string.Join("\n", Enumerable.Range(0, 30).Select(i => "line " + i)) + "\n");
        repo.Commit("create", null, "a/old-name.yaml");
        File.Move(Path.Combine(repo.Root, "a/old-name.yaml"), Path.Combine(repo.Root, "a/new-name.yaml"));
        var sha = repo.Commit("rename", null, "a/old-name.yaml", "a/new-name.yaml")!;
        Assert.Equal(ConfigChangeKind.Renamed, Assert.Single(repo.Service.GetCommitDiff("a", sha).Changes).Kind);

        File.Delete(Path.Combine(repo.Root, "a/new-name.yaml"));
        var deleted = repo.Commit("delete", null, "a/new-name.yaml")!;
        Assert.Equal(ConfigChangeKind.Deleted, Assert.Single(repo.Service.GetCommitDiff("a", deleted).Changes).Kind);
    }

    [Fact]
    public async Task Many_commits_at_once_from_one_service_all_land_without_a_locked_index()
    {
        using var repo = new Repo();
        const int count = 40;
        for (var i = 0; i < count; i++) repo.Write($"c/f{i}.yaml", $"{i}\n");

        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() => repo.Commit($"c{i}", null, $"c/f{i}.yaml"))));

        Assert.Equal(count, repo.Service.GetHistory("c", limit: 1000).Count);
        Assert.Equal(count, repo.Service.GetTextFilesAt("c", repo.Service.GetHistory("c", 1)[0].Sha).Count);       // and the last commit holds every file
    }
}
