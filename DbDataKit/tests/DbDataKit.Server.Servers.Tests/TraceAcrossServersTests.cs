using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Telemetry;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 22a GP29: a request to the console that it relays to a paired server (a real DbDataStore server, in this process) is one trace. The console's request span and the server's request span have the same trace id, the server's is a
/// child of the console's (the W3C <c>traceparent</c> header the relay adds), and a caller's own <c>traceparent</c> is the root of both. The bearer of the person's session, the signed management token and a query string are in no attribute.
/// </summary>
public sealed class TraceAcrossServersTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-e2e-trace-" + Guid.NewGuid().ToString("N")[..8]);
    private TelemetryCapture capture = null!;
    private WebApplication console = null!;
    private WebApplication store = null!;
    private HttpClient http = null!;
    private string session = null!;
    private string storeId = null!;

    public async ValueTask InitializeAsync()
    {
        capture = TelemetryCapture.Start();
        var (storeDirectory, consoleDirectory) = (Path.Combine(root, "store"), Path.Combine(root, "console"));
        Directory.CreateDirectory(storeDirectory);
        Directory.CreateDirectory(consoleDirectory);
        const string section = "telemetry:\n  enabled: true\n  endpoint: http://127.0.0.1:1\n";
        File.WriteAllText(Path.Combine(storeDirectory, "server.yaml"), section);
        File.WriteAllText(Path.Combine(consoleDirectory, ConsoleSettings.SettingsFileName), section);

        var storeSettings = new StoreSettings(storeDirectory, new Uri("https://127.0.0.1:0"));
        store = StoreServer.Build(storeSettings, StoreServer.Certificate(storeDirectory), _ => { });
        await store.StartAsync();
        var consoleSettings = new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0"));
        console = ConsoleHost.Build(consoleSettings, ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
        session = console.Services.GetRequiredService<ISessionStore>().Create(console.Services.GetRequiredService<IUserAccounts>().CreateUser("Admin person", null, UserRole.Admin).Id).Id;
        var code = StoreState.Pairing(storeDirectory).Codes().Create(ServerRoles.Close(ServerRole.Viewer));
        storeId = (await console.Services.GetRequiredService<ConsoleInstances>().PairAsync(new Uri(store.Urls.First()), code.Code, "Store", default)).InstanceId;
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await console.StopAsync();
        await console.DisposeAsync();
        await store.StopAsync();
        await store.DisposeAsync();
        capture.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static string Product(Activity span) => (string?)span.GetTagItem(HostTelemetry.ProductTag) ?? "";

    /// <summary>Each span once: both hosts of this process listen to the one source, so each exports every span.</summary>
    private List<Activity> Spans() => [.. capture.Spans.GroupBy(s => s.SpanId.ToHexString()).Select(g => g.First())];

    [Fact]
    public async Task A_request_through_the_console_to_a_paired_server_is_one_trace_with_the_callers_traceparent_as_its_root()
    {
        var callerTrace = ActivityTraceId.CreateRandom();
        var callerSpan = ActivitySpanId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/relay/{storeId}/api/v1/info?secret_query=QUERY-SENTINEL-29");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        request.Headers.Add("traceparent", $"00-{callerTrace.ToHexString()}-{callerSpan.ToHexString()}-01");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("dbdatastore", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["product"]!);
        capture.Flush(console.Services);
        capture.Flush(store.Services);

        var spans = Spans().Where(s => s.TraceId == callerTrace).ToList();
        var consoleSpan = Assert.Single(spans, s => Product(s) == "dbdatakit" && s.Kind == ActivityKind.Server);
        // the server's spans of this trace (the console also asks what the server is, on the same trace)
        var storeSpans = spans.Where(s => Product(s) == "dbdatastore" && s.Kind == ActivityKind.Server).ToList();
        Assert.NotEmpty(storeSpans);
        Assert.Equal(callerSpan, consoleSpan.ParentSpanId);            // the caller's traceparent is the root
        Assert.All(storeSpans, s => Assert.Equal(consoleSpan.SpanId, s.ParentSpanId));   // the relay passed the console's span on: W3C traceparent across the relay
        Assert.Contains(storeSpans, s => (string?)s.GetTagItem("http.route") == "/api/v1/info");

        // the scrub over every span and label of both hosts
        var everything = string.Join('\n', capture.AllAttributes());
        Assert.DoesNotContain("QUERY-SENTINEL", everything);
        Assert.DoesNotContain(session, everything);
        Assert.DoesNotContain("eyJ", everything);                       // the signed management token the relay made
        Assert.DoesNotContain(callerSpan.ToHexString() + "x", everything);
    }

    [Fact]
    public async Task Without_a_traceparent_the_server_still_continues_the_trace_of_the_console_span()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/relay/{storeId}/api/v1/info");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        capture.Flush(console.Services);
        capture.Flush(store.Services);

        var infos = Spans().Where(s => Product(s) == "dbdatastore" && (string?)s.GetTagItem("http.route") == "/api/v1/info").ToList();
        var relayed = infos.First(s => s.ParentSpanId != default && Spans().Any(c => c.SpanId == s.ParentSpanId && Product(c) == "dbdatakit"));
        var parent = Spans().Single(s => s.SpanId == relayed.ParentSpanId);
        Assert.Equal(parent.TraceId, relayed.TraceId);
    }
}
