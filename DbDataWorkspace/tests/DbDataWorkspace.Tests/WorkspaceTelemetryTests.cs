using System.Net;
using DbDataKit.Telemetry;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataWorkspace.Tests;

/// <summary>Plan 22a GP28: the Workspace server with <c>telemetry: enabled</c> makes a span for a request and counts it; the token the call carries is in no attribute; off, nothing is made.</summary>
public sealed class WorkspaceTelemetryTests : IAsyncLifetime
{
    private readonly string directory = Directory.CreateTempSubdirectory("ddw-telemetry-").FullName;
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private async Task<(WebApplication App, HttpClient Http, string Token)> StartAsync(bool telemetry)
    {
        if (telemetry) File.WriteAllText(Path.Combine(directory, "server.yaml"), "telemetry:\n  enabled: true\n  endpoint: http://127.0.0.1:1\n");
        var settings = new WorkspaceSettings(directory, new Uri("https://127.0.0.1:0"));
        var app = WorkspaceServer.Build(settings, WorkspaceServer.Certificate(directory), _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = WorkspaceState.Pairing(directory).Codes().Create(ServerRoles.Close(ServerRole.Viewer));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return (app, http, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer)));
    }

    [Fact]
    public async Task On_a_request_is_a_span_and_a_counter_and_the_token_is_nowhere()
    {
        using var capture = TelemetryCapture.Start();
        var (app, http, token) = await StartAsync(telemetry: true);
        await using var _ = app;
        using var __ = http;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/info?q=QUERY-SENTINEL-77");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        capture.Flush(app.Services);

        var span = Assert.Single(capture.Spans, s => (string?)s.GetTagItem("http.route") == "/api/v1/info");
        Assert.Equal("dbdataworkspace", span.GetTagItem(HostTelemetry.ProductTag));
        Assert.True(capture.Sum("dbdatakit.http.server.requests", (HostTelemetry.ProductTag, "dbdataworkspace"), ("http.route", "/api/v1/info")) >= 1);
        var everything = string.Join('\n', capture.AllAttributes());
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain("QUERY-SENTINEL", everything);
        await app.StopAsync();
    }

    [Fact]
    public async Task Off_makes_no_span()
    {
        using var capture = TelemetryCapture.Start();
        var (app, http, token) = await StartAsync(telemetry: false);
        await using var _ = app;
        using var __ = http;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/info");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(app.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));
        Assert.Empty(capture.Spans);
        await app.StopAsync();
    }
}
