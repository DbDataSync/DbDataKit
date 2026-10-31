using DbDataKit.Config.Git;
using Xunit;

namespace DbDataKit.Config.Git.Tests;

/// <summary>The "open a pull request" link built from a remote's address (plan 13, W12): one test per provider and per address form (https, ssh, scp); a remote that is none of the three has no link.</summary>
public class GitPullRequestLinkTests
{
    [Theory]
    [InlineData("https://github.com/acme/sales.git", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    [InlineData("https://github.com/acme/sales", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    [InlineData("https://github.com/acme/sales/", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    [InlineData("https://ada:token@github.com/acme/sales.git", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    [InlineData("git@github.com:acme/sales.git", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    [InlineData("ssh://git@github.com/acme/sales.git", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    [InlineData("ssh://git@github.com:22/acme/sales.git", "https://github.com/acme/sales/pull/new/ws/ada-draft")]
    public void GitHub(string remote, string expected) => Assert.Equal(expected, GitPullRequestLink.For(remote, "ws/ada-draft"));

    [Theory]
    [InlineData("https://dev.azure.com/acme/Data%20Platform/_git/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    [InlineData("https://acme@dev.azure.com/acme/Data%20Platform/_git/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Data%20Platform/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/acme/Data%20Platform/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    [InlineData("https://acme.visualstudio.com/Data%20Platform/_git/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    [InlineData("https://acme.visualstudio.com/DefaultCollection/Data%20Platform/_git/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    [InlineData("acme@vs-ssh.visualstudio.com:v3/acme/Data%20Platform/sales", "https://dev.azure.com/acme/Data%20Platform/_git/sales/pullrequestcreate?sourceRef=ws%2Fada-draft")]
    public void AzureDevOps(string remote, string expected) => Assert.Equal(expected, GitPullRequestLink.For(remote, "ws/ada-draft"));

    [Theory]
    [InlineData("https://gitlab.com/acme/sales.git", "https://gitlab.com/acme/sales/-/merge_requests/new?merge_request%5Bsource_branch%5D=ws%2Fada-draft")]
    [InlineData("https://gitlab.com/acme/data/sales.git", "https://gitlab.com/acme/data/sales/-/merge_requests/new?merge_request%5Bsource_branch%5D=ws%2Fada-draft")]
    [InlineData("https://oauth2:token@gitlab.com/acme/data/sales.git", "https://gitlab.com/acme/data/sales/-/merge_requests/new?merge_request%5Bsource_branch%5D=ws%2Fada-draft")]
    [InlineData("git@gitlab.com:acme/data/sales.git", "https://gitlab.com/acme/data/sales/-/merge_requests/new?merge_request%5Bsource_branch%5D=ws%2Fada-draft")]
    [InlineData("ssh://git@gitlab.com/acme/sales.git", "https://gitlab.com/acme/sales/-/merge_requests/new?merge_request%5Bsource_branch%5D=ws%2Fada-draft")]
    public void GitLab(string remote, string expected) => Assert.Equal(expected, GitPullRequestLink.For(remote, "ws/ada-draft"));

    [Theory]
    [InlineData("https://git.example.com/acme/sales.git")]
    [InlineData("git@git.example.com:acme/sales.git")]
    [InlineData("https://bitbucket.org/acme/sales.git")]
    [InlineData("/srv/git/sales.git")]
    [InlineData("C:\\git\\sales.git")]
    [InlineData("file:///srv/git/sales.git")]
    [InlineData("http://127.0.0.1:8080/sales.git")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://dev.azure.com/acme/sales")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_remote_has_no_link(string? remote) => Assert.Null(GitPullRequestLink.For(remote, "ws/ada-draft"));

    [Fact]
    public void A_branch_name_is_encoded() =>
        Assert.Equal("https://dev.azure.com/o/p/_git/r/pullrequestcreate?sourceRef=ws%2Fa%20b%26c", GitPullRequestLink.For("https://dev.azure.com/o/p/_git/r", "ws/a b&c"));
}
