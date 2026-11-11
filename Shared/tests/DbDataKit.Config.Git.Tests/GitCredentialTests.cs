using DbDataKit.Config.Git;
using LibGit2Sharp;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

/// <summary>
/// Git credentials (plan 13, W12) against a stand-in server that requires them: a push and a clone are refused without the login and with a wrong one, work with the right one, the refusal says which credential in words, and the token is
/// nowhere afterwards: not in a message, not in the repository's configuration, not in any file of the working copy.
/// </summary>
public sealed class GitCredentialTests : IDisposable
{
    private const string Token = "ghp_S3cretTokenValue0123456789abcdef";
    private readonly AuthenticatedGitServer _server = new("oauth2", Token);
    private readonly string _root = Directory.CreateTempSubdirectory("dbdata-git-cred-").FullName;
    private readonly GitAuthor _ada = new("Ada", "ada@example.com");
    private readonly GitCredential _good = new("oauth2", Token, "dbdata:build:git:sales");
    private string Draft => Path.Combine(_root, "draft");
    private string _remoteUrl = "";

    public GitCredentialTests()
    {
        _remoteUrl = _server.CreateBare("sales");
        // a checkout with one commit and a draft on a branch, whose origin is the authenticated remote
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(checkout);
        Repository.Init(checkout);
        File.WriteAllText(Path.Combine(checkout, "a.txt"), "one\n");
        using (var repo = new Repository(checkout))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("Root", "root@example.com", DateTimeOffset.Now);
            repo.Commit("first", who, who);
            repo.Network.Remotes.Add("origin", _remoteUrl);
        }
        GitWorktrees.Add(checkout, "ws/ada-draft", Draft);
        File.WriteAllText(Path.Combine(Draft, "a.txt"), "two\n");
        GitWorkspaceOperations.Commit(Draft, "Change a", _ada);
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private bool RemoteHasBranch() { using var remote = new Repository(_server.PathOf("sales")); return remote.Branches["ws/ada-draft"] is not null; }

    /// <summary>Every file under the folder, as text: the token must be in none of them.</summary>
    private static IEnumerable<string> FilesContaining(string folder, string needle) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => File.ReadAllBytes(f).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(needle)) >= 0);

    [Fact]
    public void A_push_is_refused_without_the_credential_and_nothing_reaches_the_remote()
    {
        var ex = Assert.Throws<GitOperationException>(() => GitWorkspaceOperations.Push(Draft, "ws/ada-draft"));

        Assert.Contains("asks for a login", ex.Message);
        Assert.False(RemoteHasBranch());
        Assert.All(_server.Requests, r => Assert.False(r.Authorised));
        Assert.NotEmpty(_server.Requests);
    }

    [Fact]
    public void A_push_with_a_wrong_token_names_the_credential_that_was_refused_and_not_the_token()
    {
        var wrong = _good with { Token = "not-the-token-1234567" };

        var ex = Assert.Throws<GitOperationException>(() => GitWorkspaceOperations.Push(Draft, "ws/ada-draft", wrong));

        Assert.Contains("dbdata:build:git:sales", ex.Message);
        Assert.Contains("refused", ex.Message);
        Assert.DoesNotContain("not-the-token-1234567", ex.ToString());
        Assert.False(RemoteHasBranch());
    }

    [Fact]
    public void A_push_with_the_credential_lands_on_the_remote_and_the_token_is_kept_nowhere()
    {
        GitWorkspaceOperations.Push(Draft, "ws/ada-draft", _good);

        using (var remote = new Repository(_server.PathOf("sales")))
            Assert.Equal("Change a", remote.Branches["ws/ada-draft"].Tip.Message.Trim());
        Assert.Contains(_server.Requests, r => r.Authorised && r.Method == "POST");
        Assert.Empty(FilesContaining(_root, Token));                                   // no working copy, no .git/config, no worktree record holds it
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(_root, "checkout", ".git", "config")));
        using var repo = new Repository(Draft);
        Assert.Equal(_remoteUrl, repo.Network.Remotes["origin"].Url);                 // the address stays as it was given: no user, no token
    }

    [Fact]
    public void A_clone_and_a_later_update_need_the_credential_and_leave_no_trace_of_it()
    {
        var work = Path.Combine(_root, "clone");

        var refused = Assert.Throws<GitRemoteException>(() => GitRemote.CloneOrUpdate(_remoteUrl, null, work));
        Assert.Contains("asks for a login", refused.Message);
        GitWorkspaceOperations.Push(Draft, "ws/ada-draft", _good);
        var result = GitRemote.CloneOrUpdate(_remoteUrl, "ws/ada-draft", work, _good);
        var again = GitRemote.CloneOrUpdate(_remoteUrl, "ws/ada-draft", work, _good);
        var wrong = Assert.Throws<GitRemoteException>(() => GitRemote.CloneOrUpdate(_remoteUrl, "ws/ada-draft", work, _good with { Token = "nope-nope-nope" }));

        Assert.True(result.Cloned);
        Assert.False(again.Cloned);
        Assert.Contains("dbdata:build:git:sales", wrong.Message);
        Assert.DoesNotContain("nope-nope-nope", wrong.ToString());
        Assert.Empty(FilesContaining(work, Token));
    }

    [Theory]
    [InlineData("s3cr3t", "oauth2", "s3cr3t")]
    [InlineData("deploy-user:s3cr3t", "deploy-user", "s3cr3t")]
    [InlineData("  s3cr3t\n", "oauth2", "s3cr3t")]
    [InlineData("a:b:c", "a", "b:c")]
    public void A_secrets_value_is_a_token_or_user_and_token(string value, string user, string token)
    {
        var credential = GitCredential.Parse(value, "dbdata:build:git:x");

        Assert.Equal((user, token), (credential.Username, credential.Token));
        Assert.DoesNotContain(token, credential.ToString());
    }

    [Fact]
    public void An_empty_secret_is_refused_by_name()
    {
        Assert.Contains("dbdata:build:git:x", Assert.Throws<FormatException>(() => GitCredential.Parse("  ", "dbdata:build:git:x")).Message);
    }
}
