using System.Net;
using DbDataKit.Telemetry;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataStore.Tests;

/// <summary>Plan 22a GP28: the Store server with <c>telemetry: enabled</c> makes a span for a request and counts it and its state commands; off, nothing is made; the token a call carries is in no attribute.</summary>
public sealed class StoreTelemetryTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-telemetry-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private async Task<(WebApplication App, HttpClient Http, string Token)> StartAsync(bool telemetry)
    {
        if (telemetry) File.WriteAllText(Path.Combine(directory, "server.yaml"), "telemetry:\n  enabled: true\n  endpoint: http://127.0.0.1:1\n  instance_id: store-test\n");
        var settings = new StoreSettings(directory, new Uri("https://127.0.0.1:0"));
        var app = StoreServer.Build(settings, StoreServer.Certificate(directory), _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = StoreState.Pairing(directory).Codes().Create(ServerRoles.Close(ServerRole.Viewer));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        return (app, http, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer)));
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task On_a_request_is_a_span_and_a_counter_and_the_token_is_nowhere()
    {
        using var capture = TelemetryCapture.Start();
        var (app, http, token) = await StartAsync(telemetry: true);
        await using var _ = app;
        using var __ = http;
        using var response = await GetAsync(http, "/api/v1/info?secret_query=QUERY-SENTINEL-1f", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        capture.Flush(app.Services);

        var span = Assert.Single(capture.Spans, s => (string?)s.GetTagItem("http.route") == "/api/v1/info");
        Assert.Equal("dbdatastore", span.GetTagItem(HostTelemetry.ProductTag));
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
        Assert.True(capture.Sum("dbdatakit.http.server.requests", (HostTelemetry.ProductTag, "dbdatastore"), ("http.route", "/api/v1/info")) >= 1);
        // the state database was used (the pairing, the instance id): counted by statement name, which is a module and a verb
        Assert.True(capture.Sum("dbdatakit.state.commands") > 0);
        var everything = string.Join('\n', capture.AllAttributes());
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain("QUERY-SENTINEL", everything);
        Assert.DoesNotContain(" FROM ", everything, StringComparison.OrdinalIgnoreCase);   // a statement's name is module.verb, never its text
        await app.StopAsync();
    }

    [Fact]
    public async Task Off_makes_no_span_and_registers_no_provider()
    {
        using var capture = TelemetryCapture.Start();
        var (app, http, token) = await StartAsync(telemetry: false);
        await using var _ = app;
        using var __ = http;
        using var response = await GetAsync(http, "/api/v1/info", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(app.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));
        Assert.Empty(capture.Spans);
        await app.StopAsync();
    }
}
