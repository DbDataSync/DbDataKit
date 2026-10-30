using DbDataKit.Config.Git;
using LibGit2Sharp;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

/// <summary>What a person does with a draft (plan 13, W08): see, commit, read the history, push, and merge into the checkout, where a conflict changes nothing and is named.</summary>
public sealed class GitWorkspaceOperationsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdata-git-ws-").FullName;
    private readonly GitAuthor _ada = new("Ada", "ada@example.com");
    private string Checkout => Path.Combine(_root, "checkout");
    private string Draft => Path.Combine(_root, "draft");
    private string _base = "";

    public GitWorkspaceOperationsTests()
    {
        Directory.CreateDirectory(Checkout);
        Repository.Init(Checkout);
        File.WriteAllText(Path.Combine(Checkout, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(Checkout, "b.txt"), "keep\n");
        using var repo = new Repository(Checkout);
        Commands.Stage(repo, "*");
        var who = new Signature("Root", "root@example.com", DateTimeOffset.Now);
        _base = repo.Commit("first", who, who).Sha;
        GitWorktrees.Add(Checkout, "ws/ada-draft", Draft);
    }

    /// <summary>Git on Windows may write the checkout with CRLF; what is compared is the text.</summary>
    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Changes_lists_edits_new_files_and_deletions_with_their_patches()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(Draft, "c.txt"), "new\n");
        File.Delete(Path.Combine(Draft, "b.txt"));

        var changes = GitWorkspaceOperations.Changes(Draft, _base);

        Assert.Equal(["a.txt:modified", "b.txt:deleted", "c.txt:added"], changes.Select(c => $"{c.Path}:{c.Status}").Order().ToList());
        Assert.Contains("+two", changes.Single(c => c.Path == "a.txt").Patch);
    }

    [Fact]
    public void Changes_carry_both_texts_of_a_file_for_a_side_by_side_view_and_a_patch_only_for_binary_or_large_files()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(Draft, "c.txt"), "new\n");
        File.Delete(Path.Combine(Draft, "b.txt"));
        File.WriteAllBytes(Path.Combine(Draft, "d.bin"), [0, 1, 2, 0, 3]);
        File.WriteAllText(Path.Combine(Draft, "big.txt"), new string('x', 50));

        var changes = GitWorkspaceOperations.Changes(Draft, _base, maxSideChars: 40).ToDictionary(c => c.Path);

        Assert.True(changes["a.txt"].Sides);
        Assert.Equal("one", changes["a.txt"].Before!.Trim());
        Assert.Equal("two", changes["a.txt"].After!.Trim());
        Assert.Null(changes["c.txt"].Before);
        Assert.Equal("new", changes["c.txt"].After!.Trim());
        Assert.Equal("keep", changes["b.txt"].Before!.Trim());
        Assert.Null(changes["b.txt"].After);
        Assert.False(changes["d.bin"].Sides);
        Assert.False(changes["big.txt"].Sides);
        Assert.Null(changes["big.txt"].After);
    }

    [Fact]
    public void Uncommitted_changes_are_reported_until_a_commit_takes_them()
    {
        Assert.False(GitWorkspaceOperations.HasUncommitted(Draft));
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(Draft, "new.txt"), "new\n");
        Assert.True(GitWorkspaceOperations.HasUncommitted(Draft));

        GitWorkspaceOperations.Commit(Draft, "Take them", _ada);
        Assert.False(GitWorkspaceOperations.HasUncommitted(Draft));

        File.WriteAllText(Path.Combine(Draft, "a.txt"), "three\n");      // saved again after the commit
        Assert.True(GitWorkspaceOperations.HasUncommitted(Draft));
    }

    [Fact]
    public void A_commit_records_everything_in_the_authors_name_and_an_unchanged_draft_has_nothing_to_commit()
    {
        Assert.Null(GitWorkspaceOperations.Commit(Draft, "nothing", _ada));
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        File.Delete(Path.Combine(Draft, "b.txt"));

        var sha = GitWorkspaceOperations.Commit(Draft, "Change a", _ada);

        var history = GitWorkspaceOperations.History(Draft, _base);
        Assert.Equal(sha, history.Single().Sha);
        Assert.Equal(("Change a", "Ada"), (history[0].Message, history[0].AuthorName));
        Assert.Equal("one\n", Read(Path.Combine(Checkout, "a.txt")));    // the checkout did not move
    }

    [Fact]
    public void A_draft_that_started_where_the_checkout_is_merges_by_moving_the_branch()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        var sha = GitWorkspaceOperations.Commit(Draft, "Change a", _ada);

        var merged = GitWorkspaceOperations.Merge(Checkout, "ws/ada-draft", _ada);

        Assert.True(merged.Merged);
        Assert.True(merged.FastForward);
        Assert.Equal(sha, merged.Sha);
        Assert.Equal("two\n", Read(Path.Combine(Checkout, "a.txt")));
    }

    [Fact]
    public void When_the_checkout_has_moved_on_a_merge_commit_is_made_in_the_authors_name()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        GitWorkspaceOperations.Commit(Draft, "Change a", _ada);
        File.WriteAllText(Path.Combine(Checkout, "b.txt"), "other\n");
        GitWorkspaceOperations.Commit(Checkout, "Change b", new GitAuthor("Bo", "bo@example.com"));

        var merged = GitWorkspaceOperations.Merge(Checkout, "ws/ada-draft", _ada);

        Assert.True(merged.Merged);
        Assert.False(merged.FastForward);
        using var repo = new Repository(Checkout);
        Assert.Equal("Ada", repo.Head.Tip.Author.Name);
        Assert.Equal(2, repo.Head.Tip.Parents.Count());
        Assert.Equal(("two\n", "other\n"), (Read(Path.Combine(Checkout, "a.txt")), Read(Path.Combine(Checkout, "b.txt"))));
    }

    [Fact]
    public void A_conflict_changes_nothing_and_names_the_files()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "draft\n");
        GitWorkspaceOperations.Commit(Draft, "Draft a", _ada);
        File.WriteAllText(Path.Combine(Checkout, "a.txt"), "main\n");
        var tip = GitWorkspaceOperations.Commit(Checkout, "Main a", new GitAuthor("Bo", "bo@example.com"));

        var merged = GitWorkspaceOperations.Merge(Checkout, "ws/ada-draft", _ada);

        Assert.False(merged.Merged);
        Assert.Equal(["a.txt"], merged.Conflicts);
        using var repo = new Repository(Checkout);
        Assert.Equal(tip, repo.Head.Tip.Sha);
        Assert.Equal("main\n", Read(Path.Combine(Checkout, "a.txt")));
        Assert.False(repo.RetrieveStatus().IsDirty);
    }

    [Fact]
    public void A_checkout_with_uncommitted_changes_is_not_merged_into()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        GitWorkspaceOperations.Commit(Draft, "Change a", _ada);
        File.WriteAllText(Path.Combine(Checkout, "b.txt"), "dirty\n");

        var ex = Assert.Throws<GitOperationException>(() => GitWorkspaceOperations.Merge(Checkout, "ws/ada-draft", _ada));

        Assert.Contains("not committed", ex.Message);
    }

    [Fact]
    public void A_branch_is_pushed_to_origin_and_a_repository_without_one_says_so()
    {
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        var sha = GitWorkspaceOperations.Commit(Draft, "Change a", _ada);
        Assert.Contains("no remote", Assert.Throws<GitOperationException>(() => GitWorkspaceOperations.Push(Draft, "ws/ada-draft")).Message);

        var bare = Path.Combine(_root, "remote.git");
        Repository.Init(bare, isBare: true);
        using (var repo = new Repository(Checkout)) repo.Network.Remotes.Add("origin", bare);

        GitWorkspaceOperations.Push(Draft, "ws/ada-draft");

        using var remote = new Repository(bare);
        Assert.Equal(sha, remote.Branches["ws/ada-draft"].Tip.Sha);
    }
}
