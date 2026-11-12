using DbDataKit.Config.Git;
using LibGit2Sharp;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

/// <summary>Plan 22a GP19: a configuration repository pushes each commit to its remote in the background; the remote down never fails a commit, shows in the status, and catches up when it returns.</summary>
public sealed class GitRemotePusherTests : IDisposable
{
    private const string Token = "ghp_PushTokenValue0123456789abcdef";
    private readonly string _root = Directory.CreateTempSubdirectory("dbdata-git-push-").FullName;
    private readonly GitAuthor _ada = new("Ada", "ada@example.com");

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private (GitCommitService Commits, string Config) NewConfig()
    {
        var config = Path.Combine(_root, "config");
        var commits = new GitCommitService(config);
        return (commits, config);
    }

    private string Commit(GitCommitService commits, string config, string file, string text)
    {
        var path = Path.Combine(config, file);
        File.WriteAllText(path, text);
        return commits.CommitChanges([path], "write " + file, _ada)!;
    }

    private static int Commits(string bare) { using var repo = new Repository(bare); return repo.Head.Tip is null ? 0 : repo.Commits.Count(); }

    [Fact]
    public async Task Each_commit_reaches_the_remote_in_the_background_with_the_commit_already_made()
    {
        var bare = Path.Combine(_root, "remote.git");
        Repository.Init(bare, isBare: true);
        var (commits, config) = NewConfig();
        using var pusher = new GitRemotePusher(config, bare, retry: TimeSpan.FromMilliseconds(50));
        commits.Committed += pusher.Notify;

        var first = Commit(commits, config, "a.yaml", "one\n");
        var second = Commit(commits, config, "b.yaml", "two\n");
        Assert.True(await pusher.WaitIdleAsync(TimeSpan.FromSeconds(20)), pusher.Status.LastFailure);
        Assert.Equal(2, Commits(bare));
        Assert.Equal(second, pusher.Status.LastPushedSha);
        Assert.NotEqual(first, second);
        Assert.False(pusher.Status.Failing);
    }

    [Fact]
    public async Task With_the_remote_down_the_commit_succeeds_the_status_shows_the_failure_and_the_push_catches_up_when_it_returns()
    {
        var bare = Path.Combine(_root, "later.git");                 // not there yet: the remote is down
        var (commits, config) = NewConfig();
        using var pusher = new GitRemotePusher(config, bare, retry: TimeSpan.FromMilliseconds(100));
        commits.Committed += pusher.Notify;

        Assert.NotNull(Commit(commits, config, "a.yaml", "one\n"));        // the commit is not refused
        Assert.NotNull(Commit(commits, config, "b.yaml", "two\n"));
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!pusher.Status.Failing && DateTimeOffset.UtcNow < until) await Task.Delay(20);
        Assert.True(pusher.Status.Failing);
        Assert.True(pusher.Status.Pending);
        Assert.NotNull(pusher.Status.LastFailure);
        Assert.Null(pusher.Status.LastPushedAt);

        Repository.Init(bare, isBare: true);                             // the remote returns
        Assert.True(await pusher.WaitIdleAsync(TimeSpan.FromSeconds(30)), pusher.Status.LastFailure);
        Assert.Equal(2, Commits(bare));                                  // everything committed meanwhile, in one catch-up
        Assert.NotNull(pusher.Status.LastPushedAt);
        Assert.Null(pusher.Status.LastFailure);
    }

    [Fact]
    public async Task The_credential_is_presented_to_a_remote_that_requires_it_and_appears_nowhere_a_wrong_one_is_a_visible_failure()
    {
        using var server = new AuthenticatedGitServer("oauth2", Token);
        var url = server.CreateBare("config");
        var (commits, config) = NewConfig();
        var credential = new GitCredential("oauth2", "wrong-token-value", "dbdatakit:test:git:config");
        using var pusher = new GitRemotePusher(config, url, () => credential, TimeSpan.FromMilliseconds(100));
        commits.Committed += pusher.Notify;

        Commit(commits, config, "a.yaml", "one\n");
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!pusher.Status.Failing && DateTimeOffset.UtcNow < until) await Task.Delay(20);
        Assert.True(pusher.Status.Failing);
        Assert.DoesNotContain("wrong-token-value", pusher.Status.LastFailure);
        Assert.Equal(0, Commits(server.PathOf("config")));

        credential = new GitCredential("oauth2", Token, "dbdatakit:test:git:config");        // the credential is read again for each try
        Assert.True(await pusher.WaitIdleAsync(TimeSpan.FromSeconds(30)), pusher.Status.LastFailure);
        Assert.Equal(1, Commits(server.PathOf("config")));

        // the token is in no file of the repository, in none of its configuration, and not in the status
        var found = Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories).Where(f => File.ReadAllBytes(f).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(Token)) >= 0);
        Assert.Empty(found);
        Assert.DoesNotContain(Token, pusher.Status.Remote);
    }

    [Fact]
    public void A_remote_address_with_a_login_in_it_is_refused_and_the_status_never_shows_one()
    {
        var (_, config) = NewConfig();
        var refused = Assert.Throws<GitRemoteException>(() => new GitRemotePusher(config, "https://oauth2:" + Token + "@git.example.com/org/repo.git"));
        Assert.DoesNotContain(Token, refused.Message);
        Assert.Contains("git.remote_credential", refused.Message);
    }
}
