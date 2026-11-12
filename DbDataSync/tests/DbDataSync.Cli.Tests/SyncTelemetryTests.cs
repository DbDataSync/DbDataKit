using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DbDataKit.Telemetry;
using DbDataSync.Api;
using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Plan 22a GP28: the real Sync host with <c>telemetry: enabled</c> in <c>server.yaml</c> makes a span for a request, refused or not, and counts it; a bearer token and a query string are in no attribute; off, no provider is registered.
/// </summary>
public sealed class SyncTelemetryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-telemetry-").FullName;
    private readonly List<IAsyncDisposable> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers) server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GitTempDirectory.DeleteRecursively(_root);
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async Task<(Microsoft.AspNetCore.Builder.WebApplication App, string Url)> StartAsync(bool telemetry)
    {
        ServeCommand.Prepare(_root);
        var url = $"http://127.0.0.1:{FreePort()}";
        var file = Path.Combine(_root, "server.yaml");
        var existing = File.Exists(file) ? File.ReadAllText(file) : "";
        if (telemetry) File.WriteAllText(file, existing.TrimEnd() + "\ntelemetry:\n  enabled: true\n  endpoint: http://127.0.0.1:1\n");
        var app = DbDataSyncHost.Build(ServeCommand.BuildHostArgs([], _root, Path.Combine(_root, "state.db"), url));
        _servers.Add(app);
        await app.StartAsync();
        return (app, url);
    }

    [Fact]
    public async Task On_a_request_is_a_span_and_a_counter_and_the_token_is_nowhere()
    {
        using var capture = TelemetryCapture.Start();
        var (app, url) = await StartAsync(telemetry: true);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/nothing-here?secret_query=QUERY-SENTINEL-5");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "TOKEN-SENTINEL-sync-9");
        using var response = await http.SendAsync(request);
        capture.Flush(app.Services);

        Assert.Contains(capture.Spans, s => (string?)s.GetTagItem(HostTelemetry.ProductTag) == "dbdatasync" && s.Kind == System.Diagnostics.ActivityKind.Server);
        Assert.True(capture.Sum("dbdatakit.http.server.requests", (HostTelemetry.ProductTag, "dbdatasync")) >= 1);
        var everything = string.Join('\n', capture.AllAttributes());
        Assert.DoesNotContain("TOKEN-SENTINEL", everything);
        Assert.DoesNotContain("QUERY-SENTINEL", everything);
    }

    [Fact]
    public async Task Off_registers_no_provider_and_makes_no_span()
    {
        using var capture = TelemetryCapture.Start();
        var (app, url) = await StartAsync(telemetry: false);
        using var http = new HttpClient();
        using var response = await http.GetAsync(url + "/api/v1/nothing-here");
        Assert.Null(app.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));
        Assert.Empty(capture.Spans);
    }
}
