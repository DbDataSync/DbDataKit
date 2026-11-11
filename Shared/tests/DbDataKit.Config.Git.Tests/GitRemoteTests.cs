using DbDataKit.Config.Git;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

/// <summary>A server's working copy of a remote (plan 13, R02): cloned, then made equal to the remote's branch; failures name the problem and never a credential.</summary>
public sealed class GitRemoteTests : IDisposable
{
    private readonly Repo _remote = new();
    private readonly string _work = Path.Combine(Directory.CreateTempSubdirectory("dbdata-git-work-").FullName, "clone");

    public void Dispose()
    {
        _remote.Dispose();
        var parent = Path.GetDirectoryName(_work)!;
        try
        {
            foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(parent, true);
        }
        catch (IOException) { }
    }

    private string Commit(string file, string content, string message)
    {
        _remote.Write(file, content);
        return _remote.Commit(message, null, file)!;
    }

    [Fact]
    public void An_absent_folder_is_cloned_to_the_remotes_head()
    {
        var head = Commit("a.txt", "one", "first");

        var result = GitRemote.CloneOrUpdate(_remote.Root, null, _work);

        Assert.True(result.Cloned);
        Assert.Equal(head, result.Head);
        Assert.Equal("one", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    [Fact]
    public void An_existing_clone_is_brought_to_the_remotes_branch_and_local_changes_are_discarded()
    {
        Commit("a.txt", "one", "first");
        GitRemote.CloneOrUpdate(_remote.Root, null, _work);
        var next = Commit("a.txt", "two", "second");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "edited here");

        var result = GitRemote.CloneOrUpdate(_remote.Root, null, _work);

        Assert.False(result.Cloned);
        Assert.Equal(next, result.Head);
        Assert.Equal("two", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    [Fact]
    public void A_branch_the_remote_does_not_have_is_named_in_the_error()
    {
        Commit("a.txt", "one", "first");
        GitRemote.CloneOrUpdate(_remote.Root, null, _work);

        var ex = Assert.Throws<GitRemoteException>(() => GitRemote.CloneOrUpdate(_remote.Root, "nope", _work));

        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void A_folder_with_other_things_in_it_is_not_cloned_into()
    {
        Commit("a.txt", "one", "first");
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "mine.txt"), "x");

        Assert.Throws<GitRemoteException>(() => GitRemote.CloneOrUpdate(_remote.Root, null, _work));
        Assert.True(File.Exists(Path.Combine(_work, "mine.txt")));
    }

    [Fact]
    public void A_remote_that_is_not_there_fails_and_the_error_has_no_password()
    {
        var url = "https://ada:hunter2@127.0.0.1:1/nothing.git";

        var ex = Assert.Throws<GitRemoteException>(() => GitRemote.CloneOrUpdate(url, null, _work));

        Assert.DoesNotContain("hunter2", ex.Message);
    }
}
