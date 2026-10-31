using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Config.Git.Tests;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// A project with a git credential (plan 13, W12): the server clones and pushes to a remote that requires a login, using the one secret the project names for every author; without it, or with a wrong one, the push is refused with a
/// message that names the credential; the token is in no API answer, no log line and no file outside the encrypted store; the pull request link comes with a successful push.
/// </summary>
public sealed class BuildServerGitCredentialTests : IAsyncLifetime
{
    private const string Token = "glpat-Zx9QwErTyUiOp0123456789";
    private const string Secret = "dbdata:build:git:sales";
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-gitcred-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly AuthenticatedGitServer remote = new("oauth2", Token);
    private readonly List<string> log = [];
    private readonly List<string> answers = [];
    private ProjectsFile projects = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;

    private string ServerDir => Path.Combine(root, "server");
    private string RemoteUrl => remote.BaseAddress + "sales.git";

    private sealed class Quiet : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, """{"ok":true}""", ""));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ServerDir);
        var source = Path.Combine(root, "source");
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", source, "--format", "json"], new StringWriter(), new StringWriter()));
        Repository.Init(source);
        using (var repo = new Repository(source))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            repo.Commit("first", who, who);
        }
        Repository.Clone(source, remote.PathOf("sales"), new CloneOptions { IsBare = true });

        projects = new ProjectsFile(ServerDir);
        var settings = new BuildServerSettings(ServerDir, new Uri("https://127.0.0.1:0"));
        app = BuildServer.Build(settings, BuildServer.Certificate(ServerDir), new Quiet(), projects, new TuiCommand.CliHost(_ => null).Commands, line => { lock (log) log.Add(line); });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Approver | ServerRole.Author));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        remote.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch (IOException) { }
    }

    private async Task SetSecret(string value)
    {
        using var secret = SecretValue.From(value);
        await ServerSecrets.Open(ServerDir).SetAsync(SecretName.Parse(Secret), secret);
    }

    private async Task<HttpResponseMessage> AsAda(HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "ada", ServerRole.Author)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        answers.Add(text);
        return new HttpResponseMessage(response.StatusCode) { Content = new StringContent(text) };
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>A project that follows the authenticated remote, cloned with the credential, and a committed workspace of Ada's on it.</summary>
    private async Task<string> CommittedWorkspace(string? credential = "secret:" + Secret)
    {
        await SetSecret("oauth2:" + Token);
        projects.Add(new HostedProject("sales", null, RemoteUrl, null, credential));
        projects.Pull(projects.Find("sales")!);
        var id = (string)(await Json(await AsAda(HttpMethod.Post, "/api/v1/projects/sales/workspaces", """{"name":"draft"}""")))["id"]!;
        var folder = Path.Combine(ServerDir, "workspaces", "sales", id);
        var model = Directory.EnumerateFiles(folder, "*.sql", SearchOption.AllDirectories).First();
        File.AppendAllText(model, "\n-- pushed\n");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/commit", """{"message":"Push me"}""");
        return id;
    }

    private bool RemoteHasBranch(string id) { using var bare = new Repository(remote.PathOf("sales")); return bare.Branches["ws/" + id] is not null; }

    [Fact]
    public async Task The_projects_credential_pushes_a_workspace_to_a_remote_that_requires_it()
    {
        var id = await CommittedWorkspace();

        var pushed = await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push");

        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        Assert.True((bool)(await Json(pushed))["pushed"]!);
        using var bare = new Repository(remote.PathOf("sales"));
        Assert.Equal("Push me", bare.Branches["ws/" + id].Tip.Message.Trim());
        Assert.Contains(remote.Requests, r => r.Authorised && r.Path.EndsWith("git-receive-pack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_with_no_credential_is_refused_by_the_remote_and_says_so()
    {
        var id = await CommittedWorkspace();
        projects.Add(new HostedProject("sales", null, RemoteUrl, null));      // the credential removed from the entry; the checkout is already there

        var refused = await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("asks for a login", (string)(await Json(refused))["error"]!);
        Assert.False(RemoteHasBranch(id));
    }

    [Fact]
    public async Task A_refused_token_names_the_credential_and_the_branch_stays_off_the_remote()
    {
        var id = await CommittedWorkspace();
        await SetSecret("oauth2:revoked-0000000000000000");

        var refused = await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (string)(await Json(refused))["error"]!;
        Assert.Contains(Secret, message);
        Assert.Contains("refused", message);
        Assert.DoesNotContain("revoked-0000000000000000", message);
        Assert.False(RemoteHasBranch(id));
    }

    [Fact]
    public async Task A_credential_that_is_not_in_the_secret_store_fails_naming_the_secret_and_the_command_that_sets_it()
    {
        var id = await CommittedWorkspace();
        await ServerSecrets.Open(ServerDir).DeleteAsync(SecretName.Parse(Secret));

        var refused = await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (string)(await Json(refused))["error"]!;
        Assert.Contains(Secret, message);
        Assert.Contains("dbdatabuild secret set " + Secret, message);
        Assert.False(RemoteHasBranch(id));
        Assert.DoesNotContain(remote.Requests, r => r.Path.EndsWith("git-receive-pack", StringComparison.Ordinal));                        // the remote was not even asked
    }

    [Fact]
    public async Task The_token_is_in_no_answer_no_log_line_and_no_file_outside_the_encrypted_store()
    {
        var id = await CommittedWorkspace();
        await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push");          // succeeds
        await SetSecret("oauth2:revoked-0000000000000000");
        await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push");          // refused
        await AsAda(HttpMethod.Get, "/api/v1/projects");
        await AsAda(HttpMethod.Get, "/api/v1/projects/sales/workspaces");
        await AsAda(HttpMethod.Get, $"/api/v1/projects/sales/workspaces/{id}/changes");

        foreach (var secret in new[] { Token, "revoked-0000000000000000" })
        {
            Assert.DoesNotContain(answers, a => a.Contains(secret, StringComparison.Ordinal));
            lock (log) Assert.DoesNotContain(log, l => l.Contains(secret, StringComparison.Ordinal));
            var needle = Encoding.UTF8.GetBytes(secret);
            var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar, StringComparison.Ordinal))   // the store holds it, encrypted
                .Where(f => File.ReadAllBytes(f).AsSpan().IndexOf(needle) >= 0).ToList();
            Assert.Empty(found);
        }
        foreach (var config in Directory.EnumerateFiles(root, "config", SearchOption.AllDirectories).Where(f => f.Contains(".git", StringComparison.Ordinal)))
            Assert.DoesNotContain("oauth2", File.ReadAllText(config));
        Assert.DoesNotContain("glpat", File.ReadAllText(projects.Path));                      // the file names the secret, never holds it
        Assert.Contains("git_credential: 'secret:" + Secret + "'", File.ReadAllText(projects.Path).Replace("\"", "'"));
    }

    [Fact]
    public async Task Adding_a_project_through_the_command_line_takes_a_git_credential_and_a_missing_one_fails_naming_it()
    {
        async Task<(int Exit, string Text)> Run(params string[] args)
        {
            var (o, e) = (new StringWriter(), new StringWriter());
            var exit = await ServerCli.RunAsync([.. args, "--repo", ServerDir], o, e, TextReader.Null);
            return (exit, o.ToString() + e);
        }

        var missing = await Run("server", "project", "add", "sales", "--git", RemoteUrl, "--git-credential", "sales");
        Assert.NotEqual(0, missing.Exit);
        Assert.Contains(Secret, missing.Text);
        Assert.Contains("dbdatabuild secret set " + Secret, missing.Text);
        Assert.Empty(projects.All());                                                        // nothing left behind

        await SetSecret("oauth2:" + Token);
        var added = await Run("server", "project", "add", "sales", "--git", RemoteUrl, "--git-credential", "sales");
        Assert.Equal(0, added.Exit);
        Assert.Contains("Cloned 'sales'", added.Text);
        Assert.DoesNotContain(Token, added.Text);
        Assert.Equal("secret:" + Secret, projects.Find("sales")!.GitCredential);
        Assert.Contains("credential secret:" + Secret, (await Run("server", "project", "list")).Text);

        var notACredential = await Run("server", "project", "add", "other", "--git", RemoteUrl, "--git-credential", "secret:dbdata:sync:connection:x");
        Assert.NotEqual(0, notACredential.Exit);
        Assert.Contains("not a git credential", notACredential.Text);
    }

    [Fact]
    public async Task An_entry_that_names_a_credential_without_a_git_address_or_with_a_wrong_reference_is_skipped_and_reported()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(projects.Path)!);
        await File.WriteAllTextAsync(projects.Path, "projects:\n  - {id: a, path: /x, git_credential: 'secret:dbdata:build:git:a'}\n  - {id: b, git: 'https://h/b.git', git_credential: 'env:TOKEN'}\n");

        Assert.Empty(projects.All());
        Assert.Equal(2, projects.Problems.Count(p => p.Contains("git_credential")));
    }

    [Fact]
    public async Task A_successful_push_answers_the_pull_request_link_for_a_remote_at_a_known_provider_and_none_otherwise()
    {
        var id = await CommittedWorkspace();
        var local = await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push"));
        Assert.Null(local["pull_request_url"]);                                              // 127.0.0.1 is none of the three

        // the same project as if it lived at GitHub: git's own url.<base>.insteadOf sends the stand-in's traffic there, so the push still reaches it
        const string github = "https://github.com/acme/sales.git";
        using (var repo = new Repository(Path.Combine(ServerDir, "projects", "sales")))
        {
            repo.Config.Set($"url.{RemoteUrl}.insteadOf", github);
            repo.Network.Remotes.Remove("origin");
            repo.Network.Remotes.Add("origin", github);
        }
        projects.Add(new HostedProject("sales", null, github, null, "secret:" + Secret));
        await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/commit", """{"message":"More"}""");   // nothing new: still pushable

        var linked = await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/sales/workspaces/{id}/push"));
        Assert.Equal($"https://github.com/acme/sales/pull/new/ws/{id}", (string)linked["pull_request_url"]!);
    }
}
