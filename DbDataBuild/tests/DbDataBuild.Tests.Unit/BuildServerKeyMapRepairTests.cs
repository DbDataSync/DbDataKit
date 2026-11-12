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
/// Plan 25 K10: the audited repair of a key map through the Build server. It is the administrator's (the owner's decision of 2026-11-12), not the approver's, whether it is a dry run or applied; every applied one, and every refusal, is in the
/// server's audit log with the operation, the integer and the reason; the person who called it is the one the command is told to record. Reading a map is a viewer's.
/// </summary>
public sealed class BuildServerKeyMapRepairTests : IAsyncLifetime
{
    private readonly string project = Path.Combine(Path.GetTempPath(), "ddb-km-project-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string serverDir = Path.Combine(Path.GetTempPath(), "ddb-km-server-" + Guid.NewGuid().ToString("N")[..8]);
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
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Approver | ServerRole.Operator | ServerRole.Author));
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
    public async Task An_administrator_applies_a_repair_and_it_is_audited_with_the_operation_the_integer_and_the_reason()
    {
        var response = await Post("connection_key_map_repair", """{"connection":"sqlserver","key":"customer_key","operation":"remove","value":7,"reason":"bug 123 wrote it","force":true,"apply":true}""", ServerRole.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["connection", "key-map", "repair"], call.Take(3));
        Assert.Contains("--apply", call);
        Assert.Contains("--force", call);
        var entry = Assert.Single(Audit());
        Assert.Equal("keymap.repair", entry.Command);
        Assert.Equal($"{id}/sqlserver/customer_key", entry.Target);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal("ada", entry.UserId);
        Assert.Equal("operation remove, value 7, reason: bug 123 wrote it (forced)", entry.TokenId);   // the entry's last field carries the detail
    }

    [Fact]
    public async Task An_approver_and_an_operator_are_refused_in_words_even_for_a_dry_run_and_the_refusal_is_audited()
    {
        foreach (var roles in new[] { ServerRole.Approver, ServerRole.Operator | ServerRole.Author })
        {
            var dry = await Post("connection_key_map_repair", """{"key":"customer_key","operation":"reseed"}""", roles, "olga");
            Assert.Equal(HttpStatusCode.Forbidden, dry.StatusCode);
            Assert.Contains("needs the admin role", await dry.Content.ReadAsStringAsync());
            var apply = await Post("connection_key_map_repair", """{"key":"customer_key","operation":"reseed","reason":"x","apply":true}""", roles, "olga");
            Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
        }
        Assert.Empty(runner.Calls);
        Assert.All(Audit(), e => { Assert.Equal("keymap.repair", e.Command); Assert.StartsWith("refused", e.Outcome); Assert.Equal("olga", e.UserId); });
        Assert.Equal(4, Audit().Count);
    }

    [Fact]
    public async Task An_administrators_dry_run_is_not_audited_and_the_console_is_told_the_role()
    {
        var dry = await Post("connection_key_map_repair", """{"key":"customer_key","operation":"reseed"}""", ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.DoesNotContain("--apply", runner.Calls[0]);
        Assert.Empty(Audit());

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{id}/commands");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "ada", ServerRole.Viewer)));
        var listed = JsonNode.Parse(await (await http.SendAsync(request)).Content.ReadAsStringAsync())!["commands"]!.AsArray();
        var repair = listed.Single(c => (string?)c!["name"] == "connection key-map repair")!;
        Assert.Equal("admin", (string?)repair["role"]);
        Assert.False((bool)repair["allowed"]!);                                  // a viewer's console hides it
        var show = listed.Single(c => (string?)c!["name"] == "connection key-map show")!;
        Assert.Equal("viewer", (string?)show["role"]);
        Assert.True((bool)show["allowed"]!);
    }

    [Fact]
    public async Task Reading_a_map_is_a_viewers()
    {
        var response = await Post("connection_key_map_show", """{"key":"customer_key"}""", ServerRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["connection", "key-map", "show"], runner.Calls[0].Take(3));
        Assert.Empty(Audit());
    }
}
