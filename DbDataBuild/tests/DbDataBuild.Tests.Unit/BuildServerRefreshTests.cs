using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 33 UE09: running a refresh plan and the project's tests from the Build screens goes through the generic command route. A refresh is an operator's, is evaluated for its checks into the server's own directory
/// (the option is the server's to set, not the caller's), and is audited, a refusal included; the project's tests are a viewer's.
/// </summary>
public sealed class BuildServerRefreshTests : IAsyncLifetime
{
    private readonly string project = Path.Combine(Path.GetTempPath(), "ddb-ue09-project-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string serverDir = Path.Combine(Path.GetTempPath(), "ddb-ue09-server-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly Fake runner = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private string id = null!;

    private sealed class Fake : IProjectCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public int Exit { get; set; }
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => RunAsync(argv, projectRoot, null, cancellationToken);
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, string? actor, CancellationToken cancellationToken)
        {
            Calls.Add(argv);
            return Task.FromResult(new CommandResult(Exit, """{"command":"fake","ok":true}""", ""));
        }
    }

    public async Task InitializeAsync()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        Directory.CreateDirectory(serverDir);
        id = Path.GetFileName(project);
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject(id, project, null, null));
        app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), runner, projects, new TuiCommand.CliHost(_ => null).Commands, _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator | ServerRole.Author));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var d in new[] { project, serverDir }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> Post(string tool, string body, ServerRole roles, string user = "ada")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{id}/commands/{tool}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, user, user, roles)));
        return await http.SendAsync(request);
    }

    private IReadOnlyList<AuditEntry> Audit() => new FileAuditLog(Path.Combine(serverDir, "management-audit.log")).Read();

    [Fact]
    public async Task A_refresh_by_an_operator_records_its_checks_in_the_servers_directory_and_is_audited()
    {
        var response = await Post("connection_refresh", """{"connection":"sqlserver","plan":"nightly","selectors":["marts.*"],"only_failed":true,"input":["since=2026-01-01"]}""", ServerRole.Operator);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["connection", "refresh"], call.Take(2));
        Assert.Contains("--only-failed", call);
        var results = call.ToList().IndexOf("--check-results");
        Assert.True(results > 0, "the server asks for the checks to be evaluated");
        Assert.Equal(serverDir, call[results + 1]);

        var entry = Assert.Single(Audit());
        Assert.Equal("refresh.run", entry.Command);
        Assert.Equal($"{id}/sqlserver/nightly", entry.Target);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal("ada", entry.UserId);
        Assert.DoesNotContain("2026-01-01", System.Text.Json.JsonSerializer.Serialize(entry));   // an input's value is not for the log
    }

    [Fact]
    public async Task A_caller_cannot_choose_where_the_checks_are_recorded()
    {
        var response = await Post("connection_refresh", """{"check_results":"elsewhere"}""", ServerRole.Operator);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("check_results", await response.Content.ReadAsStringAsync());
        Assert.Empty(runner.Calls);
        Assert.Empty(Audit());
    }

    [Fact]
    public async Task A_viewer_is_refused_in_words_and_the_refusal_is_audited()
    {
        var response = await Post("connection_refresh", """{"plan":"nightly"}""", ServerRole.Viewer, "vera");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("needs the operator role", await response.Content.ReadAsStringAsync());
        Assert.Empty(runner.Calls);
        var entry = Assert.Single(Audit());
        Assert.Equal("refresh.run", entry.Command);
        Assert.StartsWith("refused", entry.Outcome);
        Assert.Equal("vera", entry.UserId);
    }

    [Fact]
    public async Task A_refresh_that_ended_with_findings_is_answered_with_its_exit_code_and_audited_as_such_and_a_dry_run_says_so()
    {
        runner.Exit = 1;
        var failed = await Post("connection_refresh", """{"plan":"nightly"}""", ServerRole.Operator);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal(1, (int)JsonNode.Parse(await failed.Content.ReadAsStringAsync())!["exit"]!);

        runner.Exit = 0;
        await Post("connection_refresh", """{"plan":"nightly","dry_run":true}""", ServerRole.Operator);

        var audit = Audit();
        Assert.Equal(2, audit.Count);
        Assert.Equal("exit 1", audit[0].Outcome);
        Assert.Equal("ok", audit[1].Outcome);
        Assert.Equal("dry run", audit[1].TokenId);   // the entry's last field carries the detail
        Assert.Equal(["connection", "refresh"], runner.Calls[1].Take(2));
    }

    [Fact]
    public async Task The_projects_tests_are_a_viewers_and_are_not_audited()
    {
        var response = await Post("project_tests_run", """{"limit":3}""", ServerRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["project", "tests", "run"], runner.Calls[0].Take(3));
        Assert.Contains("--limit", runner.Calls[0]);
        Assert.DoesNotContain("--check-results", runner.Calls[0]);
        Assert.Empty(Audit());
    }
}
