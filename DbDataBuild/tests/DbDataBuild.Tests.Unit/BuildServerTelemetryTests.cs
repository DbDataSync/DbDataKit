using System.Net;
using System.Text;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Telemetry;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 22a GP28: the Build server with <c>telemetry: enabled</c> makes a span for a command request and counts it; an input's value and the token are in no attribute; off, nothing is made.</summary>
public sealed class BuildServerTelemetryTests : IAsyncLifetime
{
    private readonly string project = Path.Combine(Path.GetTempPath(), "ddb-tel-project-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string serverDir = Path.Combine(Path.GetTempPath(), "ddb-tel-server-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());

    private sealed class Fake : IProjectCommandRunner
    {
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, CancellationToken cancellationToken) => RunAsync(argv, projectRoot, null, cancellationToken);
        public Task<CommandResult> RunAsync(IReadOnlyList<string> argv, string projectRoot, string? actor, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(0, """{"command":"fake","ok":true}""", ""));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var d in new[] { project, serverDir }) try { Directory.Delete(d, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private async Task<(WebApplication App, HttpClient Http, string Token, string Id)> StartAsync(bool telemetry)
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", project, "--format", "json"], new StringWriter(), new StringWriter()));
        Directory.CreateDirectory(serverDir);
        if (telemetry) File.WriteAllText(Path.Combine(serverDir, "server.yaml"), "telemetry:\n  enabled: true\n  endpoint: http://127.0.0.1:1\n");
        var id = Path.GetFileName(project);
        var settings = new BuildServerSettings(serverDir, new Uri("https://127.0.0.1:0"));
        var projects = new ProjectsFile(serverDir);
        projects.Add(new HostedProject(id, project, null, null));
        var app = BuildServer.Build(settings, BuildServer.Certificate(serverDir), new Fake(), projects, new TuiCommand.CliHost(_ => null).Commands, _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Operator));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return (app, http, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "ada", ServerRole.Operator)), id);
    }

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient http, string id, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{id}/commands/connection_refresh") { Content = new StringContent("""{"connection":"sqlserver","plan":"nightly","input":["since=INPUT-SENTINEL-2026"]}""", Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task On_a_command_request_is_a_span_and_a_counter_and_no_input_or_token_leaves()
    {
        using var capture = TelemetryCapture.Start();
        var (app, http, token, id) = await StartAsync(telemetry: true);
        await using var _ = app;
        using var __ = http;
        using var response = await RefreshAsync(http, id, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        capture.Flush(app.Services);

        var span = Assert.Single(capture.Spans, s => ((string?)s.GetTagItem("http.route"))?.EndsWith("/commands/{tool}", StringComparison.Ordinal) == true);
        Assert.Equal("dbdatabuild", span.GetTagItem(HostTelemetry.ProductTag));
        Assert.True(capture.Sum("dbdatakit.http.server.requests", (HostTelemetry.ProductTag, "dbdatabuild")) >= 1);
        var everything = string.Join('\n', capture.AllAttributes());
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain("INPUT-SENTINEL", everything);
        Assert.DoesNotContain(id, everything);   // a project's name is in the path, not in the route template
        await app.StopAsync();
    }

    [Fact]
    public async Task Off_makes_no_span()
    {
        using var capture = TelemetryCapture.Start();
        var (app, http, token, id) = await StartAsync(telemetry: false);
        await using var _ = app;
        using var __ = http;
        using var response = await RefreshAsync(http, id, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(capture.Spans);
        Assert.Null(app.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));
        await app.StopAsync();
    }
}
